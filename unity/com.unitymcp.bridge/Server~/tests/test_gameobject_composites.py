"""
Real-logic tests for align_gameobjects / snap_to_ground, run against a small
duck-typed fake bridge (not the full TCP fake_unity_bridge.py -- these composites'
risk is in the delta-computation/ordering math, not the wire protocol, which is
already covered elsewhere). Each fake bridge records every call it receives, so
assertions can check exactly which tool was called with which args, not just the
composite's final return value.
"""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402
from unity_mcp_server.bridge_client import BridgeError  # noqa: E402


class FakeBridge:
    """Duck-typed stand-in for UnityBridgeClient -- just enough to drive the composite's logic."""

    def __init__(self, transforms=None, raycast_result=None):
        # path -> {"worldPosition": {"x":.., "y":.., "z":..}}
        self.transforms = transforms or {}
        self.raycast_result = raycast_result
        self.calls = []  # list of (tool, args)

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "get_transform":
            return self.transforms[args["path"]]

        if tool == "translate_gameobject":
            # Simulate the real tool's effect so a chain of calls (snap_to_ground
            # reads get_transform then writes translate_gameobject) sees consistent state.
            path = args["path"]
            pos = self.transforms[path]["worldPosition"]
            for axis, key in (("x", "deltaX"), ("y", "deltaY"), ("z", "deltaZ")):
                if key in args:
                    pos[axis] += args[key]
            return None

        if tool == "raycast":
            return self.raycast_result

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


def pos(x, y, z):
    return {"worldPosition": {"x": x, "y": y, "z": z}}


async def main():
    align = workflows.get_workflow("align_gameobjects").handler
    snap = workflows.get_workflow("snap_to_ground").handler

    # --- align: needs at least 2 paths ---
    bridge = FakeBridge(transforms={"A": pos(0, 0, 0)})
    try:
        await align(bridge, {"paths": ["A"], "axis": "x", "mode": "align"})
        assert False, "expected BridgeError for a single path"
    except BridgeError as e:
        assert "at least 2" in str(e), e
    print("[PASS] align_gameobjects rejects fewer than 2 paths")

    # --- align: moves every object to the FIRST path's value on the axis, by default ---
    bridge = FakeBridge(transforms={
        "A": pos(0, 5, 0),
        "B": pos(10, 5, 0),
        "C": pos(-3, 5, 0),
    })
    result = await align(bridge, {"paths": ["A", "B", "C"], "axis": "x", "mode": "align"})
    assert bridge.transforms["A"]["worldPosition"]["x"] == 0
    assert bridge.transforms["B"]["worldPosition"]["x"] == 0
    assert bridge.transforms["C"]["worldPosition"]["x"] == 0
    # A never needed to move (delta 0) -- must not have generated a spurious translate_gameobject call for it.
    translate_paths = [a["path"] for t, a in bridge.calls if t == "translate_gameobject"]
    assert "A" not in translate_paths, "align_gameobjects issued a no-op translate for an object already at the target"
    assert set(translate_paths) == {"B", "C"}, translate_paths
    print("[PASS] align_gameobjects(mode=align) moves B and C to A's x, skips a no-op move for A itself:", result["targetValues"])

    # --- align: explicit value overrides the first-path default ---
    bridge = FakeBridge(transforms={"A": pos(0, 0, 0), "B": pos(10, 0, 0)})
    await align(bridge, {"paths": ["A", "B"], "axis": "y", "mode": "align", "value": 42.0})
    assert bridge.transforms["A"]["worldPosition"]["y"] == 42.0
    assert bridge.transforms["B"]["worldPosition"]["y"] == 42.0
    print("[PASS] align_gameobjects(mode=align, value=42) aligns both objects to the explicit value, not the first path's")

    # --- distribute: evenly spaces 3 objects between the current min and max, preserving order ---
    bridge = FakeBridge(transforms={
        "Left": pos(0, 0, 0),
        "Right": pos(10, 0, 0),
        "Middle": pos(3, 0, 0),  # deliberately NOT already at the midpoint
    })
    result = await align(bridge, {"paths": ["Left", "Right", "Middle"], "axis": "x", "mode": "distribute"})
    assert bridge.transforms["Left"]["worldPosition"]["x"] == 0.0
    assert bridge.transforms["Right"]["worldPosition"]["x"] == 10.0
    assert abs(bridge.transforms["Middle"]["worldPosition"]["x"] - 5.0) < 1e-9
    print("[PASS] align_gameobjects(mode=distribute) evenly spaces 3 objects between the min and max, preserving order:", result["targetValues"])

    # --- distribute: 2 objects just snap to the two extremes (no middle points) ---
    bridge = FakeBridge(transforms={"A": pos(2, 0, 0), "B": pos(8, 0, 0)})
    await align(bridge, {"paths": ["A", "B"], "axis": "x", "mode": "distribute"})
    assert bridge.transforms["A"]["worldPosition"]["x"] == 2.0
    assert bridge.transforms["B"]["worldPosition"]["x"] == 8.0
    print("[PASS] align_gameobjects(mode=distribute) with exactly 2 objects leaves them at their own min/max (both are already the extremes)")

    # --- unknown mode is a clean error ---
    bridge = FakeBridge(transforms={"A": pos(0, 0, 0), "B": pos(1, 0, 0)})
    try:
        await align(bridge, {"paths": ["A", "B"], "axis": "x", "mode": "bogus"})
        assert False, "expected BridgeError for an unknown mode"
    except BridgeError as e:
        assert "Unknown mode" in str(e), e
    print("[PASS] align_gameobjects rejects an unknown mode cleanly")

    # --- snap_to_ground: moves the object to the raycast hit point plus clearance, via world-space translate ---
    bridge = FakeBridge(
        transforms={"Player": pos(5, 10, 5)},
        raycast_result={"hit": True, "point": {"x": 5, "y": 0, "z": 5}, "normal": {"x": 0, "y": 1, "z": 0}, "distance": 10},
    )
    result = await snap(bridge, {"path": "Player", "clearance": 0.5})
    assert bridge.transforms["Player"]["worldPosition"]["y"] == 0.5, bridge.transforms["Player"]
    translate_call = next(a for t, a in bridge.calls if t == "translate_gameobject")
    assert translate_call["worldSpace"] is True, "snap_to_ground must move via world-space translation, not local"
    print("[PASS] snap_to_ground moves the object to the ground hit point plus clearance, via world-space translate_gameobject")

    # --- snap_to_ground: no hit leaves the object untouched and raises ---
    bridge = FakeBridge(transforms={"Player": pos(0, 100, 0)}, raycast_result={"hit": False})
    try:
        await snap(bridge, {"path": "Player"})
        assert False, "expected BridgeError when nothing is hit"
    except BridgeError as e:
        assert "no collider found" in str(e)
    assert not any(t == "translate_gameobject" for t, _ in bridge.calls), "snap_to_ground must not move the object when the raycast found nothing"
    print("[PASS] snap_to_ground raises cleanly and does not move the object when the raycast finds nothing")

    print("\nAll align_gameobjects / snap_to_ground composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
