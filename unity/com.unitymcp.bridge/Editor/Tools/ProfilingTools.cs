using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityMCP;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group AB of the tool catalog -- Profiling &amp; Optimization. Every type here (ProfilerDriver, UnityStats,
    /// even though they live in the "Internal"-named UnityEditorInternal namespace) is a real, directly-compilable
    /// public API -- confirmed via live spike, no reflection needed, unlike most of this codebase's optional-package
    /// integrations. Two real batchmode limitations, both confirmed via spike rather than assumed: ProfilerDriver's
    /// frame buffer is empty (lastFrameIndex == -1) until a real Play Mode/Development Player session has actually
    /// run frames with the Profiler enabled, and UnityStats' render counters (batches/drawCalls/triangles/etc) read
    /// zero until at least one real frame has rendered (Play Mode or a Game view repaint) -- both are marked Manual
    /// Test for that reason, the same category as capture_game_view/get_frame_debugger_info from the inspection
    /// group.
    /// </summary>
    public static class ProfilingTools
    {
        [MCPTool(
            "capture_profiler_frames",
            "Enables the Profiler and reports the CPU/Memory/Rendering counter breakdown (the same categories " +
            "Unity's own Profiler window graphs) for the most recently captured frames. Manual Test: the Profiler's " +
            "frame buffer is empty until a real Play Mode/Development Player session has actually run frames with " +
            "the Profiler enabled -- this tool enables it for the next such session and reports an empty frame " +
            "list with an explanatory note if none exist yet.",
            group: "profiling")]
        public static MCPResult CaptureProfilerFrames(
            MCPToolContext ctx,
            [MCPParam("Maximum number of most-recent frames to report. Defaults to 10.")] int frameCount = 10)
        {
            if (frameCount <= 0) return MCPResult.Fail("frameCount must be positive.");

            Profiler.enabled = true;
            ProfilerDriver.enabled = true;

            int last = ProfilerDriver.lastFrameIndex;
            if (last < 0)
            {
                return MCPResult.Success(new
                {
                    frames = Array.Empty<object>(),
                    note = "No frames captured yet -- the Profiler is now enabled; run a Play Mode session (or a " +
                           "Development Player) and call this again to see real frame data.",
                });
            }

            int first = Math.Max(ProfilerDriver.firstFrameIndex, last - frameCount + 1);
            var cpuProps = ProfilerDriver.GetGraphStatisticsPropertiesForArea(ProfilerArea.CPU);
            var memProps = ProfilerDriver.GetGraphStatisticsPropertiesForArea(ProfilerArea.Memory);
            var renderProps = ProfilerDriver.GetGraphStatisticsPropertiesForArea(ProfilerArea.Rendering);

            var frames = new List<object>();
            for (int f = first; f <= last; f++)
            {
                frames.Add(new
                {
                    frame = f,
                    cpu = cpuProps.ToDictionary(p => p, p => ProfilerDriver.GetFormattedCounterValue(f, ProfilerArea.CPU, p)),
                    memory = memProps.ToDictionary(p => p, p => ProfilerDriver.GetFormattedCounterValue(f, ProfilerArea.Memory, p)),
                    rendering = renderProps.ToDictionary(p => p, p => ProfilerDriver.GetFormattedCounterValue(f, ProfilerArea.Rendering, p)),
                });
            }

            return MCPResult.Success(new { frameCount = frames.Count, frames });
        }

        [MCPTool(
            "get_memory_snapshot",
            "Reports total allocated/reserved/Mono memory plus the top N objects by runtime memory size, via " +
            "Profiler.GetRuntimeMemorySizeLong over every currently-loaded object -- a lightweight, core-API " +
            "memory breakdown that doesn't require the optional Memory Profiler package.",
            group: "profiling", readOnly: true)]
        public static MCPResult GetMemorySnapshot(
            MCPToolContext ctx,
            [MCPParam("Number of top memory consumers to report, ordered largest first. Defaults to 20.")] int topCount = 20)
        {
            if (topCount <= 0) return MCPResult.Fail("topCount must be positive.");

            var topConsumers = Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                .Select(o => new { name = o.name, type = o.GetType().Name, sizeBytes = Profiler.GetRuntimeMemorySizeLong(o) })
                .Where(x => x.sizeBytes > 0)
                .OrderByDescending(x => x.sizeBytes)
                .Take(topCount)
                .ToArray();

            return MCPResult.Success(new
            {
                totalAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong(),
                totalReservedBytes = Profiler.GetTotalReservedMemoryLong(),
                totalUnusedReservedBytes = Profiler.GetTotalUnusedReservedMemoryLong(),
                monoUsedBytes = Profiler.GetMonoUsedSizeLong(),
                monoHeapBytes = Profiler.GetMonoHeapSizeLong(),
                topConsumers,
            });
        }

        [MCPTool(
            "get_render_stats",
            "Reads real-time render statistics (draw calls, batches, triangles, vertices, SetPass calls, shadow " +
            "casters, texture memory) from the last rendered frame via UnityStats. Manual Test: these read all " +
            "zero until a real frame has actually rendered (Play Mode or a Game view repaint) -- confirmed via " +
            "live spike, not populated by batchmode/headless Editor activity alone.",
            group: "profiling", readOnly: true)]
        public static MCPResult GetRenderStats(MCPToolContext ctx)
        {
            return MCPResult.Success(new
            {
                batches = UnityStats.dynamicBatches + UnityStats.staticBatches + UnityStats.instancedBatches,
                drawCalls = UnityStats.drawCalls,
                dynamicBatchedDrawCalls = UnityStats.dynamicBatchedDrawCalls,
                staticBatchedDrawCalls = UnityStats.staticBatchedDrawCalls,
                instancedBatchedDrawCalls = UnityStats.instancedBatchedDrawCalls,
                setPassCalls = UnityStats.setPassCalls,
                triangles = UnityStats.triangles,
                vertices = UnityStats.vertices,
                shadowCasters = UnityStats.shadowCasters,
                renderTextureCount = UnityStats.renderTextureCount,
                usedTextureMemorySize = UnityStats.usedTextureMemorySize,
            });
        }
    }
}
