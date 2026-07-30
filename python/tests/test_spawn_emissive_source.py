"""
Real-logic test for spawn_emissive_source: creates a primitive, an emissive
material (keyword enabled + emission color scaled by intensity), assigns it,
and (by default) adds a real Point Light nearby with matching color.
"""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402


class FakeBridge:
    def __init__(self):
        self.calls = []

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "create_primitive":
            return {"path": "Sphere"}
        if tool == "reparent_gameobject":
            return None
        if tool == "create_material":
            return {"assetPath": "Assets/" + args["assetPath"]}
        if tool == "set_material_properties":
            return None
        if tool == "assign_material":
            return None
        if tool == "create_light":
            return {"path": f"{args.get('parentPath', '')}/{args['name']}"}
        if tool == "set_light_properties":
            return None

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def main():
    spawn = workflows.get_workflow("spawn_emissive_source").handler

    # --- Default call: primitive + emissive material + a real Point Light child ---
    bridge = FakeBridge()
    result = await spawn(bridge, {"name": "Lantern", "colorR": 1.0, "colorG": 0.5, "colorB": 0.0, "emissionIntensity": 3.0})

    assert result["path"] == "Sphere", result
    assert result["lightPath"] is not None, result

    keyword_call = next(a for t, a in bridge.calls if t == "set_material_properties" and a.get("keyword") == "_EMISSION")
    assert keyword_call["keywordEnabled"] is True, keyword_call

    color_call = next(a for t, a in bridge.calls if t == "set_material_properties" and a.get("propertyName") == "_EmissionColor")
    assert color_call["colorR"] == 3.0 and color_call["colorG"] == 1.5 and color_call["colorB"] == 0.0, color_call

    assign_call = next(a for t, a in bridge.calls if t == "assign_material")
    assert assign_call["path"] == "Sphere", assign_call

    light_call = next(a for t, a in bridge.calls if t == "create_light")
    assert light_call["type"] == "Point" and light_call["parentPath"] == "Sphere", light_call

    light_props_call = next(a for t, a in bridge.calls if t == "set_light_properties")
    assert light_props_call["colorR"] == 1.0 and light_props_call["colorG"] == 0.5, light_props_call
    print("[PASS] spawn_emissive_source creates a primitive with a scaled emissive color and a matching Point Light child")

    # --- addPointLight=False: no light should be created at all ---
    bridge2 = FakeBridge()
    result2 = await spawn(bridge2, {"name": "DeadBulb", "addPointLight": False})
    assert result2["lightPath"] is None, result2
    assert not any(t == "create_light" for t, _ in bridge2.calls)
    print("[PASS] spawn_emissive_source(addPointLight=False) creates no Light at all")

    # --- parentPath provided: primitive is reparented and the returned path reflects it ---
    bridge3 = FakeBridge()
    result3 = await spawn(bridge3, {"name": "WallSconce", "parentPath": "Hallway", "addPointLight": False})
    reparent_call = next(a for t, a in bridge3.calls if t == "reparent_gameobject")
    assert reparent_call == {"path": "Sphere", "newParentPath": "Hallway"}, reparent_call
    assert result3["path"] == "Hallway/Sphere", result3
    print("[PASS] spawn_emissive_source(parentPath=...) reparents the primitive and returns the nested path")

    print("\nAll spawn_emissive_source composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
