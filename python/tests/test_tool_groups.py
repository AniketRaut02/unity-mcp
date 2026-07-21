"""
Tests for the tool-group visibility mechanism: only 'core' is active by default,
manage_tools can list/activate/deactivate/reset groups, 'core' can't be
deactivated, and list_tools()'s actual visible set changes accordingly — run
through the real server.list_tools()/call_tool() handlers, not a mock of them.
"""
import asyncio
import os
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import run_fake_bridge  # noqa: E402


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    port = 46500
    token = "groups-test-token"

    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        from unity_mcp_server import groups, server as mcp_server_module

        # Every module-level active-group state starts fresh in this new process,
        # but reset explicitly anyway so this test doesn't depend on import order.
        groups.reset()

        # --- 1. Default visibility: only 'core' group tools ---
        tools = await mcp_server_module.list_tools()
        names = {t.name for t in tools}
        expected_default = {"create_gameobject", "get_hierarchy", "add_component", "batch_execute", "manage_tools"}
        assert names == expected_default, names
        print("[PASS] by default, only 'core'-group tools are visible:", names)

        # --- 2. list_groups reports every group, correct active state, correct membership ---
        list_groups_content = await mcp_server_module.call_tool("manage_tools", {"action": "list_groups"})
        import json
        payload = json.loads(list_groups_content[0].text)
        groups_by_name = {g["group"]: g for g in payload["groups"]}

        assert set(groups_by_name.keys()) == {"core", "scripting", "physics", "assets", "ui", "behavior_tree"}, groups_by_name.keys()
        assert groups_by_name["core"]["active"] is True
        assert groups_by_name["scripting"]["active"] is False
        assert groups_by_name["behavior_tree"]["active"] is False
        print("[PASS] manage_tools list_groups reports all 6 groups with correct active state")

        assert set(groups_by_name["scripting"]["tools"]) == {"create_script", "update_script", "get_compile_status"}, groups_by_name["scripting"]
        assert set(groups_by_name["behavior_tree"]["tools"]) == {
            "scaffold_behavior_tree_framework", "create_behavior_tree", "add_behavior_tree_node",
        }, groups_by_name["behavior_tree"]
        assert "batch_execute" in groups_by_name["core"]["tools"] and "manage_tools" in groups_by_name["core"]["tools"]
        print("[PASS] list_groups reports correct tool membership per group")

        # --- 3. Activating a group makes its tools visible in list_tools() ---
        activate_content = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "scripting"})
        activate_result = json.loads(activate_content[0].text)
        assert "scripting" in activate_result["active"], activate_result
        print("[PASS] activate returns the updated active-group list:", activate_result["active"])

        tools_after_activate = await mcp_server_module.list_tools()
        names_after_activate = {t.name for t in tools_after_activate}
        assert names_after_activate == expected_default | {"create_script", "update_script", "get_compile_status"}, names_after_activate
        print("[PASS] after activating 'scripting', its 3 tools are now visible in list_tools()")

        # --- 4. 'core' cannot be deactivated ---
        deactivate_core_content = await mcp_server_module.call_tool("manage_tools", {"action": "deactivate", "group": "core"})
        assert "cannot be deactivated" in deactivate_core_content[0].text, deactivate_core_content[0].text
        print("[PASS] deactivating 'core' is rejected with a clear message:", deactivate_core_content[0].text)

        # Core tools should still all be visible after the rejected deactivation attempt.
        tools_after_rejected_deactivate = await mcp_server_module.list_tools()
        names_after_rejected = {t.name for t in tools_after_rejected_deactivate}
        assert expected_default.issubset(names_after_rejected), names_after_rejected
        print("[PASS] core tools remain visible after the rejected deactivate attempt")

        # --- 5. Unknown group name is a clean error, not a crash ---
        bad_group_content = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "not_a_real_group"})
        assert "Unknown group" in bad_group_content[0].text, bad_group_content[0].text
        print("[PASS] activating an unknown group name fails cleanly:", bad_group_content[0].text)

        # --- 6. Deactivating a non-core group actually hides its tools again ---
        await mcp_server_module.call_tool("manage_tools", {"action": "deactivate", "group": "scripting"})
        tools_after_deactivate = await mcp_server_module.list_tools()
        names_after_deactivate = {t.name for t in tools_after_deactivate}
        assert names_after_deactivate == expected_default, names_after_deactivate
        print("[PASS] deactivating 'scripting' hides its tools again:", names_after_deactivate)

        # --- 7. reset restores the default (only 'core' active), even after activating multiple groups ---
        await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "physics"})
        await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "ui"})
        reset_content = await mcp_server_module.call_tool("manage_tools", {"action": "reset"})
        reset_result = json.loads(reset_content[0].text)
        assert reset_result["active"] == ["core"], reset_result
        tools_after_reset = await mcp_server_module.list_tools()
        names_after_reset = {t.name for t in tools_after_reset}
        assert names_after_reset == expected_default, names_after_reset
        print("[PASS] reset restores only 'core' active, even after multiple groups were activated")

        await mcp_server_module.bridge.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll tool-group filtering checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
