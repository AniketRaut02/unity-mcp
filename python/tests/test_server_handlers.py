"""
Exercises the actual handler functions registered in server.py (list_tools, call_tool)
against the fake Unity bridge -- this is the code path a real MCP client (Claude Code,
Codex) triggers, minus the stdio transport plumbing itself.
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
    port = 46000
    token = "test-token-abcde"

    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        # Import after env vars are set, since UnityBridgeClient() at module scope
        # reads them at construction time.
        from unity_mcp_server import server as mcp_server_module

        tools = await mcp_server_module.list_tools()
        names = {t.name for t in tools}
        # Only 'core'-group tools are visible by default now that group filtering
        # exists — create_script/get_compile_status (scripting) and the three BT
        # workflows (behavior_tree) are registered but hidden until activated.
        # See test_tool_groups.py for the dedicated filtering tests.
        expected = {
            "create_gameobject",
            "get_hierarchy",
            "add_component",
            "batch_execute",
            "manage_tools",
            "align_gameobjects",
            "snap_to_ground",
        }
        assert names == expected, names
        print("[PASS] server.list_tools() -> (core group only, by default)", names)

        content = await mcp_server_module.call_tool("create_gameobject", {"name": "Enemy"})
        assert len(content) == 1
        assert '"path": "Enemy"' in content[0].text
        print("[PASS] server.call_tool('create_gameobject') ->", content[0].text)

        error_content = await mcp_server_module.call_tool("boom", {})
        assert "Error calling 'boom'" in error_content[0].text
        print("[PASS] server.call_tool('boom') surfaced error text:", error_content[0].text)

        await mcp_server_module.bridge.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll server-handler checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
