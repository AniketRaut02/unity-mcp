using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// .asmdef files are plain JSON, so these tools read/write them directly rather than
    /// going through any Unity asmdef-specific API (there isn't a convenient public one
    /// for editing an existing file's fields anyway). update_assembly_definition merges
    /// only the fields the caller actually passed, preserving everything else in the file
    /// untouched -- same "don't clobber what you weren't asked to change" principle as
    /// MCPMcpServersJsonWriter's client-config merging.
    /// </summary>
    public static class AssemblyDefinitionTools
    {
        [MCPTool(
            "list_assembly_definitions",
            "Lists .asmdef files under Assets/ with their name and references. Check this before creating scripts in a " +
            "project with assembly boundaries, or before add_component/resolve_type on a type that might live in a " +
            "separate assembly from the caller's.",
            group: "scripting", readOnly: true)]
        public static MCPResult ListAssemblyDefinitions(
            MCPToolContext ctx,
            [MCPParam("Subfolder under Assets/ to search, e.g. 'Scripts'. Omit to search the whole project.")] string underPath = null)
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

            var results = new System.Collections.Generic.List<object>();
            foreach (var file in Directory.GetFiles(searchRoot, "*.asmdef", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relativePath = MCPProjectUtil.MakeRelativeToAssets(assetsRoot, file);
                try
                {
                    var json = JObject.Parse(File.ReadAllText(file));
                    results.Add(new
                    {
                        path = relativePath,
                        name = (string)json["name"],
                        references = (json["references"] as JArray)?.Select(r => (string)r).ToArray() ?? new string[0]
                    });
                }
                catch (Exception e)
                {
                    results.Add(new { path = relativePath, error = $"Could not parse: {e.Message}" });
                }
            }

            return MCPResult.Success(new { assemblies = results });
        }

        [MCPTool(
            "create_assembly_definition",
            "Creates a new .asmdef file under Assets/, scoping every script under folderPath (and its subfolders, until " +
            "another .asmdef is found) into its own compiled assembly. The file is named '<name>.asmdef' and placed " +
            "directly in folderPath. Triggers a domain reload.",
            MCPLatencyTier.Slow,
            group: "scripting")]
        public static MCPResult CreateAssemblyDefinition(
            MCPToolContext ctx,
            [MCPParam("Folder under Assets/ where the .asmdef should be created, e.g. 'Scripts/Gameplay'. Pass an empty string for Assets/ itself.")] string folderPath,
            [MCPParam("Assembly name, e.g. 'MyGame.Gameplay'.")] string name,
            [MCPParam("Names of other assemblies this one references, e.g. [\"UnityMCP.Editor\"]. Omit for none.")] string[] references = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                return MCPResult.Fail("name must not be empty.");

            var relativeAsmdefPath = ((folderPath ?? "").TrimEnd('/') + "/" + name + ".asmdef").TrimStart('/');
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, relativeAsmdefPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{relativeAsmdefPath}' already exists. Use update_assembly_definition to modify it.");

            var json = new JObject
            {
                ["name"] = name,
                ["rootNamespace"] = "",
                ["references"] = new JArray((references ?? new string[0]).Cast<object>().ToArray()),
                ["includePlatforms"] = new JArray(),
                ["excludePlatforms"] = new JArray(),
                ["allowUnsafeCode"] = false,
                ["overrideReferences"] = false,
                ["precompiledReferences"] = new JArray(),
                ["autoReferenced"] = true,
                ["defineConstraints"] = new JArray(),
                ["versionDefines"] = new JArray(),
                ["noEngineReferences"] = false
            };

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllText(fullPath, json.ToString(Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.Refresh();

            return MCPResult.Success(new { path = relativeAsmdefPath, name });
        }

        [MCPTool(
            "update_assembly_definition",
            "Edits an existing .asmdef file's references, name, or allowUnsafeCode. Omitted parameters leave the existing " +
            "value unchanged; every other field already in the file (includePlatforms, precompiledReferences, etc.) is " +
            "preserved as-is. Triggers a domain reload.",
            MCPLatencyTier.Slow,
            group: "scripting")]
        public static MCPResult UpdateAssemblyDefinition(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the existing .asmdef file, e.g. 'Scripts/Gameplay/MyGame.Gameplay.asmdef'.")] string path,
            [MCPParam("New references list, REPLACING the existing one entirely. Omit to leave references unchanged.")] string[] references = null,
            [MCPParam("New assembly name. Omit to leave unchanged.")] string name = null,
            [MCPParam("Allow unsafe code in this assembly. Omit to leave unchanged.")] bool? allowUnsafeCode = null)
        {
            if (!Path.GetExtension(path ?? "").Equals(".asmdef", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("path must end with '.asmdef'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' does not exist.");

            JObject json;
            try
            {
                json = JObject.Parse(File.ReadAllText(fullPath));
            }
            catch (Exception e)
            {
                return MCPResult.Fail($"'{path}' is not valid JSON: {e.Message}");
            }

            if (references != null) json["references"] = new JArray(references.Cast<object>().ToArray());
            if (name != null) json["name"] = name;
            if (allowUnsafeCode.HasValue) json["allowUnsafeCode"] = allowUnsafeCode.Value;

            File.WriteAllText(fullPath, json.ToString(Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.Refresh();

            return MCPResult.Success(new { path });
        }
    }
}
