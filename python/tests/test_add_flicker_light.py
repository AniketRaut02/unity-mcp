"""
Real-logic test for add_flicker_light: the relay script is scaffolded exactly
once (idempotent on a second call), the MCPFlickerLight component actually
gets attached, and provided fields are batched onto it correctly.
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

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "create_script":
            path = args["path"]
            if path in self.scripts_created:
                raise BridgeError(f"'{path}' already exists. Use update_script to modify it.")
            self.scripts_created.add(path)
            return {"path": path, "className": "MCPFlickerLight"}

        if tool == "update_script":
            return None

        if tool == "get_compile_status":
            return {"isCompiling": False, "errorCount": 0, "errors": []}

        if tool == "add_component":
            return None

        if tool == "set_component_properties_batch":
            return None

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def main():
    add_flicker = workflows.get_workflow("add_flicker_light").handler

    # --- First call: scaffolds the script, adds the component, batches the provided fields ---
    bridge = FakeBridge()
    result = await add_flicker(bridge, {
        "path": "CorridorLamp",
        "minIntensity": 0.1,
        "maxIntensity": 2.0,
        "flickerSpeed": 15,
    })

    assert result["relayScriptCreated"] is True, result
    assert any(t == "add_component" and a.get("typeName") == "MCPFlickerLight" for t, a in bridge.calls)
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["minIntensity", "maxIntensity", "flickerSpeed"], batch_call
    assert batch_call["values"] == ["0.1", "2.0", "15"], batch_call
    print("[PASS] add_flicker_light scaffolds MCPFlickerLight, attaches it, and batches the provided fields")

    # --- Second call (same bridge = script "already exists" now): must NOT re-scaffold ---
    bridge.calls.clear()
    result2 = await add_flicker(bridge, {"path": "OtherLamp"})

    assert result2["relayScriptCreated"] is False, result2
    assert not any(t == "get_compile_status" for t, _ in bridge.calls), "should not wait for compile when the script already existed"
    assert not any(t == "set_component_properties_batch" for t, _ in bridge.calls), "no fields provided -- should not call the batch setter at all"
    assert any(t == "add_component" and a.get("typeName") == "MCPFlickerLight" for t, a in bridge.calls)
    print("[PASS] add_flicker_light on a second call reuses the already-scaffolded script and skips the batch call when no fields are given")

    print("\nAll add_flicker_light composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
