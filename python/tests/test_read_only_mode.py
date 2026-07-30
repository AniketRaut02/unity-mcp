"""
Tests phase 5 of docs/tool-scaling-strategy.md: Read-Only Mode, a Tool Groups window
toggle stored in the same Library/MCP/tool_groups_config.json disabled-groups already
uses. When enabled, any tool whose read_only flag isn't literally True is refused --
this only tests the Python/composite-tool half (server.py's call_tool); the C# half
(MCPToolRegistry.Invoke, for atomic tools) has no Python-visible surface to test here
and was verified by hand against the same config file format disabled-groups already
proved works end-to-end (see test_tool_group_disabling.py).
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


def _write_config(project_root: Path, read_only_mode: bool) -> None:
    config_dir = project_root / "Library" / "MCP"
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "tool_groups_config.json").write_text(json.dumps({
        "disabledGroups": [],
        "defaultActiveGroups": [],
        "readOnlyMode": read_only_mode,
    }))


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    port = 46700
    token = "read-only-mode-test-token"
    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        from unity_mcp_server import groups, server as mcp_server_module

        def force_recheck():
            groups._last_config_check = 0.0
            groups._config_mtime = None

        # --- 1. No config file: read-only mode defaults to off ---
        force_recheck()
        assert groups.is_read_only_mode() is False
        print("[PASS] with no tool_groups_config.json, read-only mode defaults to off")

        # --- 2. A mutating composite tool call succeeds when read-only mode is off ---
        force_recheck()
        result = await mcp_server_module.call_tool("create_checkpoint", {"x": 0, "y": 0, "z": 0})
        assert "not read-only" not in result[0].text, result[0].text
        print("[PASS] a mutating composite tool works normally with read-only mode off")

        # --- 3. Enabling read-only mode refuses a non-read-only composite tool by name,
        #        with an explicit message (not disguised as 'unknown tool' -- unlike a
        #        disabled group, this isn't about hiding the tool's existence) ---
        _write_config(tmp_root, read_only_mode=True)
        force_recheck()
        assert groups.is_read_only_mode() is True
        blocked_result = await mcp_server_module.call_tool("create_checkpoint", {"x": 0, "y": 0, "z": 0})
        blocked_text = blocked_result[0].text
        assert "not read-only" in blocked_text, blocked_text
        assert "Read-Only Mode" in blocked_text, blocked_text
        print("[PASS] a non-read-only composite tool is refused by name with an explicit Read-Only Mode message")

        # --- 4. Turning it back off restores normal behavior ---
        _write_config(tmp_root, read_only_mode=False)
        force_recheck()
        assert groups.is_read_only_mode() is False
        restored_result = await mcp_server_module.call_tool("create_checkpoint", {"x": 0, "y": 0, "z": 0})
        assert "not read-only" not in restored_result[0].text, restored_result[0].text
        print("[PASS] disabling read-only mode restores normal tool-call behavior")

        await mcp_server_module.bridge.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll Read-Only Mode checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
