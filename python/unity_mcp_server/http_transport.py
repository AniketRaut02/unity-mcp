"""
Streamable-HTTP transport — Phase 7.

Runs the exact same `Server` object server.py's stdio entrypoint uses, over HTTP
instead, via the MCP SDK's own StreamableHTTPSessionManager. This is NOT needed for
normal Claude Code / Codex usage on your own machine -- stdio (the Phase 1 default)
already covers that, and is simpler. Streamable HTTP exists for what stdio can't
do: driving Unity from a different machine, or sharing one running server process
across multiple client connections instead of each client spawning its own
subprocess.

Security model, since an HTTP listener is a materially different trust boundary
than stdio: stdio is only reachable by the process that spawned it (the MCP client
itself, via inherited file descriptors). An HTTP listener, even bound to loopback,
is reachable by ANY local process or user on the machine. Two defenses, matching
this project's security posture everywhere else (see MCPServer.cs's loopback-only
bind on the Unity side):

  1. Binds to 127.0.0.1 by default -- never 0.0.0.0.
  2. Binding beyond loopback is refused outright at startup (not just discouraged
     in a comment) unless UNITY_MCP_HTTP_TOKEN is also set. When set, every request
     must carry a matching `Authorization: Bearer <token>` header or gets a 401,
     checked in a thin ASGI wrapper in front of the session manager.
"""
import logging
import os
from typing import Optional

from mcp.server import Server
from mcp.server.streamable_http_manager import StreamableHTTPSessionManager

logger = logging.getLogger("unity_mcp.http_transport")

LOOPBACK_HOSTS = ("127.0.0.1", "localhost", "::1")


def resolve_http_config() -> tuple[str, int, Optional[str]]:
    """
    Reads UNITY_MCP_HTTP_HOST / UNITY_MCP_HTTP_PORT / UNITY_MCP_HTTP_TOKEN from the
    environment. Raises ValueError -- not a silent fallback -- if asked to bind
    beyond loopback without a token: this should fail loudly at startup rather than
    quietly serve an unauthenticated endpoint to the whole network.
    """
    host = os.environ.get("UNITY_MCP_HTTP_HOST", "127.0.0.1")
    port_raw = os.environ.get("UNITY_MCP_HTTP_PORT", "8765")
    try:
        port = int(port_raw)
    except ValueError:
        raise ValueError(f"UNITY_MCP_HTTP_PORT='{port_raw}' is not a valid integer.")

    token = os.environ.get("UNITY_MCP_HTTP_TOKEN") or None

    if host not in LOOPBACK_HOSTS and not token:
        raise ValueError(
            f"Refusing to bind the streamable-HTTP transport to '{host}' (beyond loopback) without "
            "UNITY_MCP_HTTP_TOKEN set. Set that env var to a real secret, or bind to 127.0.0.1 instead."
        )

    return host, port, token


async def _send_plain_response(send, status: int, body: bytes) -> None:
    await send({"type": "http.response.start", "status": status, "headers": [(b"content-type", b"text/plain")]})
    await send({"type": "http.response.body", "body": body})


def build_asgi_app(server: Server, token: Optional[str]):
    """
    Returns (asgi_app, session_manager). asgi_app is a raw ASGI callable — no
    Starlette/FastAPI needed for a single MCP endpoint — wrapping `server` in
    streamable-HTTP framing via the SDK's own StreamableHTTPSessionManager, with an
    optional bearer-token check in front of it. session_manager is returned
    separately because its `run()` context manager must be entered by the caller
    (see run_http_server below) before any request can be handled — this function
    only constructs objects, it doesn't start anything.
    """
    session_manager = StreamableHTTPSessionManager(app=server, stateless=False)

    async def asgi_app(scope, receive, send):
        if scope["type"] != "http":
            # This server has exactly one job (the MCP endpoint); anything that
            # isn't a plain HTTP request (e.g. a websocket upgrade attempt) isn't
            # something to route anywhere.
            await _send_plain_response(send, 404, b"Not Found")
            return

        if token:
            headers = dict(scope.get("headers") or [])
            auth_header = headers.get(b"authorization", b"").decode("latin-1")
            if auth_header != f"Bearer {token}":
                await _send_plain_response(send, 401, b"Unauthorized")
                return

        await session_manager.handle_request(scope, receive, send)

    return asgi_app, session_manager


async def run_http_server(server: Server, host: str, port: int, token: Optional[str]) -> None:
    """
    Actually serves `server` over streamable HTTP with uvicorn. Runs forever (until
    cancelled) — this is the HTTP-mode entrypoint server.py's main() calls instead of
    the stdio one when UNITY_MCP_TRANSPORT=http.
    """
    import uvicorn

    asgi_app, session_manager = build_asgi_app(server, token)

    if host not in LOOPBACK_HOSTS:
        logger.warning(
            "Binding to '%s' (beyond loopback) -- make sure UNITY_MCP_HTTP_TOKEN is set to a real secret, "
            "not left as a placeholder. Anyone who can reach this host and port can drive Unity through it.",
            host,
        )

    logger.info("Streamable-HTTP transport starting on http://%s:%s%s", host, port, " (token required)" if token else "")

    async with session_manager.run():
        config = uvicorn.Config(asgi_app, host=host, port=port, log_level="warning")
        uvicorn_server = uvicorn.Server(config)
        await uvicorn_server.serve()
