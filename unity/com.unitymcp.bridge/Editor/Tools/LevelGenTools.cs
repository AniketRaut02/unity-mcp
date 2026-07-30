using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group X of the tool catalog -- Level Generation &amp; Streaming. All three atomic tools here use core Unity
    /// Editor APIs (LODGroup is core runtime; Unwrapping/StaticOcclusionCulling are core UnityEditor) -- no
    /// optional package. StaticOcclusionCulling.Compute() confirmed via live spike to run synchronously and
    /// complete immediately even in batchmode with an unsaved scene, unlike bake_navmesh/bake_lightmaps which
    /// have a real Unity requirement to save the scene first -- this one doesn't.
    /// </summary>
    public static class LevelGenTools
    {
        [MCPTool(
            "configure_lod_group",
            "Adds/reconfigures a LODGroup on a GameObject: each entry in lodLevels is one LOD level referencing a " +
            "single renderer (the common one-mesh-per-level case).",
            group: "levelgen")]
        public static MCPResult ConfigureLodGroup(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to host the LODGroup.")] string path,
            [MCPParam("LOD levels, ordered highest-detail first, each as \"screenRelativeHeight,rendererPath\", e.g. [\"0.5,LOD0\", \"0.2,LOD1\", \"0.05,LOD2\"]. rendererPath is relative to path.")] string[] lodLevels)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            if (lodLevels == null || lodLevels.Length == 0) return MCPResult.Fail("lodLevels must contain at least one \"screenRelativeHeight,rendererPath\" entry.");

            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup == null) lodGroup = go.AddComponent<LODGroup>();

            var lods = new LOD[lodLevels.Length];
            for (int i = 0; i < lodLevels.Length; i++)
            {
                var parts = lodLevels[i].Split(new[] { ',' }, 2);
                if (parts.Length != 2 || !float.TryParse(parts[0], out var screenHeight))
                    return MCPResult.Fail($"Invalid lodLevels entry '{lodLevels[i]}' -- expected \"screenRelativeHeight,rendererPath\".");

                var rendererGo = MCPSceneUtil.ResolvePath($"{path}/{parts[1]}");
                if (rendererGo == null) return MCPResult.Fail($"Renderer path '{path}/{parts[1]}' not found.");
                var renderer = rendererGo.GetComponent<Renderer>();
                if (renderer == null) return MCPResult.Fail($"GameObject at '{path}/{parts[1]}' has no Renderer.");

                lods[i] = new LOD(screenHeight, new[] { renderer });
            }

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();

            return MCPResult.Success(new { lodCount = lods.Length });
        }

        [MCPTool(
            "generate_lightmap_uvs",
            "Generates a secondary (lightmap) UV set on a Mesh asset via UnityEditor.Unwrapping -- required for " +
            "meshes that will receive baked lightmaps but weren't authored with a non-overlapping UV2 channel.",
            group: "levelgen")]
        public static MCPResult GenerateLightmapUvs(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the Mesh asset, e.g. 'Models/Corridor.fbx' or a standalone .asset mesh.")] string meshAssetPath,
            [MCPParam("Angle (degrees) above which a hard UV seam is placed. Omit for Unity's default.")] float? hardAngle = null,
            [MCPParam("Texel-space packing margin between UV islands, in pixels at a 1024 lightmap. Omit for Unity's default.")] float? packMargin = null)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, meshAssetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath)) return MCPResult.Fail($"'{meshAssetPath}' does not exist.");

            var unityPath = "Assets/" + meshAssetPath.Replace('\\', '/').TrimStart('/');
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(unityPath);
            if (mesh == null) return MCPResult.Fail($"Could not load a Mesh at '{meshAssetPath}'.");

            var settings = new UnwrapParam();
            UnwrapParam.SetDefaults(out settings);
            if (hardAngle.HasValue) settings.hardAngle = hardAngle.Value;
            if (packMargin.HasValue) settings.packMargin = packMargin.Value / 1024f;

            Unwrapping.GenerateSecondaryUVSet(mesh, settings);
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssets();

            return MCPResult.Success(new { uv2Count = mesh.uv2?.Length ?? 0 });
        }

        [MCPTool(
            "bake_occlusion_culling",
            "Bakes static occlusion culling data for the active scene via UnityEditor.StaticOcclusionCulling. " +
            "Only GameObjects with Occluder Static/Occludee Static flags set (see set_gameobject_static) " +
            "contribute -- if none are set, this completes immediately with an empty (harmless) bake.",
            group: "levelgen", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult BakeOcclusionCulling(MCPToolContext ctx)
        {
            bool success = StaticOcclusionCulling.Compute();
            if (!success) return MCPResult.Fail("StaticOcclusionCulling.Compute() returned false.");

            return MCPResult.Success();
        }
    }
}
