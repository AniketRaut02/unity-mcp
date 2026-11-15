"""
Real-logic tests for the cameras-group composites: add_camera_shake,
add_head_bob, and create_render_texture_camera.
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

        if tool == "add_component":
            return None
        if tool == "create_script":
            path = args["path"]
            if path in self.scripts_created:
                raise BridgeError(f"'{path}' already exists. Use update_script to modify it.")
            self.scripts_created.add(path)
            return {"path": path, "className": "MCPHeadBob"}
        if tool == "update_script":
            return None
        if tool == "get_compile_status":
            return {"isCompiling": False, "errorCount": 0, "errors": []}
        if tool == "set_component_properties_batch":
            return None
        if tool == "create_camera":
            return {"path": args["name"]}
        if tool == "create_render_texture":
            return {"assetPath": "Assets/" + args["assetPath"]}
        if tool == "wire_object_reference":
            return None
        if tool == "set_material_properties":
            return None

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def test_add_camera_shake():
    add_shake = workflows.get_workflow("add_camera_shake").handler

    bridge = FakeBridge()
    result = await add_shake(bridge, {"path": "Jumpscare", "listenerPath": "MainCamera"})
    assert result == {"path": "Jumpscare", "listenerPath": "MainCamera", "listenerAdded": True}, result
    source_call = next(a for t, a in bridge.calls if t == "add_component" and a.get("path") == "Jumpscare")
    assert source_call["typeName"] == "Cinemachine.CinemachineImpulseSource", source_call
    listener_call = next(a for t, a in bridge.calls if t == "add_component" and a.get("path") == "MainCamera")
    assert listener_call["typeName"] == "Cinemachine.CinemachineImpulseListener", listener_call
    print("[PASS] add_camera_shake wires an impulse source and, when listenerPath is given, a listener")

    bridge2 = FakeBridge()
    result2 = await add_shake(bridge2, {"path": "Jumpscare"})
    assert result2["listenerAdded"] is False, result2
    assert not any(t == "add_component" and a.get("typeName") == "Cinemachine.CinemachineImpulseListener" for t, a in bridge2.calls)
    print("[PASS] add_camera_shake without listenerPath adds only the impulse source")


async def test_add_head_bob():
    add_head_bob = workflows.get_workflow("add_head_bob").handler

    bridge = FakeBridge()
    result = await add_head_bob(bridge, {"path": "FPCamera", "bobAmplitude": 0.08})
    assert result["relayScriptCreated"] is True, result
    assert any(t == "add_component" and a.get("typeName") == "MCPHeadBob" for t, a in bridge.calls)
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["bobAmplitude"] and batch_call["values"] == ["0.08"], batch_call
    print("[PASS] add_head_bob scaffolds MCPHeadBob, attaches it, and batches the provided fields")

    bridge.calls.clear()
    result2 = await add_head_bob(bridge, {"path": "OtherCamera"})
    assert result2["relayScriptCreated"] is False, result2
    assert not any(t == "get_compile_status" for t, _ in bridge.calls)
    print("[PASS] add_head_bob on a second call reuses the already-scaffolded script")


async def test_create_render_texture_camera():
    create_rt_cam = workflows.get_workflow("create_render_texture_camera").handler

    bridge = FakeBridge()
    result = await create_rt_cam(bridge, {"name": "CCTV1", "width": 512, "height": 256, "targetMaterialPath": "Materials/Monitor.mat"})
    assert result == {"path": "CCTV1", "renderTexturePath": "Textures/MCP/CCTV1.renderTexture"}, result

    rt_call = next(a for t, a in bridge.calls if t == "create_render_texture")
    assert rt_call["width"] == 512 and rt_call["height"] == 256, rt_call

    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call == {
        "path": "CCTV1",
        "typeName": "UnityEngine.Camera",
        "fieldName": "targetTexture",
        "targetAssetPath": "Textures/MCP/CCTV1.renderTexture",
    }, wire_call

    material_call = next(a for t, a in bridge.calls if t == "set_material_properties")
    assert material_call["assetPath"] == "Materials/Monitor.mat" and material_call["textureAssetPath"] == "Textures/MCP/CCTV1.renderTexture", material_call
    print("[PASS] create_render_texture_camera wires a real RenderTexture to the camera and, when given, a target material")

    bridge2 = FakeBridge()
    result2 = await create_rt_cam(bridge2, {"name": "CCTV2"})
    assert not any(t == "set_material_properties" for t, _ in bridge2.calls)
    print("[PASS] create_render_texture_camera without targetMaterialPath skips the material call entirely")


async def main():
    await test_add_camera_shake()
    await test_add_head_bob()
    await test_create_render_texture_camera()
    print("\nAll cameras-group composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
