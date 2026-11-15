"""
Streamable-HTTP transport.

Runs the exact same `Server` object server.py's stdio entrypoint uses, over HTTP
instead, via the MCP SDK's own StreamableHTTPSessionManager. Not needed for normal
Claude Code / Codex use on your own machine (stdio covers that); it exists for driving
Unity from another machine, or sharing one server process across several clients.

Security model -- an HTTP listener, even on loopback, is reachable by ANY local process
or user, and by web pages through DNS rebinding:

  1. Binds to 127.0.0.1 by default, never 0.0.0.0. Binding beyond loopback is refused
     at startup unless UNITY_MCP_HTTP_TOKEN is explicitly set.
  2. A bearer token is ALWAYS required (2.0.0+). Without UNITY_MCP_HTTP_TOKEN, one is
     generated once and kept in <project>/Library/MCP/http_token (never committed),
     so client configs keep working across restarts; it is printed on stderr at startup.
  3. Requests carrying a browser Origin header that isn't localhost are refused with
     403, as the MCP spec requires, before the token is even checked.
"""
import hmac
import json
import logging
import os
import secrets
import sys
from pathlib import Path
from typing import Optional
from urllib.parse import urlsplit

from mcp.server import Server
from mcp.server.streamable_http_manager import StreamableHTTPSessionManager

from . import security

logger = logging.getLogger("unity_mcp.http_transport")

LOOPBACK_HOSTS = ("127.0.0.1", "localhost", "::1")


def _stored_token(project_root: Optional[Path]) -> str:
    """Reuses Library/MCP/http_token, creating it on first use; falls back to a one-off token with no project."""
    if project_root is None:
        return secrets.token_hex(32)
    path = security.mcp_dir(project_root) / "http_token"
    try:
        token = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(token, str) and token:
            return token
    except (OSError, ValueError):
        pass
    token = secrets.token_hex(32)
    security.write_json_atomic(path, token)  # stored as a JSON string
    return token


def resolve_http_config() -> tuple[str, int, str]:
    """
    Reads UNITY_MCP_HTTP_HOST / UNITY_MCP_HTTP_PORT / UNITY_MCP_HTTP_TOKEN. Raises
    ValueError -- not a silent fallback -- if asked to bind beyond loopback without an
    explicitly set token. Always returns a token.
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

    if not token:
        try:
            project_root = security.get_project_root()
        except FileNotFoundError:
            project_root = None
        token = _stored_token(project_root)
        where = (security.mcp_dir(project_root) / "http_token") if project_root else "not saved (no Unity project found)"
        print(
            f"unity-mcp-server: HTTP clients must send 'Authorization: Bearer {token}' (token file: {where}).",
            file=sys.stderr,
        )

    return host, port, token


def _origin_allowed(origin: str) -> bool:
    """No Origin = not a browser. A browser Origin is only accepted from this machine."""
    if not origin:
        return True
    return (urlsplit(origin).hostname or "") in LOOPBACK_HOSTS


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

        headers = dict(scope.get("headers") or [])
        if not _origin_allowed(headers.get(b"origin", b"").decode("latin-1")):
            await _send_plain_response(send, 403, b"Forbidden origin")
            return

        if token:
            auth_header = headers.get(b"authorization", b"").decode("latin-1")
            if not hmac.compare_digest(auth_header.encode(), f"Bearer {token}".encode()):
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
