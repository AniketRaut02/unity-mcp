"""
Quick diagnostic for "the bridge isn't responding" / "token rejected" situations —
the exact class of problem behind the multi-Unity-process incident this Phase 2
port-discovery fix addresses. Run this before doing any manual netstat/PID digging.

Usage:
    python3 diagnose_bridge.py [project_root]

If project_root is omitted, it's discovered the same way the server does
(UNITY_MCP_PROJECT_ROOT env var, or walking upward from the current directory).
"""
import asyncio
import sys
from pathlib import Path

from unity_mcp_server import protocol, security


async def main():
    project_root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else security.get_project_root()
    print(f"Project root: {project_root}")

    session_file = security.session_path(project_root)
    print(f"Session file: {session_file}")

    if not session_file.exists():
        print(
            "\n[FAIL] No session.json found. This means either:\n"
            "  - The Unity Editor for this project isn't open, or\n"
            "  - It's open but the bridge hasn't finished initializing yet (check Unity's Console), or\n"
            "  - It's mid-recompile (domain reload briefly deletes and rewrites this file).\n"
            "Open/wait for Unity and retry."
        )
        return 1

    try:
        session = security.read_session(project_root)
    except Exception as e:
        print(f"\n[FAIL] session.json exists but couldn't be read: {e}")
        return 1

    print(f"  token:   {session['token'][:8]}... (truncated)")
    print(f"  port:    {session['port']}")
    print(f"  pid:     {session.get('pid', '?')}")
    print(f"  started: {session.get('startedAt', '?')}")

    print(f"\nAttempting handshake against 127.0.0.1:{session['port']}...")
    try:
        reader, writer = await asyncio.open_connection("127.0.0.1", session["port"])
    except Exception as e:
        print(
            f"\n[FAIL] Could not even open a TCP connection to port {session['port']}: {e}\n"
            "This means session.json is stale — the process that wrote it (pid "
            f"{session.get('pid', '?')}) is no longer listening. If Unity is open, this "
            "usually resolves itself within a few seconds (Unity rewrites the file after "
            "any domain reload); if it persists, restart the Unity Editor for this project."
        )
        return 1

    import uuid

    handshake_id = str(uuid.uuid4())
    writer.write(protocol.encode_message({"type": "handshake", "id": handshake_id, "token": session["token"]}))
    await writer.drain()

    ack = await protocol.read_message(reader)
    writer.close()

    if ack is None:
        print("\n[FAIL] Connection closed before handshake completed.")
        return 1

    if ack.get("ok"):
        print(f"\n[OK] Handshake succeeded. This project's bridge on port {session['port']} is healthy.")
        return 0
    else:
        print(
            f"\n[FAIL] Handshake was rejected: {ack.get('error')}\n"
            f"The listener on port {session['port']} does not have the token from this project's "
            "session.json cached. This should not happen with the Phase 2 port-discovery fix unless "
            "session.json is stale (see above) — if you're still seeing this, please report it as a bug "
            "rather than working around it manually."
        )
        return 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
