"""
Regression test for the exact bug reported in production:

  "when i use the Generate button to build a tool in unity editor, the
  connection is lost, the setup window in unity shows different port and the
  diagnose bridge shows different port, also the tools available in core
  drops from 17 to 3 we can only see batch execute, manage and the newly
  created tool."

Two things are true about this, both verified here:

1. The "drops to 3" symptom is fully explained by existing behavior, not a
   new bug: while the bridge is disconnected, bridge.list_tools() raises
   BridgeError, server.py's list_tools() handler catches it and returns an
   empty Unity tool list, so only Python-side workflow tools whose group is
   currently active show up (by default: batch_execute, manage_tools, plus
   whatever else -- in this test's case, the "test_composite_tool" the user
   would have just generated). This is confirmed below as an intermediate
   check, not treated as something to "fix" -- it's the correct degraded
   state WHILE genuinely disconnected.

2. The actual bug: nothing ever told the CLIENT to look again once Unity
   came back on a new port. The connection-layer reconnect logic already
   handled the new port correctly (Phase 2's session.json re-read), but MCP
   clients cache list_tools() and don't refetch on their own -- so a client
   that was mid-session when this happened would stay stuck at the degraded
   list forever, even after Unity fully recovered. Fixed via
   UnityBridgeClient.add_reconnect_listener() + a tools/list_changed
   notification sent on every reconnect (not just on manage_tools calls,
   which was the only case handled before this fix).
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
    port_a = 46800
    port_b = 46801  # deliberately different, simulating Unity's port-search landing somewhere new
    token_a = "session-a-token"
    token_b = "session-b-token"

    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)

    fake_server_a = await run_fake_bridge(tmp_root, port_a, token_a)
    serve_task_a = asyncio.get_event_loop().create_task(fake_server_a.serve_forever())
    fake_server_b = None
    serve_task_b = None

    try:
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
            # --- 1. Baseline: connected to server_a on port_a, full core tool set visible ---
            initial = await client.list_tools()
            initial_names = {t.name for t in initial.tools}
            assert "create_gameobject" in initial_names and "get_hierarchy" in initial_names, initial_names
            baseline_count = len(initial.tools)
            print(f"[PASS] baseline: connected on port {port_a}, {baseline_count} tools visible: {initial_names}")

            # --- 2. Simulate Unity's old listener going away (domain reload starting) ---
            await mcp_server_module.bridge.close()
            fake_server_a.close()
            print("[PASS] simulated the old Unity listener disconnecting")

            # --- 3. Confirm the degraded-but-expected state WHILE genuinely disconnected:
            # only currently-active-group workflow tools remain, Unity's own tools vanish.
            # This is symptom #1 from the bug report, and it's correct behavior for this
            # moment specifically -- not what's being fixed. ---
            degraded = await client.list_tools()
            degraded_names = {t.name for t in degraded.tools}
            assert "create_gameobject" not in degraded_names, degraded_names
            assert degraded_names == {"batch_execute", "manage_tools"}, degraded_names
            print(f"[PASS] while genuinely disconnected, only core-group workflow tools remain visible (expected): {degraded_names}")

            # --- 4. Simulate Unity coming back on a DIFFERENT port with a fresh session.json
            # (exactly what MCPServer's port-search does after a restart) ---
            fake_server_b = await run_fake_bridge(tmp_root, port_b, token_b)
            serve_task_b = asyncio.get_event_loop().create_task(fake_server_b.serve_forever())
            print(f"[PASS] simulated Unity restarting on a new port ({port_b} instead of {port_a})")

            # --- 5. THE ACTUAL FIX: the next call reconnects (picking up the new port from
            # the fresh session.json automatically, per the existing Phase 2 logic) AND
            # fires a tools/list_changed notification because this is a reconnect, not a
            # first-ever connect. ---
            notifications_before = len(received_notifications)
            reconnect_result = await client.call_tool("get_hierarchy", {})
            assert not reconnect_result.isError, reconnect_result
            assert mcp_server_module.bridge.port == port_b, (
                f"expected the bridge to have reconnected to the NEW port {port_b}, "
                f"still shows {mcp_server_module.bridge.port}"
            )
            print(f"[PASS] the bridge transparently reconnected to the new port ({port_b}), same as the existing Phase 2 fix already guaranteed")

            await asyncio.sleep(0.1)  # let the notification actually get delivered
            new_notifications = [n for n in received_notifications[notifications_before:] if isinstance(n, types.ToolListChangedNotification)]
            assert len(new_notifications) >= 1, (
                "expected a tools/list_changed notification after the bridge reconnected on a new port -- "
                "this is the actual fix: without it, the client has no reason to ever look again, and stays "
                "stuck at the degraded tool list from step 3 forever, exactly as reported."
            )
            print(f"[PASS] client received a tools/list_changed notification after the reconnect ({len(new_notifications)} total)")

            # --- 6. And refetching now shows the FULL tool set is back -- the client
            # self-healed instead of staying stuck at 2 tools. ---
            recovered = await client.list_tools()
            recovered_names = {t.name for t in recovered.tools}
            assert "create_gameobject" in recovered_names and "get_hierarchy" in recovered_names, recovered_names
            assert len(recovered.tools) == baseline_count, (
                f"expected the full tool set back ({baseline_count} tools), got {len(recovered.tools)}: {recovered_names}"
            )
            print(f"[PASS] after the notification, the client's tool list is fully recovered: {recovered_names}")

            fake_server_b.close()
    finally:
        serve_task_a.cancel()
        try:
            await serve_task_a
        except asyncio.CancelledError:
            pass
        if serve_task_b is not None:
            serve_task_b.cancel()
            try:
                await serve_task_b
            except asyncio.CancelledError:
                pass

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nReconnect-on-new-port notification regression check passed.")


if __name__ == "__main__":
    asyncio.run(main())
