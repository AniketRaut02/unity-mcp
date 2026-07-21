using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityMCP;

namespace UnityMCP.Tools
{
    public static class QueryTools
    {
        [MCPTool(
            "get_hierarchy",
            "Returns the full GameObject hierarchy of the active scene as a nested tree. Cached and invalidated on " +
            "structural scene changes (create/delete/reparent) -- repeated calls between such changes are effectively " +
            "free.")]
        public static MCPResult GetHierarchy(MCPToolContext ctx)
        {
            var data = MCPHierarchyCache.GetOrBuild(BuildHierarchyData);
            return MCPResult.Success(data);
        }

        private static object BuildHierarchyData()
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects().Select(BuildNode).ToList();
            return new { scene = scene.name, roots };
        }

        private static object BuildNode(GameObject go)
        {
            var children = new List<object>();
            foreach (Transform child in go.transform)
                children.Add(BuildNode(child.gameObject));

            return new
            {
                name = go.name,
                path = MCPSceneUtil.GetPath(go),
                active = go.activeSelf,
                children
            };
        }

        [MCPTool("get_selected_object", "Returns the hierarchy path of the currently selected GameObject in the Editor, or null if nothing is selected.")]
        public static MCPResult GetSelectedObject(MCPToolContext ctx)
        {
            var selected = Selection.activeGameObject;
            return MCPResult.Success(new { path = selected != null ? MCPSceneUtil.GetPath(selected) : null });
        }

        [MCPTool("get_console_logs", "Returns the most recent N Unity console log entries captured since the Editor last started.", MCPLatencyTier.Fast)]
        public static MCPResult GetConsoleLogs(
            MCPToolContext ctx,
            [MCPParam("How many of the most recent log entries to return.")] int count = 50)
        {
            var logs = MCPConsoleCapture.GetRecent(count);
            return MCPResult.Success(new { logs });
        }

        [MCPTool("get_project_info", "Returns basic project metadata: Unity version, active scene name/path, and build target.")]
        public static MCPResult GetProjectInfo(MCPToolContext ctx)
        {
            return MCPResult.Success(new
            {
                unityVersion = Application.unityVersion,
                activeScene = SceneManager.GetActiveScene().name,
                scenePath = SceneManager.GetActiveScene().path,
                buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString()
            });
        }
    }
}
