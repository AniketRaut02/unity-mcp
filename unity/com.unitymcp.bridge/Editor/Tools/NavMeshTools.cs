using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityMCP;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group O of the tool catalog -- NavMesh &amp; Navigation.
    ///
    /// Two real API-limitation findings from live spikes against a real Unity Editor drove this file's design,
    /// documented here once rather than repeated per-tool:
    ///
    ///   1. There is no public API to modify an EXISTING agent type's build settings (radius/height/slope/step) --
    ///      NavMesh.CreateSettings() only returns a new NavMeshBuildSettings struct with fixed default values, and
    ///      mutating that struct's fields is a silent no-op (confirmed: reading the settings back by ID afterward
    ///      shows the original, unmodified values). The Navigation window's "Agents" tab presumably uses a
    ///      non-public internal API to persist this. So configure_navmesh_settings below stores its values as
    ///      this MCP server's own session defaults (used by bake_navmesh_volume), not as a real edit to Unity's
    ///      built-in agent type registry.
    ///   2. Baking requires the active scene to already be saved to disk -- same requirement LightingTools.cs's
    ///      bake_lightmaps found for lightmap baking, confirmed by the exact same kind of live spike. Here it's
    ///      also a practical one: the baked NavMeshData is written as an asset next to the scene file.
    ///
    /// As of package 1.28.0 the scene-authoring tools here target the com.unity.ai.navigation package
    /// (Unity.AI.Navigation): bake_navmesh drives NavMeshSurface, create_offmesh_link creates a NavMeshLink, and
    /// mark_navmesh_area adds a NavMeshModifier. The legacy equivalents they replaced (UnityEditor.AI.NavMeshBuilder,
    /// OffMeshLink, GameObjectUtility.SetNavMeshArea) are all deprecated in Unity 6 and emit CS0618.
    ///
    /// bake_navmesh_volume is the exception and deliberately still uses the RUNTIME
    /// UnityEngine.AI.NavMeshBuilder.BuildNavMeshData() -- that API is NOT deprecated (it's the same mechanism
    /// NavMeshSurface itself is built on), and it takes an explicit NavMeshBuildSettings by value, so custom
    /// radius/height/slope/step genuinely do apply per call, with no registry-lookup indirection.
    /// </summary>
    public static class NavMeshTools
    {
        [MCPTool(
            "bake_navmesh",
            "Bakes the navigation mesh for the active scene via the AI Navigation package's NavMeshSurface " +
            "components. Bakes every NavMeshSurface already in the scene; if the scene has none, creates a single " +
            "one collecting all objects, which reproduces the old scene-wide bake. The baked NavMeshData is saved " +
            "as an asset next to the scene file, so the active scene must already be saved.",
            group: "navmesh", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult BakeNavMesh(MCPToolContext ctx)
        {
            var activeScene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(activeScene.path))
                return MCPResult.Fail("The active scene hasn't been saved yet. Call save_scene (or create_scene) first -- the baked NavMesh is written as an asset next to the scene file, so Unity needs a saved scene.");

            var surfaces = UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None);
            bool createdSurface = false;

            if (surfaces.Length == 0)
            {
                var surfaceGo = new GameObject("NavMesh Surface");
                Undo.RegisterCreatedObjectUndo(surfaceGo, "MCP: Create NavMesh Surface");
                var surface = Undo.AddComponent<NavMeshSurface>(surfaceGo);
                surface.collectObjects = CollectObjects.All;
                surfaces = new[] { surface };
                createdSurface = true;
            }

            var baked = new List<string>();
            foreach (var surface in surfaces)
            {
                try
                {
                    surface.BuildNavMesh();
                }
                catch (Exception e)
                {
                    return MCPResult.Fail($"NavMeshSurface.BuildNavMesh() threw on '{MCPSceneUtil.GetPath(surface.gameObject)}': {e.Message}");
                }

                PersistNavMeshData(surface, activeScene);
                baked.Add(MCPSceneUtil.GetPath(surface.gameObject));
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(activeScene);

            var triangulation = NavMesh.CalculateTriangulation();
            return MCPResult.Success(new
            {
                surfacesBaked = baked,
                createdSurface,
                vertexCount = triangulation.vertices.Length,
                triangleCount = triangulation.indices.Length / 3,
            });
        }

        /// <summary>
        /// Writes a freshly baked surface's NavMeshData to disk if it's still an in-memory object. NavMeshSurface
        /// builds into memory; without this the bake is lost on scene reload, since a scene can only reference
        /// NavMeshData that exists as an asset. Mirrors where Unity's own Navigation window puts it:
        /// &lt;scene folder&gt;/&lt;scene name&gt;/NavMesh-&lt;surface name&gt;.asset.
        /// </summary>
        private static void PersistNavMeshData(NavMeshSurface surface, Scene scene)
        {
            var data = surface.navMeshData;
            if (data == null) return;

            if (AssetDatabase.Contains(data))
            {
                EditorUtility.SetDirty(data);
                return;
            }

            var sceneFolder = Path.Combine(
                Path.GetDirectoryName(scene.path) ?? "Assets",
                Path.GetFileNameWithoutExtension(scene.path)).Replace('\\', '/');
            if (!Directory.Exists(sceneFolder))
            {
                Directory.CreateDirectory(sceneFolder);
                AssetDatabase.Refresh();
            }

            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{sceneFolder}/NavMesh-{surface.name}.asset");
            AssetDatabase.CreateAsset(data, assetPath);
            EditorUtility.SetDirty(surface);
        }

        private static float _defaultAgentRadius = 0.5f;
        private static float _defaultAgentHeight = 2f;
        private static float _defaultAgentSlope = 45f;
        private static float _defaultAgentStepHeight = 0.4f;

        [MCPTool(
            "configure_navmesh_settings",
            "Sets this MCP server's session-default agent radius/height/max-slope/step-height, used by " +
            "bake_navmesh_volume whenever its own parameters are omitted. Note: Unity has no public scripting API " +
            "to modify an EXISTING agent type's settings (e.g. the built-in 'Humanoid' type baked by bake_navmesh) " +
            "-- confirmed via reflection, not a limitation of this tool -- so this does not affect bake_navmesh.",
            group: "navmesh")]
        public static MCPResult ConfigureNavMeshSettings(
            MCPToolContext ctx,
            [MCPParam("Default agent radius in meters. Omit to leave unchanged.")] float? radius = null,
            [MCPParam("Default agent height in meters. Omit to leave unchanged.")] float? height = null,
            [MCPParam("Default maximum traversable slope in degrees. Omit to leave unchanged.")] float? maxSlope = null,
            [MCPParam("Default maximum step/ledge height in meters. Omit to leave unchanged.")] float? stepHeight = null)
        {
            if (radius.HasValue) _defaultAgentRadius = radius.Value;
            if (height.HasValue) _defaultAgentHeight = height.Value;
            if (maxSlope.HasValue) _defaultAgentSlope = maxSlope.Value;
            if (stepHeight.HasValue) _defaultAgentStepHeight = stepHeight.Value;

            return MCPResult.Success(new
            {
                radius = _defaultAgentRadius,
                height = _defaultAgentHeight,
                maxSlope = _defaultAgentSlope,
                stepHeight = _defaultAgentStepHeight,
            });
        }

        [MCPTool("add_navmesh_agent", "Adds and configures a NavMeshAgent on a GameObject.", group: "navmesh")]
        public static MCPResult AddNavMeshAgent(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Agent radius in meters. Omit for Unity's default (0.5).")] float? radius = null,
            [MCPParam("Agent height in meters. Omit for Unity's default (2).")] float? height = null,
            [MCPParam("Movement speed. Omit for Unity's default.")] float? speed = null,
            [MCPParam("Angular speed in degrees/second. Omit for Unity's default.")] float? angularSpeed = null,
            [MCPParam("Acceleration. Omit for Unity's default.")] float? acceleration = null,
            [MCPParam("Distance from the destination at which the agent is considered to have arrived. Omit for Unity's default.")] float? stoppingDistance = null,
            [MCPParam("Whether the agent slows down automatically as it approaches its destination. Omit for Unity's default.")] bool? autoBraking = null,
            [MCPParam("NavMesh area names this agent can traverse. Omit to leave the default (all areas).")] string[] areaNames = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null) agent = go.AddComponent<NavMeshAgent>();

            if (radius.HasValue) agent.radius = radius.Value;
            if (height.HasValue) agent.height = height.Value;
            if (speed.HasValue) agent.speed = speed.Value;
            if (angularSpeed.HasValue) agent.angularSpeed = angularSpeed.Value;
            if (acceleration.HasValue) agent.acceleration = acceleration.Value;
            if (stoppingDistance.HasValue) agent.stoppingDistance = stoppingDistance.Value;
            if (autoBraking.HasValue) agent.autoBraking = autoBraking.Value;

            if (areaNames != null)
            {
                int mask = 0;
                foreach (var areaName in areaNames)
                {
                    int area = NavMesh.GetAreaFromName(areaName);
                    if (area < 0) return MCPResult.Fail($"NavMesh area '{areaName}' does not exist. Use define_navmesh_area first, or check spelling.");
                    mask |= 1 << area;
                }
                agent.areaMask = mask;
            }

            return MCPResult.Success();
        }

        [MCPTool(
            "set_agent_destination",
            "Commands a NavMeshAgent to path toward a world-space point. Use this both to actually move an agent " +
            "and as a reachability test -- pathStatus reports whether a complete path was found.",
            group: "navmesh", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult SetAgentDestination(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the NavMeshAgent.")] string path,
            [MCPParam("Destination world-space X.")] float x,
            [MCPParam("Destination world-space Y.")] float y,
            [MCPParam("Destination world-space Z.")] float z)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null) return MCPResult.Fail($"GameObject at '{path}' has no NavMeshAgent component.");

            var destination = new Vector3(x, y, z);
            bool accepted = agent.SetDestination(destination);

            return MCPResult.Success(new
            {
                accepted,
                pathStatus = agent.pathStatus.ToString(),
                pathPending = agent.pathPending,
                remainingDistance = agent.pathPending ? (float?)null : agent.remainingDistance,
            });
        }

        [MCPTool("add_navmesh_obstacle", "Adds and configures a NavMeshObstacle (a dynamic blocker that can carve a hole in the baked NavMesh).", group: "navmesh")]
        public static MCPResult AddNavMeshObstacle(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Box or Capsule. Omit for Unity's default (Capsule).")] NavMeshObstacleShape? shape = null,
            [MCPParam("Radius, Capsule shape only. Omit for Unity's default.")] float? radius = null,
            [MCPParam("Height, Capsule shape only. Omit for Unity's default.")] float? height = null,
            [MCPParam("Box size X, Box shape only. Omit for Unity's default.")] float? sizeX = null,
            [MCPParam("Box size Y, Box shape only. Omit for Unity's default.")] float? sizeY = null,
            [MCPParam("Box size Z, Box shape only. Omit for Unity's default.")] float? sizeZ = null,
            [MCPParam("Whether this obstacle carves a hole in the NavMesh while stationary. Defaults to false (Unity's default).")] bool carving = false)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var obstacle = go.GetComponent<NavMeshObstacle>();
            if (obstacle == null) obstacle = go.AddComponent<NavMeshObstacle>();

            if (shape.HasValue) obstacle.shape = shape.Value;
            if (radius.HasValue) obstacle.radius = radius.Value;
            if (height.HasValue) obstacle.height = height.Value;
            if (sizeX.HasValue || sizeY.HasValue || sizeZ.HasValue)
            {
                var size = obstacle.size;
                if (sizeX.HasValue) size.x = sizeX.Value;
                if (sizeY.HasValue) size.y = sizeY.Value;
                if (sizeZ.HasValue) size.z = sizeZ.Value;
                obstacle.size = size;
            }
            obstacle.carving = carving;

            return MCPResult.Success();
        }

        [MCPTool(
            "create_offmesh_link",
            "Creates a new GameObject with a NavMeshLink connecting two points -- for jump/climb/vault gaps a baked " +
            "NavMesh alone can't cross. (Creates the AI Navigation package's NavMeshLink; the legacy OffMeshLink " +
            "component it replaced is deprecated in Unity 6. The tool name is unchanged for compatibility.)",
            group: "navmesh")]
        public static MCPResult CreateOffMeshLink(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'NavMeshLink'.")] string name = null,
            [MCPParam("Hierarchy path of the GameObject marking the link's start point.")] string startPath = null,
            [MCPParam("Hierarchy path of the GameObject marking the link's end point.")] string endPath = null,
            [MCPParam("Whether an agent can traverse the link in either direction. Defaults to true (Unity's default).")] bool biDirectional = true,
            [MCPParam("Traversal cost modifier. Omit for Unity's default (-1, meaning 'use the agent's default').")] float costOverride = -1f,
            [MCPParam("Whether the link is enabled. Defaults to true (Unity's default).")] bool activated = true,
            [MCPParam("Width of the link in meters. Defaults to 0, meaning a single-file line between the two points.")] float width = 0f)
        {
            if (string.IsNullOrEmpty(startPath) || string.IsNullOrEmpty(endPath))
                return MCPResult.Fail("startPath and endPath are both required.");

            var startGo = MCPSceneUtil.ResolvePath(startPath);
            if (startGo == null) return MCPResult.Fail($"startPath '{startPath}' not found.");
            var endGo = MCPSceneUtil.ResolvePath(endPath);
            if (endGo == null) return MCPResult.Fail($"endPath '{endPath}' not found.");

            var go = new GameObject(string.IsNullOrEmpty(name) ? "NavMeshLink" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create NavMeshLink");
            go.transform.position = startGo.transform.position;

            var link = Undo.AddComponent<NavMeshLink>(go);
            link.startTransform = startGo.transform;
            link.endTransform = endGo.transform;
            link.bidirectional = biDirectional;
            link.costModifier = costOverride;
            link.activated = activated;
            link.width = width;
            // startTransform/endTransform are only resolved into the actual link endpoints on
            // UpdateLink(); without this the link stays at its default zero-length placement.
            link.UpdateLink();

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool(
            "define_navmesh_area",
            "Creates or updates a named NavMesh area type with a traversal cost, stored in " +
            "ProjectSettings/NavMeshAreas.asset (the same file the Navigation window's Areas tab edits). Up to 29 " +
            "custom areas are supported alongside the 3 built-in ones (Walkable/Not Walkable/Jump).",
            group: "navmesh")]
        public static MCPResult DefineNavMeshArea(
            MCPToolContext ctx,
            [MCPParam("Area name.")] string name,
            [MCPParam("Traversal cost multiplier (1 = normal, higher = avoided when a cheaper path exists). Defaults to 1.")] float cost = 1f)
        {
            if (string.IsNullOrWhiteSpace(name))
                return MCPResult.Fail("name must not be empty.");

            var existingIndex = NavMesh.GetAreaFromName(name);
            if (existingIndex >= 0)
            {
                NavMesh.SetAreaCost(existingIndex, cost);
                return MCPResult.Success(new { name, index = existingIndex, cost, updated = true });
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset");
            if (assets.Length == 0)
                return MCPResult.Fail("Could not load ProjectSettings/NavMeshAreas.asset.");

            var serializedSettings = new SerializedObject(assets[0]);
            var areas = serializedSettings.FindProperty("areas");
            if (areas == null)
                return MCPResult.Fail("Could not find the 'areas' property on NavMeshAreas.asset -- this Unity version's format may have changed.");

            for (int i = 3; i < areas.arraySize; i++)
            {
                var nameProp = areas.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (string.IsNullOrEmpty(nameProp.stringValue))
                {
                    nameProp.stringValue = name;
                    areas.GetArrayElementAtIndex(i).FindPropertyRelative("cost").floatValue = cost;
                    serializedSettings.ApplyModifiedProperties();
                    AssetDatabase.SaveAssets();
                    return MCPResult.Success(new { name, index = i, cost, updated = false });
                }
            }

            return MCPResult.Fail("All 32 NavMesh area slots are already in use.");
        }

        [MCPTool(
            "mark_navmesh_area",
            "Sets a GameObject's NavMesh area type (used when baking, to mark e.g. slow terrain or hazards). " +
            "Applies to children too by default, matching how area painting is normally used on a whole prop. " +
            "(Adds the AI Navigation package's NavMeshModifier; the legacy GameObjectUtility.SetNavMeshArea it " +
            "replaced is deprecated in Unity 6. Note this now adds a component rather than setting a static flag, " +
            "so calling it again on the same GameObject updates that component instead of stacking duplicates.)",
            group: "navmesh")]
        public static MCPResult MarkNavMeshArea(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("NavMesh area name, e.g. 'Walkable', 'Not Walkable', 'Jump', or a name from define_navmesh_area.")] string areaName,
            [MCPParam("Whether to also mark every child GameObject. Defaults to true.")] bool includeChildren = true)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            int area = NavMesh.GetAreaFromName(areaName);
            if (area < 0) return MCPResult.Fail($"NavMesh area '{areaName}' does not exist.");

            var modifier = go.GetComponent<NavMeshModifier>();
            if (modifier == null) modifier = Undo.AddComponent<NavMeshModifier>(go);
            else Undo.RecordObject(modifier, "MCP: Mark NavMesh Area");

            modifier.overrideArea = true;
            modifier.area = area;
            modifier.applyToChildren = includeChildren;
            EditorUtility.SetDirty(modifier);

            // One modifier now covers the whole subtree, so report the subtree size rather than a
            // per-GameObject write count -- the number callers actually care about is unchanged.
            int affected = includeChildren ? go.GetComponentsInChildren<Transform>(true).Length : 1;

            return MCPResult.Success(new { areaName, areaIndex = area, gameObjectsMarked = affected, appliedToChildren = includeChildren });
        }

        [MCPTool("sample_navmesh", "Finds the nearest valid point on the baked NavMesh to a given world-space position.", group: "navmesh", readOnly: true)]
        public static MCPResult SampleNavMesh(
            MCPToolContext ctx,
            [MCPParam("Query world-space X.")] float x,
            [MCPParam("Query world-space Y.")] float y,
            [MCPParam("Query world-space Z.")] float z,
            [MCPParam("Maximum search distance in meters. Defaults to 1.")] float maxDistance = 1f,
            [MCPParam("NavMesh area names to search. Omit to search all areas.")] string[] areaNames = null)
        {
            int areaMask = NavMesh.AllAreas;
            if (areaNames != null)
            {
                areaMask = 0;
                foreach (var areaName in areaNames)
                {
                    int area = NavMesh.GetAreaFromName(areaName);
                    if (area < 0) return MCPResult.Fail($"NavMesh area '{areaName}' does not exist.");
                    areaMask |= 1 << area;
                }
            }

            bool found = NavMesh.SamplePosition(new Vector3(x, y, z), out var hit, maxDistance, areaMask);
            if (!found) return MCPResult.Success(new { found = false });

            return MCPResult.Success(new
            {
                found = true,
                position = new { x = hit.position.x, y = hit.position.y, z = hit.position.z },
                distance = hit.distance,
                mask = hit.mask,
            });
        }

        private static readonly Dictionary<string, NavMeshDataInstance> _bakedVolumes = new Dictionary<string, NavMeshDataInstance>();

        [MCPTool(
            "bake_navmesh_volume",
            "Bakes a local NavMesh volume at runtime/edit-time from real scene geometry within a given bounds box -- " +
            "for procedural levels, or areas that shouldn't wait for a full-scene bake_navmesh. Uses " +
            "UnityEngine.AI.NavMeshBuilder.BuildNavMeshData() directly (the same mechanism Unity's own NavMeshSurface " +
            "component is built on), so custom agent radius/height/slope/step genuinely apply per call -- unlike " +
            "bake_navmesh, which is limited to Unity's built-in, non-scriptable agent type settings. Calling again " +
            "with the same volumeId replaces the previous bake for that ID rather than accumulating duplicates.",
            group: "navmesh", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult BakeNavMeshVolume(
            MCPToolContext ctx,
            [MCPParam("Bounds center X.")] float centerX,
            [MCPParam("Bounds center Y.")] float centerY,
            [MCPParam("Bounds center Z.")] float centerZ,
            [MCPParam("Bounds size X.")] float sizeX,
            [MCPParam("Bounds size Y.")] float sizeY,
            [MCPParam("Bounds size Z.")] float sizeZ,
            [MCPParam("Identifier for this volume. Reusing the same ID replaces the previous bake instead of adding a duplicate. Defaults to 'default'.")] string volumeId = "default",
            [MCPParam("Layers to collect geometry from. Omit to collect from every layer.")] string[] layerNames = null,
            [MCPParam("Agent radius. Omit to use the configure_navmesh_settings default.")] float? agentRadius = null,
            [MCPParam("Agent height. Omit to use the configure_navmesh_settings default.")] float? agentHeight = null,
            [MCPParam("Maximum traversable slope in degrees. Omit to use the configure_navmesh_settings default.")] float? agentSlope = null,
            [MCPParam("Maximum step/ledge height. Omit to use the configure_navmesh_settings default.")] float? agentStepHeight = null)
        {
            int layerMask = ~0;
            if (layerNames != null)
            {
                layerMask = 0;
                foreach (var layerName in layerNames)
                {
                    int layer = LayerMask.NameToLayer(layerName);
                    if (layer < 0) return MCPResult.Fail($"Layer '{layerName}' does not exist.");
                    layerMask |= 1 << layer;
                }
            }

            var bounds = new Bounds(new Vector3(centerX, centerY, centerZ), new Vector3(sizeX, sizeY, sizeZ));
            var sources = new List<NavMeshBuildSource>();
            UnityEngine.AI.NavMeshBuilder.CollectSources(bounds, layerMask, NavMeshCollectGeometry.RenderMeshes, 0, new List<NavMeshBuildMarkup>(), sources);

            var settings = new NavMeshBuildSettings
            {
                agentTypeID = 0,
                agentRadius = agentRadius ?? _defaultAgentRadius,
                agentHeight = agentHeight ?? _defaultAgentHeight,
                agentSlope = agentSlope ?? _defaultAgentSlope,
                agentClimb = agentStepHeight ?? _defaultAgentStepHeight,
            };

            NavMeshData navMeshData;
            try
            {
                navMeshData = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            }
            catch (Exception e)
            {
                return MCPResult.Fail($"BuildNavMeshData() threw: {e.Message}");
            }

            if (navMeshData == null)
                return MCPResult.Fail("BuildNavMeshData() returned null -- check the Console for details.");

            if (_bakedVolumes.TryGetValue(volumeId, out var previousInstance))
                NavMesh.RemoveNavMeshData(previousInstance);

            _bakedVolumes[volumeId] = NavMesh.AddNavMeshData(navMeshData);

            return MCPResult.Success(new { volumeId, sourceCount = sources.Count });
        }
    }
}
