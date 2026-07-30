using System;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Editor Control & Session tools -- Group A of the tool catalog. Session-level
    /// introspection and control (play/edit/compile state, undo/redo, selection, menu
    /// items) that every other module can be built on, rather than each tool re-deriving
    /// "is the Editor busy right now" for itself.
    /// </summary>
    public static class EditorControlTools
    {
        [MCPTool(
            "get_editor_state",
            "Reports the Editor's current state in one call: play/pause/compiling/updating flags, the active scene's " +
            "name/path/dirty flag, and the current selection count. Check this before a sequence of calls that assumes " +
            "a particular mode (e.g. don't call Play-mode-only tools while isCompiling is true).",
            group: "core", readOnly: true)]
        public static MCPResult GetEditorState(MCPToolContext ctx)
        {
            var scene = EditorSceneManager.GetActiveScene();

            return MCPResult.Success(new
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                activeSceneName = scene.name,
                activeScenePath = scene.path,
                activeSceneIsDirty = scene.isDirty,
                selectionCount = Selection.objects.Length
            });
        }

        [MCPTool(
            "execute_menu_item",
            "Invokes any Unity Editor menu item by its exact path (e.g. 'GameObject/3D Object/Cube', 'Edit/Play'). Escape " +
            "hatch for features with no dedicated tool -- but note it fires whatever that menu item actually does, " +
            "including menu items that don't make sense headless (opening a dialog) or destroy state (e.g. discarding " +
            "unsaved changes). No result data beyond confirmation the item was found and invoked; check get_editor_state " +
            "or get_console_logs afterward to see its effect.",
            group: "core")]
        public static MCPResult ExecuteMenuItem(
            MCPToolContext ctx,
            [MCPParam("Exact menu path, e.g. 'GameObject/3D Object/Cube' or 'Edit/Play'.")] string menuPath)
        {
            if (string.IsNullOrWhiteSpace(menuPath))
                return MCPResult.Fail("menuPath must not be empty.");

            bool found = EditorApplication.ExecuteMenuItem(menuPath);
            if (!found)
                return MCPResult.Fail($"Menu item '{menuPath}' was not found, is disabled, or requires a specific context (e.g. an object selected) that isn't currently met.");

            return MCPResult.Success(new { menuPath });
        }

        [MCPTool("undo", "Performs one Editor Undo step (equivalent to Ctrl/Cmd+Z), reverting the most recent recorded operation.", group: "core")]
        public static MCPResult Undo(MCPToolContext ctx)
        {
            var groupBefore = UnityEditor.Undo.GetCurrentGroupName();
            UnityEditor.Undo.PerformUndo();
            return MCPResult.Success(new { undoneGroup = groupBefore });
        }

        [MCPTool("redo", "Performs one Editor Redo step (equivalent to Ctrl/Cmd+Shift+Z), reapplying the most recently undone operation.", group: "core")]
        public static MCPResult Redo(MCPToolContext ctx)
        {
            UnityEditor.Undo.PerformRedo();
            return MCPResult.Success(new { redoneGroup = UnityEditor.Undo.GetCurrentGroupName() });
        }

        [MCPTool(
            "get_undo_stack",
            "Reports the current Undo group's index and name. Unity's public Undo API does not expose the full history " +
            "of past group names (only the current one) -- this is the current position, not a complete stack listing.",
            group: "core", readOnly: true)]
        public static MCPResult GetUndoStack(MCPToolContext ctx)
        {
            return MCPResult.Success(new
            {
                currentGroup = UnityEditor.Undo.GetCurrentGroup(),
                currentGroupName = UnityEditor.Undo.GetCurrentGroupName()
            });
        }

        [MCPTool(
            "set_editor_selection",
            "Sets the Editor's current selection to the given scene GameObjects (by hierarchy path) and/or project " +
            "assets (by path relative to Assets/), replacing whatever was selected before. Pass empty arrays for both to " +
            "clear the selection entirely.",
            group: "core")]
        public static MCPResult SetEditorSelection(
            MCPToolContext ctx,
            [MCPParam("Hierarchy paths of scene GameObjects to select, e.g. [\"Player\", \"Enemies/Goblin1\"]. Omit for none.")] string[] gameObjectPaths = null,
            [MCPParam("Asset paths relative to Assets/ to select, e.g. [\"Materials/Red.mat\"]. Omit for none.")] string[] assetPaths = null)
        {
            var objects = new System.Collections.Generic.List<UnityEngine.Object>();
            var missing = new System.Collections.Generic.List<string>();

            foreach (var path in gameObjectPaths ?? Array.Empty<string>())
            {
                var go = MCPSceneUtil.ResolvePath(path);
                if (go == null) missing.Add(path);
                else objects.Add(go);
            }

            foreach (var assetPath in assetPaths ?? Array.Empty<string>())
            {
                var fullAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
                var asset = AssetDatabase.LoadMainAssetAtPath(fullAssetPath);
                if (asset == null) missing.Add(assetPath);
                else objects.Add(asset);
            }

            if (missing.Count > 0)
                return MCPResult.Fail($"Not found, selection unchanged: {string.Join(", ", missing)}");

            Selection.objects = objects.ToArray();
            return MCPResult.Success(new { selectedCount = objects.Count });
        }

        [MCPTool("get_editor_selection", "Returns the Editor's current selection as scene-object hierarchy paths and/or project-asset paths.", group: "core", readOnly: true)]
        public static MCPResult GetEditorSelection(MCPToolContext ctx)
        {
            var gameObjectPaths = Selection.gameObjects.Select(MCPSceneUtil.GetPath).ToArray();

            var assetPaths = Selection.objects
                .Where(o => !(o is GameObject) && AssetDatabase.Contains(o))
                .Select(AssetDatabase.GetAssetPath)
                .ToArray();

            return MCPResult.Success(new { gameObjectPaths, assetPaths });
        }

        [MCPTool(
            "focus_scene_view",
            "Frames the Scene view camera on a target GameObject by hierarchy path (also selects it, same as " +
            "double-clicking it in the Hierarchy) -- use before capture_scene_view so the screenshot actually shows the " +
            "object being checked.",
            group: "core")]
        public static MCPResult FocusSceneView(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to frame.")] string path,
            [MCPParam("Skip the framing animation and snap instantly. Defaults to true.")] bool instant = true)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return MCPResult.Fail("No active Scene view found. Open a Scene view window in the Editor first.");

            Selection.activeGameObject = go;
            sceneView.FrameSelected(false, instant);

            return MCPResult.Success(new { path });
        }

        [MCPTool(
            "list_unity_instances",
            "Reports this Editor process's own PID/port/project, plus a count of other OS processes that look like Unity " +
            "Editor instances (by process name) currently running on this machine. Cannot report the OTHER instances' " +
            "project paths or ports -- there is no shared registry of running Unity MCP bridges to look them up in; use " +
            "each project's own Setup window (multi-instance conflict detection) for that.",
            group: "core", readOnly: true)]
        public static MCPResult ListUnityInstances(MCPToolContext ctx)
        {
            var current = Process.GetCurrentProcess();
            var others = Process.GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .Select(p => p.Id)
                .ToArray();

            return MCPResult.Success(new
            {
                thisInstance = new
                {
                    pid = current.Id,
                    port = MCPServer.BoundPort,
                    projectRoot = MCPProjectUtil.ProjectRoot
                },
                otherProcessCount = others.Length,
                otherProcessIds = others
            });
        }
    }
}
