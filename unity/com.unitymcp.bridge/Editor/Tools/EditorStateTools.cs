using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    // Play Mode control (enter/exit/pause) used to be duplicated here AND in
    // PlayModeTools.cs under different groups ("core" vs "testing"). Both
    // definitions registered the same tool name, so MCPToolRegistry.Rescan()
    // silently dropped whichever one it happened to encounter second (see
    // MCPToolRegistry.Rescan -- assembly/type enumeration order is not
    // guaranteed stable), meaning the ACTIVE implementation of enter_play_mode
    // etc. could flip between the naive version below (returned immediately,
    // before the domain reload a Play Mode transition triggers had settled)
    // and PlayModeTools' version (waits for the transition to fully complete).
    // Whichever tool call landed right after an immediate-return enter_play_mode
    // could hit the bridge mid-teardown, which is one real contributor to "the
    // connection just drops for no obvious reason" reports. Kept only
    // PlayModeTools' wait-for-settle versions; this file now owns only
    // save_project, which was never duplicated.
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
    }
}