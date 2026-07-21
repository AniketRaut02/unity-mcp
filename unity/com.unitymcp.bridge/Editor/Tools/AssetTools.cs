using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    public static class AssetTools
    {
        [MCPTool(
            "create_prefab",
            "Saves an existing GameObject in the active scene as a new prefab asset under Assets/. Standard Unity " +
            "behavior applies: the scene GameObject becomes an instance of the new prefab.",
            group: "assets")]
        public static MCPResult CreatePrefab(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the scene GameObject to save as a prefab.")] string gameObjectPath,
            [MCPParam("Destination path relative to Assets/, e.g. 'Prefabs/Enemy.prefab'.")] string assetPath)
        {
            var go = MCPSceneUtil.ResolvePath(gameObjectPath);
            if (go == null) return MCPResult.Fail($"GameObject path '{gameObjectPath}' not found.");

            if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.prefab'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/');

            var savedPrefab = PrefabUtility.SaveAsPrefabAsset(go, unityAssetPath, out var success);
            if (!success || savedPrefab == null)
                return MCPResult.Fail($"PrefabUtility.SaveAsPrefabAsset failed for '{unityAssetPath}'.");

            return MCPResult.Success(new { assetPath = unityAssetPath, gameObjectPath });
        }

        [MCPTool(
            "instantiate_prefab",
            "Instantiates a prefab asset into the active scene, optionally under a parent hierarchy path and at a given " +
            "local position.",
            group: "assets")]
        public static MCPResult InstantiatePrefab(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the prefab to instantiate, e.g. 'Prefabs/Enemy.prefab'.")] string assetPath,
            [MCPParam("Hierarchy path of an existing GameObject to parent the instance under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("Local X position for the new instance.")] float posX = 0f,
            [MCPParam("Local Y position for the new instance.")] float posY = 0f,
            [MCPParam("Local Z position for the new instance.")] float posZ = 0f)
        {
            if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.prefab'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/');
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(unityAssetPath);
            if (prefabAsset == null)
                return MCPResult.Fail($"Could not load a GameObject prefab at '{unityAssetPath}'.");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            Undo.RegisterCreatedObjectUndo(instance, "MCP: Instantiate Prefab");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = MCPSceneUtil.ResolvePath(parentPath);
                if (parent == null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                    return MCPResult.Fail($"Parent path '{parentPath}' not found.");
                }
                instance.transform.SetParent(parent.transform, false);
            }

            instance.transform.localPosition = new Vector3(posX, posY, posZ);

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(instance) });
        }

        [MCPTool(
            "create_material",
            "Creates a new Material asset under Assets/ with the given shader (default 'Standard') and optional initial " +
            "color. For URP/HDRP projects, pass the pipeline's shader name explicitly (e.g. " +
            "'Universal Render Pipeline/Lit') — 'Standard' only exists in the Built-in Render Pipeline.",
            group: "assets")]
        public static MCPResult CreateMaterial(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Materials/EnemyRed.mat'.")] string assetPath,
            [MCPParam("Exact shader name — 'Standard' (Built-in RP), 'Universal Render Pipeline/Lit' (URP), 'HDRP/Lit' (HDRP), etc.")] string shaderName = "Standard",
            [MCPParam("Initial color red component (0-1). Omit to keep the shader's default.")] float? colorR = null,
            [MCPParam("Initial color green component (0-1). Omit to keep the shader's default.")] float? colorG = null,
            [MCPParam("Initial color blue component (0-1). Omit to keep the shader's default.")] float? colorB = null,
            [MCPParam("Initial color alpha component (0-1). Omit to keep the shader's default.")] float? colorA = null)
        {
            if (!assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.mat'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            var shader = Shader.Find(shaderName);
            if (shader == null)
                return MCPResult.Fail(
                    $"Shader '{shaderName}' not found. Check the exact shader name for your project's render pipeline " +
                    "(Built-in: 'Standard'; URP: 'Universal Render Pipeline/Lit'; HDRP: 'HDRP/Lit').");

            var material = new Material(shader);

            if (colorR.HasValue || colorG.HasValue || colorB.HasValue || colorA.HasValue)
            {
                var color = material.color;
                if (colorR.HasValue) color.r = colorR.Value;
                if (colorG.HasValue) color.g = colorG.Value;
                if (colorB.HasValue) color.b = colorB.Value;
                if (colorA.HasValue) color.a = colorA.Value;
                material.color = color;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/');
            AssetDatabase.CreateAsset(material, unityAssetPath);

            return MCPResult.Success(new { assetPath = unityAssetPath, shader = shaderName });
        }

        [MCPTool("set_material_color", "Sets the main color (including alpha) on an existing Material asset under Assets/.", group: "assets")]
        public static MCPResult SetMaterialColor(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the existing material, e.g. 'Materials/EnemyRed.mat'.")] string assetPath,
            [MCPParam("Red component (0-1). Omit to leave unchanged.")] float? colorR = null,
            [MCPParam("Green component (0-1). Omit to leave unchanged.")] float? colorG = null,
            [MCPParam("Blue component (0-1). Omit to leave unchanged.")] float? colorB = null,
            [MCPParam("Alpha component (0-1). Omit to leave unchanged.")] float? colorA = null)
        {
            if (!assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.mat'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/');
            var material = AssetDatabase.LoadAssetAtPath<Material>(unityAssetPath);
            if (material == null)
                return MCPResult.Fail($"Could not load a Material at '{unityAssetPath}'.");

            var color = material.color;
            if (colorR.HasValue) color.r = colorR.Value;
            if (colorG.HasValue) color.g = colorG.Value;
            if (colorB.HasValue) color.b = colorB.Value;
            if (colorA.HasValue) color.a = colorA.Value;
            material.color = color;

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return MCPResult.Success(new
            {
                assetPath = unityAssetPath,
                color = new { r = color.r, g = color.g, b = color.b, a = color.a }
            });
        }

        [MCPTool(
            "create_scriptable_object",
            "Creates a new ScriptableObject asset instance of the given class under Assets/. typeName must derive from " +
            "ScriptableObject (e.g. one created via create_script with template=ScriptableObject) and must already be " +
            "compiled — check get_compile_status first if you just created it.",
            group: "assets")]
        public static MCPResult CreateScriptableObject(
            MCPToolContext ctx,
            [MCPParam("Full or short name of a compiled class deriving from ScriptableObject, e.g. 'EnemyStats'.")] string typeName,
            [MCPParam("Destination path relative to Assets/, e.g. 'Data/GoblinStats.asset'.")] string assetPath)
        {
            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.asset'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError))
            {
                // The compile-status hint is specific to this tool's own common failure
                // mode (a script just created via create_script hasn't finished
                // compiling yet) -- only worth appending for the "not found" case, not
                // for a genuine ambiguity error, where the type clearly does exist.
                var hint = typeError.EndsWith("not found.") ? " Has its script finished compiling? Check get_compile_status." : "";
                return MCPResult.Fail(typeError + hint);
            }

            if (!typeof(ScriptableObject).IsAssignableFrom(type))
                return MCPResult.Fail($"'{typeName}' does not derive from ScriptableObject.");

            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null)
                return MCPResult.Fail($"ScriptableObject.CreateInstance failed for '{typeName}'.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/');
            AssetDatabase.CreateAsset(instance, unityAssetPath);

            return MCPResult.Success(new { assetPath = unityAssetPath, type = typeName });
        }

        [MCPTool(
            "list_assets",
            "Lists asset paths (relative to Assets/) filtered by extension (e.g. 'prefab', 'mat', 'asset'), optionally " +
            "under a subfolder. Omit extension to list every file. .meta files are always excluded.",
            group: "assets")]
        public static MCPResult ListAssets(
            MCPToolContext ctx,
            [MCPParam("File extension to filter by, without the dot, e.g. 'prefab' or 'mat'. Omit to list every file.")] string extension = null,
            [MCPParam("Subfolder under Assets/ to search, e.g. 'Prefabs/Enemies'. Omit to search the whole project.")] string underPath = null)
        {
            var projectRoot = MCPProjectUtil.ProjectRoot;
            var assetsRoot = Path.Combine(projectRoot, "Assets");

            string searchRoot;
            if (string.IsNullOrEmpty(underPath))
            {
                searchRoot = assetsRoot;
            }
            else
            {
                if (!MCPPathGuard.TryResolveWithinAssets(projectRoot, underPath, out searchRoot, out var guardError))
                    return MCPResult.Fail(guardError);
            }

            if (!Directory.Exists(searchRoot))
                return MCPResult.Fail($"'{underPath}' is not a directory under Assets/.");

            var pattern = string.IsNullOrEmpty(extension) ? "*" : $"*.{extension.TrimStart('.')}";
            var assets = Directory.GetFiles(searchRoot, pattern, SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .Select(f => MCPProjectUtil.MakeRelativeToAssets(assetsRoot, f))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            return MCPResult.Success(new { assets });
        }

        [MCPTool(
            "delete_asset",
            "Deletes any asset (prefab, material, ScriptableObject, folder, etc.) under Assets/ via AssetDatabase, so " +
            "Unity's bookkeeping (.meta files, GUID database) stays consistent. Not undoable via Ctrl+Z.",
            MCPLatencyTier.Fast,
            destructive: true,
            group: "assets")]
        public static MCPResult DeleteAsset(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the asset (or folder) to delete.")] string assetPath)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/');
            if (!AssetDatabase.DeleteAsset(unityAssetPath))
                return MCPResult.Fail($"AssetDatabase failed to delete '{unityAssetPath}'.");

            return MCPResult.Success();
        }
    }
}
