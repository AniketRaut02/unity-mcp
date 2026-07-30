"""
Real-logic tests for the gameplay-group composites: define_scriptable_object_type,
create_event_channel, create_save_system, create_game_manager, create_inventory_system,
create_interactable, create_door, create_key_lock_pair, create_checkpoint,
create_objective_system.
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
        if tool == "create_ui_element":
            parent = args.get("parentPath")
            leaf = args["name"]
            return {"path": f"{parent}/{leaf}" if parent else leaf}
        if tool in ("set_transform", "add_component", "wire_object_reference", "wire_unity_event",
                    "set_component_properties_batch", "reparent_gameobject", "rename_gameobject",
                    "add_trigger_volume", "create_scriptable_object", "set_rect_transform"):
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


async def test_define_scriptable_object_type():
    define_type = workflows.get_workflow("define_scriptable_object_type").handler

    bridge = FakeBridge()
    result = await define_type(bridge, {
        "className": "EnemyStats", "fields": ["maxHealth:float", "displayName:string", "isBoss:bool"],
        "createAssetMenuPath": "MCP/EnemyStats",
    })
    assert result == {"path": "Scripts/MCP/EnemyStats.cs", "className": "EnemyStats"}, result

    update_call = next(a for t, a in bridge.calls if t == "update_script")
    content = update_call["content"]
    assert "public class EnemyStats : ScriptableObject" in content, content
    assert "public float maxHealth;" in content, content
    assert "public string displayName;" in content, content
    assert "public bool isBoss;" in content, content
    assert '[CreateAssetMenu(menuName = "MCP/EnemyStats")]' in content, content
    print("[PASS] define_scriptable_object_type generates a real class with correctly-typed fields and a CreateAssetMenu attribute")

    bridge2 = FakeBridge()
    try:
        await define_type(bridge2, {"className": "Bad", "fields": ["oops:notatype"]})
        assert False, "expected BridgeError for an unsupported field type"
    except BridgeError as e:
        assert "Unsupported field type" in str(e), e
    print("[PASS] define_scriptable_object_type rejects an unsupported field type cleanly")


async def test_create_event_channel():
    create_channel = workflows.get_workflow("create_event_channel").handler

    bridge = FakeBridge()
    result = await create_channel(bridge, {"assetPath": "Data/OnPlayerDied.asset", "argType": "String"})
    assert result == {"assetPath": "Data/OnPlayerDied.asset", "className": "MCPStringEventChannel"}, result

    update_call = next(a for t, a in bridge.calls if t == "update_script")
    assert "public UnityEvent<string> OnEventRaised;" in update_call["content"], update_call
    assert "public void Raise(string value)" in update_call["content"], update_call

    create_so_call = next(a for t, a in bridge.calls if t == "create_scriptable_object")
    assert create_so_call == {"typeName": "MCPStringEventChannel", "assetPath": "Data/OnPlayerDied.asset"}, create_so_call
    print("[PASS] create_event_channel scaffolds a real UnityEvent<string> channel class and instantiates it")

    bridge2 = FakeBridge()
    result2 = await create_channel(bridge2, {"assetPath": "Data/OnWin.asset"})
    assert result2["className"] == "MCPVoidEventChannel", result2
    update_call2 = next(a for t, a in bridge2.calls if t == "update_script")
    assert "public UnityEvent OnEventRaised;" in update_call2["content"], update_call2
    assert "public void Raise()" in update_call2["content"], update_call2
    print("[PASS] create_event_channel defaults to a Void channel")


async def test_create_save_system():
    create_save_system = workflows.get_workflow("create_save_system").handler

    bridge = FakeBridge()
    result = await create_save_system(bridge, {})
    assert result == {"path": "SaveSystem"}, result
    assert "Scripts/MCP/MCPSaveData.cs" in bridge.scripts_created
    assert "Scripts/MCP/MCPSaveSystem.cs" in bridge.scripts_created
    component_call = next(a for t, a in bridge.calls if t == "add_component")
    assert component_call == {"path": "SaveSystem", "typeName": "MCPSaveSystem"}, component_call
    print("[PASS] create_save_system scaffolds both MCPSaveData and MCPSaveSystem and attaches the latter")


async def test_create_game_manager():
    create_game_manager = workflows.get_workflow("create_game_manager").handler

    bridge = FakeBridge()
    result = await create_game_manager(bridge, {"initialState": "Playing"})
    assert result == {"path": "GameManager"}, result
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["currentState"] and batch_call["values"] == ["Playing"], batch_call
    print("[PASS] create_game_manager attaches MCPGameManager and sets the given initial state")


async def test_create_inventory_system():
    create_inventory = workflows.get_workflow("create_inventory_system").handler

    bridge = FakeBridge()
    result = await create_inventory(bridge, {"path": "Player"})
    assert result == {"path": "Player"}, result
    assert "Scripts/MCP/MCPInventory.cs" in bridge.scripts_created
    assert any(t == "add_component" and a["typeName"] == "MCPInventory" for t, a in bridge.calls)
    print("[PASS] create_inventory_system attaches MCPInventory to the given path")


async def test_create_interactable():
    create_interactable = workflows.get_workflow("create_interactable").handler

    bridge = FakeBridge()
    result = await create_interactable(bridge, {"path": "Lever", "promptMessage": "Pull Lever"})
    assert result == {"path": "Lever"}, result
    assert "Scripts/MCP/IInteractable.cs" in bridge.scripts_created
    assert "Scripts/MCP/MCPInteractable.cs" in bridge.scripts_created
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["promptMessage"] and batch_call["values"] == ["Pull Lever"], batch_call
    print("[PASS] create_interactable scaffolds the shared IInteractable interface plus MCPInteractable and sets promptMessage")


async def test_create_door():
    create_door = workflows.get_workflow("create_door").handler

    bridge = FakeBridge()
    result = await create_door(bridge, {"name": "VaultDoor", "x": 1, "y": 0, "z": 2, "isLocked": True, "requiredKeyId": "vaultkey"})
    assert result == {"path": "VaultDoor"}, result

    primitive_call = next(a for t, a in bridge.calls if t == "create_primitive")
    assert primitive_call == {"type": "Cube", "x": 1, "y": 0, "z": 2}, primitive_call
    rename_call = next(a for t, a in bridge.calls if t == "rename_gameobject")
    assert rename_call == {"path": "Cube", "newName": "VaultDoor"}, rename_call
    assert not any(t == "reparent_gameobject" for t, _ in bridge.calls)

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert set(batch_call["fieldNames"]) == {"isLocked", "requiredKeyId", "openAngle"}, batch_call
    values = dict(zip(batch_call["fieldNames"], batch_call["values"]))
    assert values["isLocked"] == "True" and values["requiredKeyId"] == "vaultkey", values
    print("[PASS] create_door creates a Cube placeholder, renames it, and configures MCPDoor's lock state")

    bridge2 = FakeBridge()
    result2 = await create_door(bridge2, {"parentPath": "Level/Hallway"})
    assert result2 == {"path": "Level/Hallway/Door"}, result2
    reparent_call = next(a for t, a in bridge2.calls if t == "reparent_gameobject")
    assert reparent_call == {"path": "Cube", "newParentPath": "Level/Hallway"}, reparent_call
    print("[PASS] create_door with parentPath reparents the new door under it")


async def test_create_key_lock_pair():
    create_key_lock_pair = workflows.get_workflow("create_key_lock_pair").handler

    bridge = FakeBridge()
    result = await create_key_lock_pair(bridge, {"keyId": "goldkey", "doorPath": "VaultDoor", "keyName": "GoldKey", "x": 5, "y": 0, "z": 0})
    assert result == {"keyPath": "GoldKey", "doorPath": "VaultDoor"}, result

    assert "Scripts/MCP/MCPKeyItem.cs" in bridge.scripts_created
    assert "Scripts/MCP/MCPDoor.cs" in bridge.scripts_created

    key_batch = next(a for t, a in bridge.calls if t == "set_component_properties_batch" and a["typeName"] == "MCPKeyItem")
    assert key_batch["fieldNames"] == ["keyId"] and key_batch["values"] == ["goldkey"], key_batch

    door_batch = next(a for t, a in bridge.calls if t == "set_component_properties_batch" and a["typeName"] == "MCPDoor")
    door_values = dict(zip(door_batch["fieldNames"], door_batch["values"]))
    assert door_values["requiredKeyId"] == "goldkey" and door_values["isLocked"] == "True", door_values

    wire_call = next(a for t, a in bridge.calls if t == "wire_unity_event")
    assert wire_call["path"] == "GoldKey" and wire_call["eventFieldName"] == "onPickup", wire_call
    assert wire_call["targetPath"] == "VaultDoor" and wire_call["methodName"] == "Unlock", wire_call
    assert "dynamic" not in wire_call, "should rely on wire_unity_event's dynamic-by-default forwarding, not bake a fixed argument"
    print("[PASS] create_key_lock_pair creates a key, locks the door to match, and wires onPickup->Unlock dynamically")


async def test_create_checkpoint():
    create_checkpoint = workflows.get_workflow("create_checkpoint").handler

    bridge = FakeBridge()
    result = await create_checkpoint(bridge, {"x": 3, "y": 0, "z": 4, "radius": 5})
    assert result == {"path": "Checkpoint"}, result

    transform_call = next(a for t, a in bridge.calls if t == "set_transform")
    assert transform_call == {"path": "Checkpoint", "posX": 3, "posY": 0, "posZ": 4}, transform_call

    trigger_call = next(a for t, a in bridge.calls if t == "add_trigger_volume")
    assert trigger_call == {"path": "Checkpoint", "shape": "Sphere", "radius": 5}, trigger_call

    assert "Scripts/MCP/MCPCheckpoint.cs" in bridge.scripts_created

    wire_call = next(a for t, a in bridge.calls if t == "wire_unity_event")
    assert wire_call["typeName"] == "MCPTriggerRelay" and wire_call["eventFieldName"] == "onTriggerEnter", wire_call
    assert wire_call["targetTypeName"] == "MCPCheckpoint" and wire_call["methodName"] == "Activate", wire_call
    print("[PASS] create_checkpoint creates a real trigger volume and wires onTriggerEnter to MCPCheckpoint.Activate")


async def test_create_objective_system():
    create_objectives = workflows.get_workflow("create_objective_system").handler

    bridge = FakeBridge()
    result = await create_objectives(bridge, {"path": "GameManager"})
    assert result == {"path": "GameManager", "uiPath": None}, result
    assert "Scripts/MCP/MCPObjectiveSystem.cs" in bridge.scripts_created
    assert not any(t == "create_ui_element" for t, _ in bridge.calls)
    print("[PASS] create_objective_system without uiCanvasPath skips the UI hook entirely")

    bridge2 = FakeBridge()
    result2 = await create_objectives(bridge2, {"path": "GameManager", "uiCanvasPath": "Canvas"})
    assert result2 == {"path": "GameManager", "uiPath": "Canvas/ObjectiveList"}, result2
    assert "Scripts/MCP/MCPObjectiveListUI.cs" in bridge2.scripts_created

    wire_call = next(a for t, a in bridge2.calls if t == "wire_unity_event")
    assert wire_call["path"] == "GameManager" and wire_call["eventFieldName"] == "onObjectiveCompleted", wire_call
    assert wire_call["targetPath"] == "Canvas/ObjectiveList" and wire_call["methodName"] == "AppendLine", wire_call
    print("[PASS] create_objective_system with uiCanvasPath creates a real Text list and wires onObjectiveCompleted to it")


async def main():
    await test_define_scriptable_object_type()
    await test_create_event_channel()
    await test_create_save_system()
    await test_create_game_manager()
    await test_create_inventory_system()
    await test_create_interactable()
    await test_create_door()
    await test_create_key_lock_pair()
    await test_create_checkpoint()
    await test_create_objective_system()
    print("\nAll gameplay-group composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
