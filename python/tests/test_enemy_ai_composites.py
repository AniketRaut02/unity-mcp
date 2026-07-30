"""
Real-logic tests for the enemy_ai-group composites (create_enemy, add_sight_sensor,
add_hearing_sensor, add_patrol_route, configure_chase/search/attack_behavior,
add_stalker_ai, add_enemy_spawner) and the behavior_tree-group set_blackboard_key.
"""
import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402
from unity_mcp_server.bridge_client import BridgeError  # noqa: E402


class FakeBridge:
    def __init__(self):
        self.calls = []
        self.scripts_created = set()
        self._blackboard_data = {}
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
                    "set_component_properties_batch", "reparent_gameobject", "rename_gameobject",
                    "add_navmesh_agent"):
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
        if tool == "get_component_field":
            return {"value": json.dumps(self._blackboard_data) if self._blackboard_data else "{}"}
        if tool == "set_component_field":
            self._blackboard_data = json.loads(args["value"])
            return None

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def test_set_blackboard_key():
    set_key = workflows.get_workflow("set_blackboard_key").handler

    bridge = FakeBridge()
    result = await set_key(bridge, {"path": "Enemy", "key": "alertLevel", "value": 2})
    assert result == {"path": "Enemy", "key": "alertLevel", "value": 2}, result
    assert bridge._blackboard_data == {"alertLevel": 2}, bridge._blackboard_data
    print("[PASS] set_blackboard_key writes the first key into a fresh {} blackboard")

    # Second key on the same (now populated) blackboard must merge, not overwrite.
    result2 = await set_key(bridge, {"path": "Enemy", "key": "lastSeenPlayer", "value": "corridor"})
    assert bridge._blackboard_data == {"alertLevel": 2, "lastSeenPlayer": "corridor"}, bridge._blackboard_data
    print("[PASS] set_blackboard_key merges a second key without clobbering the first")

    # MCPBlackboard.cs is only ever actually created once, even across both calls above --
    # the second create_script attempt hits FakeBridge's "already exists" branch and is not
    # re-added to scripts_created, matching the idempotent-scaffold contract.
    assert len(bridge.scripts_created) == 1
    print("[PASS] set_blackboard_key scaffolds MCPBlackboard exactly once (idempotent)")


async def test_create_enemy():
    create_enemy = workflows.get_workflow("create_enemy").handler

    bridge = FakeBridge()
    result = await create_enemy(bridge, {"name": "Stalker", "maxHealth": 60, "moveSpeed": 4})

    assert result["path"] == "Stalker", result
    assert result["modelPath"] == "Stalker/Model", result

    agent_call = next(a for t, a in bridge.calls if t == "add_navmesh_agent")
    assert agent_call["path"] == "Stalker" and agent_call["speed"] == 4, agent_call

    assert any(t == "add_component" and a.get("typeName") == "MCPHealth" for t, a in bridge.calls)
    assert any(t == "add_component" and a.get("typeName") == "MCPEnemyBrain" for t, a in bridge.calls)
    brain_batch = next(a for t, a in bridge.calls if t == "set_component_properties_batch" and a.get("typeName") == "MCPEnemyBrain")
    assert brain_batch["fieldNames"] == ["chaseSpeed"] and brain_batch["values"] == ["4"], brain_batch

    assert any(t == "add_component" and a.get("typeName") == "MCPSightSensor" for t, a in bridge.calls)
    assert any(t == "add_component" and a.get("typeName") == "MCPHearingSensor" for t, a in bridge.calls)
    print("[PASS] create_enemy assembles NavMeshAgent + MCPHealth + MCPEnemyBrain + model + both sensors")

    bridge2 = FakeBridge()
    result2 = await create_enemy(bridge2, {"name": "Bare", "addSightSensor": False, "addHearingSensor": False, "modelPrimitive": None})
    assert result2["modelPath"] is None, result2
    assert not any(t == "add_component" and a.get("typeName") in ("MCPSightSensor", "MCPHearingSensor") for t, a in bridge2.calls)
    print("[PASS] create_enemy with sensors/model disabled skips them entirely")


async def test_add_sight_and_hearing_sensors():
    add_sight = workflows.get_workflow("add_sight_sensor").handler
    bridge = FakeBridge()
    result = await add_sight(bridge, {"path": "Enemy", "targetPath": "Player", "viewDistance": 25})
    assert result == {"path": "Enemy"}, result
    scripts = {a["path"] for t, a in bridge.calls if t == "create_script"}
    assert "Scripts/MCP/MCPEnemyBrain.cs" in scripts and "Scripts/MCP/MCPSightSensor.cs" in scripts, scripts
    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "target" and wire_call["targetGameObjectPath"] == "Player", wire_call
    print("[PASS] add_sight_sensor scaffolds MCPEnemyBrain + MCPSightSensor and wires the target")

    add_hearing = workflows.get_workflow("add_hearing_sensor").handler
    bridge2 = FakeBridge()
    result2 = await add_hearing(bridge2, {"path": "Enemy", "hearingSensitivity": 2})
    assert result2 == {"path": "Enemy"}, result2
    batch_call = next(a for t, a in bridge2.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["hearingSensitivity"], batch_call
    print("[PASS] add_hearing_sensor scaffolds its dependencies and batches its field")


async def test_add_patrol_route():
    add_route = workflows.get_workflow("add_patrol_route").handler
    bridge = FakeBridge()
    result = await add_route(bridge, {"path": "Enemy", "waypointPositions": ["0,0,0", "5,0,0", "5,0,5"]})

    assert result == {"path": "Enemy", "routePath": "Enemy/PatrolRoute", "waypointCount": 3}, result

    waypoint_calls = [a for t, a in bridge.calls if t == "create_gameobject" and a["name"].startswith("Waypoint")]
    assert [c["name"] for c in waypoint_calls] == ["Waypoint0", "Waypoint1", "Waypoint2"], waypoint_calls

    transform_calls = [a for t, a in bridge.calls if t == "set_transform"]
    assert transform_calls[1] == {"path": "Enemy/PatrolRoute/Waypoint1", "posX": 5.0, "posY": 0.0, "posZ": 0.0}, transform_calls

    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "patrolRouteParent" and wire_call["targetGameObjectPath"] == "Enemy/PatrolRoute", wire_call

    state_batch = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert state_batch["fieldNames"] == ["currentState"] and state_batch["values"] == ["Patrol"], state_batch
    print("[PASS] add_patrol_route creates ordered waypoints, wires the holder, and defaults to starting Patrol")

    bridge2 = FakeBridge()
    await add_route(bridge2, {"path": "Enemy", "waypointPositions": ["1,1,1"], "startPatrolling": False})
    assert not any(t == "set_component_properties_batch" and a.get("fieldNames") == ["currentState"] for t, a in bridge2.calls)
    print("[PASS] add_patrol_route(startPatrolling=False) does not force the Patrol state")


async def test_configure_behaviors_and_stalker():
    configure_chase = workflows.get_workflow("configure_chase_behavior").handler
    bridge = FakeBridge()
    await configure_chase(bridge, {"path": "Enemy", "chaseSpeed": 7})
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["chaseSpeed"], batch_call
    print("[PASS] configure_chase_behavior batches only chaseSpeed/attackRange fields given")

    configure_search = workflows.get_workflow("configure_search_behavior").handler
    bridge2 = FakeBridge()
    await configure_search(bridge2, {"path": "Enemy", "searchDuration": 10})
    batch_call2 = next(a for t, a in bridge2.calls if t == "set_component_properties_batch")
    assert batch_call2["fieldNames"] == ["searchDuration"], batch_call2
    print("[PASS] configure_search_behavior batches only searchDuration")

    configure_attack = workflows.get_workflow("configure_attack_behavior").handler
    bridge3 = FakeBridge()
    await configure_attack(bridge3, {"path": "Enemy", "telegraphDuration": 1.2})
    batch_call3 = next(a for t, a in bridge3.calls if t == "set_component_properties_batch")
    assert batch_call3["fieldNames"] == ["telegraphDuration"], batch_call3
    print("[PASS] configure_attack_behavior batches only telegraphDuration")

    add_stalker = workflows.get_workflow("add_stalker_ai").handler
    bridge4 = FakeBridge()
    result = await add_stalker(bridge4, {"path": "Enemy", "retreatDistance": 10})
    assert result == {"path": "Enemy"}, result
    batch_call4 = next(a for t, a in bridge4.calls if t == "set_component_properties_batch")
    assert batch_call4["fieldNames"] == ["useStalkerBehavior", "stalkerRetreatDistance"], batch_call4
    assert batch_call4["values"][0] == "True", batch_call4
    print("[PASS] add_stalker_ai enables useStalkerBehavior and batches only the distances given")

    bridge5 = FakeBridge()
    await add_stalker(bridge5, {"path": "Enemy", "enabled": False})
    batch_call5 = next(a for t, a in bridge5.calls if t == "set_component_properties_batch")
    assert batch_call5["values"] == ["False"], batch_call5
    print("[PASS] add_stalker_ai(enabled=False) disables stalker behavior")


async def test_add_enemy_spawner():
    add_spawner = workflows.get_workflow("add_enemy_spawner").handler

    bridge = FakeBridge()
    result = await add_spawner(bridge, {"name": "WaveSpawner", "x": 1, "y": 2, "z": 3, "enemyPrefabPath": "Prefabs/Ghoul.prefab", "waveSize": 6})
    assert result == {"path": "WaveSpawner"}, result

    create_call = next(a for t, a in bridge.calls if t == "create_gameobject")
    assert create_call["name"] == "WaveSpawner", create_call
    transform_call = next(a for t, a in bridge.calls if t == "set_transform")
    assert transform_call == {"path": "WaveSpawner", "posX": 1, "posY": 2, "posZ": 3}, transform_call
    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "enemyPrefab" and wire_call["targetAssetPath"] == "Prefabs/Ghoul.prefab", wire_call
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["waveSize"], batch_call
    print("[PASS] add_enemy_spawner creates a new spawner GameObject, positions it, wires the prefab, batches waveSize")

    bridge2 = FakeBridge()
    result2 = await add_spawner(bridge2, {"path": "ExistingSpawnPoint"})
    assert result2 == {"path": "ExistingSpawnPoint"}, result2
    assert not any(t == "create_gameobject" for t, _ in bridge2.calls)
    print("[PASS] add_enemy_spawner with an existing path reuses it instead of creating a new GameObject")


async def main():
    await test_set_blackboard_key()
    await test_create_enemy()
    await test_add_sight_and_hearing_sensors()
    await test_add_patrol_route()
    await test_configure_behaviors_and_stalker()
    await test_add_enemy_spawner()
    print("\nAll enemy_ai-group (+ set_blackboard_key) composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
