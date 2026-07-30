"""
Tests for the Unity-side "Tool Groups" window's disabled-group / default-active-group
mechanism: groups.py reads Library/MCP/tool_groups_config.json (written by Unity, never
by any MCP tool), a disabled group is excluded from manage_tools list_groups and
refused (as "Unknown group"/"Unknown tool", not a distinct "disabled" error) by
activate/call_tool, and defaultActiveGroups seeds a fresh/reset session.
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


def _write_config(project_root: Path, disabled=None, default_active=None) -> None:
    config_dir = project_root / "Library" / "MCP"
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "tool_groups_config.json").write_text(json.dumps({
        "disabledGroups": disabled or [],
        "defaultActiveGroups": default_active or [],
    }))


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    port = 46600
    token = "group-disable-test-token"
    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        from unity_mcp_server import groups, server as mcp_server_module
        from unity_mcp_server.bridge_client import BridgeError

        # Force an immediate re-read regardless of the mtime-based throttle, since these
        # tests write the config file repeatedly within the same second.
        def force_recheck():
            groups._last_config_check = 0.0
            groups._config_mtime = None

        # --- 1. No config file at all: no disabled groups, groups.reset() seeds {"core"} ---
        force_recheck()
        groups.reset()
        assert groups.get_disabled_groups() == set(), groups.get_disabled_groups()
        assert groups.get_active_groups() == {"core"}, groups.get_active_groups()
        print("[PASS] with no tool_groups_config.json, no groups are disabled and reset() seeds {'core'}")

        # --- 2. disabledGroups excludes a group from manage_tools list_groups ---
        _write_config(tmp_root, disabled=["terrain"])
        force_recheck()
        list_groups_result = await mcp_server_module.call_tool("manage_tools", {"action": "list_groups"})
        payload = json.loads(list_groups_result[0].text)
        group_names = {g["group"] for g in payload["groups"]}
        assert "terrain" not in group_names, group_names
        assert "weapons" in group_names, "a non-disabled group must still be listed"
        print("[PASS] a disabled group is excluded entirely from manage_tools list_groups")

        # --- 3. activate() on a disabled group is refused as 'Unknown group', not 'disabled' ---
        force_recheck()
        activate_result = await mcp_server_module.call_tool("manage_tools", {"action": "activate", "group": "terrain"})
        text = activate_result[0].text
        assert "Unknown group" in text, text
        assert "disabled" not in text.lower(), f"error text must not reveal the disabled concept: {text}"
        assert "terrain" not in text.split("Valid groups:")[-1], "the disabled group must not appear in the valid-groups hint either"
        print("[PASS] activate() on a disabled group fails as 'Unknown group', with no trace of the disabled concept or name in the hint")

        # --- 4. Direct call_tool of a composite tool whose group is disabled -> 'Unknown tool' ---
        force_recheck()
        scatter_result = await mcp_server_module.call_tool("scatter_props", {"prefabPath": "Props/Rock.prefab"})
        scatter_text = scatter_result[0].text
        assert "Unknown tool" in scatter_text, scatter_text
        print("[PASS] calling a composite tool directly by name in a disabled group fails as 'Unknown tool'")

        # --- 5. A non-disabled group's composite tool call still reaches the real handler
        #        (whatever it returns from there -- the point is it got past the
        #        disabled-group gate, not that the fake bridge fully implements it) ---
        force_recheck()
        checkpoint_result = await mcp_server_module.call_tool("create_checkpoint", {"x": 0, "y": 0, "z": 0})
        checkpoint_text = checkpoint_result[0].text
        assert "Unknown tool" not in checkpoint_text, f"a non-disabled group's tool must not be blocked by the disabled-group gate: {checkpoint_text}"
        print("[PASS] a non-disabled group's composite tool is not blocked by the disabled-group gate")

        # --- 6. "core" can never be disabled, even if the config file lists it ---
        _write_config(tmp_root, disabled=["core", "weapons"])
        force_recheck()
        groups.reset()
        assert not groups.is_disabled("core"), "core must never be disabled regardless of config content"
        assert groups.is_disabled("weapons")
        assert groups.is_active("core"), "core must remain active even with a hostile config file"
        print("[PASS] 'core' can never be disabled even if the config file lists it")

        # --- 7. defaultActiveGroups seeds a fresh reset(), minus any disabled groups ---
        _write_config(tmp_root, disabled=["weapons"], default_active=["weapons", "audio", "lighting"])
        force_recheck()
        groups.reset()
        assert groups.get_active_groups() == {"core", "audio", "lighting"}, groups.get_active_groups()
        print("[PASS] defaultActiveGroups seeds reset()'s active set, with disabled groups excluded even if listed there too")

        # --- 8. Clearing the config file (deleting it) falls back to {'core'} again ---
        (tmp_root / "Library" / "MCP" / "tool_groups_config.json").unlink()
        force_recheck()
        groups.reset()
        assert groups.get_active_groups() == {"core"}, groups.get_active_groups()
        assert groups.get_disabled_groups() == set()
        print("[PASS] deleting tool_groups_config.json cleanly falls back to defaults")

        await mcp_server_module.bridge.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll tool-group disabling checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
