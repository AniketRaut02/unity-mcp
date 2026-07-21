"""
Regression test for the exact bug reported in production: manage_tools reported
a group as active, but the client's visible tool list never actually changed,
because MCP clients cache list_tools() at session start and don't re-fetch on
their own — the server has to explicitly send a tools/list_changed notification.

This test is deliberately NOT calling server.list_tools()/call_tool() as plain
functions the way the other test files do (that bypasses the real MCP request
context entirely, which is exactly how this bug shipped unnoticed). Instead it
uses the MCP SDK's own in-memory transport to run a REAL ClientSession against
our REAL Server object, so it exercises the actual protocol path a real Claude
Code / Codex session goes through: initialize, list_tools, call_tool, and
actually receiving (or not receiving) the server's notification.
"""
import asyncio
import os
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import run_fake_bridge  # noqa: E402


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    port = 46600
    token = "notify-test-token"

    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    fake_server = await run_fake_bridge(tmp_root, port, token)
    serve_task = asyncio.get_event_loop().create_task(fake_server.serve_forever())
    try:
        async with fake_server:
            import mcp.types as types
            from mcp.shared.memory import create_connected_server_and_client_session

            from unity_mcp_server import groups, server as mcp_server_module

            groups.reset()

            received_notifications = []

            async def capture_handler(message):
                if isinstance(message, types.ServerNotification):
                    received_notifications.append(message.root)

            async with create_connected_server_and_client_session(
                mcp_server_module.server, message_handler=capture_handler
            ) as client:
                # --- 1. Initial list_tools: only 'core' visible, as expected ---
                initial = await client.list_tools()
                initial_names = {t.name for t in initial.tools}
                assert "create_script" not in initial_names, initial_names
                assert "get_hierarchy" in initial_names, initial_names
                print("[PASS] initial client-side tool list has only 'core' tools, matching server state:", initial_names)

                # --- 2. Activate 'scripting' via manage_tools, through the REAL protocol path ---
                activate_result = await client.call_tool("manage_tools", {"action": "activate", "group": "scripting"})
                assert not activate_result.isError, activate_result
                print("[PASS] manage_tools activate call succeeds through the real client/server session")

                # --- 3. THE ACTUAL BUG: did the client receive a tools/list_changed notification? ---
                # Give the event loop a moment to deliver the notification the server sent.
                await asyncio.sleep(0.1)
                tool_list_changed = [n for n in received_notifications if isinstance(n, types.ToolListChangedNotification)]
                assert len(tool_list_changed) >= 1, (
                    f"expected at least one ToolListChangedNotification after activating a group, got "
                    f"{[type(n).__name__ for n in received_notifications]} — this is exactly the reported bug: "
                    f"group state changes server-side but the client is never told to refresh."
                )
                print(f"[PASS] client actually received a tools/list_changed notification ({len(tool_list_changed)} total)")

                # --- 4. Re-fetching now shows the newly-activated group's tools ---
                after_activate = await client.list_tools()
                after_names = {t.name for t in after_activate.tools}
                assert "create_script" in after_names, after_names
                assert "update_script" in after_names, after_names
                assert "get_compile_status" in after_names, after_names
                print("[PASS] after re-fetching, the client's tool list now includes the activated group's tools:", after_names)

                # --- 5. And the newly-visible tool is actually callable, not just listed ---
                call_result = await client.call_tool("create_script", {"path": "Scripts/Foo.cs"})
                assert not call_result.isError, call_result
                print("[PASS] the newly-visible tool ('create_script') is actually callable, not just listed")

            fake_server.close()
            await mcp_server_module.bridge.close()
    finally:
        serve_task.cancel()
        try:
            await serve_task
        except asyncio.CancelledError:
            pass

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nGroup-activation notification regression check passed.")


if __name__ == "__main__":
    asyncio.run(main())
