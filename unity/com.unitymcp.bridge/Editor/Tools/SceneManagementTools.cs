using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    public enum MCPSceneOpenMode { Single, Additive }

    /// <summary>
    /// Scene Management -- Group B of the tool catalog, new `scene` group. Everything
    /// here operates on the multi-scene APIs (UnityEngine.SceneManagement.SceneManager /
    /// UnityEditor.SceneManagement.EditorSceneManager) rather than assuming a single
    /// always-active scene, since additive scene loading is common in real projects.
    /// </summary>
    public static class SceneManagementTools
    {
        [MCPTool(
            "open_scene",
            "Opens a scene asset by path. Single mode closes all other currently loaded scenes first; Additive loads " +
            "alongside whatever is already open. Triggers a domain reload if any scripts referenced by the scene need " +
            "recompiling.",
            MCPLatencyTier.Slow,
            group: "scene")]
        public static MCPResult OpenScene(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the scene to open, e.g. 'Scenes/Level1.unity'.")] string path,
            [MCPParam("Single closes all other loaded scenes first; Additive loads alongside them. Defaults to Single.")] MCPSceneOpenMode mode = MCPSceneOpenMode.Single)
        {
            if (!TryResolveScenePath(path, out var assetPath, out var fullPath, out var error))
                return MCPResult.Fail(error);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' does not exist.");

            var openMode = mode == MCPSceneOpenMode.Additive ? OpenSceneMode.Additive : OpenSceneMode.Single;

            try
            {
                var scene = EditorSceneManager.OpenScene(assetPath, openMode);
                return MCPResult.Success(new { name = scene.name, path = scene.path });
            }
            catch (Exception e)
            {
                return MCPResult.Fail($"Failed to open scene '{path}': {e.Message}");
            }
        }

        [MCPTool("save_scene", "Saves a loaded scene by name to disk. Omit sceneName to save the active scene.", group: "scene")]
        public static MCPResult SaveScene(
            MCPToolContext ctx,
            [MCPParam("Name of the loaded scene to save. Omit to save the active scene.")] string sceneName = null)
        {
            var scene = string.IsNullOrEmpty(sceneName) ? SceneManager.GetActiveScene() : SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid())
                return MCPResult.Fail($"Scene '{sceneName}' is not currently loaded.");

            bool saved = EditorSceneManager.SaveScene(scene);
            if (!saved)
                return MCPResult.Fail($"Failed to save scene '{scene.name}' (see Console for details).");

            return MCPResult.Success(new { name = scene.name, path = scene.path });
        }

        [MCPTool(
            "create_scene",
            "Creates a new empty scene (with a default Camera and Light, same as File > New Scene) and saves it as an " +
            "asset at the given path. The new scene becomes the active scene. Triggers a domain reload.",
            MCPLatencyTier.Slow,
            group: "scene")]
        public static MCPResult CreateScene(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ for the new scene asset, e.g. 'Scenes/NewLevel.unity'.")] string path,
            [MCPParam("Load alongside currently open scenes instead of replacing them. Defaults to false.")] bool additive = false)
        {
            if (!TryResolveScenePath(path, out var assetPath, out var fullPath, out var error))
                return MCPResult.Fail(error);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            var newSceneMode = additive ? NewSceneMode.Additive : NewSceneMode.Single;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, newSceneMode);

            bool saved = EditorSceneManager.SaveScene(scene, assetPath);
            if (!saved)
                return MCPResult.Fail($"Failed to save new scene to '{path}'.");

            return MCPResult.Success(new { name = scene.name, path = scene.path });
        }

        [MCPTool(
            "close_scene",
            "Unloads an additively loaded scene by name. Fails if it's the only loaded scene (Unity requires at least " +
            "one open) or if it isn't currently loaded.",
            group: "scene")]
        public static MCPResult CloseScene(
            MCPToolContext ctx,
            [MCPParam("Name of the loaded scene to close.")] string sceneName,
            [MCPParam("Also remove it from the Hierarchy entirely, rather than just unloading its objects. Defaults to true.")] bool removeScene = true)
        {
            var scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid())
                return MCPResult.Fail($"Scene '{sceneName}' is not currently loaded.");

            if (SceneManager.sceneCount <= 1)
                return MCPResult.Fail("Cannot close the only loaded scene -- Unity requires at least one scene open.");

            bool closed = EditorSceneManager.CloseScene(scene, removeScene);
            if (!closed)
                return MCPResult.Fail($"Failed to close scene '{sceneName}' (see Console for details).");

            return MCPResult.Success(new { closedScene = sceneName });
        }

        [MCPTool(
            "get_scene_hierarchy",
            "Returns the GameObject hierarchy of a specific loaded scene (any loaded scene, not just the active one) as " +
            "a nested tree, with root-level pagination and an optional depth limit to bound response size for large " +
            "scenes. A node's childCount is always reported even when maxDepth truncates its children, so a truncated " +
            "leaf can be told apart from a genuine one.",
            group: "scene", readOnly: true)]
        public static MCPResult GetSceneHierarchy(
            MCPToolContext ctx,
            [MCPParam("Name of the loaded scene to inspect. Omit to use the active scene.")] string sceneName = null,
            [MCPParam("Index of the first root GameObject to include (0-based). Defaults to 0.")] int offset = 0,
            [MCPParam("Maximum number of root GameObjects to include. Defaults to 50.")] int limit = 50,
            [MCPParam("Maximum depth of children to descend below each root (0 = roots only). Defaults to -1 (unlimited).")] int maxDepth = -1)
        {
            var scene = string.IsNullOrEmpty(sceneName) ? SceneManager.GetActiveScene() : SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid())
                return MCPResult.Fail($"Scene '{sceneName}' is not currently loaded.");

            var allRoots = scene.GetRootGameObjects();
            var page = allRoots.Skip(offset).Take(limit).Select(go => BuildNode(go, maxDepth, 0)).ToList();

            return MCPResult.Success(new
            {
                scene = scene.name,
                totalRootCount = allRoots.Length,
                offset,
                limit,
                roots = page
            });
        }

        private static object BuildNode(GameObject go, int maxDepth, int currentDepth)
        {
            var children = new List<object>();
            if (maxDepth < 0 || currentDepth < maxDepth)
            {
                foreach (Transform child in go.transform)
                    children.Add(BuildNode(child.gameObject, maxDepth, currentDepth + 1));
            }

            return new
            {
                name = go.name,
                path = MCPSceneUtil.GetPath(go),
                active = go.activeSelf,
                childCount = go.transform.childCount,
                children
            };
        }

        [MCPTool(
            "set_active_scene",
            "Sets which loaded scene new GameObjects are instantiated into by default. The scene must already be " +
            "loaded (e.g. via open_scene with Additive mode).",
            group: "scene")]
        public static MCPResult SetActiveScene(
            MCPToolContext ctx,
            [MCPParam("Name of the loaded scene to make active.")] string sceneName)
        {
            var scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid())
                return MCPResult.Fail($"Scene '{sceneName}' is not currently loaded.");

            // UnityEngine.SceneManagement.SceneManager.SetActiveScene is the runtime
            // (Play mode / build) API -- called from the Editor outside Play mode, it can
            // actually change the active scene while still returning false (confirmed
            // against a real Editor instance, not assumed), which would make this tool
            // report failure for a call that actually succeeded. EditorSceneManager's own
            // SetActiveScene is the one written for Editor-context callers; it returns
            // void, so success is confirmed afterward by checking the active scene changed.
            EditorSceneManager.SetActiveScene(scene);

            if (SceneManager.GetActiveScene() != scene)
                return MCPResult.Fail($"Failed to set '{sceneName}' as the active scene.");

            return MCPResult.Success(new { activeScene = sceneName });
        }

        [MCPTool(
            "merge_scenes",
            "Merges one loaded scene's GameObjects into another loaded scene, then unloads the source scene. Both " +
            "scenes must already be loaded. Destructive: the source scene ceases to exist as a separate scene.",
            MCPLatencyTier.Slow,
            destructive: true,
            group: "scene")]
        public static MCPResult MergeScenes(
            MCPToolContext ctx,
            [MCPParam("Name of the loaded scene whose contents will be merged in, and which will be unloaded afterward.")] string sourceSceneName,
            [MCPParam("Name of the loaded scene to merge into.")] string destinationSceneName)
        {
            var source = SceneManager.GetSceneByName(sourceSceneName);
            if (!source.IsValid())
                return MCPResult.Fail($"Source scene '{sourceSceneName}' is not currently loaded.");

            var destination = SceneManager.GetSceneByName(destinationSceneName);
            if (!destination.IsValid())
                return MCPResult.Fail($"Destination scene '{destinationSceneName}' is not currently loaded.");

            if (source == destination)
                return MCPResult.Fail("Source and destination scenes must be different.");

            EditorSceneManager.MergeScenes(source, destination);
            return MCPResult.Success(new { mergedInto = destinationSceneName });
        }

        [MCPTool("list_scenes_in_build", "Lists every scene currently registered in Build Settings, in build order, with its enabled state.", group: "scene", readOnly: true)]
        public static MCPResult ListScenesInBuild(MCPToolContext ctx)
        {
            var scenes = EditorBuildSettings.scenes.Select((s, i) => new
            {
                index = i,
                path = s.path,
                enabled = s.enabled,
                guid = s.guid.ToString()
            }).ToList();

            return MCPResult.Success(new { scenes });
        }

        [MCPTool(
            "add_scene_to_build",
            "Adds a scene asset to Build Settings at a given index (or the end if omitted). Fails if the scene is " +
            "already registered, rather than creating a duplicate entry.",
            group: "scene")]
        public static MCPResult AddSceneToBuild(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the scene to add, e.g. 'Scenes/Level1.unity'.")] string path,
            [MCPParam("Index to insert at. Omit to append at the end.")] int? index = null,
            [MCPParam("Whether the scene is enabled in the build. Defaults to true.")] bool enabled = true)
        {
            if (!TryResolveScenePath(path, out var assetPath, out var fullPath, out var error))
                return MCPResult.Fail(error);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' does not exist.");

            var existing = EditorBuildSettings.scenes.ToList();
            if (existing.Any(s => s.path == assetPath))
                return MCPResult.Fail($"'{assetPath}' is already registered in Build Settings.");

            var newEntry = new EditorBuildSettingsScene(assetPath, enabled);
            int insertAt = index.HasValue ? Mathf.Clamp(index.Value, 0, existing.Count) : existing.Count;
            existing.Insert(insertAt, newEntry);

            EditorBuildSettings.scenes = existing.ToArray();
            return MCPResult.Success(new { path = assetPath, index = insertAt, enabled });
        }

        [MCPTool(
            "get_scene_stats",
            "Reports GameObject/vertex/light/collider counts for a loaded scene -- use to gauge scene complexity before " +
            "a lighting bake, a build, or a performance pass.",
            group: "scene", readOnly: true)]
        public static MCPResult GetSceneStats(
            MCPToolContext ctx,
            [MCPParam("Name of the loaded scene to inspect. Omit to use the active scene.")] string sceneName = null)
        {
            var scene = string.IsNullOrEmpty(sceneName) ? SceneManager.GetActiveScene() : SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid())
                return MCPResult.Fail($"Scene '{sceneName}' is not currently loaded.");

            int gameObjectCount = 0;
            long vertexCount = 0;
            int lightCount = 0;
            int colliderCount = 0;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    gameObjectCount++;
                    var go = t.gameObject;

                    var meshFilter = go.GetComponent<MeshFilter>();
                    if (meshFilter != null && meshFilter.sharedMesh != null)
                        vertexCount += meshFilter.sharedMesh.vertexCount;

                    var skinnedMesh = go.GetComponent<SkinnedMeshRenderer>();
                    if (skinnedMesh != null && skinnedMesh.sharedMesh != null)
                        vertexCount += skinnedMesh.sharedMesh.vertexCount;

                    if (go.GetComponent<Light>() != null) lightCount++;
                    colliderCount += go.GetComponents<Collider>().Length;
                }
            }

            return MCPResult.Success(new
            {
                scene = scene.name,
                gameObjectCount,
                vertexCount,
                lightCount,
                colliderCount
            });
        }

        private static bool TryResolveScenePath(string path, out string assetPath, out string fullPath, out string error)
        {
            assetPath = null;
            fullPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "path must not be empty.";
                return false;
            }

            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                error = "path must end with '.unity'.";
                return false;
            }

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out fullPath, out error))
                return false;

            assetPath = "Assets/" + path.Replace('\\', '/').TrimStart('/');
            return true;
        }
    }
}
