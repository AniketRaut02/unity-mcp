using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>Group E of the tool catalog -- Prefab Mode, variants, and instance-override management, extending AssetTools.cs's create_prefab/instantiate_prefab.</summary>
    public static class PrefabTools
    {
        [MCPTool(
            "create_prefab_variant",
            "Creates a Prefab Variant of an existing prefab -- a new prefab asset that inherits from the base and only " +
            "stores the differences from it. basePath and variantPath are both relative to Assets/.",
            MCPLatencyTier.Slow,
            group: "assets")]
        public static MCPResult CreatePrefabVariant(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the existing base prefab, e.g. 'Prefabs/Enemy.prefab'.")] string basePath,
            [MCPParam("Path relative to Assets/ for the new variant, e.g. 'Prefabs/EnemyElite.prefab'.")] string variantPath)
        {
            if (!TryLoadPrefabAsset(basePath, out var basePrefab, out var baseError))
                return MCPResult.Fail(baseError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, variantPath, out var variantFullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(variantFullPath))
                return MCPResult.Fail($"'{variantPath}' already exists.");

            var variantAssetPath = "Assets/" + variantPath.Replace('\\', '/').TrimStart('/');
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(variantFullPath));
                var variant = PrefabUtility.SaveAsPrefabAsset(instance, variantAssetPath);
                if (variant == null)
                    return MCPResult.Fail($"Failed to save variant to '{variantPath}'.");

                bool isActuallyVariant = PrefabUtility.GetPrefabAssetType(variant) == PrefabAssetType.Variant;
                return MCPResult.Success(new { path = variantPath, isVariant = isActuallyVariant });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [MCPTool(
            "open_prefab_mode",
            "Opens a prefab asset in isolated Prefab Mode for editing. While open, other scene tools (create_gameobject, " +
            "add_component, set_transform, etc.) operate on the prefab's own contents, addressed by hierarchy path " +
            "starting from the prefab's root. Call close_prefab_mode when done to save and return to the main scene.",
            group: "assets")]
        public static MCPResult OpenPrefabMode(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the prefab to edit, e.g. 'Prefabs/Enemy.prefab'.")] string path)
        {
            if (!TryLoadPrefabAsset(path, out _, out var loadError))
                return MCPResult.Fail(loadError);

            var assetPath = "Assets/" + path.Replace('\\', '/').TrimStart('/');
            var stage = PrefabStageUtility.OpenPrefab(assetPath);
            if (stage == null)
                return MCPResult.Fail($"Could not open '{path}' in Prefab Mode.");

            return MCPResult.Success(new { path = stage.assetPath, rootName = stage.prefabContentsRoot.name });
        }

        [MCPTool(
            "close_prefab_mode",
            "Exits Prefab Mode (if currently open), saving changes back to the prefab asset by default, and returns to " +
            "the main scene view.",
            group: "assets")]
        public static MCPResult ClosePrefabMode(
            MCPToolContext ctx,
            [MCPParam("Save changes to the prefab asset before closing. Defaults to true.")] bool save = true)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return MCPResult.Fail("No Prefab Mode is currently open.");

            var assetPath = stage.assetPath;
            if (save)
            {
                var saved = PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
                if (saved == null)
                    return MCPResult.Fail($"Failed to save changes to '{assetPath}'.");
            }

            StageUtility.GoToMainStage();
            return MCPResult.Success(new { path = assetPath, saved = save });
        }

        [MCPTool(
            "apply_prefab_overrides",
            "Applies a prefab instance's overrides back to its source prefab asset, so every other instance picks up " +
            "the change too. Fails if the GameObject isn't part of a prefab instance.",
            destructive: true,
            group: "assets")]
        public static MCPResult ApplyPrefabOverrides(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the prefab instance (or any GameObject within it).")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return MCPResult.Fail($"GameObject at '{path}' is not part of a prefab instance.");

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);

            return MCPResult.Success();
        }

        [MCPTool(
            "revert_prefab_overrides",
            "Reverts a prefab instance's overrides back to the source prefab's defaults, discarding every " +
            "instance-specific change. Fails if the GameObject isn't part of a prefab instance.",
            destructive: true,
            group: "assets")]
        public static MCPResult RevertPrefabOverrides(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the prefab instance (or any GameObject within it).")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return MCPResult.Fail($"GameObject at '{path}' is not part of a prefab instance.");

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);

            return MCPResult.Success();
        }

        [MCPTool(
            "get_prefab_overrides",
            "Lists a prefab instance's overrides relative to its source prefab: added/removed components, added " +
            "GameObjects, and which objects have modified properties, plus the source prefab's own asset path. Fails " +
            "if the GameObject isn't part of a prefab instance.",
            group: "assets", readOnly: true)]
        public static MCPResult GetPrefabOverrides(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the prefab instance (or any GameObject within it).")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return MCPResult.Fail($"GameObject at '{path}' is not part of a prefab instance.");

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
            var sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;

            var addedComponents = PrefabUtility.GetAddedComponents(root)
                .Select(a => new { path = MCPSceneUtil.GetPath(a.instanceComponent.gameObject), type = a.instanceComponent.GetType().FullName })
                .ToList();

            var addedGameObjects = PrefabUtility.GetAddedGameObjects(root)
                .Select(a => MCPSceneUtil.GetPath(a.instanceGameObject))
                .ToList();

            var removedComponents = PrefabUtility.GetRemovedComponents(root)
                .Select(r => new { path = MCPSceneUtil.GetPath(r.containingInstanceGameObject), type = r.assetComponent != null ? r.assetComponent.GetType().FullName : null })
                .ToList();

            var modifiedPaths = PrefabUtility.GetObjectOverrides(root)
                .Select(o => o.instanceObject is GameObject modifiedGo ? MCPSceneUtil.GetPath(modifiedGo)
                    : o.instanceObject is Component modifiedComp ? MCPSceneUtil.GetPath(modifiedComp.gameObject)
                    : null)
                .Where(p => p != null)
                .Distinct()
                .ToList();

            return MCPResult.Success(new
            {
                sourcePrefabPath = sourcePath,
                addedComponents,
                addedGameObjects,
                removedComponents,
                modifiedObjectPaths = modifiedPaths
            });
        }

        [MCPTool(
            "unpack_prefab",
            "Unpacks a prefab instance, breaking its connection to the source prefab asset. 'completely' unpacks every " +
            "nested prefab inside it too; otherwise only the outermost level is unpacked (nested prefabs stay linked).",
            destructive: true,
            group: "assets")]
        public static MCPResult UnpackPrefab(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the prefab instance (or any GameObject within it).")] string path,
            [MCPParam("Unpack every nested prefab inside it too, not just the outermost level. Defaults to false.")] bool completely = false)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return MCPResult.Fail($"GameObject at '{path}' is not part of a prefab instance.");

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.AutomatedAction);

            return MCPResult.Success();
        }

        private static bool TryLoadPrefabAsset(string path, out GameObject prefab, out string error)
        {
            prefab = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                error = "path must end with '.prefab'.";
                return false;
            }

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out var fullPath, out error))
                return false;

            if (!File.Exists(fullPath))
            {
                error = $"'{path}' does not exist.";
                return false;
            }

            var assetPath = "Assets/" + path.Replace('\\', '/').TrimStart('/');
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                error = $"'{path}' exists but could not be loaded as a prefab GameObject.";
                return false;
            }

            return true;
        }
    }
}
