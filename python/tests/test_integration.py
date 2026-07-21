"""
End-to-end sanity test for Phase 2: spins up the fake Unity bridge, points a real
UnityBridgeClient at it, and drives the actual list_tools/call_tool handlers used
by server.py — without needing a real Unity Editor or a real MCP client.
"""
import asyncio
import json
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import run_fake_bridge  # noqa: E402
from unity_mcp_server.bridge_client import BridgeError, UnityBridgeClient  # noqa: E402


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    port = 45999
    token = "test-token-12345"

    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        # Port is no longer passed explicitly — the client reads it from
        # session.json on connect, exactly like it would against real Unity.
        client = UnityBridgeClient(project_root=tmp_root)

        # 1. list_tools
        tools = await client.list_tools()
        assert len(tools) == 6, f"expected 6 tools, got {len(tools)}"
        names = {t["name"] for t in tools}
        assert names == {
            "create_gameobject", "get_hierarchy", "add_component",
            "create_script", "update_script", "get_compile_status",
        }, names
        print("[PASS] list_tools returned expected tools:", names)
        assert client.port == port, f"expected discovered port {port}, got {client.port}"
        print("[PASS] client discovered the correct port from session.json:", client.port)

        # 2. successful call
        result = await client.call("create_gameobject", {"name": "Player"})
        assert result == {"path": "Player"}, result
        print("[PASS] create_gameobject call returned:", result)

        # 3. call that Unity reports as failed
        try:
            await client.call("boom", {})
            raise AssertionError("expected BridgeError")
        except BridgeError as e:
            print("[PASS] failed tool call raised BridgeError as expected:", e)

        # 4. unknown tool
        try:
            await client.call("does_not_exist", {})
            raise AssertionError("expected BridgeError")
        except BridgeError as e:
            print("[PASS] unknown tool raised BridgeError as expected:", e)

        # 5. bad token should be rejected (simulates a stale/mismatched session.json)
        bad_client = UnityBridgeClient(project_root=tmp_root)
        session_path = tmp_root / "Library" / "MCP" / "session.json"
        session_data = json.loads(session_path.read_text())
        session_data["token"] = "wrong-token"
        session_path.write_text(json.dumps(session_data))
        try:
            await bad_client.list_tools()
            raise AssertionError("expected handshake rejection")
        except BridgeError as e:
            print("[PASS] bad/stale token was rejected as expected:", e)

        await client.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll Phase 2 integration checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
