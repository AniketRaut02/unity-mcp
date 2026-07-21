using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    public static class EditorStateTools
    {
        [MCPTool(
            "save_project",
            "Saves all modified assets to disk and saves all currently open scenes. Equivalent to pressing Ctrl/Cmd+S.",
            group: "core"
        )]
        public static MCPResult SaveProject(MCPToolContext ctx)
        {
            // Save modified assets in the project
            AssetDatabase.SaveAssets();
            
            // Save all open and modified scenes
            bool scenesSaved = EditorSceneManager.SaveOpenScenes();

            if (!scenesSaved)
            {
                return MCPResult.Fail("Failed to save one or more open scenes.");
            }

            return MCPResult.Success();
        }

        [MCPTool(
            "enter_play_mode",
            "Enters Play Mode in the Unity Editor. Use this to start simulating the game.",
            group: "core"
        )]
        public static MCPResult EnterPlayMode(MCPToolContext ctx)
        {
            if (EditorApplication.isPlaying)
            {
                return MCPResult.Fail("The Unity Editor is already in Play Mode.");
            }

            EditorApplication.isPlaying = true;
            return MCPResult.Success(new { state = "Playing" });
        }

        [MCPTool(
            "exit_play_mode",
            "Exits Play Mode in the Unity Editor, returning to Edit Mode.",
            group: "core"
        )]
        public static MCPResult ExitPlayMode(MCPToolContext ctx)
        {
            if (!EditorApplication.isPlaying)
            {
                return MCPResult.Fail("The Unity Editor is not currently in Play Mode.");
            }

            EditorApplication.isPlaying = false;
            return MCPResult.Success(new { state = "Edit Mode" });
        }

        [MCPTool(
            "pause_play_mode",
            "Toggles or sets the pause state of the Unity Editor. Highly useful for freezing the scene during Play Mode to inspect values.",
            group: "core"
        )]
        public static MCPResult PausePlayMode(
            MCPToolContext ctx,
            [MCPParam("Set to true to pause the Editor, or false to unpause. Omit to simply toggle the current state.")] bool? pause = null)
        {
            if (pause.HasValue)
            {
                EditorApplication.isPaused = pause.Value;
            }
            else
            {
                EditorApplication.isPaused = !EditorApplication.isPaused;
            }

            return MCPResult.Success(new { isPaused = EditorApplication.isPaused });
        }
    }
}