"""
Real-logic tests for the vfx-group composites: add_dust_motes, add_blood_splatter, add_breath_fog.
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
        self._name_counters = {}

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "create_gameobject":
            return {"path": args["name"]}
        if tool == "create_particle_system":
            n = self._name_counters.get(args.get("name", "ParticleSystem"), 0)
            self._name_counters[args.get("name", "ParticleSystem")] = n + 1
            parent = args.get("parentPath")
            leaf = args.get("name", "ParticleSystem")
            return {"path": f"{parent}/{leaf}" if parent else leaf}
        if tool in ("set_transform", "add_component", "add_decal", "set_particle_module", "play_particle_system",
                    "set_component_properties_batch"):
            return None
        if tool == "create_script":
            path = args["path"]
            if path in self.scripts_created:
                raise BridgeError(f"'{path}' already exists. Use update_script to modify it.")
            self.scripts_created.add(path)
            return {"path": path}
        if tool == "update_script":
            return None
        if tool == "get_compile_status":
            return {"isCompiling": False, "errorCount": 0, "errors": []}

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def test_add_dust_motes():
    add_dust_motes = workflows.get_workflow("add_dust_motes").handler

    bridge = FakeBridge()
    result = await add_dust_motes(bridge, {"path": "Attic", "radius": 4, "density": 2, "driftSpeed": 0.2})
    assert result == {"path": "Attic/DustMotes"}, result

    create_call = next(a for t, a in bridge.calls if t == "create_particle_system")
    assert create_call["parentPath"] == "Attic", create_call
    assert create_call["simulationSpace"] == "World", create_call
    assert create_call["shapeType"] == "Sphere" and create_call["shapeRadius"] == 4, create_call
    assert create_call["rateOverTime"] == 2, create_call

    module_call = next(a for t, a in bridge.calls if t == "set_particle_module")
    assert module_call["path"] == "Attic/DustMotes" and module_call["noiseStrength"] == 0.2, module_call
    print("[PASS] add_dust_motes creates a sparse World-space sphere particle system and enables gentle Noise drift")

    bridge2 = FakeBridge()
    await add_dust_motes(bridge2, {"path": "Basement"})
    create_call2 = next(a for t, a in bridge2.calls if t == "create_particle_system")
    assert create_call2["shapeRadius"] == 3.0 and create_call2["rateOverTime"] == 3.0, create_call2
    print("[PASS] add_dust_motes uses sensible sparse defaults (radius=3, density=3) when omitted")


async def test_add_blood_splatter():
    add_blood_splatter = workflows.get_workflow("add_blood_splatter").handler

    bridge = FakeBridge()
    result = await add_blood_splatter(bridge, {
        "x": 1, "y": 2, "z": 3, "decalMaterialAssetPath": "Materials/Blood.mat", "particleBurstCount": 20,
    })
    assert result == {"path": "BloodSplatter", "particlePath": "BloodSplatter/Burst"}, result

    create_call = next(a for t, a in bridge.calls if t == "create_gameobject")
    assert create_call["name"] == "BloodSplatter", create_call
    transform_call = next(a for t, a in bridge.calls if t == "set_transform")
    assert transform_call == {"path": "BloodSplatter", "posX": 1, "posY": 2, "posZ": 3}, transform_call

    decal_call = next(a for t, a in bridge.calls if t == "add_decal")
    assert decal_call["path"] == "BloodSplatter" and decal_call["materialAssetPath"] == "Materials/Blood.mat", decal_call

    particle_call = next(a for t, a in bridge.calls if t == "create_particle_system")
    assert particle_call["parentPath"] == "BloodSplatter" and particle_call["maxParticles"] == 20, particle_call
    assert particle_call["looping"] is False, particle_call
    assert particle_call["rateOverTime"] == 20 * 8, particle_call

    play_call = next(a for t, a in bridge.calls if t == "play_particle_system")
    assert play_call == {"path": "BloodSplatter/Burst", "action": "Play"}, play_call
    print("[PASS] add_blood_splatter positions a new GameObject, adds a decal, and plays a real particle burst")

    bridge2 = FakeBridge()
    result2 = await add_blood_splatter(bridge2, {"x": 0, "y": 0, "z": 0})
    assert result2["path"] == "BloodSplatter", result2
    assert not any(t == "add_decal" for t, _ in bridge2.calls)
    print("[PASS] add_blood_splatter without decalMaterialAssetPath skips the decal entirely")


async def test_add_breath_fog():
    add_breath_fog = workflows.get_workflow("add_breath_fog").handler

    bridge = FakeBridge()
    result = await add_breath_fog(bridge, {"path": "Player/Camera", "breathInterval": 6, "particleCount": 12})
    assert result == {"path": "Player/Camera/BreathFog"}, result

    create_call = next(a for t, a in bridge.calls if t == "create_particle_system")
    assert create_call["parentPath"] == "Player/Camera" and create_call["rateOverTime"] == 0.0, create_call
    assert create_call["simulationSpace"] == "Local", create_call

    assert "Scripts/MCP/MCPBreathFog.cs" in bridge.scripts_created

    component_call = next(a for t, a in bridge.calls if t == "add_component" and a["typeName"] == "MCPBreathFog")
    assert component_call["path"] == "Player/Camera/BreathFog", component_call

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["breathInterval", "particleCount"], batch_call
    assert batch_call["values"] == ["6", "12"], batch_call
    print("[PASS] add_breath_fog creates a zero-continuous-rate particle system and scaffolds MCPBreathFog to pulse it on a timer")


async def main():
    await test_add_dust_motes()
    await test_add_blood_splatter()
    await test_add_breath_fog()
    print("\nAll vfx-group composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
