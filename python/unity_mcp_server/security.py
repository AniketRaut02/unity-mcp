"""
Session discovery: reads the single session.json file Unity writes, which contains
BOTH the port the bridge actually bound and the token to authenticate with.

Phase 2 change and why: Phase 1 assumed a fixed global port (45678) with a separate
token file. If a second Unity process (another project, or a stale orphaned instance)
grabbed that port first, this project's token would never match what that other
listener has cached -- there's no way to tell whose listener you're talking to from
the port number alone. Unity now self-selects a free port and publishes (port, token)
together per-project, so reading *this project's* session.json always gives you the
port and token for *this project's* actual live listener, regardless of how many
other Unity processes are running on the machine.
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


def session_path(project_root: Path) -> Path:
    return project_root / "Library" / "MCP" / "session.json"


def read_session(project_root: Path) -> dict:
    """
    Returns {"token": str, "port": int, "pid": int, "projectPath": str, ...} for
    the bridge currently running against this project. Raises FileNotFoundError if
    no bridge is running (file doesn't exist) -- there is no fallback/default port;
    a missing file means "Unity isn't open" or "the bridge hasn't started yet",
    both of which should surface clearly rather than silently guessing a port.
    """
    path = session_path(project_root)
    if not path.exists():
        raise FileNotFoundError(
            f"No session file found at {path}. Is the Unity Editor open with the MCP bridge running? "
            "(Unity writes this file fresh every time the bridge (re)starts, including after each "
            "domain reload -- if you just recompiled scripts, wait a moment and retry.)"
        )

    try:
        data = json.loads(path.read_text())
    except json.JSONDecodeError as e:
        raise ValueError(f"Session file at {path} is not valid JSON: {e}") from e

    missing = [k for k in ("token", "port") if k not in data]
    if missing:
        raise ValueError(f"Session file at {path} is missing required field(s): {missing}")

    return data


def get_project_root() -> Path:
    env_root = os.environ.get("UNITY_MCP_PROJECT_ROOT")
    if env_root:
        root = Path(env_root).resolve()
        if not root.is_dir():
            raise FileNotFoundError(f"UNITY_MCP_PROJECT_ROOT is set to '{root}' but that path does not exist.")
        return root
    return find_project_root()
