"""
Unity MCP Server entrypoint. Serves the same tool set over one of two transports
(see main() / UNITY_MCP_TRANSPORT): stdio (default, what Claude Code / Codex use
normally) or streamable HTTP (Phase 7, http_transport.py — for remote/multi-client
scenarios stdio can't cover). Two sources feed the tool list this process
advertises, regardless of transport:

  1. Unity's own [MCPTool]-decorated C# methods, discovered by reflection on the
     Unity side and fetched fresh via list_tools() on every call -- add a tool in
     C#, restart the bridge listener, and it shows up here with no changes to
     this file.
  2. Python-side "workflow" tools (workflows.py) -- composite tools built by
     calling Unity's atomic tools, sometimes several times with real branching
     logic (batch_execute, create_behavior_tree, ...). These are hand-written,
     not reflected, since there's no single Unity method for the registry to
     discover.

Both are merged into one list_tools() response and dispatched from the same
call_tool() handler, so an MCP client can't tell which is which -- which is the
whole point of the composite-tool layer being invisible from the outside.
"""
import asyncio
import json
import logging
import os
import sys

import mcp.server.stdio
import mcp.types as types
from mcp.server import Server
from mcp.server.lowlevel.server import NotificationOptions

from . import groups, workflows
from .bridge_client import BridgeError, UnityBridgeClient

# Log to stderr only — stdout is the MCP transport channel and must stay clean JSON-RPC.
logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
logger = logging.getLogger("unity_mcp.server")

server = Server("unity-mcp", version="0.6.0")
bridge = UnityBridgeClient()


# StreamableHTTPSessionManager (http_transport.py) calls server.create_initialization_options()
# with NO arguments internally -- meaning without this override, the HTTP transport would
# silently get NotificationOptions() (every capability False), never declaring tools_changed,
# and the reconnect-notification fix above would silently stop working for HTTP clients only.
# Overriding the method at the instance level, once, here, is what makes stdio's run() and
# http_transport.py's session manager both get the same capability defaults automatically —
# one source of truth instead of two call sites that could drift out of sync with each other.
_original_create_initialization_options = server.create_initialization_options


def _create_initialization_options_with_defaults(notification_options=None, experimental_capabilities=None):
    return _original_create_initialization_options(
        notification_options=notification_options or NotificationOptions(tools_changed=True),
        experimental_capabilities=experimental_capabilities or {},
    )


server.create_initialization_options = _create_initialization_options_with_defaults


async def _notify_tools_changed(reason: str) -> None:
    """
    Tells the connected MCP client its cached tool list may be stale and it should
    refetch. Used both when manage_tools changes which groups are active, and — this
    is the fix for a real reported bug — whenever the bridge reconnects to Unity
    after having been dropped (e.g. a domain reload, on whatever port Unity ends up
    on this time). Without this second case, a client that was mid-session when
    Unity restarted would stay stuck seeing only the Python-side workflow tools
    (Unity's own tools vanish from list_tools() while the bridge is unreachable)
    even after Unity fully comes back — the connection recovers, but nothing ever
    told the client to look again.
    """
    try:
        await server.request_context.session.send_tool_list_changed()
    except LookupError:
        # No active request context -- happens when this runs outside a real client
        # session (our own tests calling handlers directly, or a reconnect that
        # happens to complete between requests with no request context available to
        # notify through). Nothing to notify in that case; the next real request will
        # get the current, correct list anyway.
        pass
    except Exception as e:
        # Never let a failed notification take down an otherwise-successful operation.
        logger.warning("Failed to send tools/list_changed notification (%s): %s", reason, e)


bridge.add_reconnect_listener(lambda: _notify_tools_changed("bridge reconnected"))


@server.list_tools()
async def list_tools() -> list[types.Tool]:
    try:
        unity_tools = await bridge.list_tools()
    except BridgeError as e:
        # Don't crash the MCP session just because Unity isn't open yet — surface
        # only the workflow tools (which at least list correctly even though calling
        # one will still fail until Unity is reachable) so the client stays
        # connected and can retry later.
        logger.error("Could not fetch tool list from Unity: %s", e)
        unity_tools = []

    active = groups.get_active_groups()

    tools = []
    hidden = 0
    for t in unity_tools:
        tool_group = t.get("group") or "core"
        if tool_group not in active:
            hidden += 1
            continue
        schema = t.get("schema") or {"type": "object", "properties": {}}
        tools.append(
            types.Tool(
                name=t["name"],
                description=t.get("description", ""),
                inputSchema=schema,
            )
        )

    for wf in workflows.all_workflows():
        if wf.group not in active:
            hidden += 1
            continue
        tools.append(types.Tool(name=wf.name, description=wf.description, inputSchema=wf.schema))

    logger.info(
        "Advertised %d tool(s) (active groups: %s); %d hidden by inactive groups.",
        len(tools), sorted(active), hidden,
    )
    return tools


@server.call_tool()
async def call_tool(name: str, arguments: dict) -> list[types.TextContent]:
    wf = workflows.get_workflow(name)
    if wf is not None:
        try:
            result = await wf.handler(bridge, arguments or {})
            text = _format_result(result)

            # manage_tools is the only *tool call* that changes which tools are
            # visible (as opposed to a reconnect, handled above via
            # add_reconnect_listener) -- same notify-the-client requirement applies:
            # changing groups.active_groups() alone does nothing for an
            # already-connected client, which caches its tool list and won't
            # refetch unless told to.
            if name == "manage_tools":
                await _notify_tools_changed("manage_tools call")

            return [types.TextContent(type="text", text=text)]
        except Exception as e:
            # Broad on purpose: workflow handlers can raise BridgeError (a failed
            # Unity call) or their own validation errors (manage_tools rejecting an
            # unknown group name, say) — either way this should come back as a clean
            # tool-error message, not crash the whole stdio session.
            logger.warning("Workflow '%s' failed: %s", name, e)
            return [types.TextContent(type="text", text=f"Error running '{name}': {e}")]

    try:
        result = await bridge.call(name, arguments or {})
        return [types.TextContent(type="text", text=_format_result(result))]
    except BridgeError as e:
        logger.warning("Tool call '%s' failed: %s", name, e)
        # Returned as normal content (not raised) so the client/model sees a
        # readable error message instead of a transport-level failure.
        return [types.TextContent(type="text", text=f"Error calling '{name}': {e}")]


def _format_result(result) -> str:
    if result is None:
        return "OK"
    if isinstance(result, str):
        return result
    return json.dumps(result, indent=2)


async def run_stdio() -> None:
    async with mcp.server.stdio.stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream,
            write_stream,
            server.create_initialization_options(),
        )


def main() -> None:
    """
    Entrypoint for both transports. Defaults to stdio (what Claude Code / Codex use
    normally, and all that Phase 1 through 6 ever needed) unless UNITY_MCP_TRANSPORT=http
    is set, in which case it serves streamable HTTP instead — see http_transport.py for
    when you'd actually want that and its security requirements.
    """
    transport = os.environ.get("UNITY_MCP_TRANSPORT", "stdio").lower()

    try:
        if transport == "stdio":
            asyncio.run(run_stdio())
        elif transport == "http":
            from . import http_transport

            host, port, token = http_transport.resolve_http_config()
            asyncio.run(http_transport.run_http_server(server, host, port, token))
        else:
            print(f"Unknown UNITY_MCP_TRANSPORT '{transport}' — expected 'stdio' or 'http'.", file=sys.stderr)
            sys.exit(1)
    except KeyboardInterrupt:
        pass
    except ValueError as e:
        # http_transport.resolve_http_config's loopback/token validation raises this —
        # a real misconfiguration, not something to swallow silently.
        print(f"unity-mcp-server: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
