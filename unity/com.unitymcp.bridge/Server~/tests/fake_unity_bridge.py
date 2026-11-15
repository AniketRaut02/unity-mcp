"""
Fake Unity Editor for testing unity_mcp_server without a real Unity install.

v2 (protocol 2): like the real Editor, this fake is the WebSocket CLIENT. It writes
Library/MCP/unity.json (its token), polls Library/MCP/servers/ for Python servers,
connects to each one, says hello, pushes its tool list (tools/register), and then
answers tools/call / tools/batch / ping -- JSON-RPC 2.0, one message per text frame.

It also mirrors the real Editor's reload contract so tests can exercise it:
  - an answer cache keyed by request id (a re-sent id returns the stored answer
    instead of running the tool twice), and
  - simulate_reload(): announce unity/reloading, drop every connection, stay away
    for a while, then reconnect.

State is per-fake-instance (FakeBridgeState), not a module global, so each
run_fake_bridge() call gets an isolated simulated Unity.
"""
import asyncio
import json
import os
import sys
from pathlib import Path

from websockets.asyncio.client import connect as ws_connect

FAKE_TOOLS = [
    {
        "name": "create_gameobject",
        "description": "Creates a new GameObject in the active scene.",
        "latency_tier": "fast",
        "group": "core",
        "schema": {
            "type": "object",
            "properties": {"name": {"type": "string"}, "parentPath": {"type": "string"}},
            "required": ["name"],
        },
    },
    {
        "name": "get_hierarchy",
        "description": "Returns the scene hierarchy.",
        "latency_tier": "fast",
        "group": "core",
        "schema": {"type": "object", "properties": {}, "required": []},
        "read_only": True,
    },
    {
        "name": "add_component",
        "description": "Adds a component to a GameObject.",
        "latency_tier": "fast",
        "group": "core",
        "schema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "create_script",
        "description": "Creates a new C# script.",
        "latency_tier": "slow",
        "group": "scripting",
        "schema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "update_script",
        "description": "Overwrites an existing C# script.",
        "latency_tier": "slow",
        "group": "scripting",
        "schema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "get_compile_status",
        "description": "Returns compile status.",
        "latency_tier": "fast",
        "group": "scripting",
        "schema": {"type": "object", "properties": {}, "required": []},
    },
]


class FakeBridgeState:
    """Simulated Unity-side state for one fake bridge instance."""

    def __init__(self):
        self.created_scripts: set[str] = set()
        self.compile_status_calls = 0
        # get_compile_status reports isCompiling=True for this many calls after a
        # create_script, so tests can verify a caller actually polls in a loop
        # rather than checking once and assuming done.
        self.compile_busy_until_call = 0
        # path -> {"posX": ..., "deleted": bool}, tracks real mutations from
        # set_transform/delete_gameobject so generated-tool tests can verify the
        # chain actually did what it claims, not just that each call returned ok=True.
        self.gameobjects: dict[str, dict] = {}


def _run_tool(tool: str, args: dict, state: FakeBridgeState):
    """Returns (ok, result, error) for a single tool call — shared by 'call' and 'batch' handling."""
    if tool == "create_gameobject":
        parent = args.get("parentPath")
        name = args.get("name", "Unnamed")
        path = f"{parent}/{name}" if parent else name
        state.gameobjects[path] = {"posX": 0, "deleted": False}
        return True, {"path": path}, None

    elif tool == "get_hierarchy":
        return True, {"scene": "FakeScene", "roots": []}, None

    elif tool == "boom":
        return False, None, "simulated failure"

    elif tool == "set_transform":
        path = args.get("path")
        if path not in state.gameobjects or state.gameobjects[path]["deleted"]:
            return False, None, f"Path '{path}' not found."
        if "posX" in args:
            state.gameobjects[path]["posX"] = args["posX"]
        return True, None, None

    elif tool == "delete_gameobject":
        path = args.get("path")
        if not args.get("confirm"):
            return False, None, "Destructive action requires confirm=true."
        if path not in state.gameobjects or state.gameobjects[path]["deleted"]:
            return False, None, f"Path '{path}' not found."
        state.gameobjects[path]["deleted"] = True
        return True, None, None

    elif tool == "create_script":
        path = args["path"]
        if path in state.created_scripts:
            return False, None, f"'{path}' already exists. Use update_script to modify it."
        state.created_scripts.add(path)
        # Simulate two "still compiling" polls before reporting done, so
        # _wait_for_compile's loop is actually exercised, not just its first check.
        state.compile_busy_until_call = state.compile_status_calls + 2
        class_name = path.rsplit("/", 1)[-1].replace(".cs", "")
        return True, {"path": path, "className": class_name}, None

    elif tool == "update_script":
        path = args["path"]
        if path not in state.created_scripts:
            return False, None, f"'{path}' does not exist. Use create_script to create it."
        return True, None, None

    elif tool == "add_component":
        return True, None, None

    elif tool == "get_compile_status":
        state.compile_status_calls += 1
        is_compiling = state.compile_status_calls <= state.compile_busy_until_call
        return True, {"isCompiling": is_compiling, "errorCount": 0, "warningCount": 0, "errors": [], "warnings": []}, None

    else:
        return False, None, f"unknown tool {tool}"


class FakeUnity:
    """
    Duck-types the v1 fake's asyncio.Server surface (serve_forever / close / async with /
    fake_state) so existing tests keep their shape.
    """

    POLL_SECONDS = 0.05

    def __init__(self, project_root: Path, token: str, present_token: str = None, protocol_version: int = 2):
        self.project_root = project_root
        self.token = token
        self.present_token = present_token or token
        self.protocol_version = protocol_version
        self.fake_state = FakeBridgeState()
        self.answer_cache: dict[str, dict] = {}
        self.executed_ids: list[str] = []
        self.hello_errors: list[dict] = []
        # Test hook: when set to a tool name, the next call to it runs but its answer is
        # not sent -- as if a domain reload hit between executing and replying.
        self.drop_reply_for_tool: str = None
        self._connections: dict[str, object] = {}
        self._tasks: dict[str, asyncio.Task] = {}
        self._refused: set[str] = set()
        self._paused = False
        self._closed = False

    @property
    def servers_dir(self) -> Path:
        return self.project_root / "Library" / "MCP" / "servers"

    async def serve_forever(self):
        while not self._closed:
            if not self._paused and self.servers_dir.is_dir():
                for path in sorted(self.servers_dir.glob("*.json")):
                    key = path.name
                    if key in self._refused or key in self._tasks:
                        continue
                    try:
                        info = json.loads(path.read_text(encoding="utf-8"))
                    except (OSError, ValueError):
                        continue
                    self._tasks[key] = asyncio.ensure_future(self._run_connection(key, info["port"]))
            await asyncio.sleep(self.POLL_SECONDS)

    async def _run_connection(self, key: str, port: int):
        try:
            async with ws_connect(f"ws://127.0.0.1:{port}/unity", max_size=None) as ws:
                await ws.send(json.dumps({
                    "jsonrpc": "2.0", "id": "hello-1", "method": "hello",
                    "params": {"token": self.present_token, "protocolVersion": self.protocol_version,
                               "packageVersion": "2.0.0-fake", "unityVersion": "fake-6000.0", "pid": os.getpid(),
                               "projectPath": str(self.project_root)},
                }))
                reply = json.loads(await ws.recv())
                if "error" in reply:
                    self.hello_errors.append(reply["error"])
                    self._refused.add(key)
                    return
                self._connections[key] = ws
                await ws.send(json.dumps({"jsonrpc": "2.0", "method": "tools/register", "params": {"tools": FAKE_TOOLS}}))
                async for raw in ws:
                    reply = self._handle(json.loads(raw))
                    if reply is not None:
                        await ws.send(json.dumps(reply))
        except Exception:
            pass  # refused or dropped: the poll loop reconnects
        finally:
            self._connections.pop(key, None)
            self._tasks.pop(key, None)

    def _handle(self, msg: dict):
        method, request_id, params = msg.get("method"), msg.get("id"), msg.get("params") or {}
        if method == "ping":
            return {"jsonrpc": "2.0", "id": request_id, "result": {}}
        if request_id in self.answer_cache:
            return self.answer_cache[request_id]

        if method == "tools/call":
            self.executed_ids.append(request_id)
            ok, result, error = _run_tool(params["tool"], params.get("args") or {}, self.fake_state)
            reply = {"jsonrpc": "2.0", "id": request_id}
            reply.update({"result": result} if ok else {"error": {"code": -32000, "message": error}})
        elif method == "tools/batch":
            self.executed_ids.append(request_id)
            results = []
            for item in params.get("calls") or []:
                ok, result, error = _run_tool(item["tool"], item.get("args") or {}, self.fake_state)
                results.append({"ok": ok, "result": result, "error": error})
            reply = {"jsonrpc": "2.0", "id": request_id, "result": {"results": results}}
        else:
            return {"jsonrpc": "2.0", "id": request_id, "error": {"code": -32601, "message": f"Unknown method {method}"}}

        self.answer_cache[request_id] = reply
        if method == "tools/call" and params.get("tool") == self.drop_reply_for_tool:
            self.drop_reply_for_tool = None
            asyncio.ensure_future(self.simulate_reload(0.2))
            return None
        return reply

    async def push_tools(self, tools: list):
        for ws in list(self._connections.values()):
            await ws.send(json.dumps({"jsonrpc": "2.0", "method": "tools/register", "params": {"tools": tools}}))

    async def simulate_reload(self, away_seconds: float = 0.3):
        """What a domain reload looks like from Python's side: notice, drop, silence, reconnect."""
        self._paused = True
        for ws in list(self._connections.values()):
            try:
                await ws.send(json.dumps({"jsonrpc": "2.0", "method": "unity/reloading"}))
                await ws.close()
            except Exception:
                pass
        for task in list(self._tasks.values()):
            task.cancel()
        await asyncio.sleep(away_seconds)
        self._paused = False

    def close(self):
        """Like the Editor quitting: drop connections and remove unity.json."""
        self._closed = True
        for task in list(self._tasks.values()):
            task.cancel()
        try:
            (self.project_root / "Library" / "MCP" / "unity.json").unlink()
        except OSError:
            pass

    async def wait_closed(self):
        await asyncio.sleep(0)

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        self.close()


def write_unity_info(project_root: Path, token: str) -> None:
    mcp_dir = project_root / "Library" / "MCP"
    mcp_dir.mkdir(parents=True, exist_ok=True)
    (project_root / "Assets").mkdir(exist_ok=True)
    (project_root / "ProjectSettings").mkdir(exist_ok=True)
    (mcp_dir / "unity.json").write_text(json.dumps({
        "token": token, "pid": os.getpid(), "projectPath": str(project_root),
        "unityVersion": "fake-6000.0", "packageVersion": "2.0.0-fake", "protocolVersion": 2,
    }))


async def run_fake_bridge(project_root: Path, port: int = None, token: str = "fake-token", **kwargs) -> FakeUnity:
    """
    `port` is accepted for compatibility with v1 tests and ignored: in v2 each Python
    server picks its own port and the fake finds it through Library/MCP/servers/.
    """
    write_unity_info(project_root, token)
    return FakeUnity(project_root, token, **kwargs)


if __name__ == "__main__":
    async def _main():
        fake = await run_fake_bridge(Path(sys.argv[1]), token=sys.argv[2] if len(sys.argv) > 2 else "fake-token")
        await fake.serve_forever()

    asyncio.run(_main())
