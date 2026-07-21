"""
Real end-to-end test for the streamable-HTTP transport (Phase 7) — actually starts
a uvicorn server serving the real ASGI app from http_transport.py, and connects a
real HTTP client (the MCP SDK's own streamablehttp_client) to it. Not a simulation:
actual sockets, actual HTTP requests, actual JSON-RPC-over-HTTP framing.

Also verifies the bearer-token auth path with a real 401 for a request that omits
or gets the token wrong, and confirms resolve_http_config's loopback-or-token rule
refuses a genuinely dangerous configuration rather than silently allowing it.
"""
import asyncio
import os
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import httpx  # noqa: E402

from fake_unity_bridge import run_fake_bridge  # noqa: E402


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    bridge_port = 46900
    http_port = 46901
    token = "http-test-token"

    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    fake_bridge_server = await run_fake_bridge(tmp_root, bridge_port, token)
    serve_bridge_task = asyncio.get_event_loop().create_task(fake_bridge_server.serve_forever())

    uvicorn_server_task = None
    try:
        from unity_mcp_server import groups, http_transport
        from unity_mcp_server import server as mcp_server_module

        groups.reset()

        # --- 1. resolve_http_config refuses a non-loopback bind with no token ---
        os.environ["UNITY_MCP_HTTP_HOST"] = "0.0.0.0"
        os.environ.pop("UNITY_MCP_HTTP_TOKEN", None)
        try:
            http_transport.resolve_http_config()
            raise AssertionError("expected ValueError for 0.0.0.0 with no token")
        except ValueError as e:
            assert "Refusing to bind" in str(e), e
            print("[PASS] resolve_http_config refuses binding beyond loopback without a token:", e)
        finally:
            os.environ.pop("UNITY_MCP_HTTP_HOST", None)

        # --- 2. Start the REAL HTTP server (loopback, with an auth token) ---
        import uvicorn

        http_token = "streamable-http-secret"
        asgi_app, session_manager = http_transport.build_asgi_app(mcp_server_module.server, http_token)

        async def serve():
            async with session_manager.run():
                config = uvicorn.Config(asgi_app, host="127.0.0.1", port=http_port, log_level="warning")
                srv = uvicorn.Server(config)
                await srv.serve()

        uvicorn_server_task = asyncio.get_event_loop().create_task(serve())
        await asyncio.sleep(0.3)  # give uvicorn a moment to actually bind and start listening

        base_url = f"http://127.0.0.1:{http_port}/mcp"

        # --- 3. A request with no token is genuinely rejected with a real 401 ---
        async with httpx.AsyncClient() as http_client:
            resp = await http_client.post(base_url, json={"jsonrpc": "2.0", "method": "ping", "id": 1})
            assert resp.status_code == 401, resp.status_code
            print("[PASS] a real HTTP request with no Authorization header gets a real 401")

            resp_wrong = await http_client.post(
                base_url, json={"jsonrpc": "2.0", "method": "ping", "id": 1},
                headers={"Authorization": "Bearer wrong-token"},
            )
            assert resp_wrong.status_code == 401, resp_wrong.status_code
            print("[PASS] a real HTTP request with the WRONG token also gets a real 401")

        # --- 4. A real MCP client, over real HTTP, with the correct token, actually works ---
        from mcp import ClientSession
        from mcp.client.streamable_http import streamablehttp_client

        async with streamablehttp_client(base_url, headers={"Authorization": f"Bearer {http_token}"}) as (
            read_stream, write_stream, _get_session_id,
        ):
            async with ClientSession(read_stream, write_stream) as client:
                await client.initialize()
                print("[PASS] a real MCP ClientSession successfully initialized over real streamable HTTP")

                tools_result = await client.list_tools()
                tool_names = {t.name for t in tools_result.tools}
                assert "create_gameobject" in tool_names, tool_names
                assert "batch_execute" in tool_names, tool_names
                print("[PASS] list_tools() over real HTTP returns the real tool set:", tool_names)

                call_result = await client.call_tool("create_gameobject", {"name": "HttpTestObject"})
                assert not call_result.isError, call_result
                assert "HttpTestObject" in str(call_result.content), call_result.content
                print("[PASS] call_tool() over real HTTP actually reaches the fake Unity bridge and gets a real result:", call_result.content)

        uvicorn_server_task.cancel()
        try:
            await uvicorn_server_task
        except asyncio.CancelledError:
            pass

        await mcp_server_module.bridge.close()
        fake_bridge_server.close()
    finally:
        if uvicorn_server_task is not None and not uvicorn_server_task.done():
            uvicorn_server_task.cancel()
            try:
                await uvicorn_server_task
            except asyncio.CancelledError:
                pass
        serve_bridge_task.cancel()
        try:
            await serve_bridge_task
        except asyncio.CancelledError:
            pass

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll streamable-HTTP transport checks passed -- real server, real client, real network calls.")


if __name__ == "__main__":
    asyncio.run(main())
