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
            "Returns the active scene's GameObject tree, summary first: by default only the top level, each node with " +
            "its childCount, up to 200 nodes. Expand with path (start at a subtree), depth (more levels; -1 = all) and " +
            "limit (node cap; 0 = none). depth=-1 with limit=0 and no path is the full tree, cached until the scene's " +
            "structure changes.", readOnly: true)]
        public static MCPResult GetHierarchy(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path to start from. Omit for the scene's root objects.")] string path = null,
            [MCPParam("Levels to include below the start: 1 = direct children only (default), -1 = everything.")] int depth = 1,
            [MCPParam("Maximum nodes to return. Defaults to 200; 0 = no cap.")] int limit = 200)
        {
            if (path == null && depth < 0 && limit <= 0)
                return MCPResult.Success(MCPHierarchyCache.GetOrBuild(BuildHierarchyData));

            var budget = new NodeBudget { Left = limit > 0 ? limit : int.MaxValue };
            var scene = SceneManager.GetActiveScene();
            var result = new Dictionary<string, object> { ["scene"] = scene.name };

            if (string.IsNullOrEmpty(path))
            {
                var roots = new List<object>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (budget.Left <= 0) { budget.Truncated = true; break; }
                    roots.Add(BuildLimitedNode(root, depth - 1, budget));
                }
                result["rootCount"] = scene.rootCount;
                result["roots"] = roots;
            }
            else
            {
                var start = MCPSceneUtil.ResolvePath(path);
                if (start == null) return MCPResult.Fail($"Path '{path}' not found.");
                result["node"] = BuildLimitedNode(start, depth, budget);
            }

            result["returnedNodes"] = budget.Used;
            if (budget.Truncated) result["truncated"] = true;
            if (depth >= 0 || budget.Truncated)
                result["hint"] = "Nodes list childCount; expand one with path=<its path>, or raise depth/limit.";
            return MCPResult.Success(result);
        }

        private class NodeBudget
        {
            public int Left;
            public int Used;
            public bool Truncated;
        }

        // depthLeft: 0 = this node only, negative = unlimited.
        private static Dictionary<string, object> BuildLimitedNode(GameObject go, int depthLeft, NodeBudget budget)
        {
            budget.Left--;
            budget.Used++;
            var node = new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["path"] = MCPSceneUtil.GetPath(go),
                ["active"] = go.activeSelf,
                ["childCount"] = go.transform.childCount
            };

            if (depthLeft != 0 && go.transform.childCount > 0)
            {
                var children = new List<object>();
                foreach (Transform child in go.transform)
                {
                    if (budget.Left <= 0) { budget.Truncated = true; break; }
                    children.Add(BuildLimitedNode(child.gameObject, depthLeft - 1, budget));
                }
                node["children"] = children;
            }
            return node;
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

        [MCPTool("get_selected_object", "Returns the hierarchy path of the currently selected GameObject in the Editor, or null if nothing is selected.", readOnly: true)]
        public static MCPResult GetSelectedObject(MCPToolContext ctx)
        {
            var selected = Selection.activeGameObject;
            return MCPResult.Success(new { path = selected != null ? MCPSceneUtil.GetPath(selected) : null });
        }

        [MCPTool("get_console_logs", "Returns the most recent N Unity console log entries captured since the Editor last started (default 20, stack traces left out unless asked for).", MCPLatencyTier.Fast, readOnly: true)]
        public static MCPResult GetConsoleLogs(
            MCPToolContext ctx,
            [MCPParam("How many of the most recent log entries to return. Defaults to 20.")] int count = 20,
            [MCPParam("Include each entry's stack trace. Defaults to false (they are long).")] bool includeStackTrace = false)
        {
            var logs = MCPConsoleCapture.GetRecent(count)
                .Select(l => includeStackTrace
                    ? (object)new { l.type, l.message, l.stackTrace }
                    : new { l.type, l.message })
                .ToList();
            return MCPResult.Success(new { logs });
        }

        [MCPTool("get_project_info", "Returns basic project metadata: Unity version, active scene name/path, and build target.", readOnly: true)]
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
