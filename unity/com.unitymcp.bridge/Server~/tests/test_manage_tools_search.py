"""
Integration tests for manage_tools' new "search" action, the activate/deactivate
"groups" (plural, batch) parameter, and the soft activation-budget guard -- run through
the real server.call_tool()/workflows.py handler against the fake Unity bridge, not a
mock of them. See test_tool_search.py for pure BM25-algorithm unit tests.
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


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    port = 46700
    token = "search-test-token"

    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        from unity_mcp_server import groups, workflows, server as mcp_server_module

        groups.reset()

        # --- 1. search finds a real Unity tool by keyword, without needing its group active ---
        result = await mcp_server_module.call_tool("manage_tools", {"action": "search", "query": "create gameobject"})
        payload = json.loads(result[0].text)
        tool_names = [r.get("tool") for r in payload["results"]]
        assert "create_gameobject" in tool_names, payload
        top = payload["results"][0]
        assert top.get("tool") == "create_gameobject", payload
        assert "activate" in payload["hint"], payload
        print("[PASS] search finds a real tool by keyword and ranks the best match first, with a hint to activate")

        # --- 2. search finds a composite (workflow) tool too, and reports its real group ---
        result2 = await mcp_server_module.call_tool("manage_tools", {"action": "search", "query": "snap to ground"})
        payload2 = json.loads(result2[0].text)
        snap_hit = next((r for r in payload2["results"] if r.get("tool") == "snap_to_ground"), None)
        assert snap_hit is not None, payload2
        assert snap_hit["group"] == "core", snap_hit
        print("[PASS] search also finds composite (Python @workflow) tools, with their real group")

        # --- 3. search reports whether a hit's group is currently active ---
        result3 = await mcp_server_module.call_tool("manage_tools", {"action": "search", "query": "create script"})
        payload3 = json.loads(result3[0].text)
        script_hit = next(r for r in payload3["results"] if r.get("tool") == "create_script")
        assert script_hit["active"] is False, script_hit  # 'scripting' not active by default
        print("[PASS] search reports each hit's real current active state")

        # --- 4. a nonsense query returns no results, cleanly ---
        result4 = await mcp_server_module.call_tool("manage_tools", {"action": "search", "query": "xyzzyplugh"})
        payload4 = json.loads(result4[0].text)
        assert payload4["results"] == [], payload4
        print("[PASS] a query matching nothing returns an empty result list, not an error")

        # --- 5. search requires a query ---
        no_query_result = await mcp_server_module.call_tool("manage_tools", {"action": "search"})
        assert "'query' is required" in no_query_result[0].text, no_query_result[0].text
        print("[PASS] search without a query fails cleanly")

        # --- 6. activate accepts a "groups" array, batching multiple groups in one call ---
        activate_result = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "groups": ["scripting"]})
        activate_payload = json.loads(activate_result[0].text)
        assert set(activate_payload["active"]) == {"core", "scripting"}, activate_payload
        print("[PASS] activate accepts a 'groups' array (single-element case)")

        groups.reset()
        # --- 7. 'group' (singular) still works for backward compatibility ---
        single_result = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "scripting"})
        single_payload = json.loads(single_result[0].text)
        assert set(single_payload["active"]) == {"core", "scripting"}, single_payload
        print("[PASS] the original singular 'group' parameter still works unchanged")

        groups.reset()
        # --- 8. an unknown group in a 'groups' array fails cleanly, naming the bad one(s) ---
        bad_result = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "groups": ["scripting", "not_a_real_group"]})
        assert "Unknown group(s): not_a_real_group" in bad_result[0].text, bad_result[0].text
        print("[PASS] an unknown group inside a 'groups' array is named clearly in the error")

        # --- 9. 'core' still can't be deactivated, even via the plural form ---
        core_result = await mcp_server_module.call_tool("manage_tools", {"action": "deactivate", "groups": ["core"]})
        assert "'core' cannot be deactivated" in core_result[0].text, core_result[0].text
        print("[PASS] 'core' still can't be deactivated via the plural 'groups' form")

        # --- 10. soft budget guard: warns (but still activates) once the estimated cost is exceeded ---
        original_budget = workflows._SOFT_ACTIVE_TOKEN_BUDGET
        workflows._SOFT_ACTIVE_TOKEN_BUDGET = 1  # force the guard to trigger on any real activation
        try:
            budget_result = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "scripting"})
            budget_payload = json.loads(budget_result[0].text)
            assert "warning" in budget_payload, budget_payload
            assert "scripting" in set(budget_payload["active"]), budget_payload  # still actually activated
            print("[PASS] the soft budget guard warns without blocking the activation")
        finally:
            workflows._SOFT_ACTIVE_TOKEN_BUDGET = original_budget

        groups.reset()
        # --- 11. no warning under the (real, default) budget for a small fake corpus ---
        normal_result = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "scripting"})
        normal_payload = json.loads(normal_result[0].text)
        assert "warning" not in normal_payload, normal_payload
        print("[PASS] no spurious warning under the real default budget for a small active set")

        await mcp_server_module.bridge.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll manage_tools search/groups-array/budget-guard checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
