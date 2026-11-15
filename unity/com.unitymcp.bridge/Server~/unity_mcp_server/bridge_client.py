"""
The Unity side of this MCP server (v2, protocol 2).

Connection model: this process is the long-lived server and the Unity Editor is the
client. On start() it serves a loopback-only WebSocket (ws://127.0.0.1:<os-picked
port>/unity) and advertises that port in Library/MCP/servers/<pid>-<port>.json. The
Editor polls that folder, connects to every live server, and re-connects after each
domain reload. A reload is therefore just "Unity went away for a few seconds": calls
are held here instead of failing, then sent (or re-sent) once Unity is back.

Wire format: JSON-RPC 2.0, one message per WebSocket text frame.
  Unity -> us   hello (request)            params {token, protocolVersion, packageVersion, ...}
  Unity -> us   tools/register (notif.)    params {tools: [...]}, sent after every hello
                                           and again whenever Unity's tool list changes
  Unity -> us   unity/reloading (notif.)   sent just before a domain reload
  us -> Unity   ping, tools/call {tool, args}, tools/batch {calls}

Reload safety: every held call keeps its JSON-RPC id. If Unity drops mid-call we
re-send the SAME id after it reconnects; Unity keeps recent answers by id (its answer
cache survives reloads), so a call that already ran returns its stored answer instead
of running twice.

Auth: Unity creates the token (new per Editor launch) and writes it to unity.json;
we read that file fresh on each hello and compare in constant time. Connections with
a browser Origin header are refused outright (Unity's ClientWebSocket never sends one).

The class keeps its v1 name and public API (call / batch / list_tools / close /
add_reconnect_listener) so workflows.py and server.py did not need to change shape.
"""
import asyncio
import atexit
import contextlib
import hmac
import json
import logging
import os
import time
import uuid
from pathlib import Path
from typing import Any, Awaitable, Callable, Optional

from . import security

logger = logging.getLogger("unity_mcp.bridge")

PROTOCOL_VERSION = 2
SERVER_VERSION = "2.0.0"
DEFAULT_HOLD_TIMEOUT = 60.0
PING_INTERVAL = 10.0
PING_TIMEOUT = 20.0
HELLO_TIMEOUT = 10.0
MAX_MESSAGE_BYTES = 16 * 1024 * 1024

ERR_TOOL = -32000
ERR_UNAUTHORIZED = -32001
ERR_VERSION = -32002


class BridgeError(Exception):
    """Raised for any failure talking to Unity: not connected, timeout, auth, or a tool-level error."""


def _hold_timeout_from_env() -> float:
    raw = os.environ.get("UNITY_MCP_RELOAD_TIMEOUT")
    if not raw:
        return DEFAULT_HOLD_TIMEOUT
    try:
        value = float(raw)
    except ValueError:
        logger.warning("UNITY_MCP_RELOAD_TIMEOUT=%r is not a number; using %ss.", raw, DEFAULT_HOLD_TIMEOUT)
        return DEFAULT_HOLD_TIMEOUT
    return max(value, 0.0)


class _Pending:
    """One in-flight request. `sent_on` is the connection it was last written to, so a reconnect re-sends it exactly once."""

    __slots__ = ("message", "future", "sent_on")

    def __init__(self, message: dict, future: asyncio.Future):
        self.message = message
        self.future = future
        self.sent_on = None


class UnityBridgeClient:
    def __init__(self, project_root: Optional[Path] = None, hold_timeout: Optional[float] = None):
        # Resolved lazily in start(): a missing project root must surface as a tool error,
        # not crash the whole MCP server at import time.
        self._project_root_override = Path(project_root) if project_root else None
        self._project_root: Optional[Path] = None
        self._hold_timeout = _hold_timeout_from_env() if hold_timeout is None else hold_timeout

        self._started = False
        self._start_lock: Optional[asyncio.Lock] = None
        self._uvicorn = None
        self._serve_task: Optional[asyncio.Task] = None
        self._port: Optional[int] = None
        self._server_file: Optional[Path] = None

        self._ws = None  # the live Unity connection, if any
        self._ws_closed: Optional[asyncio.Event] = None  # set when that connection ends
        self._connected: Optional[asyncio.Event] = None
        self._ping_task: Optional[asyncio.Task] = None
        self._pending: dict[str, _Pending] = {}
        self._ping_futures: dict[str, asyncio.Future] = {}

        self._tools: Optional[list[dict]] = None
        self._tools_received: Optional[asyncio.Event] = None
        self._unity_reloading = False
        self._ever_connected = False
        self.unity_info: dict = {}

        self._reconnect_listeners: list[Callable[[], Awaitable[None]]] = []
        self._tools_changed_listeners: list[Callable[[], Awaitable[None]]] = []

    # ------------------------------------------------------------------ public API

    def add_reconnect_listener(self, callback: Callable[[], Awaitable[None]]) -> None:
        """Runs after Unity connects again having been connected before (not on the first connect)."""
        self._reconnect_listeners.append(callback)

    def add_tools_changed_listener(self, callback: Callable[[], Awaitable[None]]) -> None:
        """Runs whenever Unity pushes a tool list that differs from the one we had."""
        self._tools_changed_listeners.append(callback)

    @property
    def is_connected(self) -> bool:
        return self._ws is not None

    @property
    def port(self) -> Optional[int]:
        """The port this server listens on for Unity (OS-assigned), once started."""
        return self._port

    async def start(self) -> None:
        """Starts the WebSocket listener and advertises it to Unity. Idempotent."""
        if self._started:
            return
        if self._start_lock is None:
            self._start_lock = asyncio.Lock()
        async with self._start_lock:
            if self._started:
                return
            self._ensure_events()
            self._project_root = self._resolve_project_root()
            await self._start_listener()
            self._write_server_file()
            self._started = True

    async def call(self, tool: str, args: dict, timeout: float = 30.0) -> Any:
        return await self._request("tools/call", {"tool": tool, "args": args or {}}, timeout, label=f"Tool '{tool}'")

    async def batch(self, calls: list[dict], timeout: float = 60.0) -> list[dict]:
        """All of `calls` as ONE message and ONE response -- the latency win over N separate calls."""
        result = await self._request("tools/batch", {"calls": calls}, timeout, label=f"Batch of {len(calls)} calls")
        return (result or {}).get("results", [])

    async def list_tools(self, timeout: float = 10.0) -> list[dict]:
        """
        The tool list Unity last pushed. Falls back to the on-disk cache (so the client's
        tool list never shrinks while Unity reloads), and only waits when Unity looks
        open but hasn't pushed yet in this process.
        """
        await self.start()
        if self._tools is not None:
            return self._tools

        cached = self._read_tool_cache()
        if cached is not None:
            return cached

        if self.is_connected or self._unity_info_exists():
            try:
                await asyncio.wait_for(self._tools_received.wait(), timeout)
                return self._tools or []
            except asyncio.TimeoutError:
                pass
        raise BridgeError(self._not_connected_message(waited=None))

    async def close(self) -> None:
        if self._ping_task:
            self._ping_task.cancel()
        ws = self._ws
        if ws is not None:
            with contextlib.suppress(Exception):
                await ws.close()
        self._detach(ws)
        for pending in list(self._pending.values()):
            if not pending.future.done():
                pending.future.set_exception(BridgeError("Bridge closed."))
        self._pending.clear()
        if self._uvicorn is not None:
            self._uvicorn.should_exit = True
        if self._serve_task is not None:
            with contextlib.suppress(Exception):
                await asyncio.wait_for(self._serve_task, 5)
        self._remove_server_file()
        self._started = False

    # ------------------------------------------------------------------ requests

    async def _request(self, method: str, params: dict, timeout: float, label: str) -> Any:
        try:
            await self.start()
        except BridgeError:
            raise
        except Exception as e:
            raise BridgeError(f"Could not start the Unity bridge listener: {e}") from e

        request_id = str(uuid.uuid4())
        pending = _Pending(
            {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params},
            asyncio.get_running_loop().create_future(),
        )
        self._pending[request_id] = pending
        try:
            response = await self._await_response(pending, timeout, label)
        finally:
            self._pending.pop(request_id, None)

        if "error" in response:
            error = response.get("error") or {}
            raise BridgeError(error.get("message") or f"Unity returned an unspecified error for {label}.")
        return response.get("result")

    async def _await_response(self, pending: _Pending, timeout: float, label: str) -> dict:
        """
        Waits for the answer, holding through disconnects. `timeout` only counts time
        while Unity is connected; time spent waiting for Unity to come back is bounded
        separately by the hold timeout, so a reload never eats a call's own budget.
        """
        remaining = timeout
        while True:
            if not self.is_connected:
                if not self._ever_connected and not self._unity_info_exists():
                    raise BridgeError(self._not_connected_message(waited=None))
                try:
                    await asyncio.wait_for(self._connected.wait(), self._hold_timeout)
                except asyncio.TimeoutError:
                    raise BridgeError(self._not_connected_message(waited=self._hold_timeout)) from None

            ws, closed = self._ws, self._ws_closed
            if ws is None:
                continue
            await self._send_pending(pending, ws)

            closed_wait = asyncio.ensure_future(closed.wait())
            started = time.monotonic()
            try:
                done, _ = await asyncio.wait({pending.future, closed_wait}, timeout=remaining, return_when=asyncio.FIRST_COMPLETED)
            finally:
                closed_wait.cancel()
            remaining -= time.monotonic() - started

            if pending.future in done:
                return pending.future.result()
            if not done:
                raise BridgeError(f"{label} timed out after {timeout}s.")
            # The connection dropped (most likely a domain reload): loop, wait for Unity,
            # and re-send the same id.
            if remaining <= 0:
                raise BridgeError(f"{label} timed out after {timeout}s.")

    async def _send_pending(self, pending: _Pending, ws) -> None:
        if pending.sent_on is ws:
            return
        try:
            await ws.send_text(json.dumps(pending.message, separators=(",", ":")))
            pending.sent_on = ws
        except Exception as e:
            logger.info("Send to Unity failed (%s); treating the connection as lost.", e)
            self._detach(ws)

    def _not_connected_message(self, waited: Optional[float]) -> str:
        if waited is None:
            return (
                "Unity Editor is not connected. Open this project in Unity (with the Unity MCP Bridge "
                "package installed) and retry."
            )
        if self._unity_reloading:
            return f"Unity is reloading scripts and did not come back within {waited:g}s; retry shortly."
        return f"Lost connection to Unity and it did not reconnect within {waited:g}s; retry shortly."

    # ------------------------------------------------------------------ WebSocket endpoint

    async def _ws_endpoint(self, ws) -> None:
        from starlette.websockets import WebSocketDisconnect

        if ws.headers.get("origin"):
            # Browsers always send Origin; Unity's ClientWebSocket never does. Refusing
            # before accept() blocks web pages (DNS rebinding) from reaching Unity.
            logger.warning("Refused a WebSocket connection with Origin %r.", ws.headers.get("origin"))
            await ws.close(code=1008)
            return

        await ws.accept()
        try:
            hello = await asyncio.wait_for(ws.receive_text(), HELLO_TIMEOUT)
            hello_id, params = self._check_hello(json.loads(hello))
        except _HelloRejected as rejection:
            with contextlib.suppress(Exception):
                await ws.send_text(json.dumps(rejection.response))
                await ws.close(code=1008)
            logger.warning("Rejected Unity hello: %s", rejection.response["error"]["message"])
            return
        except (asyncio.TimeoutError, WebSocketDisconnect, ValueError, TypeError) as e:
            logger.warning("Unity connection closed before a valid hello: %r", e)
            with contextlib.suppress(Exception):
                await ws.close(code=1008)
            return

        await self._attach(ws, hello_id, params)
        try:
            while True:
                text = await ws.receive_text()
                try:
                    message = json.loads(text)
                except ValueError:
                    logger.warning("Ignoring malformed message from Unity.")
                    continue
                await self._on_message(message)
        except WebSocketDisconnect:
            pass
        except Exception as e:
            logger.warning("Unity connection ended: %r", e)
        finally:
            self._detach(ws)

    def _check_hello(self, message: dict) -> dict:
        request_id = message.get("id")

        def reject(code: int, text: str):
            raise _HelloRejected({"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": text}})

        if message.get("method") != "hello" or request_id is None:
            reject(ERR_UNAUTHORIZED, "Expected a hello request first.")
        params = message.get("params") or {}

        if params.get("protocolVersion") != PROTOCOL_VERSION:
            reject(
                ERR_VERSION,
                f"Protocol mismatch: Unity package speaks {params.get('protocolVersion')!r} "
                f"(package {params.get('packageVersion', '?')}), this server speaks {PROTOCOL_VERSION} "
                f"(server {SERVER_VERSION}). Update the Unity package and the Python server to the same release.",
            )

        try:
            expected = security.read_unity_info(self._project_root)["token"]
        except (FileNotFoundError, ValueError) as e:
            reject(ERR_UNAUTHORIZED, f"Cannot verify Unity's token: {e}")
        presented = params.get("token")
        if not isinstance(presented, str) or not hmac.compare_digest(presented.encode(), str(expected).encode()):
            reject(ERR_UNAUTHORIZED, "Invalid token.")

        return request_id, params

    async def _attach(self, ws, hello_id, params: dict) -> None:
        old = self._ws
        if old is not None:
            # One Unity per project (Unity enforces this with its instance lock); a newer
            # connection simply replaces an older one that hasn't noticed it's dead yet.
            self._detach(old)
            with contextlib.suppress(Exception):
                await old.close()

        await ws.send_text(json.dumps({
            "jsonrpc": "2.0",
            "id": hello_id,
            "result": {"protocolVersion": PROTOCOL_VERSION, "serverVersion": SERVER_VERSION, "pid": os.getpid()},
        }))

        is_reconnect = self._ever_connected
        self._ws = ws
        self._ws_closed = asyncio.Event()
        self._unity_reloading = False
        self._ever_connected = True
        self.unity_info = {k: v for k, v in params.items() if k != "token"}
        self._connected.set()
        self._ping_task = asyncio.ensure_future(self._ping_loop(ws))
        logger.info("Unity connected (package %s, Unity %s).", params.get("packageVersion"), params.get("unityVersion"))

        for pending in list(self._pending.values()):
            await self._send_pending(pending, ws)

        if is_reconnect:
            await self._fire(self._reconnect_listeners, "reconnect")

    def _detach(self, ws) -> None:
        if ws is None or self._ws is not ws:
            return
        self._ws = None
        self._connected.clear()
        if self._ws_closed is not None:
            self._ws_closed.set()
        if self._ping_task is not None and self._ping_task is not asyncio.current_task():
            self._ping_task.cancel()
        self._ping_task = None
        for future in self._ping_futures.values():
            if not future.done():
                future.cancel()
        self._ping_futures.clear()
        logger.info("Unity disconnected%s.", " (reloading)" if self._unity_reloading else "")

    async def _on_message(self, message: dict) -> None:
        method = message.get("method")
        if method is not None:
            params = message.get("params") or {}
            if method == "tools/register":
                await self._on_tools_registered(params.get("tools") or [])
            elif method == "unity/reloading":
                self._unity_reloading = True
            return

        request_id = message.get("id")
        ping = self._ping_futures.pop(request_id, None)
        if ping is not None:
            if not ping.done():
                ping.set_result(True)
            return
        pending = self._pending.get(request_id)
        if pending is not None and not pending.future.done():
            pending.future.set_result(message)

    async def _on_tools_registered(self, tools: list[dict]) -> None:
        changed = tools != self._tools
        self._tools = tools
        self._tools_received.set()
        try:
            security.write_json_atomic(self._tool_cache_path(), tools)
        except OSError as e:
            logger.warning("Could not write the tool cache: %s", e)
        logger.info("Unity registered %d tool(s)%s.", len(tools), "" if changed else " (unchanged)")
        if changed:
            await self._fire(self._tools_changed_listeners, "tools changed")

    async def _ping_loop(self, ws) -> None:
        try:
            while self._ws is ws:
                await asyncio.sleep(PING_INTERVAL)
                ping_id = f"ping-{uuid.uuid4()}"
                future = asyncio.get_running_loop().create_future()
                self._ping_futures[ping_id] = future
                try:
                    await ws.send_text(json.dumps({"jsonrpc": "2.0", "id": ping_id, "method": "ping"}))
                    await asyncio.wait_for(future, PING_TIMEOUT)
                except asyncio.CancelledError:
                    raise
                except Exception:
                    logger.warning("Unity did not answer a ping within %ss; dropping the connection.", PING_TIMEOUT)
                    self._detach(ws)
                    with contextlib.suppress(Exception):
                        await ws.close()
                    return
                finally:
                    self._ping_futures.pop(ping_id, None)
        except asyncio.CancelledError:
            pass

    async def _fire(self, listeners: list, what: str) -> None:
        for listener in listeners:
            try:
                await listener()
            except Exception as e:
                logger.warning("%s listener failed: %s", what, e)

    # ------------------------------------------------------------------ plumbing

    def _ensure_events(self) -> None:
        if self._connected is None:
            self._connected = asyncio.Event()
            self._tools_received = asyncio.Event()

    def _resolve_project_root(self) -> Path:
        if self._project_root_override is not None:
            return self._project_root_override
        try:
            return security.get_project_root()
        except FileNotFoundError as e:
            raise BridgeError(str(e)) from e

    async def _start_listener(self) -> None:
        import uvicorn
        from starlette.applications import Starlette
        from starlette.routing import WebSocketRoute

        app = Starlette(routes=[WebSocketRoute("/unity", self._ws_endpoint)])
        config = uvicorn.Config(
            app,
            host="127.0.0.1",
            port=0,
            log_level="warning",
            ws="websockets-sansio",
            ws_max_size=MAX_MESSAGE_BYTES,
            ws_ping_interval=None,  # liveness is our own ping/pong (see _ping_loop)
            lifespan="off",
        )
        self._uvicorn = _quiet_uvicorn_server(config)
        self._serve_task = asyncio.ensure_future(self._uvicorn.serve())
        while not self._uvicorn.started:
            if self._serve_task.done():
                self._serve_task.result()  # re-raise the startup error
                raise BridgeError("The Unity bridge listener stopped during startup.")
            await asyncio.sleep(0.01)
        self._port = self._uvicorn.servers[0].sockets[0].getsockname()[1]
        logger.info("Waiting for Unity on ws://127.0.0.1:%s/unity", self._port)

    def _write_server_file(self) -> None:
        self._server_file = security.servers_dir(self._project_root) / f"{os.getpid()}-{self._port}.json"
        security.write_json_atomic(self._server_file, {
            "pid": os.getpid(),
            "port": self._port,
            "protocolVersion": PROTOCOL_VERSION,
            "serverVersion": SERVER_VERSION,
            "startedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        })
        atexit.register(self._remove_server_file)

    def _remove_server_file(self) -> None:
        if self._server_file is not None:
            with contextlib.suppress(OSError):
                self._server_file.unlink()

    def _unity_info_exists(self) -> bool:
        return self._project_root is not None and security.unity_info_path(self._project_root).exists()

    def _tool_cache_path(self) -> Path:
        return security.mcp_dir(self._project_root) / "tools_cache.json"

    def _read_tool_cache(self) -> Optional[list[dict]]:
        try:
            data = json.loads(self._tool_cache_path().read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        return data if isinstance(data, list) else None


class _HelloRejected(Exception):
    def __init__(self, response: dict):
        super().__init__(response["error"]["message"])
        self.response = response


def _quiet_uvicorn_server(config):
    import uvicorn

    class QuietServer(uvicorn.Server):
        # uvicorn would otherwise take over Ctrl+C/SIGTERM for the whole process; this
        # listener lives inside the MCP server's own lifecycle instead.
        @contextlib.contextmanager
        def capture_signals(self):
            yield

    return QuietServer(config)
