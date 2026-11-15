"""
Project discovery and the on-disk rendezvous files shared with the Unity Editor.

v2 (2.0.0) connection model: this Python process is the long-lived server and the
Unity Editor connects OUT to it over a loopback WebSocket. Two kinds of files under
<project>/Library/MCP/ make that work:

  servers/<pid>-<port>.json  written by each Python server on startup (one per MCP
                             client session) so Unity knows which ports to connect
                             to; deleted on clean exit, and by Unity if the PID is dead.
  unity.json                 written by the Unity Editor while it holds the project's
                             instance lock: the session token Unity will present in its
                             hello (new per Editor launch, unchanged across domain
                             reloads), plus version info. Deleted when the Editor quits,
                             which is how a server tells "Unity is reloading" (file
                             still there) apart from "Unity is closed" (file gone).

Library/ is per-machine and never committed, so neither file leaves the machine.
"""
import json
import os
from pathlib import Path


def find_project_root(start: str = ".") -> Path:
    """
    Walks upward from `start` looking for a Unity project, identified by the
    presence of both Assets/ and ProjectSettings/ directories.
    """
    current = Path(start).resolve()
    for candidate in [current, *current.parents]:
        if (candidate / "Assets").is_dir() and (candidate / "ProjectSettings").is_dir():
            return candidate
    raise FileNotFoundError(
        f"Could not locate a Unity project root (Assets/ + ProjectSettings/) starting from '{start}'. "
        "Set the UNITY_MCP_PROJECT_ROOT environment variable to point at the project explicitly."
    )


def get_project_root() -> Path:
    env_root = os.environ.get("UNITY_MCP_PROJECT_ROOT")
    if env_root:
        root = Path(env_root).resolve()
        if not root.is_dir():
            raise FileNotFoundError(f"UNITY_MCP_PROJECT_ROOT is set to '{root}' but that path does not exist.")
        return root
    return find_project_root()


def mcp_dir(project_root: Path) -> Path:
    return project_root / "Library" / "MCP"


def servers_dir(project_root: Path) -> Path:
    return mcp_dir(project_root) / "servers"


def unity_info_path(project_root: Path) -> Path:
    return mcp_dir(project_root) / "unity.json"


def read_unity_info(project_root: Path) -> dict:
    """
    Returns unity.json's contents ({"token", "pid", "protocolVersion", ...}). Raises
    FileNotFoundError when the Editor isn't running for this project (it deletes the
    file on quit), ValueError when the file is unreadable or has no token.
    """
    path = unity_info_path(project_root)
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise
    except (OSError, json.JSONDecodeError) as e:
        raise ValueError(f"{path} is not readable JSON: {e}") from e
    if not isinstance(data, dict) or not data.get("token"):
        raise ValueError(f"{path} has no token.")
    return data


def write_json_atomic(path: Path, data) -> None:
    """Writes via a temp file + os.replace so a concurrent reader never sees half a file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    tmp.write_text(json.dumps(data, separators=(",", ":")), encoding="utf-8")
    os.replace(tmp, path)
