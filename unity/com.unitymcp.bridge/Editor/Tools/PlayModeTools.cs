using UnityEditor;
using UnityMCP;
using UnityMCP.Support;

namespace UnityMCP.Tools
{
    public static class PlayModeTools
    {
        [MCPTool("enter_play_mode", "Starts Play mode and waits for the transition (including any domain reload) to fully settle before returning.", group: "testing", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult EnterPlayMode(
            MCPToolContext ctx,
            [MCPParam("Maximum time to wait for the transition to complete, in seconds. Defaults to 30.")] float timeoutSeconds = 30f)
        {
            if (EditorApplication.isPlaying)
                return MCPResult.Success(new { alreadyPlaying = true });

            var start = EditorApplication.timeSinceStartup;
            EditorApplication.isPlaying = true;

            if (!MCPPlayModeUtil.WaitForState(targetIsPlaying: true, timeoutSeconds))
                return MCPResult.Fail($"Timed out after {timeoutSeconds}s entering Play mode.");

            return MCPResult.Success(new { waitedSeconds = EditorApplication.timeSinceStartup - start });
        }

        [MCPTool("exit_play_mode", "Stops Play mode and waits for the transition back to Edit mode to fully settle before returning.", group: "testing", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult ExitPlayMode(
            MCPToolContext ctx,
            [MCPParam("Maximum time to wait for the transition to complete, in seconds. Defaults to 30.")] float timeoutSeconds = 30f)
        {
            if (!EditorApplication.isPlaying)
                return MCPResult.Success(new { alreadyStopped = true });

            var start = EditorApplication.timeSinceStartup;
            EditorApplication.isPlaying = false;

            if (!MCPPlayModeUtil.WaitForState(targetIsPlaying: false, timeoutSeconds))
                return MCPResult.Fail($"Timed out after {timeoutSeconds}s exiting Play mode.");

            return MCPResult.Success(new { waitedSeconds = EditorApplication.timeSinceStartup - start });
        }

        [MCPTool("pause_play_mode", "Pauses or resumes Play mode. Fails clearly if the Editor isn't currently in Play mode.", group: "testing")]
        public static MCPResult PausePlayMode(
            MCPToolContext ctx,
            [MCPParam("true to pause, false to resume.")] bool paused = true)
        {
            if (!EditorApplication.isPlaying)
                return MCPResult.Fail("Not in Play mode — nothing to pause or resume.");

            EditorApplication.isPaused = paused;

            return MCPResult.Success(new { isPaused = EditorApplication.isPaused });
        }
    }
}
