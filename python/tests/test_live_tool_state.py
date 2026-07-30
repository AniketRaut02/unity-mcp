"""
Tests groups.py's _write_live_state: the fix for a real reported gap where a group
activated mid-session via manage_tools didn't show as active in the Unity-side Tool
Groups window, because the window only ever read tool_groups_config.json's
defaultActiveGroups (which seeds a *new* session and is never updated by a live one).
Library/MCP/live_tool_state.json is the missing half: written every time
activate()/deactivate()/reset() actually changes this process's active-group set, read
by the Unity Editor's new MCPLiveStateReader.
"""
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


def main():
    tmp_root = Path(tempfile.mkdtemp())
    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    from unity_mcp_server import groups, security

    state_path = security.get_project_root() / "Library" / "MCP" / "live_tool_state.json"

    # --- 1. Importing/using the module at all writes an initial state file ---
    groups.reset()
    assert state_path.exists(), "live_tool_state.json should exist after reset()"
    data = json.loads(state_path.read_text())
    assert data["activeGroups"] == ["core"], data
    assert isinstance(data["pid"], int)
    print("[PASS] live_tool_state.json exists after reset() with activeGroups == ['core']")

    # --- 2. activate() updates the file with the new real active set ---
    groups.activate("physics")
    groups.activate("audio")
    data = json.loads(state_path.read_text())
    assert set(data["activeGroups"]) == {"core", "physics", "audio"}, data
    print("[PASS] activate() updates live_tool_state.json to reflect the real active set")

    # --- 3. deactivate() removes a group from the file too ---
    groups.deactivate("physics")
    data = json.loads(state_path.read_text())
    assert set(data["activeGroups"]) == {"core", "audio"}, data
    print("[PASS] deactivate() updates live_tool_state.json")

    # --- 4. reset() restores it to just the default-active set ---
    groups.reset()
    data = json.loads(state_path.read_text())
    assert data["activeGroups"] == ["core"], data
    print("[PASS] reset() writes live_tool_state.json back to the default-active set")

    # --- 5. A disabled group is never reported as live-active, even if somehow added ---
    groups._active_groups.add("terrain")  # simulate stale internal state
    config_dir = tmp_root / "Library" / "MCP"
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "tool_groups_config.json").write_text(json.dumps({"disabledGroups": ["terrain"]}))
    groups._last_config_check = 0.0
    groups._config_mtime = None
    groups.activate("terrain")  # no-op from get_active_groups' perspective since disabled
    data = json.loads(state_path.read_text())
    assert "terrain" not in data["activeGroups"], data
    print("[PASS] a disabled group never appears in live_tool_state.json's activeGroups")

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll live-tool-state checks passed.")


if __name__ == "__main__":
    main()
