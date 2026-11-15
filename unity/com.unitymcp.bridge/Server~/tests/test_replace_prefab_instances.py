"""
Real-logic test for replace_prefab_instances, against a small duck-typed fake
bridge that simulates just enough scene/prefab state to drive the composite's
actual decision-making: which objects match the source prefab, and whether the
delete -> instantiate -> set_transform -> rename sequence preserves transform
and name correctly. Not the full TCP fake_unity_bridge.py -- this composite's
risk is in that orchestration logic, not the wire protocol.
"""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402
from unity_mcp_server.bridge_client import BridgeError  # noqa: E402


class FakeBridge:
    def __init__(self, objects: dict):
        # path -> {"sourcePrefabPath": str or None, "localPosition": {...}, "localEulerAngles": {...}, "localScale": {...}}
        # An object with sourcePrefabPath=None simulates "not part of a prefab instance"
        # (get_prefab_overrides raises for it, exactly like the real tool does).
        self.objects = objects
        self.calls = []

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "get_scene_hierarchy":
            roots = [
                {"path": path, "children": []}
                for path in self.objects
                if "/" not in path
            ]
            return {"roots": roots, "totalRootCount": len(roots)}

        if tool == "get_prefab_overrides":
            obj = self.objects.get(args["path"])
            if obj is None or obj.get("sourcePrefabPath") is None:
                raise BridgeError(f"GameObject at '{args['path']}' is not part of a prefab instance.")
            return {"sourcePrefabPath": obj["sourcePrefabPath"]}

        if tool == "get_transform":
            obj = self.objects[args["path"]]
            return {
                "localPosition": obj["localPosition"],
                "localEulerAngles": obj["localEulerAngles"],
                "localScale": obj["localScale"],
            }

        if tool == "delete_gameobject":
            assert args.get("confirm") is True, "delete_gameobject must be called with confirm=true"
            del self.objects[args["path"]]
            return None

        if tool == "instantiate_prefab":
            # Simulate Unity naming the new instance after the prefab asset's file name.
            new_name = args["assetPath"].rsplit("/", 1)[-1].replace(".prefab", "")
            new_path = new_name
            self.objects[new_path] = {
                "sourcePrefabPath": args["assetPath"],
                "localPosition": {"x": args["posX"], "y": args["posY"], "z": args["posZ"]},
                "localEulerAngles": {"x": 0, "y": 0, "z": 0},
                "localScale": {"x": 1, "y": 1, "z": 1},
            }
            return {"path": new_path}

        if tool == "set_transform":
            path = args["path"]
            obj = self.objects[path]
            obj["localEulerAngles"] = {"x": args["rotX"], "y": args["rotY"], "z": args["rotZ"]}
            obj["localScale"] = {"x": args["scaleX"], "y": args["scaleY"], "z": args["scaleZ"]}
            return None

        if tool == "rename_gameobject":
            old_path = args["path"]
            obj = self.objects.pop(old_path)
            self.objects[args["newName"]] = obj
            return {"path": args["newName"]}

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def main():
    replace = workflows.get_workflow("replace_prefab_instances").handler

    # --- Only matching instances are replaced; non-instances and other-prefab instances are left alone ---
    bridge = FakeBridge({
        "Goblin1": {
            "sourcePrefabPath": "Assets/Prefabs/GoblinOld.prefab",
            "localPosition": {"x": 1, "y": 0, "z": 2},
            "localEulerAngles": {"x": 0, "y": 90, "z": 0},
            "localScale": {"x": 1, "y": 1, "z": 1},
        },
        "Goblin2": {
            "sourcePrefabPath": "Assets/Prefabs/GoblinOld.prefab",
            "localPosition": {"x": 5, "y": 0, "z": 5},
            "localEulerAngles": {"x": 0, "y": 0, "z": 0},
            "localScale": {"x": 2, "y": 2, "z": 2},
        },
        "Torch1": {
            "sourcePrefabPath": "Assets/Prefabs/Torch.prefab",
            "localPosition": {"x": 0, "y": 0, "z": 0},
            "localEulerAngles": {"x": 0, "y": 0, "z": 0},
            "localScale": {"x": 1, "y": 1, "z": 1},
        },
        "PlainObject": {"sourcePrefabPath": None, "localPosition": {}, "localEulerAngles": {}, "localScale": {}},
    })

    result = await replace(bridge, {"oldPrefabPath": "Assets/Prefabs/GoblinOld.prefab", "newPrefabPath": "Assets/Prefabs/GoblinNew.prefab"})

    assert result["replacedCount"] == 2, result
    replaced_old_paths = {r["oldPath"] for r in result["replacements"]}
    assert replaced_old_paths == {"Goblin1", "Goblin2"}, replaced_old_paths
    print("[PASS] replace_prefab_instances replaces exactly the 2 matching instances, skipping the other prefab and the plain object")

    # Torch1 and PlainObject must never have been touched (no delete/instantiate call naming them).
    touched_paths = {a.get("path") for t, a in bridge.calls if t in ("delete_gameobject", "get_transform")}
    assert "Torch1" not in touched_paths and "PlainObject" not in touched_paths
    print("[PASS] non-matching objects (different prefab, non-prefab) were never touched")

    # The new instances must exist with the OLD instances' transform values preserved, under the OLD names.
    assert "Goblin1" in bridge.objects, bridge.objects.keys()
    new_goblin1 = bridge.objects["Goblin1"]
    assert new_goblin1["sourcePrefabPath"] == "Assets/Prefabs/GoblinNew.prefab", new_goblin1
    assert new_goblin1["localPosition"] == {"x": 1, "y": 0, "z": 2}, new_goblin1
    assert new_goblin1["localEulerAngles"] == {"x": 0, "y": 90, "z": 0}, new_goblin1
    print("[PASS] the replacement instance is the NEW prefab, at the old instance's exact position/rotation, renamed back to the original name")

    new_goblin2 = bridge.objects["Goblin2"]
    assert new_goblin2["localScale"] == {"x": 2, "y": 2, "z": 2}, new_goblin2
    print("[PASS] a second instance's non-default scale is also preserved through the replacement")

    # The old GoblinOld instances are actually gone (deleted, not just renamed/left behind).
    delete_calls = [a["path"] for t, a in bridge.calls if t == "delete_gameobject"]
    assert set(delete_calls) == {"Goblin1", "Goblin2"}, delete_calls
    print("[PASS] delete_gameobject was called with confirm=true for exactly the 2 old instances")

    # --- No matches: a clean no-op, not an error ---
    bridge2 = FakeBridge({"Torch1": {"sourcePrefabPath": "Assets/Prefabs/Torch.prefab", "localPosition": {}, "localEulerAngles": {}, "localScale": {}}})
    result2 = await replace(bridge2, {"oldPrefabPath": "Assets/Prefabs/NothingMatchesThis.prefab", "newPrefabPath": "Assets/Prefabs/X.prefab"})
    assert result2["replacedCount"] == 0 and result2["replacements"] == []
    print("[PASS] replace_prefab_instances is a clean no-op (not an error) when nothing matches")

    print("\nAll replace_prefab_instances composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
