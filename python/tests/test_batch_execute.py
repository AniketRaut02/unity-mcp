"""
Phase 4 test: verifies batch_execute end-to-end on the Python side --
UnityBridgeClient.batch() sends one wire message and gets one response back with
per-item results, and server.py correctly advertises batch_execute as a synthetic
tool and special-cases it in call_tool().
"""
import asyncio
import os
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import run_fake_bridge  # noqa: E402
from unity_mcp_server.bridge_client import BridgeError, UnityBridgeClient  # noqa: E402


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    port = 46300
    token = "batch-test-token"

    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        client = UnityBridgeClient(project_root=tmp_root)

        # 1. bridge_client.batch() -- mixed success/failure results, in order.
        results = await client.batch(
            [
                {"tool": "create_gameobject", "args": {"name": "A"}},
                {"tool": "boom", "args": {}},
                {"tool": "create_gameobject", "args": {"name": "B"}},
            ]
        )
        assert len(results) == 3, f"expected 3 results, got {len(results)}"
        assert results[0]["ok"] is True and results[0]["result"] == {"path": "A"}, results[0]
        assert results[1]["ok"] is False and results[1]["error"] == "simulated failure", results[1]
        assert results[2]["ok"] is True and results[2]["result"] == {"path": "B"}, results[2]
        print("[PASS] client.batch() returns per-item results in order:", results)

        await client.close()

        # 2. server.py: batch_execute appears in list_tools() and call_tool() routes to it.
        os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)
        from unity_mcp_server import server as mcp_server_module

        tools = await mcp_server_module.list_tools()
        names = {t.name for t in tools}
        assert "batch_execute" in names, f"batch_execute missing from list_tools(): {names}"
        print("[PASS] server.list_tools() includes the synthetic batch_execute tool")

        content = await mcp_server_module.call_tool(
            "batch_execute",
            {"calls": [{"tool": "create_gameobject", "args": {"name": "C"}}, {"tool": "boom", "args": {}}]},
        )
        assert len(content) == 1
        assert '"path": "C"' in content[0].text
        assert "simulated failure" in content[0].text
        print("[PASS] server.call_tool('batch_execute') routes through bridge.batch() correctly:", content[0].text)

        await mcp_server_module.bridge.close()
        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll Phase 4 batch_execute checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
