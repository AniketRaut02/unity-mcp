"""
Quick diagnostic for "Unity tools aren't showing up / calls say Unity is not connected".

v2 connection model: each MCP server (one per AI client session) listens on its own
loopback port and advertises it in <project>/Library/MCP/servers/; the Unity Editor
writes Library/MCP/unity.json and connects out to every advertised server. This script
checks each piece of that chain.

Usage:
    python diagnose_bridge.py [project_root]

If project_root is omitted, it's discovered the same way the server does
(UNITY_MCP_PROJECT_ROOT env var, or walking upward from the current directory).
"""
import json
import socket
import sys
import time
from pathlib import Path

from unity_mcp_server import security
from unity_mcp_server.bridge_client import PROTOCOL_VERSION


def _listening(port: int) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=1):
            return True
    except OSError:
        return False


def main() -> int:
    project_root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else security.get_project_root()
    print(f"Project root: {project_root}")
    problems = 0

    try:
        info = security.read_unity_info(project_root)
        print(f"[OK]   Unity Editor is running for this project (pid {info.get('pid')}, Unity {info.get('unityVersion')}, "
              f"package {info.get('packageVersion')}, protocol {info.get('protocolVersion')}).")
        if info.get("protocolVersion") != PROTOCOL_VERSION:
            problems += 1
            print(f"[FAIL] Protocol mismatch: Unity speaks {info.get('protocolVersion')}, this server speaks "
                  f"{PROTOCOL_VERSION}. Update the Unity package and the Python server to the same release.")
    except FileNotFoundError:
        problems += 1
        print("[FAIL] No Library/MCP/unity.json: the Unity Editor isn't open for this project, the bridge package "
              "isn't installed, or another Unity instance owns this project (check Unity's Console).")
    except ValueError as e:
        problems += 1
        print(f"[FAIL] {e}")

    servers = sorted(security.servers_dir(project_root).glob("*.json"))
    if not servers:
        print("[INFO] No MCP servers advertised in Library/MCP/servers/: no AI client session is running for this "
              "project right now (each client starts its own server when it launches).")
    for path in servers:
        try:
            entry = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            problems += 1
            print(f"[FAIL] {path.name}: unreadable ({e})")
            continue
        port = entry.get("port")
        if _listening(port):
            print(f"[OK]   Server pid {entry.get('pid')} is listening on 127.0.0.1:{port} (server {entry.get('serverVersion')}).")
        else:
            print(f"[WARN] {path.name}: nothing listening on port {port}. That server has exited; Unity removes "
                  "stale entries automatically once it notices the process is gone.")

    cache = security.mcp_dir(project_root) / "tools_cache.json"
    if cache.exists():
        age = time.time() - cache.stat().st_mtime
        count = len(json.loads(cache.read_text(encoding="utf-8")))
        print(f"[INFO] Tool cache: {count} Unity tools, last pushed by Unity {age:.0f}s ago.")
    else:
        print("[INFO] No tool cache yet: Unity has never connected to a server for this project.")

    print("\nUnity's own view (which servers it is connected to, retry state) is in Window > Unity MCP > Setup.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
