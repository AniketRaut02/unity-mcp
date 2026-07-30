using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
            group: "assets", readOnly: true)]
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

        [MCPTool(
            "import_asset",
            "Copies an external file into the project under Assets/ and imports it. sourcePath is an absolute path " +
            "OUTSIDE the project (anywhere the Editor process can read); destinationPath is relative to Assets/, " +
            "guarded the same as every other Assets/-writing tool.",
            MCPLatencyTier.Slow,
            group: "assets")]
        public static MCPResult ImportAsset(
            MCPToolContext ctx,
            [MCPParam("Absolute path to the external file to import.")] string sourcePath,
            [MCPParam("Destination path relative to Assets/, e.g. 'Textures/Wall.png'.")] string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return MCPResult.Fail($"Source file '{sourcePath}' does not exist.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, destinationPath, out var fullDestPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullDestPath))
                return MCPResult.Fail($"'{destinationPath}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullDestPath));
            File.Copy(sourcePath, fullDestPath);

            var unityAssetPath = "Assets/" + destinationPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.ImportAsset(unityAssetPath, ImportAssetOptions.Default);

            return MCPResult.Success(new { assetPath = unityAssetPath });
        }

        [MCPTool(
            "move_asset",
            "Moves or renames an asset under Assets/ via AssetDatabase, preserving its GUID so references from other " +
            "assets stay intact (unlike a raw filesystem move). Both paths are relative to Assets/.",
            group: "assets")]
        public static MCPResult MoveAsset(
            MCPToolContext ctx,
            [MCPParam("Current path relative to Assets/.")] string sourcePath,
            [MCPParam("New path relative to Assets/.")] string destinationPath)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, sourcePath, out var fullSourcePath, out var sourceError))
                return MCPResult.Fail(sourceError);

            if (!File.Exists(fullSourcePath) && !Directory.Exists(fullSourcePath))
                return MCPResult.Fail($"'{sourcePath}' does not exist.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, destinationPath, out var fullDestPath, out var destError))
                return MCPResult.Fail(destError);

            if (File.Exists(fullDestPath) || Directory.Exists(fullDestPath))
                return MCPResult.Fail($"'{destinationPath}' already exists.");

            var sourceAssetPath = "Assets/" + sourcePath.Replace('\\', '/').TrimStart('/');
            var destAssetPath = "Assets/" + destinationPath.Replace('\\', '/').TrimStart('/');

            var error = AssetDatabase.MoveAsset(sourceAssetPath, destAssetPath);
            if (!string.IsNullOrEmpty(error))
                return MCPResult.Fail(error);

            return MCPResult.Success(new { path = destAssetPath });
        }

        [MCPTool(
            "get_asset_dependencies",
            "Lists what an asset references (its dependencies). Optionally also lists which OTHER project assets " +
            "reference it (reverse dependencies) -- slower, since it scans every asset under Assets/ to find them.",
            group: "assets", readOnly: true)]
        public static MCPResult GetAssetDependencies(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the asset to inspect.")] string assetPath,
            [MCPParam("Also list assets under Assets/ that reference this one. Defaults to false (slower when true).")] bool includeReferencedBy = false,
            [MCPParam("Include indirect (transitive) dependencies, not just direct ones. Defaults to false.")] bool recursive = false)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            var dependencies = AssetDatabase.GetDependencies(unityAssetPath, recursive)
                .Where(p => p != unityAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            List<string> referencedBy = null;
            if (includeReferencedBy)
            {
                referencedBy = new List<string>();
                foreach (var candidate in AssetDatabase.GetAllAssetPaths())
                {
                    if (candidate == unityAssetPath || !candidate.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                    if (AssetDatabase.GetDependencies(candidate, false).Contains(unityAssetPath))
                        referencedBy.Add(candidate);
                }
                referencedBy.Sort(StringComparer.Ordinal);
            }

            return MCPResult.Success(new { dependencies, referencedBy });
        }

        [MCPTool(
            "reimport_asset",
            "Forces a reimport of an asset using its current importer settings -- use after set_texture_import_settings " +
            "/ set_model_import_settings, or if an asset seems stale.",
            MCPLatencyTier.Slow,
            group: "assets")]
        public static MCPResult ReimportAsset(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the asset to reimport.")] string assetPath)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.ImportAsset(unityAssetPath, ImportAssetOptions.ForceUpdate);

            return MCPResult.Success();
        }

        [MCPTool(
            "set_texture_import_settings",
            "Configures a texture asset's import settings: type, compression, mipmaps, sRGB, max size. Omitted " +
            "parameters are left unchanged. Triggers a reimport.",
            MCPLatencyTier.Slow,
            group: "assets")]
        public static MCPResult SetTextureImportSettings(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the texture asset.")] string assetPath,
            [MCPParam("TextureImporterType name, e.g. 'Default', 'NormalMap', 'Sprite', 'GUI'. Omit to leave unchanged.")] string textureType = null,
            [MCPParam("TextureImporterCompression name, e.g. 'Uncompressed', 'Compressed', 'CompressedHQ', 'CompressedLQ'. Omit to leave unchanged.")] string textureCompression = null,
            [MCPParam("Generate mipmaps. Omit to leave unchanged.")] bool? mipmapEnabled = null,
            [MCPParam("Import as sRGB (color) vs linear (data). Omit to leave unchanged.")] bool? sRGBTexture = null,
            [MCPParam("Max texture size in pixels, e.g. 2048. Omit to leave unchanged.")] int? maxTextureSize = null)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            if (!(AssetImporter.GetAtPath(unityAssetPath) is TextureImporter importer))
                return MCPResult.Fail($"'{assetPath}' is not imported as a texture (no TextureImporter).");

            if (textureType != null)
            {
                if (!Enum.TryParse<TextureImporterType>(textureType, out var parsed))
                    return MCPResult.Fail($"Unknown textureType '{textureType}'. Valid: {string.Join(", ", Enum.GetNames(typeof(TextureImporterType)))}");
                importer.textureType = parsed;
            }

            if (textureCompression != null)
            {
                if (!Enum.TryParse<TextureImporterCompression>(textureCompression, out var parsed))
                    return MCPResult.Fail($"Unknown textureCompression '{textureCompression}'. Valid: {string.Join(", ", Enum.GetNames(typeof(TextureImporterCompression)))}");
                importer.textureCompression = parsed;
            }

            if (mipmapEnabled.HasValue) importer.mipmapEnabled = mipmapEnabled.Value;
            if (sRGBTexture.HasValue) importer.sRGBTexture = sRGBTexture.Value;
            if (maxTextureSize.HasValue) importer.maxTextureSize = maxTextureSize.Value;

            importer.SaveAndReimport();
            return MCPResult.Success();
        }

        [MCPTool(
            "set_model_import_settings",
            "Configures a model asset's import settings: animation import, animation type, material import, global " +
            "scale. Omitted parameters are left unchanged. Triggers a reimport.",
            MCPLatencyTier.Slow,
            group: "assets")]
        public static MCPResult SetModelImportSettings(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the model asset.")] string assetPath,
            [MCPParam("Import animation clips from the model. Omit to leave unchanged.")] bool? importAnimation = null,
            [MCPParam("ModelImporterAnimationType name, e.g. 'Generic', 'Humanoid', 'Legacy', 'None'. Omit to leave unchanged.")] string animationType = null,
            [MCPParam("Import materials from the model (maps to materialImportMode: true -> ImportStandard, false -> None -- the older plain importMaterials bool this maps onto was removed in newer Unity versions). Omit to leave unchanged.")] bool? importMaterials = null,
            [MCPParam("Uniform scale factor applied on import. Omit to leave unchanged.")] float? globalScale = null)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            if (!(AssetImporter.GetAtPath(unityAssetPath) is ModelImporter importer))
                return MCPResult.Fail($"'{assetPath}' is not imported as a model (no ModelImporter).");

            if (importAnimation.HasValue) importer.importAnimation = importAnimation.Value;

            if (animationType != null)
            {
                if (!Enum.TryParse<ModelImporterAnimationType>(animationType, out var parsed))
                    return MCPResult.Fail($"Unknown animationType '{animationType}'. Valid: {string.Join(", ", Enum.GetNames(typeof(ModelImporterAnimationType)))}");
                importer.animationType = parsed;
            }

            if (importMaterials.HasValue)
                importer.materialImportMode = importMaterials.Value
                    ? ModelImporterMaterialImportMode.ImportStandard
                    : ModelImporterMaterialImportMode.None;
            if (globalScale.HasValue) importer.globalScale = globalScale.Value;

            importer.SaveAndReimport();
            return MCPResult.Success();
        }

        [MCPTool("create_folder", "Creates a folder under Assets/, creating any missing parent folders along the way (each as its own real folder asset with a .meta file, via AssetDatabase, not a raw filesystem directory).", group: "assets")]
        public static MCPResult CreateFolder(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ for the new folder, e.g. 'Prefabs/Enemies'.")] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return MCPResult.Fail("path must not be empty.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (Directory.Exists(fullPath))
                return MCPResult.Fail($"'{path}' already exists.");

            var segments = path.Replace('\\', '/').Trim('/').Split('/');
            var currentAssetPath = "Assets";
            foreach (var segment in segments)
            {
                var candidate = currentAssetPath + "/" + segment;
                if (!AssetDatabase.IsValidFolder(candidate))
                {
                    var guid = AssetDatabase.CreateFolder(currentAssetPath, segment);
                    if (string.IsNullOrEmpty(guid))
                        return MCPResult.Fail($"Failed to create folder '{candidate}'.");
                }
                currentAssetPath = candidate;
            }

            return MCPResult.Success(new { path = currentAssetPath });
        }

        [MCPTool("create_render_texture", "Creates a new RenderTexture asset, for camera-to-texture setups (CCTV/monitor props, minimaps, portals).", group: "assets")]
        public static MCPResult CreateRenderTexture(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Textures/CCTV.renderTexture'.")] string assetPath,
            [MCPParam("Width in pixels. Defaults to 1024.")] int width = 1024,
            [MCPParam("Height in pixels. Defaults to 1024.")] int height = 1024,
            [MCPParam("Depth buffer bits: 0, 16, 24, or 32. Defaults to 24.")] int depthBufferBits = 24)
        {
            if (!assetPath.EndsWith(".renderTexture", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.renderTexture'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            var renderTexture = new RenderTexture(width, height, depthBufferBits);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.CreateAsset(renderTexture, unityAssetPath);

            return MCPResult.Success(new { assetPath = unityAssetPath });
        }

        [MCPTool(
            "mark_addressable",
            "Marks an asset as Addressable and optionally assigns it to a named group. Requires the Addressables " +
            "package (com.unity.addressables) to be installed and initialized in this project; fails with a clear " +
            "message if it isn't. Accessed via reflection since Addressables is an optional package this tool " +
            "assembly can't take a hard compile-time dependency on -- verify with a real project that has Addressables " +
            "set up before relying on this.",
            group: "assets")]
        public static MCPResult MarkAddressable(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the asset to mark addressable.")] string assetPath,
            [MCPParam("Name of an existing Addressable group to assign it to. Omit to use the default group.")] string groupName = null)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            var guid = AssetDatabase.AssetPathToGUID(unityAssetPath);
            if (string.IsNullOrEmpty(guid))
                return MCPResult.Fail($"Could not resolve a GUID for '{assetPath}'.");

            var settingsObjType = FindTypeByFullName("UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject");
            if (settingsObjType == null)
                return MCPResult.Fail("The Addressables package (com.unity.addressables) is not installed in this project.");

            try
            {
                var settings = settingsObjType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (settings == null)
                    return MCPResult.Fail("Addressables Settings asset not found -- initialize Addressables first (Window > Asset Management > Addressables > Groups).");

                object targetGroup;
                if (!string.IsNullOrEmpty(groupName))
                {
                    targetGroup = settings.GetType().GetMethod("FindGroup", new[] { typeof(string) })?.Invoke(settings, new object[] { groupName });
                    if (targetGroup == null)
                        return MCPResult.Fail($"Addressable group '{groupName}' not found.");
                }
                else
                {
                    targetGroup = settings.GetType().GetProperty("DefaultGroup")?.GetValue(settings);
                    if (targetGroup == null)
                        return MCPResult.Fail("No default Addressable group is configured.");
                }

                var createOrMoveEntry = settings.GetType().GetMethod("CreateOrMoveEntry", new[] { typeof(string), targetGroup.GetType(), typeof(bool), typeof(bool) });
                if (createOrMoveEntry == null)
                    return MCPResult.Fail("Could not find AddressableAssetSettings.CreateOrMoveEntry on this Addressables version.");

                var entry = createOrMoveEntry.Invoke(settings, new object[] { guid, targetGroup, false, true });
                if (entry == null)
                    return MCPResult.Fail("CreateOrMoveEntry did not return an entry -- the asset may not be addressable-eligible.");

                return MCPResult.Success(new { assetPath = unityAssetPath, group = groupName ?? "(default)" });
            }
            catch (Exception e)
            {
                return MCPResult.Fail($"Addressables reflection call failed (package version mismatch?): {e.Message}");
            }
        }

        [MCPTool(
            "create_asset_bundle",
            "Assigns one or more assets to a named AssetBundle, then builds all asset bundles for the current build " +
            "target into outputFolder (relative to the project root, NOT Assets/ -- bundle output conventionally lives " +
            "outside Assets/ so the bundles themselves aren't reimported as assets).",
            MCPLatencyTier.Slow,
            group: "assets")]
        public static MCPResult CreateAssetBundle(
            MCPToolContext ctx,
            [MCPParam("Asset paths relative to Assets/ to include in the bundle.")] string[] assetPaths,
            [MCPParam("Bundle name, e.g. 'enemies'. Created if it doesn't already exist.")] string bundleName,
            [MCPParam("Output folder for the built bundles, relative to the project root, e.g. 'AssetBundles'.")] string outputFolder,
            [MCPParam("Optional variant name for the bundle, e.g. 'hd'/'sd'. Omit for none.")] string variant = "")
        {
            if (assetPaths == null || assetPaths.Length == 0)
                return MCPResult.Fail("assetPaths must contain at least one entry.");

            if (string.IsNullOrWhiteSpace(bundleName))
                return MCPResult.Fail("bundleName must not be empty.");

            foreach (var assetPath in assetPaths)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);

                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{assetPath}' does not exist.");

                var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
                var importer = AssetImporter.GetAtPath(unityAssetPath);
                if (importer == null)
                    return MCPResult.Fail($"Could not get an importer for '{assetPath}'.");

                importer.SetAssetBundleNameAndVariant(bundleName, variant ?? "");
            }

            var normalizedRoot = Path.GetFullPath(MCPProjectUtil.ProjectRoot);
            var normalizedOutput = Path.GetFullPath(Path.Combine(MCPProjectUtil.ProjectRoot, outputFolder));
            if (!normalizedOutput.StartsWith(normalizedRoot, StringComparison.Ordinal))
                return MCPResult.Fail("outputFolder must be within the project.");

            Directory.CreateDirectory(normalizedOutput);
            var manifest = BuildPipeline.BuildAssetBundles(normalizedOutput, BuildAssetBundleOptions.None, EditorUserBuildSettings.activeBuildTarget);
            if (manifest == null)
                return MCPResult.Fail("BuildPipeline.BuildAssetBundles failed -- check the Console for details.");

            return MCPResult.Success(new
            {
                outputFolder,
                bundleName,
                assetCount = assetPaths.Length,
                allBundles = manifest.GetAllAssetBundles()
            });
        }

        private static Type FindTypeByFullName(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                var match = types.FirstOrDefault(t => t.FullName == fullName);
                if (match != null) return match;
            }
            return null;
        }
    }
}
