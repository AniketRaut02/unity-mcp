"""
Real-logic tests for the Batch 18 composite: analyze_performance (profiling group).
"""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402


class FakeBridge:
    def __init__(self):
        self.calls = []
        self.scene_stats = {"scene": "Test", "gameObjectCount": 10, "vertexCount": 1000, "lightCount": 2, "colliderCount": 5}
        self.render_stats = {"batches": 0, "drawCalls": 0, "setPassCalls": 0, "triangles": 0, "vertices": 0}
        self.memory = {"totalAllocatedBytes": 1000, "topConsumers": []}

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))
        if tool == "get_scene_stats":
            return self.scene_stats
        if tool == "get_render_stats":
            return self.render_stats
        if tool == "get_memory_snapshot":
            return self.memory
        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def test_analyze_performance_healthy_no_render_data():
    analyze = workflows.get_workflow("analyze_performance").handler

    bridge = FakeBridge()
    result = await analyze(bridge, {})
    assert result["healthy"] is True, result
    assert result["issues"] == [], result
    assert len(result["notes"]) == 1 and "Render stats are all zero" in result["notes"][0], result
    assert any(t == "get_scene_stats" for t, _ in bridge.calls)
    assert any(t == "get_render_stats" for t, _ in bridge.calls)
    assert any(t == "get_memory_snapshot" for t, _ in bridge.calls)
    print("[PASS] analyze_performance reports healthy with no issues and a render-data-unavailable note when nothing has rendered")


async def test_analyze_performance_flags_lights_and_colliders():
    analyze = workflows.get_workflow("analyze_performance").handler

    bridge = FakeBridge()
    bridge.scene_stats = {"scene": "Test", "gameObjectCount": 500, "vertexCount": 500000, "lightCount": 20, "colliderCount": 300}
    result = await analyze(bridge, {"maxRealtimeLights": 8, "maxColliders": 200})
    assert result["healthy"] is False, result
    assert any("20 Light components" in i for i in result["issues"]), result
    assert any("300 Collider components" in i for i in result["issues"]), result
    print("[PASS] analyze_performance flags too many lights and too many colliders against configurable thresholds")


async def test_analyze_performance_flags_render_stats_when_available():
    analyze = workflows.get_workflow("analyze_performance").handler

    bridge = FakeBridge()
    bridge.render_stats = {"batches": 50, "drawCalls": 900, "setPassCalls": 150, "triangles": 1000000, "vertices": 2000000}
    result = await analyze(bridge, {"maxDrawCalls": 500, "maxSetPassCalls": 100})
    assert result["healthy"] is False, result
    assert any("900 draw calls" in i for i in result["issues"]), result
    assert any("150 SetPass calls" in i for i in result["issues"]), result
    assert result["notes"] == [], "should not report the render-data-unavailable note once real render stats exist"
    print("[PASS] analyze_performance flags excessive draw calls/SetPass calls once real render stats are available")


async def test_analyze_performance_custom_thresholds_avoid_false_positives():
    analyze = workflows.get_workflow("analyze_performance").handler

    bridge = FakeBridge()
    bridge.scene_stats = {"scene": "Test", "gameObjectCount": 50, "vertexCount": 5000, "lightCount": 15, "colliderCount": 50}
    result = await analyze(bridge, {"maxRealtimeLights": 20})
    assert result["healthy"] is True, result
    print("[PASS] analyze_performance respects a raised maxRealtimeLights threshold instead of always using the default")


async def main():
    await test_analyze_performance_healthy_no_render_data()
    await test_analyze_performance_flags_lights_and_colliders()
    await test_analyze_performance_flags_render_stats_when_available()
    await test_analyze_performance_custom_thresholds_avoid_false_positives()
    print("\nAll Batch 18 composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
