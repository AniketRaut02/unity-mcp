"""
manage_tools describe/call: use a hidden tool without activating its group.

  - describe returns a hidden tool's full schema and reports its group as inactive.
  - call runs a hidden Unity tool and a hidden composite, and the group stays inactive.
  - A disabled group's composite is "Unknown tool" to both describe and call.
  - Read-Only Mode refuses a non-read-only composite through call, like a direct call.
  - Only activate/deactivate/reset send tools/list_changed (describe/call/search don't).
"""
import asyncio
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import run_fake_bridge  # noqa: E402


def write_config(root: Path, **fields) -> None:
    (root / "Library" / "MCP").mkdir(parents=True, exist_ok=True)
    (root / "Library" / "MCP" / "tool_groups_config.json").write_text(json.dumps(fields))


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)
    fake = await run_fake_bridge(tmp_root, token="describe-token")
    serve_task = asyncio.get_event_loop().create_task(fake.serve_forever())

    from unity_mcp_server import groups, workflows
    from unity_mcp_server import server as mcp_server_module
    from unity_mcp_server.bridge_client import BridgeError

    def refresh_config():
        groups._last_config_check = 0.0
        groups._config_mtime = None

    bridge = mcp_server_module.bridge
    manage = workflows.get_workflow("manage_tools").handler
    composite = next(wf for wf in workflows.all_workflows() if wf.group == "levelgen" and not wf.read_only)

    try:
        groups.reset()
        assert not groups.is_active("scripting")

        described = await manage(bridge, {"action": "describe", "tools": ["create_script", "nope"]})
        create_script, unknown = described["tools"]
        assert create_script["group"] == "scripting" and create_script["active"] is False, create_script
        assert create_script["inputSchema"]["type"] == "object", create_script
        assert "Unknown tool" in unknown["error"], unknown
        print("[PASS] describe returns a hidden tool's schema and flags unknown names")

        result = await manage(bridge, {"action": "call", "tool": "get_compile_status", "arguments": {}})
        assert result["isCompiling"] in (True, False), result
        assert not groups.is_active("scripting"), "call must not activate the group"
        print("[PASS] call runs a hidden Unity tool without activating its group")

        described = await manage(bridge, {"action": "describe", "tool": composite.name})
        assert described["tools"][0]["group"] == "levelgen", described
        print(f"[PASS] describe covers composite tools too ({composite.name})")

        write_config(tmp_root, disabledGroups=["levelgen"])
        refresh_config()
        described = await manage(bridge, {"action": "describe", "tool": composite.name})
        assert "Unknown tool" in described["tools"][0]["error"], described
        try:
            await manage(bridge, {"action": "call", "tool": composite.name, "arguments": {}})
            raise AssertionError("expected Unknown tool for a disabled group's composite")
        except BridgeError as e:
            assert "Unknown tool" in str(e), e
        print("[PASS] a disabled group's composite is unknown to both describe and call")

        write_config(tmp_root, readOnlyMode=True)
        refresh_config()
        try:
            await manage(bridge, {"action": "call", "tool": composite.name, "arguments": {}})
            raise AssertionError("expected Read-Only Mode refusal")
        except BridgeError as e:
            assert "Read-Only Mode" in str(e), e
        print("[PASS] Read-Only Mode refuses a non-read-only composite through call")
        write_config(tmp_root)
        refresh_config()

        # Notifications: only group changes should make clients refetch the tool list.
        import mcp.types as types
        from mcp.shared.memory import create_connected_server_and_client_session

        seen = []

        async def capture(message):
            if isinstance(message, types.ServerNotification) and isinstance(message.root, types.ToolListChangedNotification):
                seen.append(message.root)

        async with create_connected_server_and_client_session(mcp_server_module.server, message_handler=capture) as client:
            await client.list_tools()
            await asyncio.sleep(0.2)
            before = len(seen)
            await client.call_tool("manage_tools", {"action": "describe", "tool": "create_script"})
            await client.call_tool("manage_tools", {"action": "call", "tool": "get_compile_status", "arguments": {}})
            await asyncio.sleep(0.2)
            assert len(seen) == before, "describe/call must not trigger tools/list_changed"
            await client.call_tool("manage_tools", {"action": "activate", "group": "scripting"})
            await asyncio.sleep(0.2)
            assert len(seen) > before, "activate must trigger tools/list_changed"
        print("[PASS] only activate/deactivate/reset trigger tools/list_changed")
    finally:
        await bridge.close()
        fake.close()
        serve_task.cancel()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll describe/call checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
