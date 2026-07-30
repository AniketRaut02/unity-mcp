"""
Real-logic test for add_trigger_volume: the collider is added with the right
shape/isTrigger, the relay script is scaffolded exactly once (idempotent on a
second call), and the relay component actually gets attached.
"""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402
from unity_mcp_server.bridge_client import BridgeError  # noqa: E402


class FakeBridge:
    def __init__(self):
        self.calls = []
        self.scripts_created = set()
        self.compile_status_calls = 0

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "add_collider":
            return None

        if tool == "create_script":
            path = args["path"]
            if path in self.scripts_created:
                raise BridgeError(f"'{path}' already exists. Use update_script to modify it.")
            self.scripts_created.add(path)
            return {"path": path, "className": "MCPTriggerRelay"}

        if tool == "update_script":
            return None

        if tool == "get_compile_status":
            self.compile_status_calls += 1
            return {"isCompiling": False, "errorCount": 0, "errors": []}

        if tool == "add_component":
            return None

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def main():
    add_trigger = workflows.get_workflow("add_trigger_volume").handler

    # --- First call: scaffolds the relay script, adds a Box trigger collider, attaches the component ---
    bridge = FakeBridge()
    result = await add_trigger(bridge, {"path": "DoorTrigger", "shape": "Box", "size": 2.0})

    assert result["relayScriptCreated"] is True, result
    collider_call = next(a for t, a in bridge.calls if t == "add_collider")
    assert collider_call["isTrigger"] is True, collider_call
    assert collider_call["type"] == "Box", collider_call
    assert collider_call["sizeX"] == 2.0 and collider_call["sizeY"] == 2.0 and collider_call["sizeZ"] == 2.0, collider_call
    assert any(t == "add_component" and a.get("typeName") == "MCPTriggerRelay" for t, a in bridge.calls)
    print("[PASS] add_trigger_volume(Box) adds a trigger Box collider with the requested size and attaches MCPTriggerRelay")

    # --- Second call (same bridge = script "already exists" now): must NOT re-scaffold or wait for compile again ---
    bridge.calls.clear()
    result2 = await add_trigger(bridge, {"path": "DoorTrigger2", "shape": "Sphere", "radius": 3.0})

    assert result2["relayScriptCreated"] is False, result2
    assert not any(t == "get_compile_status" for t, _ in bridge.calls), "should not wait for compile when the script already existed"
    collider_call2 = next(a for t, a in bridge.calls if t == "add_collider")
    assert collider_call2["type"] == "Sphere" and collider_call2["radius"] == 3.0, collider_call2
    print("[PASS] add_trigger_volume(Sphere) on a second call reuses the already-scaffolded relay script (idempotent, no re-wait)")

    # --- Unknown shape is a clean error ---
    bridge2 = FakeBridge()
    try:
        await add_trigger(bridge2, {"path": "X", "shape": "Cylinder"})
        assert False, "expected BridgeError for an unknown shape"
    except BridgeError as e:
        assert "Unknown shape" in str(e)
    print("[PASS] add_trigger_volume rejects an unknown shape cleanly")

    print("\nAll add_trigger_volume composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
