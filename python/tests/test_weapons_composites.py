"""
Real-logic tests for the weapons-group composites: create_weapon,
configure_hitscan/projectile, add_ammo_system, add_recoil, add_muzzle_flash,
add_weapon_sway, add_hit_reaction, add_melee_attack, create_damage_receiver,
add_weapon_switching.
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
        if tool in ("reparent_gameobject", "rename_gameobject", "set_transform", "add_component",
                    "wire_object_reference", "set_component_properties_batch", "add_collider", "delete_gameobject"):
            return None
        if tool == "create_light":
            return {"path": f"{args.get('parentPath', '')}/{args['name']}" if args.get("parentPath") else args["name"]}
        if tool == "create_prefab":
            return {"assetPath": "Assets/" + args["assetPath"]}
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


async def test_create_weapon():
    create_weapon = workflows.get_workflow("create_weapon").handler

    bridge = FakeBridge()
    result = await create_weapon(bridge, {"name": "Pistol", "parentPath": "Player/PlayerCamera", "modelPrimitive": "Cube"})

    assert result["path"] == "Player/PlayerCamera/Pistol", result
    assert result["muzzlePath"] == "Player/PlayerCamera/Pistol/Muzzle", result
    assert result["modelPath"] == "Player/PlayerCamera/Pistol/Model", result

    reparent_call = next(a for t, a in bridge.calls if t == "reparent_gameobject")
    assert reparent_call["newParentPath"] == "Player/PlayerCamera/Pistol", reparent_call
    rename_call = next(a for t, a in bridge.calls if t == "rename_gameobject")
    assert rename_call["newName"] == "Model", rename_call
    muzzle_transform_call = next(a for t, a in bridge.calls if t == "set_transform")
    assert muzzle_transform_call["posZ"] == 0.5, muzzle_transform_call
    print("[PASS] create_weapon assembles weapon + reparented model + offset Muzzle child")

    bridge2 = FakeBridge()
    result2 = await create_weapon(bridge2, {"name": "Knife"})
    assert result2["modelPath"] is None, result2
    assert not any(t == "create_primitive" for t, _ in bridge2.calls)
    print("[PASS] create_weapon without modelPrimitive creates no model child")


async def test_configure_hitscan():
    configure_hitscan = workflows.get_workflow("configure_hitscan").handler

    bridge = FakeBridge()
    result = await configure_hitscan(bridge, {"path": "Pistol", "muzzlePath": "Pistol/Muzzle", "damage": 15, "spread": 2})
    assert result == {"path": "Pistol"}, result

    scripts = {a["path"] for t, a in bridge.calls if t == "create_script"}
    assert "Scripts/MCP/IDamageable.cs" in scripts and "Scripts/MCP/MCPHitReaction.cs" in scripts and "Scripts/MCP/MCPHitscanWeapon.cs" in scripts, scripts

    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "muzzle" and wire_call["targetGameObjectPath"] == "Pistol/Muzzle", wire_call

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert set(batch_call["fieldNames"]) == {"damage", "spread"}, batch_call
    print("[PASS] configure_hitscan scaffolds IDamageable + MCPHitReaction + MCPHitscanWeapon, wires muzzle, batches fields")


async def test_configure_projectile():
    configure_projectile = workflows.get_workflow("configure_projectile").handler

    # No projectilePrefabPath given -- should auto-create the default prefab.
    bridge = FakeBridge()
    result = await configure_projectile(bridge, {"path": "Launcher", "damage": 40})
    assert result == {"path": "Launcher", "projectilePrefabPath": "Prefabs/MCP/DefaultProjectile.prefab"}, result

    assert any(t == "create_primitive" and a["type"] == "Sphere" for t, a in bridge.calls)
    assert any(t == "add_collider" and a.get("isTrigger") is True for t, a in bridge.calls)
    assert any(t == "create_prefab" and a["assetPath"] == "Prefabs/MCP/DefaultProjectile.prefab" for t, a in bridge.calls)
    assert any(t == "delete_gameobject" for t, a in bridge.calls)

    prefab_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a.get("fieldName") == "projectilePrefab")
    assert prefab_wire["targetAssetPath"] == "Prefabs/MCP/DefaultProjectile.prefab", prefab_wire
    print("[PASS] configure_projectile auto-creates a default projectile prefab when none is given")

    # With an existing prefab path -- should NOT create a temp primitive at all.
    bridge2 = FakeBridge()
    result2 = await configure_projectile(bridge2, {"path": "Launcher", "projectilePrefabPath": "Prefabs/Rocket.prefab"})
    assert result2["projectilePrefabPath"] == "Prefabs/Rocket.prefab", result2
    assert not any(t == "create_primitive" for t, _ in bridge2.calls)
    assert not any(t == "create_prefab" for t, _ in bridge2.calls)
    print("[PASS] configure_projectile with an existing projectilePrefabPath skips default-prefab creation entirely")


async def test_add_ammo_system():
    add_ammo = workflows.get_workflow("add_ammo_system").handler
    bridge = FakeBridge()
    result = await add_ammo(bridge, {"path": "Pistol", "magazineSize": 8, "reloadTime": 1.0})
    assert result == {"path": "Pistol"}, result
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert set(batch_call["fieldNames"]) == {"magazineSize", "reloadTime"}, batch_call
    print("[PASS] add_ammo_system attaches MCPAmmoSystem and batches only provided fields")


async def test_add_recoil():
    add_recoil = workflows.get_workflow("add_recoil").handler
    bridge = FakeBridge()
    result = await add_recoil(bridge, {"path": "Pistol", "kickPitch": 4})
    assert result == {"path": "Pistol"}, result
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["kickPitch"], batch_call
    print("[PASS] add_recoil attaches MCPRecoil and batches only provided fields")


async def test_add_muzzle_flash():
    add_muzzle_flash = workflows.get_workflow("add_muzzle_flash").handler
    bridge = FakeBridge()
    result = await add_muzzle_flash(bridge, {"muzzlePath": "Pistol/Muzzle", "intensity": 12})
    assert result == {"path": "Pistol/Muzzle/MuzzleFlashLight"}, result
    light_call = next(a for t, a in bridge.calls if t == "create_light")
    assert light_call["type"] == "Point" and light_call["parentPath"] == "Pistol/Muzzle", light_call
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["intensity"], batch_call
    print("[PASS] add_muzzle_flash creates a real Point light at the muzzle and attaches MCPMuzzleFlash")


async def test_add_weapon_sway():
    add_sway = workflows.get_workflow("add_weapon_sway").handler
    bridge = FakeBridge()
    result = await add_sway(bridge, {"path": "Pistol", "swayAmount": 0.05})
    assert result == {"path": "Pistol"}, result
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["fieldNames"] == ["swayAmount"], batch_call
    print("[PASS] add_weapon_sway attaches MCPWeaponSway and batches only provided fields")


async def test_add_hit_reaction():
    add_reaction = workflows.get_workflow("add_hit_reaction").handler
    bridge = FakeBridge()
    result = await add_reaction(bridge, {"path": "Enemy", "impactPrefabPath": "VFX/Spark.prefab", "impactSoundPath": "Audio/Clang.wav"})
    assert result == {"path": "Enemy"}, result
    prefab_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a.get("fieldName") == "impactPrefab")
    assert prefab_wire["targetAssetPath"] == "VFX/Spark.prefab", prefab_wire
    sound_wire = next(a for t, a in bridge.calls if t == "wire_object_reference" and a.get("fieldName") == "impactSound")
    assert sound_wire["targetAssetPath"] == "Audio/Clang.wav", sound_wire
    print("[PASS] add_hit_reaction attaches MCPHitReaction and wires both prefab and sound")


async def test_add_melee_attack():
    add_melee = workflows.get_workflow("add_melee_attack").handler
    bridge = FakeBridge()
    result = await add_melee(bridge, {"path": "Player/PlayerCamera", "originPath": "Player/PlayerCamera/AttackOrigin", "damage": 50})
    assert result == {"path": "Player/PlayerCamera"}, result
    scripts = {a["path"] for t, a in bridge.calls if t == "create_script"}
    assert "Scripts/MCP/IDamageable.cs" in scripts and "Scripts/MCP/MCPMeleeAttack.cs" in scripts, scripts
    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "origin" and wire_call["targetGameObjectPath"] == "Player/PlayerCamera/AttackOrigin", wire_call
    print("[PASS] add_melee_attack scaffolds shared damage deps + its own script, wires origin")


async def test_create_damage_receiver():
    create_receiver = workflows.get_workflow("create_damage_receiver").handler

    bridge = FakeBridge()
    result = await create_receiver(bridge, {"path": "Enemy", "maxHealth": 150, "headZonePath": "Enemy/Head", "headshotMultiplier": 4})
    assert result == {"path": "Enemy", "headZonePath": "Enemy/Head"}, result

    health_call = next(a for t, a in bridge.calls if t == "add_component" and a.get("typeName") == "MCPHealth")
    assert health_call["path"] == "Enemy", health_call
    zone_call = next(a for t, a in bridge.calls if t == "add_component" and a.get("typeName") == "MCPHitZone")
    assert zone_call["path"] == "Enemy/Head", zone_call
    zone_wire = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert zone_wire["fieldName"] == "health" and zone_wire["targetGameObjectPath"] == "Enemy", zone_wire
    multiplier_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch" and a.get("typeName") == "MCPHitZone")
    assert multiplier_call["fieldNames"] == ["damageMultiplier"] and multiplier_call["values"] == ["4"], multiplier_call
    print("[PASS] create_damage_receiver attaches MCPHealth and, with headZonePath, a wired MCPHitZone with its multiplier")

    bridge2 = FakeBridge()
    result2 = await create_receiver(bridge2, {"path": "Barrel"})
    assert result2["headZonePath"] is None, result2
    assert not any(t == "add_component" and a.get("typeName") == "MCPHitZone" for t, a in bridge2.calls)
    print("[PASS] create_damage_receiver without headZonePath attaches only MCPHealth")


async def test_add_weapon_switching():
    add_switching = workflows.get_workflow("add_weapon_switching").handler

    bridge = FakeBridge()
    result = await add_switching(bridge, {"path": "Player/Weapons", "weaponPaths": ["Pistol", "Rifle", "Knife"]})
    assert result == {"path": "Player/Weapons", "weaponCount": 3}, result

    reparent_calls = [a for t, a in bridge.calls if t == "reparent_gameobject"]
    assert [c["path"] for c in reparent_calls] == ["Pistol", "Rifle", "Knife"], reparent_calls
    assert all(c["newParentPath"] == "Player/Weapons" for c in reparent_calls)
    assert any(t == "add_component" and a.get("typeName") == "MCPWeaponSwitcher" for t, a in bridge.calls)
    print("[PASS] add_weapon_switching reparents weapons in order under the holder and attaches MCPWeaponSwitcher")

    bridge2 = FakeBridge()
    result2 = await add_switching(bridge2, {"path": "Player/Weapons"})
    assert result2["weaponCount"] is None, result2
    assert not any(t == "reparent_gameobject" for t, _ in bridge2.calls)
    print("[PASS] add_weapon_switching without weaponPaths reparents nothing (weapons already children)")


async def main():
    await test_create_weapon()
    await test_configure_hitscan()
    await test_configure_projectile()
    await test_add_ammo_system()
    await test_add_recoil()
    await test_add_muzzle_flash()
    await test_add_weapon_sway()
    await test_add_hit_reaction()
    await test_add_melee_attack()
    await test_create_damage_receiver()
    await test_add_weapon_switching()
    print("\nAll weapons-group composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
