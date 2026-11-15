"""
Real-logic tests for the audio-group composites: add_audio_occlusion, add_ambient_bed,
add_scare_stinger, add_footstep_audio_set, add_dynamic_music.
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
        if tool == "create_primitive":
            n = self._name_counters.get(args["type"], 0)
            self._name_counters[args["type"]] = n + 1
            return {"path": f"{args['type']}{'' if n == 0 else n}"}
        if tool in ("set_transform", "add_component", "wire_object_reference",
                    "set_component_properties_batch", "set_component_field",
                    "add_audio_source", "set_audio_source_properties"):
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


async def test_add_audio_occlusion():
    add_occlusion = workflows.get_workflow("add_audio_occlusion").handler

    bridge = FakeBridge()
    result = await add_occlusion(bridge, {"path": "Radio", "listenerPath": "Player/Camera"})
    assert result == {"path": "Radio"}, result

    assert "Scripts/MCP/MCPAudioOcclusion.cs" in bridge.scripts_created

    component_calls = [a["typeName"] for t, a in bridge.calls if t == "add_component"]
    assert component_calls == ["AudioLowPassFilter", "MCPAudioOcclusion"], component_calls

    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "listener" and wire_call["targetGameObjectPath"] == "Player/Camera", wire_call
    print("[PASS] add_audio_occlusion explicitly adds AudioLowPassFilter before MCPAudioOcclusion and wires the listener")

    bridge2 = FakeBridge()
    await add_occlusion(bridge2, {"path": "Radio"})
    assert not any(t == "wire_object_reference" for t, _ in bridge2.calls)
    print("[PASS] add_audio_occlusion without listenerPath skips wiring (relies on the script's own runtime auto-find)")


async def test_add_ambient_bed():
    add_bed = workflows.get_workflow("add_ambient_bed").handler

    bridge = FakeBridge()
    result = await add_bed(bridge, {"path": "AmbientZone", "clipAssetPath": "Audio/Wind.wav", "volume": 0.7})
    assert result == {"path": "AmbientZone"}, result

    source_call = next(a for t, a in bridge.calls if t == "add_audio_source")
    assert source_call == {"path": "AmbientZone", "spatialBlend": 0.0}, source_call

    props_call = next(a for t, a in bridge.calls if t == "set_audio_source_properties")
    assert props_call["loop"] is True and props_call["playOnAwake"] is True and props_call["volume"] == 0.7, props_call
    assert not bridge.scripts_created
    print("[PASS] add_ambient_bed with no fadeInDuration sets volume immediately and skips MCPAmbientFade entirely")

    bridge2 = FakeBridge()
    await add_bed(bridge2, {"path": "AmbientZone", "volume": 0.7, "fadeInDuration": 4})
    props_call2 = next(a for t, a in bridge2.calls if t == "set_audio_source_properties")
    assert props_call2["volume"] == 0.0, props_call2
    assert "Scripts/MCP/MCPAmbientFade.cs" in bridge2.scripts_created
    fade_component = next(a for t, a in bridge2.calls if t == "add_component" and a["typeName"] == "MCPAmbientFade")
    assert fade_component, fade_component
    batch_call = next(a for t, a in bridge2.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["targetVolume", "fadeInDuration"] and batch_call["values"] == ["0.7", "4"], batch_call
    print("[PASS] add_ambient_bed with fadeInDuration starts silent and scaffolds MCPAmbientFade with the real target volume")


async def test_add_scare_stinger():
    add_stinger = workflows.get_workflow("add_scare_stinger").handler

    bridge = FakeBridge()
    result = await add_stinger(bridge, {
        "path": "Closet", "stingerClipAssetPath": "Audio/Sting.wav",
        "mixerAssetPath": "Audio/Main.mixer", "duckAmount": -15,
    })
    assert result == {"path": "Closet"}, result

    assert "Scripts/MCP/MCPScareStinger.cs" in bridge.scripts_created
    wire_calls = {a["fieldName"]: a for t, a in bridge.calls if t == "wire_object_reference"}
    assert wire_calls["stingerClip"]["targetAssetPath"] == "Audio/Sting.wav", wire_calls
    assert wire_calls["mixer"]["targetAssetPath"] == "Audio/Main.mixer", wire_calls

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["duckAmount"] and batch_call["values"] == ["-15"], batch_call
    print("[PASS] add_scare_stinger wires clip + mixer and batches only duckAmount when that's all that's given")

    bridge2 = FakeBridge()
    await add_stinger(bridge2, {"path": "Closet"})
    assert not any(t == "wire_object_reference" for t, _ in bridge2.calls)
    assert not any(t == "set_component_properties_batch" for t, _ in bridge2.calls)
    print("[PASS] add_scare_stinger with nothing but path skips wiring and field batching (Trigger() just no-ops those parts)")


async def test_add_footstep_audio_set():
    add_footsteps = workflows.get_workflow("add_footstep_audio_set").handler

    bridge = FakeBridge()
    result = await add_footsteps(bridge, {
        "path": "Player",
        "surfaceClips": ["Concrete,Audio/StepConcrete.wav", "Wood,Audio/StepWood.wav"],
        "fallbackClipAssetPath": "Audio/StepGeneric.wav",
        "stepInterval": 0.4,
    })
    assert result == {"path": "Player", "surfaceCount": 2}, result

    assert "Scripts/MCP/MCPSurfaceClip.cs" in bridge.scripts_created
    assert "Scripts/MCP/MCPSurfaceFootsteps.cs" in bridge.scripts_created
    # A self-contained script, deliberately NOT referencing fps_controller's MCPFootsteps.
    assert not any("MCPFootsteps" in c for c in bridge.scripts_created)

    child_creates = [a for t, a in bridge.calls if t == "create_gameobject"]
    assert [c["name"] for c in child_creates] == ["Surface0", "Surface1"], child_creates
    assert all(c["parentPath"] == "Player" for c in child_creates), child_creates

    tag_calls = [a for t, a in bridge.calls if t == "set_component_field" and a["typeName"] == "MCPSurfaceClip"]
    assert [c["value"] for c in tag_calls] == ["Concrete", "Wood"], tag_calls

    clip_wires = [a for t, a in bridge.calls if t == "wire_object_reference" and a["typeName"] == "MCPSurfaceClip"]
    assert clip_wires[0]["path"] == "Player/Surface0" and clip_wires[0]["targetAssetPath"] == "Audio/StepConcrete.wav", clip_wires
    assert clip_wires[1]["path"] == "Player/Surface1" and clip_wires[1]["targetAssetPath"] == "Audio/StepWood.wav", clip_wires

    fallback_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a["fieldName"] == "fallbackClip")
    assert fallback_wire["targetAssetPath"] == "Audio/StepGeneric.wav", fallback_wire

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["stepInterval"] and batch_call["values"] == ["0.4"], batch_call
    print("[PASS] add_footstep_audio_set creates one child MCPSurfaceClip per surface entry, in order, with tag+clip wired")

    bridge2 = FakeBridge()
    result2 = await add_footsteps(bridge2, {"path": "Player"})
    assert result2 == {"path": "Player", "surfaceCount": 0}, result2
    assert not any(t == "create_gameobject" for t, _ in bridge2.calls)
    print("[PASS] add_footstep_audio_set with no surfaceClips attaches the bare component and creates no children")


async def test_add_dynamic_music():
    add_music = workflows.get_workflow("add_dynamic_music").handler

    bridge = FakeBridge()
    result = await add_music(bridge, {
        "path": "MusicRig",
        "layerClipPaths": ["Audio/Calm.wav", "Audio/Tense.wav", "Audio/Chase.wav"],
        "fadeSpeed": 2,
    })
    assert result == {"path": "MusicRig", "layerCount": 3}, result

    assert "Scripts/MCP/MCPDynamicMusic.cs" in bridge.scripts_created

    layer_creates = [a for t, a in bridge.calls if t == "create_gameobject"]
    assert [c["name"] for c in layer_creates] == ["Layer0", "Layer1", "Layer2"], layer_creates
    assert all(c["parentPath"] == "MusicRig" for c in layer_creates), layer_creates

    source_calls = [a for t, a in bridge.calls if t == "add_audio_source"]
    assert [c["path"] for c in source_calls] == ["MusicRig/Layer0", "MusicRig/Layer1", "MusicRig/Layer2"], source_calls
    assert all(c["spatialBlend"] == 0.0 for c in source_calls), source_calls

    props_calls = [a for t, a in bridge.calls if t == "set_audio_source_properties"]
    assert props_calls[1]["clipAssetPath"] == "Audio/Tense.wav", props_calls
    assert all(c["loop"] is True and c["playOnAwake"] is False and c["volume"] == 0.0 for c in props_calls), props_calls

    music_component = next(a for t, a in bridge.calls if t == "add_component" and a["typeName"] == "MCPDynamicMusic")
    assert music_component["path"] == "MusicRig", music_component

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["fadeSpeed"] and batch_call["values"] == ["2"], batch_call
    print("[PASS] add_dynamic_music creates ordered child layers (calmest to most tense), each a silent looping AudioSource, then attaches MCPDynamicMusic")


async def main():
    await test_add_audio_occlusion()
    await test_add_ambient_bed()
    await test_add_scare_stinger()
    await test_add_footstep_audio_set()
    await test_add_dynamic_music()
    print("\nAll audio-group composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
