"""
Real-logic tests for the Batch 17 composites: scatter_props (terrain),
generate_grid_layout / place_spawn_points / carve_room / connect_rooms /
set_scene_streaming / validate_level_navmesh (levelgen), create_scare_sequence
(timeline), and generate_input_reader / add_rebinding_ui (input).
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
        self._counters = {}
        self.snap_fail_on = set()  # set of instance indices (0-based, in creation order) that fail snap_to_ground
        self.hierarchy_roots = []
        self.transforms = {}  # path -> get_transform result
        self.agent_dest_results = []  # queue of set_agent_destination results

    def _next_name(self, stem: str) -> str:
        n = self._counters.get(stem, 0)
        self._counters[stem] = n + 1
        return stem if n == 0 else f"{stem}{n}"

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "create_gameobject":
            parent = args.get("parentPath")
            path = f"{parent}/{args['name']}" if parent else args["name"]
            return {"path": path}
        if tool == "instantiate_prefab":
            stem = Path(args["assetPath"]).stem
            name = self._next_name(stem)
            parent = args.get("parentPath")
            path = f"{parent}/{name}" if parent else name
            return {"path": path}
        if tool == "snap_to_ground":
            idx = len([c for c in self.calls if c[0] == "snap_to_ground"]) - 1
            if idx in self.snap_fail_on:
                raise BridgeError(f"snap_to_ground: no collider found below '{args['path']}'.")
            return {"path": args["path"]}
        if tool in ("set_transform", "rename_gameobject", "add_trigger_volume", "wire_unity_event",
                    "add_component", "set_component_properties_batch", "wire_object_reference",
                    "add_navmesh_agent", "delete_gameobject", "add_timeline_track", "add_timeline_clip",
                    "bind_timeline_track", "add_timeline_signal", "create_scriptable_object"):
            return None
        if tool == "get_hierarchy":
            return {"scene": "TestScene", "roots": self.hierarchy_roots}
        if tool == "get_transform":
            return self.transforms[args["path"]]
        if tool == "set_agent_destination":
            return self.agent_dest_results.pop(0)
        if tool == "create_timeline":
            return {"assetPath": "Assets/" + args["assetPath"], "directorPath": args.get("directorPath")}
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

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool} {args}")


async def test_scatter_props():
    scatter_props = workflows.get_workflow("scatter_props").handler

    bridge = FakeBridge()
    result = await scatter_props(bridge, {"prefabPath": "Props/Rock.prefab", "count": 5, "radius": 3.0, "seed": 42})
    assert result["count"] == 5 and len(result["paths"]) == 5, result
    instantiate_calls = [a for t, a in bridge.calls if t == "instantiate_prefab"]
    assert len(instantiate_calls) == 5
    for c in instantiate_calls:
        assert c["assetPath"] == "Props/Rock.prefab"
        assert (c["posX"] - 0.0) ** 2 + (c["posZ"] - 0.0) ** 2 <= (3.0 + 1e-6) ** 2, c
    assert sum(1 for t, _ in bridge.calls if t == "snap_to_ground") == 5
    assert sum(1 for t, _ in bridge.calls if t == "set_transform") == 5  # random Y rotation by default
    print("[PASS] scatter_props places count instances within the radius and snaps + rotates each one")

    bridge2 = FakeBridge()
    result2 = await scatter_props(bridge2, {"prefabPath": "Props/Rock.prefab", "count": 5, "radius": 3.0, "seed": 42})
    positions1 = [(c["posX"], c["posZ"]) for t, c in bridge.calls if t == "instantiate_prefab"]
    positions2 = [(c["posX"], c["posZ"]) for t, c in bridge2.calls if t == "instantiate_prefab"]
    assert positions1 == positions2, "same seed must produce the same layout"
    print("[PASS] scatter_props is reproducible given the same seed")

    bridge3 = FakeBridge()
    bridge3.snap_fail_on = {2}
    result3 = await scatter_props(bridge3, {"prefabPath": "Props/Rock.prefab", "count": 4, "seed": 1})
    assert result3["count"] == 4, result3
    print("[PASS] scatter_props tolerates a snap_to_ground miss instead of failing the whole batch")


async def test_generate_grid_layout():
    generate_grid = workflows.get_workflow("generate_grid_layout").handler

    bridge = FakeBridge()
    result = await generate_grid(bridge, {"rows": 2, "cols": 2, "cellSize": 5})
    assert result["path"] == "LevelGrid", result
    assert len(result["cells"]) == 4, result
    by_rc = {(c["row"], c["col"]): c for c in result["cells"]}
    assert by_rc[(0, 0)]["x"] == 0 and by_rc[(0, 0)]["z"] == 0
    assert by_rc[(1, 1)]["x"] == 5 and by_rc[(1, 1)]["z"] == 5
    assert all(c["path"].startswith("LevelGrid/Cell_") for c in result["cells"])
    print("[PASS] generate_grid_layout without roomPrefabPath creates empty anchor cells at correct grid spacing")

    bridge2 = FakeBridge()
    result2 = await generate_grid(bridge2, {"rows": 1, "cols": 3, "cellSize": 4, "roomPrefabPath": "Rooms/Room.prefab"})
    assert len(result2["cells"]) == 3, result2
    instantiate_calls = [a for t, a in bridge2.calls if t == "instantiate_prefab"]
    assert len(instantiate_calls) == 3
    assert [c["posX"] for c in instantiate_calls] == [0, 4, 8]
    print("[PASS] generate_grid_layout with roomPrefabPath instantiates a room per cell")


async def test_place_spawn_points():
    place_spawns = workflows.get_workflow("place_spawn_points").handler

    bridge = FakeBridge()
    result = await place_spawns(bridge, {"spawnType": "Enemy", "count": 3, "radius": 5, "seed": 7})
    assert len(result["paths"]) == 3, result
    assert all(p.startswith("SpawnPoints/EnemySpawn_") for p in result["paths"]), result

    bridge2 = FakeBridge()
    try:
        await place_spawns(bridge2, {"count": 50, "radius": 1.0, "minDistance": 5.0, "seed": 1})
        assert False, "expected a failure -- 50 points can't fit in a tiny radius with a large minDistance"
    except BridgeError as e:
        assert "only placed" in str(e), e
    print("[PASS] place_spawn_points scatters count points and fails clearly when minDistance/radius/count are infeasible")


async def test_carve_room():
    carve_room = workflows.get_workflow("carve_room").handler

    bridge = FakeBridge()
    bridge.hierarchy_roots = [{
        "name": "RoomA", "path": "RoomA", "active": True, "children": [
            {"name": "Connector_North", "path": "RoomA/Connector_North", "active": True, "children": []},
            {"name": "Mesh", "path": "RoomA/Mesh", "active": True, "children": [
                {"name": "Connector_South", "path": "RoomA/Mesh/Connector_South", "active": True, "children": []},
            ]},
        ],
    }]
    result = await carve_room(bridge, {"roomPrefabPath": "Rooms/RoomA.prefab", "name": "RoomA"})
    assert result["path"] == "RoomA", result
    assert set(result["connectors"]) == {"RoomA/Connector_North", "RoomA/Mesh/Connector_South"}, result
    print("[PASS] carve_room finds connector children recursively by name prefix, including nested ones")


async def test_connect_rooms():
    connect_rooms = workflows.get_workflow("connect_rooms").handler

    bridge = FakeBridge()
    # Fixed room's connector faces +Z (rotY=0) at world (10, 0, 5).
    bridge.transforms["Fixed/Connector"] = {
        "worldPosition": {"x": 10.0, "y": 0.0, "z": 5.0}, "worldEulerAngles": {"x": 0, "y": 0.0, "z": 0},
    }
    # Moving room sits at origin; its connector is 2 units along +Z from the room's own pivot, facing +Z (rotY=0).
    bridge.transforms["Moving"] = {
        "worldPosition": {"x": 0.0, "y": 0.0, "z": 0.0}, "worldEulerAngles": {"x": 0, "y": 0.0, "z": 0},
    }
    bridge.transforms["Moving/Connector"] = {
        "worldPosition": {"x": 0.0, "y": 0.0, "z": 2.0}, "worldEulerAngles": {"x": 0, "y": 0.0, "z": 0},
    }

    result = await connect_rooms(bridge, {
        "fixedConnectorPath": "Fixed/Connector", "movingRoomPath": "Moving", "movingConnectorPath": "Moving/Connector",
    })
    # Moving connector must end up facing -Z (opposite the fixed connector's +Z), i.e. rotated 180 degrees.
    assert abs(result["rotationY"] - 180.0) < 1e-4, result
    # After a 180-degree turn, the connector's local +Z offset now points -Z from the room's new position,
    # so the room's new position must be 2 units further along +Z than the fixed connector.
    assert abs(result["position"]["x"] - 10.0) < 1e-4, result
    assert abs(result["position"]["z"] - 7.0) < 1e-4, result
    set_transform_call = next(a for t, a in bridge.calls if t == "set_transform")
    assert set_transform_call["path"] == "Moving", set_transform_call
    print("[PASS] connect_rooms rotates/translates the moving room so its connector meets the fixed one, facing opposite")


async def test_set_scene_streaming():
    set_streaming = workflows.get_workflow("set_scene_streaming").handler

    bridge = FakeBridge()
    result = await set_streaming(bridge, {"sceneName": "CaveInterior", "x": 1, "y": 0, "z": 2, "radius": 8})
    assert result == {"path": "CaveInteriorStreamZone", "sceneName": "CaveInterior"}, result
    assert "Scripts/MCP/MCPSceneStreamer.cs" in bridge.scripts_created

    trigger_call = next(a for t, a in bridge.calls if t == "add_trigger_volume")
    assert trigger_call == {"path": "CaveInteriorStreamZone", "shape": "Sphere", "radius": 8}, trigger_call

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["sceneName"] and batch_call["values"] == ["CaveInterior"], batch_call

    wire_calls = [a for t, a in bridge.calls if t == "wire_unity_event"]
    assert any(w["eventFieldName"] == "onTriggerEnter" and w["methodName"] == "LoadStream" for w in wire_calls), wire_calls
    assert any(w["eventFieldName"] == "onTriggerExit" and w["methodName"] == "UnloadStream" for w in wire_calls), wire_calls
    print("[PASS] set_scene_streaming wires a real trigger zone to MCPSceneStreamer's Load/UnloadStream")


async def test_validate_level_navmesh():
    validate_navmesh = workflows.get_workflow("validate_level_navmesh").handler

    bridge = FakeBridge()
    bridge.agent_dest_results = [
        {"accepted": True, "pathStatus": "Complete", "pathPending": False, "remainingDistance": 1.0},
        {"accepted": True, "pathStatus": "PartialPath", "pathPending": False, "remainingDistance": None},
    ]
    points = [{"x": 1, "y": 0, "z": 1, "label": "A"}, {"x": 100, "y": 0, "z": 100, "label": "B"}]
    result = await validate_navmesh(bridge, {"points": points})
    assert result["allReachable"] is False, result
    assert result["results"][0]["reachable"] is True and result["results"][1]["reachable"] is False, result

    assert any(t == "add_navmesh_agent" for t, _ in bridge.calls)
    delete_call = next(a for t, a in bridge.calls if t == "delete_gameobject")
    assert delete_call["confirm"] is True, delete_call
    print("[PASS] validate_level_navmesh reports per-point reachability from real pathStatus and cleans up its temp agent")

    bridge2 = FakeBridge()
    bridge2.agent_dest_results = [{"accepted": True, "pathStatus": "Complete", "pathPending": False, "remainingDistance": 0.0}]

    orig_call = bridge2.call

    async def call_with_failure(tool, args):
        if tool == "set_agent_destination":
            raise RuntimeError("boom")
        return await orig_call(tool, args)

    bridge2.call = call_with_failure
    try:
        await validate_navmesh(bridge2, {"points": [{"x": 1, "y": 0, "z": 1}]})
        assert False, "expected the RuntimeError to propagate"
    except RuntimeError:
        pass
    assert any(t == "delete_gameobject" for t, _ in bridge2.calls), "temp agent must still be cleaned up on failure"
    print("[PASS] validate_level_navmesh cleans up its temp agent even if a destination check raises")


async def test_create_scare_sequence():
    create_scare = workflows.get_workflow("create_scare_sequence").handler

    bridge = FakeBridge()
    result = await create_scare(bridge, {
        "timelineAssetPath": "Timelines/Scare1.playable",
        "lightPath": "Hallway/Light", "audioSourcePath": "Hallway/Stinger", "cameraShakePath": "Hallway/ShakeSource",
    })
    assert result["timelineAssetPath"] == "Assets/Timelines/Scare1.playable", result
    assert set(result["tracksAdded"]) == {"Light", "AudioCue", "CameraShakeCue"}, result

    track_types = {a["trackName"]: a["trackType"] for t, a in bridge.calls if t == "add_timeline_track"}
    assert track_types == {"Light": "Activation", "AudioCue": "Signal", "CameraShakeCue": "Signal"}, track_types

    bind_call = next(a for t, a in bridge.calls if t == "bind_timeline_track")
    assert bind_call["targetPath"] == "Hallway/Light" and bind_call["trackName"] == "Light", bind_call

    signal_calls = [a for t, a in bridge.calls if t == "add_timeline_signal"]
    audio_signal = next(s for s in signal_calls if s["trackName"] == "AudioCue")
    assert audio_signal["receiverPath"] == "Hallway/Stinger" and audio_signal["targetTypeName"] == "MCPScareStinger" and audio_signal["methodName"] == "Trigger", audio_signal
    shake_signal = next(s for s in signal_calls if s["trackName"] == "CameraShakeCue")
    assert shake_signal["receiverPath"] == "Hallway/ShakeSource" and shake_signal["targetTypeName"] == "Cinemachine.CinemachineImpulseSource" and shake_signal["methodName"] == "GenerateImpulse", shake_signal
    assert audio_signal["signalAssetPath"] != shake_signal["signalAssetPath"], "each signal beat needs its own SignalAsset"
    print("[PASS] create_scare_sequence wires an Activation track for the light and Signal tracks for audio/camera-shake beats")

    bridge2 = FakeBridge()
    try:
        await create_scare(bridge2, {"timelineAssetPath": "Timelines/Scare2.playable", "animatorPath": "Ghost"})
        assert False, "expected a failure -- animatorPath without animationClipPath"
    except BridgeError as e:
        assert "animationClipPath is required" in str(e), e
    print("[PASS] create_scare_sequence requires animationClipPath whenever animatorPath is given")


async def test_generate_input_reader():
    generate_reader = workflows.get_workflow("generate_input_reader").handler

    bridge = FakeBridge()
    result = await generate_reader(bridge, {
        "className": "PlayerInputReader", "mapName": "Gameplay", "actions": ["Jump:button", "Move:vector2"],
    })
    assert result == {"path": "Scripts/MCP/PlayerInputReader.cs", "className": "PlayerInputReader"}, result

    content = next(a for t, a in bridge.calls if t == "update_script")["content"]
    assert "public event Action OnJump;" in content, content
    assert "public event Action<Vector2> OnMove;" in content, content
    assert '_map.FindAction("Jump").performed += OnJumpPerformed;' in content, content
    assert '_map.FindAction("Move").canceled += OnMoveCanceled;' in content, content
    assert 'actionMapName = "Gameplay"' in content, content
    print("[PASS] generate_input_reader emits a real event-per-action ScriptableObject reader")

    bridge2 = FakeBridge()
    try:
        await generate_reader(bridge2, {"className": "Bad", "mapName": "X", "actions": ["Fire:notatype"]})
        assert False, "expected a failure for an unsupported action type"
    except BridgeError as e:
        assert "Unsupported action type" in str(e), e
    print("[PASS] generate_input_reader rejects an unsupported action type cleanly")


async def test_add_rebinding_ui():
    add_rebinding = workflows.get_workflow("add_rebinding_ui").handler

    bridge = FakeBridge()
    result = await add_rebinding(bridge, {
        "buttonPath": "Canvas/RebindJumpButton", "actionsAssetPath": "Input/PlayerControls.inputactions",
        "mapName": "Gameplay", "actionName": "Jump", "labelPath": "Canvas/JumpLabel",
    })
    assert result == {"path": "Canvas/RebindJumpButton"}, result
    assert "Scripts/MCP/MCPRebindButton.cs" in bridge.scripts_created

    wire_ref_calls = [a for t, a in bridge.calls if t == "wire_object_reference"]
    actions_wire = next(w for w in wire_ref_calls if w["fieldName"] == "actions")
    assert actions_wire["targetAssetPath"] == "Input/PlayerControls.inputactions", actions_wire
    label_wire = next(w for w in wire_ref_calls if w["fieldName"] == "promptLabel")
    assert label_wire["targetGameObjectPath"] == "Canvas/JumpLabel", label_wire

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    values = dict(zip(batch_call["fieldNames"], batch_call["values"]))
    assert values == {"actionMapName": "Gameplay", "actionName": "Jump", "bindingIndex": "0"}, values

    click_wire = next(a for t, a in bridge.calls if t == "wire_unity_event")
    assert click_wire["typeName"] == "Button" and click_wire["eventFieldName"] == "onClick", click_wire
    assert click_wire["methodName"] == "StartRebind", click_wire
    print("[PASS] add_rebinding_ui wires a real rebind button with its InputActionAsset, target action, and label")


async def main():
    await test_scatter_props()
    await test_generate_grid_layout()
    await test_place_spawn_points()
    await test_carve_room()
    await test_connect_rooms()
    await test_set_scene_streaming()
    await test_validate_level_navmesh()
    await test_create_scare_sequence()
    await test_generate_input_reader()
    await test_add_rebinding_ui()
    print("\nAll Batch 17 composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
