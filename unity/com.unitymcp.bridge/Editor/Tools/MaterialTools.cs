using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>Group H of the tool catalog -- Materials &amp; Shaders, extending AssetTools.cs's create_material/set_material_color.</summary>
    public static class MaterialTools
    {
        [MCPTool(
            "set_material_properties",
            "Sets a single shader property on an existing material by name (e.g. '_Color', '_Metallic', '_MainTex') -- " +
            "color, float, texture (by asset path), or a keyword to enable/disable. Provide exactly one of: color " +
            "components (colorR/G/B/A) / floatValue / textureAssetPath / keyword(+keywordEnabled).",
            group: "assets")]
        public static MCPResult SetMaterialProperties(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the material.")] string assetPath,
            [MCPParam("Shader property name, e.g. '_Color' or '_Metallic'. Not required when setting a keyword.")] string propertyName = null,
            [MCPParam("Color red component (0-1). Set alongside colorG/B/A to set a color property.")] float? colorR = null,
            [MCPParam("Color green component (0-1).")] float? colorG = null,
            [MCPParam("Color blue component (0-1).")] float? colorB = null,
            [MCPParam("Color alpha component (0-1). Omit to leave the property's current alpha.")] float? colorA = null,
            [MCPParam("Float value to set on a float/range property.")] float? floatValue = null,
            [MCPParam("Asset path relative to Assets/ of a texture to assign to a texture property.")] string textureAssetPath = null,
            [MCPParam("Shader keyword to enable/disable (e.g. '_NORMALMAP'), instead of a named property.")] string keyword = null,
            [MCPParam("Required alongside 'keyword': true to enable it, false to disable it.")] bool? keywordEnabled = null)
        {
            if (!TryLoadMaterial(assetPath, out var material, out var loadError))
                return MCPResult.Fail(loadError);

            bool settingColor = colorR.HasValue || colorG.HasValue || colorB.HasValue || colorA.HasValue;
            int kindsProvided = (settingColor ? 1 : 0) + (floatValue.HasValue ? 1 : 0) + (textureAssetPath != null ? 1 : 0) + (keyword != null ? 1 : 0);
            if (kindsProvided != 1)
                return MCPResult.Fail("Provide exactly one of: color components, floatValue, textureAssetPath, or keyword.");

            if (keyword != null)
            {
                if (!keywordEnabled.HasValue)
                    return MCPResult.Fail("keywordEnabled is required when setting a keyword.");

                if (keywordEnabled.Value) material.EnableKeyword(keyword);
                else material.DisableKeyword(keyword);

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
                return MCPResult.Success(new { keyword, enabled = keywordEnabled.Value });
            }

            if (string.IsNullOrEmpty(propertyName))
                return MCPResult.Fail("propertyName is required unless setting a keyword.");

            if (!material.HasProperty(propertyName))
                return MCPResult.Fail($"Material's shader ('{material.shader.name}') has no property named '{propertyName}'.");

            if (textureAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, textureAssetPath, out var texFullPath, out var texGuardError))
                    return MCPResult.Fail(texGuardError);
                if (!File.Exists(texFullPath))
                    return MCPResult.Fail($"'{textureAssetPath}' does not exist.");

                var texUnityPath = "Assets/" + textureAssetPath.Replace('\\', '/').TrimStart('/');
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(texUnityPath);
                if (texture == null)
                    return MCPResult.Fail($"Could not load a Texture at '{textureAssetPath}'.");

                material.SetTexture(propertyName, texture);
            }
            else if (floatValue.HasValue)
            {
                material.SetFloat(propertyName, floatValue.Value);
            }
            else
            {
                var color = material.GetColor(propertyName);
                if (colorR.HasValue) color.r = colorR.Value;
                if (colorG.HasValue) color.g = colorG.Value;
                if (colorB.HasValue) color.b = colorB.Value;
                if (colorA.HasValue) color.a = colorA.Value;
                material.SetColor(propertyName, color);
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return MCPResult.Success();
        }

        [MCPTool("assign_material", "Assigns a material to a Renderer's material slot by index (0 for the main/only slot) on a GameObject.", group: "assets")]
        public static MCPResult AssignMaterial(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Renderer.")] string path,
            [MCPParam("Path relative to Assets/ of the material to assign.")] string materialAssetPath,
            [MCPParam("Material slot index. Defaults to 0 (the main slot).")] int slotIndex = 0)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return MCPResult.Fail($"GameObject at '{path}' has no Renderer.");

            if (!TryLoadMaterial(materialAssetPath, out var material, out var loadError))
                return MCPResult.Fail(loadError);

            var materials = renderer.sharedMaterials;
            if (slotIndex < 0 || slotIndex >= materials.Length)
                return MCPResult.Fail($"slotIndex {slotIndex} is out of range -- this Renderer has {materials.Length} material slot(s).");

            Undo.RecordObject(renderer, "MCP: Assign Material");
            materials[slotIndex] = material;
            renderer.sharedMaterials = materials;

            return MCPResult.Success();
        }

        [MCPTool("get_material_properties", "Reads a material's exposed shader properties: name, type, and current value (color/float/texture-path/vector).", group: "assets", readOnly: true)]
        public static MCPResult GetMaterialProperties(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the material.")] string assetPath)
        {
            if (!TryLoadMaterial(assetPath, out var material, out var loadError))
                return MCPResult.Fail(loadError);

            var shader = material.shader;
            var properties = new System.Collections.Generic.List<object>();

            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                var name = ShaderUtil.GetPropertyName(shader, i);
                var type = ShaderUtil.GetPropertyType(shader, i);

                object value = type switch
                {
                    ShaderUtil.ShaderPropertyType.Color => (object)ColorToAnon(material.GetColor(name)),
                    ShaderUtil.ShaderPropertyType.Vector => ColorToAnon(material.GetVector(name)),
                    ShaderUtil.ShaderPropertyType.Float => material.GetFloat(name),
                    ShaderUtil.ShaderPropertyType.Range => material.GetFloat(name),
                    ShaderUtil.ShaderPropertyType.TexEnv => AssetDatabase.GetAssetPath(material.GetTexture(name)),
                    _ => null
                };

                properties.Add(new { name, type = type.ToString(), value });
            }

            return MCPResult.Success(new { shader = shader.name, properties });
        }

        [MCPTool("list_shaders", "Lists shaders available to this project (built-in, package, and project-authored), optionally filtered by a name substring.", group: "assets", readOnly: true)]
        public static MCPResult ListShaders(
            MCPToolContext ctx,
            [MCPParam("Case-insensitive substring to filter shader names by, e.g. 'Universal Render Pipeline'. Omit to list all.")] string nameContains = null)
        {
            var allShaders = ShaderUtil.GetAllShaderInfo()
                .Where(s => string.IsNullOrEmpty(nameContains) || s.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(s => new { name = s.name, supported = s.supported })
                .OrderBy(s => s.name, StringComparer.Ordinal)
                .ToList();

            return MCPResult.Success(new { shaders = allShaders, count = allShaders.Count });
        }

        [MCPTool(
            "create_shader_graph",
            "Creates a new, blank Unlit Shader Graph asset. Requires the Shader Graph package (com.unity.shadergraph, " +
            "included with URP/HDRP) to be installed; fails clearly if it isn't. Writes the graph's on-disk JSON " +
            "directly rather than depending on the Shader Graph editor assembly at compile time, since that format " +
            "isn't part of a stable public API and can vary across Shader Graph versions -- verify against a real " +
            "project with Shader Graph installed before relying on this.",
            group: "assets")]
        public static MCPResult CreateShaderGraph(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Shaders/Toon.shadergraph'.")] string assetPath)
        {
            if (!IsShaderGraphInstalled())
                return MCPResult.Fail("The Shader Graph package (com.unity.shadergraph) is not installed in this project.");

            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.shadergraph'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllText(fullPath, MCPShaderGraphTemplate.BlankUnlitGraphJson);

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.ImportAsset(unityAssetPath);

            return MCPResult.Success(new { assetPath = unityAssetPath });
        }

        [MCPTool(
            "inspect_shader_graph",
            "Returns a best-effort summary of a Shader Graph asset's raw on-disk JSON: rough node count and any " +
            "top-level target/output info that's reliably present. Not a structured node/edge graph -- Shader Graph's " +
            "JSON schema isn't a stable public API and varies across versions, so this only surfaces what can be read " +
            "generically. Verify against a real Shader Graph asset before relying on the exact fields returned.",
            group: "assets")]
        public static MCPResult InspectShaderGraph(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the .shadergraph asset.")] string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.shadergraph'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            string json;
            try { json = File.ReadAllText(fullPath); }
            catch (Exception e) { return MCPResult.Fail($"Could not read '{assetPath}': {e.Message}"); }

            Newtonsoft.Json.Linq.JObject root;
            try { root = Newtonsoft.Json.Linq.JObject.Parse(json); }
            catch (Exception e) { return MCPResult.Fail($"'{assetPath}' is not valid JSON -- is it really a Shader Graph asset? {e.Message}"); }

            // m_Nodes/m_Type appear (as of recent Shader Graph versions) as a flat list of
            // serialized node entries under the graph's top-level object -- counted
            // generically by scanning for any array named "m_Nodes" or "m_SerializableNodes"
            // rather than assuming one specific schema version.
            int nodeCount = 0;
            foreach (var arrayKey in new[] { "m_Nodes", "m_SerializableNodes" })
            {
                if (root[arrayKey] is Newtonsoft.Json.Linq.JArray arr)
                {
                    nodeCount += arr.Count;
                }
            }

            return MCPResult.Success(new
            {
                assetPath,
                approximateNodeCount = nodeCount,
                topLevelKeys = root.Properties().Select(p => p.Name).ToList()
            });
        }

        [MCPTool("set_render_queue", "Sets a material's render queue value directly, or resets it to the shader's default (-1).", group: "assets")]
        public static MCPResult SetRenderQueue(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the material.")] string assetPath,
            [MCPParam("Render queue value, e.g. 2000 (opaque), 3000 (transparent). Pass -1 to reset to the shader's default.")] int renderQueue)
        {
            if (!TryLoadMaterial(assetPath, out var material, out var loadError))
                return MCPResult.Fail(loadError);

            material.renderQueue = renderQueue;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return MCPResult.Success(new { renderQueue = material.renderQueue });
        }

        [MCPTool("create_material_variant", "Creates a copy of an existing material as a new asset (Unity materials don't have a formal 'variant' concept like prefabs -- this duplicates the source's shader and property values into an independent new asset).", group: "assets")]
        public static MCPResult CreateMaterialVariant(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the source material.")] string sourcePath,
            [MCPParam("Destination path relative to Assets/ for the new material.")] string destinationPath)
        {
            if (!TryLoadMaterial(sourcePath, out var source, out var loadError))
                return MCPResult.Fail(loadError);

            if (!destinationPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("destinationPath must end with '.mat'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, destinationPath, out var destFullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(destFullPath))
                return MCPResult.Fail($"'{destinationPath}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(destFullPath));
            var destUnityPath = "Assets/" + destinationPath.Replace('\\', '/').TrimStart('/');

            var copy = new Material(source);
            AssetDatabase.CreateAsset(copy, destUnityPath);

            return MCPResult.Success(new { assetPath = destUnityPath });
        }

        [MCPTool(
            "set_global_shader_property",
            "Sets a global shader property (via Shader.SetGlobalX) visible to every material/shader in the scene -- " +
            "use for project-wide effects like fog/scanline/tint parameters a custom shader reads. Not persisted to " +
            "any asset; resets when the Editor session/Play mode ends.",
            group: "assets")]
        public static MCPResult SetGlobalShaderProperty(
            MCPToolContext ctx,
            [MCPParam("Global shader property name, e.g. '_GlobalFogColor'.")] string propertyName,
            [MCPParam("Float value to set. Provide exactly one of floatValue/color components.")] float? floatValue = null,
            [MCPParam("Color red component (0-1).")] float? colorR = null,
            [MCPParam("Color green component (0-1).")] float? colorG = null,
            [MCPParam("Color blue component (0-1).")] float? colorB = null,
            [MCPParam("Color alpha component (0-1). Defaults to 1.")] float colorA = 1f)
        {
            bool settingColor = colorR.HasValue || colorG.HasValue || colorB.HasValue;
            if (floatValue.HasValue == settingColor)
                return MCPResult.Fail("Provide exactly one of floatValue or color components (colorR/G/B).");

            if (floatValue.HasValue)
            {
                Shader.SetGlobalFloat(propertyName, floatValue.Value);
            }
            else
            {
                if (!colorR.HasValue || !colorG.HasValue || !colorB.HasValue)
                    return MCPResult.Fail("Setting a color requires colorR, colorG, and colorB.");
                Shader.SetGlobalColor(propertyName, new Color(colorR.Value, colorG.Value, colorB.Value, colorA));
            }

            return MCPResult.Success();
        }

        private static bool TryLoadMaterial(string assetPath, out Material material, out string error)
        {
            material = null;
            error = null;

            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                error = "assetPath must end with '.mat'.";
                return false;
            }

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out error))
                return false;

            if (!File.Exists(fullPath))
            {
                error = $"'{assetPath}' does not exist.";
                return false;
            }

            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            material = AssetDatabase.LoadAssetAtPath<Material>(unityAssetPath);
            if (material == null)
            {
                error = $"Could not load a Material at '{unityAssetPath}'.";
                return false;
            }

            return true;
        }

        private static object ColorToAnon(Color c) => new { r = c.r, g = c.g, b = c.b, a = c.a };
        private static object ColorToAnon(Vector4 v) => new { x = v.x, y = v.y, z = v.z, w = v.w };

        private static bool IsShaderGraphInstalled()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .Any(t => t.FullName == "UnityEditor.ShaderGraph.GraphData");
        }

        private static Type[] SafeGetTypes(System.Reflection.Assembly asm)
        {
            try { return asm.GetTypes(); } catch { return Type.EmptyTypes; }
        }
    }

    /// <summary>
    /// A minimal, known-valid "blank Unlit Shader Graph" JSON template, used by create_shader_graph so it never
    /// needs the Shader Graph editor assembly at compile time (that format isn't a stable public API). This is a
    /// best-effort snapshot of the format as of recent Shader Graph versions -- if Unity's importer rejects it on a
    /// given project's Shader Graph version, that's the actual, honest limit of this approach without the package
    /// installed to verify against.
    /// </summary>
    internal static class MCPShaderGraphTemplate
    {
        public const string BlankUnlitGraphJson = @"{
    ""m_SGVersion"": 3,
    ""m_Type"": ""UnityEditor.ShaderGraph.GraphData"",
    ""m_ObjectId"": ""00000000000000000000000000000000"",
    ""m_Properties"": [],
    ""m_Keywords"": [],
    ""m_Dropdowns"": [],
    ""m_CategoryData"": [],
    ""m_Nodes"": [],
    ""m_GroupDatas"": [],
    ""m_StickyNoteDatas"": [],
    ""m_Edges"": [],
    ""m_VertexContext"": { ""m_Position"": {} },
    ""m_FragmentContext"": { ""m_Position"": {} },
    ""m_PreviewData"": { ""serializedMesh"": { ""m_SerializedMesh"": """", ""m_Guid"": """" } },
    ""m_Path"": ""Shader Graphs"",
    ""m_GraphPrecision"": 1,
    ""m_PreviewMode"": 2,
    ""m_OutputNode"": { ""m_Id"": """" },
    ""m_ActiveTargets"": []
}";
    }
}
