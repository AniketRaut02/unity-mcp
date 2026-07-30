"""
Verifies phase 4 of docs/tool-scaling-strategy.md: the MCP spec's readOnlyHint/
destructiveHint/openWorldHint annotations actually reach a real list_tools() response,
not just that the underlying [MCPTool]/@workflow data exists. Exercises the real
server.list_tools() handler against the fake Unity bridge -- the same code path a real
MCP client hits -- rather than unit-testing the annotation-construction logic in
isolation, since the thing worth verifying is the wiring between them.
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
    port = 46020
    token = "test-token-annotations"

    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        from unity_mcp_server import server as mcp_server_module
        from unity_mcp_server import groups as tool_groups

        tools = {t.name: t for t in await mcp_server_module.list_tools()}

        get_hierarchy = tools["get_hierarchy"]
        assert get_hierarchy.annotations is not None
        assert get_hierarchy.annotations.readOnlyHint is True, get_hierarchy.annotations
        assert get_hierarchy.annotations.destructiveHint is False
        assert get_hierarchy.annotations.openWorldHint is False
        print("[PASS] get_hierarchy (atomic, read_only=True in the fake fixture) -> readOnlyHint=True")

        create_gameobject = tools["create_gameobject"]
        assert create_gameobject.annotations.readOnlyHint is False
        assert create_gameobject.annotations.destructiveHint is False
        print("[PASS] create_gameobject (atomic, no hints set) -> both hints False, not True by accident")

        manage_tools = tools["manage_tools"]
        assert manage_tools.annotations.readOnlyHint is False
        assert manage_tools.annotations.destructiveHint is False
        print("[PASS] manage_tools (composite, mutates active-group state) -> readOnlyHint=False")

        tool_groups.activate("assets")
        tools_with_assets = {t.name: t for t in await mcp_server_module.list_tools()}
        replace_prefab = tools_with_assets["replace_prefab_instances"]
        assert replace_prefab.annotations.destructiveHint is True, replace_prefab.annotations
        print("[PASS] replace_prefab_instances (composite, destructive=True) -> destructiveHint=True")

        await mcp_server_module.bridge.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll tool-annotation checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
