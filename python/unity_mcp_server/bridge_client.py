"""
Persistent async TCP client for the Unity Editor MCP bridge.

Design notes:
- One long-lived connection, not one-connection-per-call — a fresh TCP + handshake
  round trip per tool call would dominate latency on top of the main-thread hop
  Unity already needs.
- Reconnects lazily on the next call after a drop (e.g. Unity domain reload from
  a script compile). Both the port AND the token are re-read from session.json on
  every (re)connect -- not just the token -- because Unity may bind a different
  port than last time if its previous one was still in use (see security.py for why
  this matters: a stale/foreign listener on the "usual" port is exactly the failure
  mode this is designed to avoid).
- Fires registered "reconnect listeners" whenever a connection is re-established
  after having been lost (not on the very first connect). This is what lets
  server.py tell an already-connected MCP client "your tool list may be stale,
  refetch it" via a tools/list_changed notification -- otherwise a client that
  was mid-session when Unity dropped (any domain reload, not just from the Tool
  Builder) stays stuck showing whatever degraded tool list it had at the moment
  of disconnect (typically just the Python-side workflow tools, since Unity's own
  tools vanish from list_tools() while the bridge is unreachable) even after
  Unity comes back and the full tool set is available again.
- Requests are correlated by UUID so concurrent calls from the MCP server don't
  get their responses crossed, even though Unity processes them in its own
  (possibly reordered by priority) queue.
"""
import asyncio
import logging
import uuid
from pathlib import Path
from typing import Any, Awaitable, Callable, Optional

from . import protocol, security

logger = logging.getLogger("unity_mcp.bridge")


class BridgeError(Exception):
    """Raised for any failure talking to the Unity bridge: connection, auth, or tool-level errors."""


class UnityBridgeClient:
    def __init__(
        self,
        project_root: Optional[Path] = None,
        host: str = "127.0.0.1",
    ):
        self._project_root = project_root or security.get_project_root()
        self._host = host
        self._port: Optional[int] = None  # discovered fresh from session.json on each connect()

        self._reader: Optional[asyncio.StreamReader] = None
        self._writer: Optional[asyncio.StreamWriter] = None
        self._connect_lock = asyncio.Lock()
        self._write_lock = asyncio.Lock()
        self._pending: dict[str, "asyncio.Future"] = {}
        self._reader_task: Optional[asyncio.Task] = None
        self._connected = asyncio.Event()

        self._ever_connected = False
        self._reconnect_listeners: list[Callable[[], Awaitable[None]]] = []

    def add_reconnect_listener(self, callback: Callable[[], Awaitable[None]]) -> None:
        """
        Registers an async callback to run every time a connection is re-established
        after having been lost -- not on the very first connect of this process (there's
        nothing to "refresh" for a client that hasn't seen a tool list yet).
        """
        self._reconnect_listeners.append(callback)

    @property
    def is_connected(self) -> bool:
        return self._connected.is_set()

    @property
    def port(self) -> Optional[int]:
        """The port from the most recent successful connect — useful for diagnostics."""
        return self._port

    async def connect(self) -> None:
        async with self._connect_lock:
            if self._connected.is_set():
                return

            is_reconnect = self._ever_connected

            try:
                session = security.read_session(self._project_root)
            except (FileNotFoundError, ValueError) as e:
                # Same reasoning as the socket-connect wrapping below: a session.json
                # that's missing or briefly inconsistent (e.g. mid-restart, between the
                # old listener closing and the new one finishing its write) needs to
                # surface as BridgeError like every other connection failure here, not
                # escape as a raw exception type no caller is watching for.
                raise BridgeError(f"Could not read Unity bridge session info: {e}") from e

            token = session["token"]
            self._port = session["port"]

            logger.info("Connecting to Unity bridge at %s:%s (from session.json)", self._host, self._port)
            try:
                reader, writer = await asyncio.open_connection(self._host, self._port)
            except OSError as e:
                # Anything from the raw socket layer (connection refused because nothing's
                # listening on this port yet, host unreachable, etc.) needs to become a
                # BridgeError like every other failure mode here -- an uncaught OSError
                # escapes past every caller's `except BridgeError`, which previously meant
                # a single tool call could crash the entire MCP request instead of
                # degrading gracefully (e.g. list_tools() falling back to just the
                # Python-side workflow tools while Unity is unreachable, as it's designed
                # to do -- but only if this raises the exception type it's actually
                # written to catch).
                raise BridgeError(f"Could not connect to Unity bridge at {self._host}:{self._port}: {e}") from e
            self._reader, self._writer = reader, writer

            handshake_id = str(uuid.uuid4())
            await self._send_raw({"type": "handshake", "id": handshake_id, "token": token})

            ack = await protocol.read_message(reader)
            if ack is None:
                self._reset_connection_state()
                raise BridgeError("Connection closed by Unity during handshake.")
            if not ack.get("ok"):
                self._reset_connection_state()
                raise BridgeError(
                    f"Unity rejected handshake on port {self._port}: {ack.get('error', 'unknown error')}. "
                    "This session.json may be stale — if Unity just restarted, retrying should pick up the fresh one."
                )

            self._reader_task = asyncio.create_task(self._read_loop())
            self._connected.set()
            self._ever_connected = True
            logger.info("Handshake OK — connected to Unity bridge on port %s.", self._port)

            if is_reconnect:
                logger.info("Reconnected after a drop — notifying %d listener(s) to refresh.", len(self._reconnect_listeners))
                for listener in self._reconnect_listeners:
                    try:
                        await listener()
                    except Exception as e:
                        # A failed notification should never take down the connection
                        # that just succeeded -- log and move on.
                        logger.warning("Reconnect listener failed: %s", e)

    async def _ensure_connected(self) -> None:
        if not self._connected.is_set():
            await self.connect()

    async def _send_raw(self, message: dict) -> None:
        async with self._write_lock:
            self._writer.write(protocol.encode_message(message))
            await self._writer.drain()

    async def _read_loop(self) -> None:
        try:
            while True:
                msg = await protocol.read_message(self._reader)
                if msg is None:
                    logger.warning("Unity bridge closed the connection.")
                    break
                request_id = msg.get("id")
                future = self._pending.pop(request_id, None)
                if future is not None and not future.done():
                    future.set_result(msg)
        except Exception as e:
            logger.warning("Bridge read loop terminated: %s", e)
        finally:
            self._fail_all_pending(BridgeError("Connection to Unity was lost."))
            self._reset_connection_state()

    def _fail_all_pending(self, error: Exception) -> None:
        for future in self._pending.values():
            if not future.done():
                future.set_exception(error)
        self._pending.clear()

    def _reset_connection_state(self) -> None:
        self._connected.clear()
        self._reader = None
        self._writer = None

    async def call(self, tool: str, args: dict, timeout: float = 30.0) -> Any:
        await self._ensure_connected()

        request_id = str(uuid.uuid4())
        future: asyncio.Future = asyncio.get_event_loop().create_future()
        self._pending[request_id] = future

        try:
            await self._send_raw({"type": "call", "id": request_id, "tool": tool, "args": args})
        except Exception as e:
            self._pending.pop(request_id, None)
            raise BridgeError(f"Failed to send call for '{tool}': {e}") from e

        try:
            response = await asyncio.wait_for(future, timeout=timeout)
        except asyncio.TimeoutError:
            self._pending.pop(request_id, None)
            raise BridgeError(f"Tool '{tool}' timed out after {timeout}s.")

        if not response.get("ok", False):
            raise BridgeError(response.get("error") or f"Unity returned an unspecified error for '{tool}'.")

        return response.get("result")

    async def list_tools(self, timeout: float = 10.0) -> list[dict]:
        await self._ensure_connected()

        request_id = str(uuid.uuid4())
        future: asyncio.Future = asyncio.get_event_loop().create_future()
        self._pending[request_id] = future

        await self._send_raw({"type": "list_tools", "id": request_id})

        try:
            response = await asyncio.wait_for(future, timeout=timeout)
        except asyncio.TimeoutError:
            self._pending.pop(request_id, None)
            raise BridgeError(f"list_tools timed out after {timeout}s.")

        return response.get("tools", [])

    async def batch(self, calls: list[dict], timeout: float = 60.0) -> list[dict]:
        """
        Sends every {tool, args} in `calls` as ONE wire message and gets back ONE
        response with all results in order -- the actual latency win over looping
        `call()` N times, which would still pay N separate socket round trips even
        over a persistent connection. Longer default timeout than a single call since
        it's covering up to MaxBatchSize sub-calls worth of Unity-side work.
        """
        await self._ensure_connected()

        request_id = str(uuid.uuid4())
        future: asyncio.Future = asyncio.get_event_loop().create_future()
        self._pending[request_id] = future

        try:
            await self._send_raw({"type": "batch", "id": request_id, "calls": calls})
        except Exception as e:
            self._pending.pop(request_id, None)
            raise BridgeError(f"Failed to send batch of {len(calls)} calls: {e}") from e

        try:
            response = await asyncio.wait_for(future, timeout=timeout)
        except asyncio.TimeoutError:
            self._pending.pop(request_id, None)
            raise BridgeError(f"Batch of {len(calls)} calls timed out after {timeout}s.")

        if not response.get("ok", False):
            # A batch-level rejection (empty batch, too large, rate limited) rather
            # than a per-item failure -- per-item failures still come back ok=True
            # with individual {ok: false, error: ...} entries in "results".
            raise BridgeError(response.get("error") or "Unity rejected the batch.")

        return response.get("results", [])

    async def close(self) -> None:
        if self._reader_task:
            self._reader_task.cancel()
        if self._writer:
            self._writer.close()
        self._reset_connection_state()
