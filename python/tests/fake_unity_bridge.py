"""
Fake Unity bridge for testing unity_mcp_server without a real Unity Editor.

Speaks the exact same wire protocol the real C# MCPServer speaks: length-prefixed
JSON over TCP on 127.0.0.1, handshake against a token from Library/MCP/session.json
(which also carries the port, per the Phase 2 fix), list_tools, call, and batch.

State is per-fake-server-instance (FakeBridgeState), not a module global, so each
run_fake_bridge() call gets an isolated simulated Unity -- deliberately, since some
Phase 5 tests need to call the same workflow twice against ONE fake server to verify
idempotent behavior (e.g. scaffolding skips already-created files the second time),
while different test files spinning up different fake servers must not see each
other's simulated state.
"""
import asyncio
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from unity_mcp_server import protocol  # noqa: E402

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


async def handle_client(reader, writer, token, state: FakeBridgeState):
    authenticated = False
    while True:
        msg = await protocol.read_message(reader)
        if msg is None:
            break

        if not authenticated:
            if msg.get("type") == "handshake" and msg.get("token") == token:
                authenticated = True
                writer.write(protocol.encode_message({"type": "handshake_ack", "ok": True, "id": msg["id"]}))
            else:
                writer.write(
                    protocol.encode_message(
                        {"type": "handshake_ack", "ok": False, "id": msg.get("id"), "error": "bad token"}
                    )
                )
                await writer.drain()
                break
            await writer.drain()
            continue

        if msg["type"] == "list_tools":
            writer.write(protocol.encode_message({"type": "tools_list", "id": msg["id"], "tools": FAKE_TOOLS}))
        elif msg["type"] == "call":
            ok, result, error = _run_tool(msg["tool"], msg.get("args") or {}, state)
            writer.write(protocol.encode_message({"type": "result", "id": msg["id"], "ok": ok, "result": result, "error": error}))
        elif msg["type"] == "batch":
            calls = msg.get("calls") or []
            results = []
            for item in calls:
                ok, result, error = _run_tool(item["tool"], item.get("args") or {}, state)
                results.append({"ok": ok, "result": result, "error": error})
            writer.write(protocol.encode_message({"type": "batch_result", "id": msg["id"], "ok": True, "results": results}))
        await writer.drain()

    writer.close()


async def run_fake_bridge(project_root: Path, port: int, token: str):
    session_dir = project_root / "Library" / "MCP"
    session_dir.mkdir(parents=True, exist_ok=True)
    (project_root / "Assets").mkdir(exist_ok=True)
    (project_root / "ProjectSettings").mkdir(exist_ok=True)

    session = {
        "token": token,
        "port": port,
        "pid": os.getpid(),
        "projectPath": str(project_root),
        "unityVersion": "fake-2022.3.0f1",
        "startedAt": "2026-07-07T00:00:00Z",
    }
    (session_dir / "session.json").write_text(json.dumps(session, indent=2))

    state = FakeBridgeState()
    server = await asyncio.start_server(lambda r, w: handle_client(r, w, token, state), "127.0.0.1", port)
    server.fake_state = state  # exposed for tests that need to verify real side effects, not just ok=True
    return server


if __name__ == "__main__":
    async def _main():
        root = Path(sys.argv[1])
        port = int(sys.argv[2])
        token = sys.argv[3]
        srv = await run_fake_bridge(root, port, token)
        async with srv:
            await srv.serve_forever()

    asyncio.run(_main())
