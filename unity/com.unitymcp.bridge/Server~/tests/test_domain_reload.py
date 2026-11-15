"""
v2 domain-reload guarantees, end to end through a real MCP client session.

Replaces v1's test_reconnect_notifies_client.py, which asserted the OLD behaviour
(Unity's tools vanishing from the client while Unity reloads). In v2:

  1. The tool list never shrinks during a reload (served from the pushed list / cache).
  2. A call made while Unity is reloading is held, then answered -- no error.
  3. A write whose reply was lost to a reload is re-sent with the same id and answered
     from Unity's answer cache: it runs exactly once.
  4. A changed tool list pushed by Unity reaches the client as tools/list_changed,
     with no request in flight.
  5. A reload longer than the hold timeout fails with a "retry shortly" message.
  6. With Unity closed (no unity.json, never connected) calls fail immediately.
  7. A WebSocket connection carrying a browser Origin header is refused.
"""
import asyncio
import os
import shutil
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import FAKE_TOOLS, run_fake_bridge  # noqa: E402


async def wait_until(predicate, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        await asyncio.sleep(0.02)
    raise AssertionError("condition not reached in time")


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    os.environ["UNITY_MCP_PROJECT_ROOT"] = str(tmp_root)
    fake = await run_fake_bridge(tmp_root, token="reload-token")
    serve_task = asyncio.get_event_loop().create_task(fake.serve_forever())

    import mcp.types as types
    from mcp.shared.memory import create_connected_server_and_client_session

    from unity_mcp_server import groups, server as mcp_server_module
    from unity_mcp_server.bridge_client import BridgeError, UnityBridgeClient

    groups.reset()
    bridge = mcp_server_module.bridge
    notifications = []

    async def capture(message):
        if isinstance(message, types.ServerNotification):
            notifications.append(message.root)

    try:
        async with create_connected_server_and_client_session(mcp_server_module.server, message_handler=capture) as client:
            baseline = {t.name for t in (await client.list_tools()).tools}
            assert {"create_gameobject", "get_hierarchy"} <= baseline, baseline
            print(f"[PASS] baseline: {len(baseline)} tools visible")

            # 1 + 2: reload in progress -> full tool list, and a call is held then answered.
            reload_task = asyncio.ensure_future(fake.simulate_reload(away_seconds=1.0))
            await wait_until(lambda: not bridge.is_connected)
            during = {t.name for t in (await client.list_tools()).tools}
            assert during == baseline, f"tool list changed during reload: {baseline ^ during}"
            print("[PASS] tool list unchanged while Unity is reloading")

            started = time.monotonic()
            held = await client.call_tool("get_hierarchy", {})
            assert not held.isError, held
            assert time.monotonic() - started >= 0.5, "call should have waited for the reload"
            print(f"[PASS] call during reload was held {time.monotonic() - started:.1f}s and then answered")
            await reload_task

            # 3: the reply to a write is lost to a reload -> re-sent, answered from cache, runs once.
            fake.drop_reply_for_tool = "create_gameobject"
            before = len(fake.executed_ids)
            result = await client.call_tool("create_gameobject", {"name": "Once"})
            assert not result.isError, result
            ran = fake.executed_ids[before:]
            assert len(ran) == 1 and len(set(fake.executed_ids)) == len(fake.executed_ids), fake.executed_ids
            assert "Once" in result.content[0].text, result.content[0].text
            print("[PASS] interrupted write was re-sent with the same id and executed exactly once")

            # 4: Unity pushes a different tool list -> client told to refetch, no request in flight.
            await wait_until(lambda: bridge.is_connected)
            seen = len(notifications)
            extra = dict(FAKE_TOOLS[0], name="new_unity_tool")
            await fake.push_tools(FAKE_TOOLS + [extra])
            await wait_until(lambda: any(isinstance(n, types.ToolListChangedNotification) for n in notifications[seen:]))
            assert "new_unity_tool" in {t.name for t in (await client.list_tools()).tools}
            print("[PASS] a changed tool list pushed by Unity reached the client as tools/list_changed")
    finally:
        await bridge.close()

    # 5: a reload longer than the hold timeout fails with a retry hint.
    short = UnityBridgeClient(project_root=tmp_root, hold_timeout=0.5)
    await short.list_tools()
    await wait_until(lambda: short.is_connected)
    reload_task = asyncio.ensure_future(fake.simulate_reload(away_seconds=3.0))
    await wait_until(lambda: not short.is_connected)
    try:
        await short.call("get_hierarchy", {})
        raise AssertionError("expected a hold timeout")
    except BridgeError as e:
        assert "retry shortly" in str(e) and "reloading" in str(e), e
        print("[PASS] reload longer than the hold timeout ->", e)
    await short.close()
    reload_task.cancel()

    fake.close()
    serve_task.cancel()

    # 6: Unity not running at all -> immediate, clear failure.
    closed_root = Path(tempfile.mkdtemp())
    (closed_root / "Assets").mkdir()
    (closed_root / "ProjectSettings").mkdir()
    lonely = UnityBridgeClient(project_root=closed_root)
    started = time.monotonic()
    try:
        await lonely.call("get_hierarchy", {})
        raise AssertionError("expected a not-connected error")
    except BridgeError as e:
        assert time.monotonic() - started < 2, "should not wait the hold timeout when Unity is closed"
        assert "not connected" in str(e), e
        print("[PASS] Unity closed -> immediate error:", e)

    # 7: a browser-style Origin header is refused before the WebSocket opens.
    from websockets.asyncio.client import connect as ws_connect

    try:
        async with ws_connect(f"ws://127.0.0.1:{lonely.port}/unity", origin="http://evil.example"):
            raise AssertionError("connection with an Origin header should be refused")
    except AssertionError:
        raise
    except Exception as e:
        print("[PASS] WebSocket with a browser Origin header refused:", type(e).__name__)
    await lonely.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    shutil.rmtree(closed_root, ignore_errors=True)
    print("\nAll v2 domain-reload checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
