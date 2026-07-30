"""
Real-logic tests for the fps_controller-group composites: create_fps_player,
configure_ground_movement/sprint/crouch/jump, add_head_look, add_footstep_system,
add_interaction_raycaster, add_stamina_system, add_flashlight, add_lean_system.
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

        if tool == "create_gameobject":
            return {"path": args["name"]}
        if tool == "set_transform":
            return None
        if tool == "add_character_controller":
            return None
        if tool == "create_camera":
            return {"path": f"{args.get('parentPath', '')}/{args['name']}" if args.get("parentPath") else args["name"]}
        if tool == "create_light":
            return {"path": f"{args.get('parentPath', '')}/{args['name']}" if args.get("parentPath") else args["name"]}
        if tool == "set_light_properties":
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
        if tool == "add_component":
            return None
        if tool == "wire_object_reference":
            return None
        if tool == "set_component_properties_batch":
            return None

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def test_create_fps_player():
    create_fps_player = workflows.get_workflow("create_fps_player").handler

    bridge = FakeBridge()
    result = await create_fps_player(bridge, {"name": "Hero", "mouseSensitivity": 4})

    assert result == {"path": "Hero", "cameraPath": "Hero/PlayerCamera"}, result
    assert any(t == "add_character_controller" and a["path"] == "Hero" for t, a in bridge.calls)
    camera_call = next(a for t, a in bridge.calls if t == "create_camera")
    assert camera_call["parentPath"] == "Hero" and camera_call["tagAsMainCamera"] is True, camera_call
    assert any(t == "add_component" and a.get("typeName") == "MCPFPSController" for t, a in bridge.calls)
    assert any(t == "add_component" and a.get("typeName") == "MCPMouseLook" for t, a in bridge.calls)
    body_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a.get("fieldName") == "bodyTransform")
    assert body_wire["targetGameObjectPath"] == "Hero", body_wire
    cam_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a.get("fieldName") == "cameraTransform")
    assert cam_wire["targetGameObjectPath"] == "Hero/PlayerCamera", cam_wire
    sens_batch = next(a for t, a in bridge.calls if t == "set_component_properties_batch" and a.get("typeName") == "MCPMouseLook")
    assert sens_batch["fieldNames"] == ["sensitivity"] and sens_batch["values"] == ["4"], sens_batch
    print("[PASS] create_fps_player assembles CharacterController + camera + MCPFPSController + MCPMouseLook, all wired")

    # Second call with a different bridge (same scripts "fresh") -- default name applies.
    bridge2 = FakeBridge()
    result2 = await create_fps_player(bridge2, {})
    assert result2["path"] == "Player", result2


async def test_configure_movement_composites():
    for workflow_name, fields in [
        ("configure_ground_movement", {"walkSpeed": 5, "acceleration": 12, "friction": 9}),
        ("configure_sprint", {"sprintSpeed": 8}),
        ("configure_crouch", {"crouchHeight": 1.1, "standUpClearanceCheck": False}),
        ("configure_jump", {"jumpHeight": 1.5, "coyoteTime": 0.2}),
    ]:
        handler = workflows.get_workflow(workflow_name).handler
        bridge = FakeBridge()
        result = await handler(bridge, {"path": "Hero", **fields})
        assert result == {"path": "Hero"}, (workflow_name, result)
        assert any(t == "add_component" and a.get("typeName") == "MCPFPSController" for t, a in bridge.calls), workflow_name
        batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
        assert set(batch_call["fieldNames"]) == set(fields.keys()), (workflow_name, batch_call)
        assert any(t == "create_script" for t, _ in bridge.calls), workflow_name
        assert any(t == "get_compile_status" for t, _ in bridge.calls), f"{workflow_name}: a freshly-scaffolded script should wait for compile"
        print(f"[PASS] {workflow_name} scaffolds MCPFPSController and batches exactly its own fields: {sorted(fields.keys())}")


async def test_add_head_look():
    add_head_look = workflows.get_workflow("add_head_look").handler

    bridge = FakeBridge()
    result = await add_head_look(bridge, {"path": "Hero", "cameraPath": "Hero/PlayerCamera", "sensitivity": 3})
    assert result == {"path": "Hero"}, result
    body_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a.get("fieldName") == "bodyTransform")
    assert body_wire["targetGameObjectPath"] == "Hero"
    cam_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a.get("fieldName") == "cameraTransform")
    assert cam_wire["targetGameObjectPath"] == "Hero/PlayerCamera"
    print("[PASS] add_head_look wires bodyTransform/cameraTransform when cameraPath is given")

    bridge2 = FakeBridge()
    result2 = await add_head_look(bridge2, {"path": "Hero"})
    assert not any(t == "wire_object_reference" and a.get("fieldName") == "cameraTransform" for t, a in bridge2.calls)
    print("[PASS] add_head_look without cameraPath leaves cameraTransform unwired")


async def test_add_footstep_system():
    add_footsteps = workflows.get_workflow("add_footstep_system").handler

    bridge = FakeBridge()
    result = await add_footsteps(bridge, {"path": "Hero", "footstepClipPath": "Audio/Step.wav", "stepInterval": 0.4})
    assert result == {"path": "Hero"}, result
    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "defaultFootstepClip" and wire_call["targetAssetPath"] == "Audio/Step.wav", wire_call
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["stepInterval"], batch_call
    print("[PASS] add_footstep_system wires the clip and batches only the provided numeric fields")


async def test_add_interaction_raycaster():
    add_raycaster = workflows.get_workflow("add_interaction_raycaster").handler

    bridge = FakeBridge()
    result = await add_raycaster(bridge, {"path": "Hero/PlayerCamera", "range": 4})
    assert result == {"path": "Hero/PlayerCamera"}, result
    scripts = {a["path"] for t, a in bridge.calls if t == "create_script"}
    assert "Scripts/MCP/IInteractable.cs" in scripts and "Scripts/MCP/MCPInteractionRaycaster.cs" in scripts, scripts
    print("[PASS] add_interaction_raycaster scaffolds both IInteractable and the raycaster script")


async def test_add_stamina_system():
    add_stamina = workflows.get_workflow("add_stamina_system").handler

    bridge = FakeBridge()
    result = await add_stamina(bridge, {"path": "Hero", "maxStamina": 80, "drainRate": 25})
    assert result == {"path": "Hero"}, result
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert set(batch_call["fieldNames"]) == {"maxStamina", "drainRate"}, batch_call
    print("[PASS] add_stamina_system attaches MCPStamina and batches only the provided fields")


async def test_add_flashlight():
    add_flashlight = workflows.get_workflow("add_flashlight").handler

    bridge = FakeBridge()
    result = await add_flashlight(bridge, {"path": "Hero/PlayerCamera", "range": 15, "batteryCapacity": 50})
    assert result["path"] == "Hero/PlayerCamera" and result["lightPath"] == "Hero/PlayerCamera/FlashlightBeam", result

    light_call = next(a for t, a in bridge.calls if t == "create_light")
    assert light_call["type"] == "Spot" and light_call["parentPath"] == "Hero/PlayerCamera", light_call
    light_props_call = next(a for t, a in bridge.calls if t == "set_light_properties")
    assert light_props_call["range"] == 15, light_props_call

    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "spotLight" and wire_call["targetGameObjectPath"] == "Hero/PlayerCamera/FlashlightBeam", wire_call

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert set(batch_call["fieldNames"]) == {"batteryCapacity"}, batch_call
    print("[PASS] add_flashlight creates a real Spot light child and wires it into MCPFlashlight")


async def test_add_lean_system():
    add_lean = workflows.get_workflow("add_lean_system").handler

    bridge = FakeBridge()
    result = await add_lean(bridge, {"path": "Hero/PlayerCamera", "leanAngle": 20})
    assert result == {"path": "Hero/PlayerCamera"}, result
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["leanAngle"], batch_call
    print("[PASS] add_lean_system attaches MCPLean and batches only the provided fields")


async def main():
    await test_create_fps_player()
    await test_configure_movement_composites()
    await test_add_head_look()
    await test_add_footstep_system()
    await test_add_interaction_raycaster()
    await test_add_stamina_system()
    await test_add_flashlight()
    await test_add_lean_system()
    print("\nAll fps_controller-group composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
