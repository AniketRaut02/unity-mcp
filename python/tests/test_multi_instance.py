"""
Regression test for the exact incident this was built to fix: with a fixed global
port, a second Unity-like listener owning the "usual" port causes a client for
project A to potentially talk to project B's listener instead.

This test runs two fake bridges for two different fake "projects" on two different
ports (simulating Unity's own auto-incrementing port search when the first port is
taken), and confirms each project's client connects ONLY to its own project's
listener -- never the other one -- purely by reading each project's own session.json.
"""
import asyncio
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import run_fake_bridge  # noqa: E402
from unity_mcp_server.bridge_client import UnityBridgeClient  # noqa: E402


async def main():
    project_a = Path(tempfile.mkdtemp())
    project_b = Path(tempfile.mkdtemp())

    # Simulates Unity's port search: project A grabs the "usual" port, project B's
    # bridge would have tried the same port, found it taken, and moved to the next one.
    port_a = 46100
    port_b = 46101

    token_a = "token-for-project-A"
    token_b = "token-for-project-B"

    server_a = await run_fake_bridge(project_a, port_a, token_a)
    server_b = await run_fake_bridge(project_b, port_b, token_b)

    async with server_a, server_b:
        asyncio.get_event_loop().create_task(server_a.serve_forever())
        asyncio.get_event_loop().create_task(server_b.serve_forever())

        client_a = UnityBridgeClient(project_root=project_a)
        client_b = UnityBridgeClient(project_root=project_b)

        await client_a.list_tools()
        await client_b.list_tools()

        assert client_a.port == port_a, f"client_a connected to {client_a.port}, expected {port_a}"
        assert client_b.port == port_b, f"client_b connected to {client_b.port}, expected {port_b}"
        print(f"[PASS] client_a (project A) correctly connected to its own port {client_a.port}")
        print(f"[PASS] client_b (project B) correctly connected to its own port {client_b.port}")

        # Each client's calls must land on its OWN bridge, not the other one.
        result_a = await client_a.call("create_gameobject", {"name": "FromProjectA"})
        result_b = await client_b.call("create_gameobject", {"name": "FromProjectB"})
        assert result_a == {"path": "FromProjectA"}, result_a
        assert result_b == {"path": "FromProjectB"}, result_b
        print("[PASS] each client's tool calls were served by its own project's bridge, not the other one")

        await client_a.close()
        await client_b.close()
        server_a.close()
        server_b.close()

    shutil.rmtree(project_a, ignore_errors=True)
    shutil.rmtree(project_b, ignore_errors=True)
    print("\nMulti-instance / port-collision regression check passed.")


if __name__ == "__main__":
    asyncio.run(main())
