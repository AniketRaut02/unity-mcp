using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    public static class ScriptingTools
    {
        [MCPTool(
            "create_script",
            "Creates a new C# script under Assets/ from a boilerplate template (MonoBehaviour, PlainClass, or ScriptableObject). " +
            "path is relative to Assets/, e.g. 'Scripts/Enemy.cs'. The class name is derived from the file name and must be a " +
            "valid C# identifier. Fails if the file already exists — use update_script to modify an existing one. Triggers a " +
            "domain reload.",
            MCPLatencyTier.Slow,
            group: "scripting")]
        public static MCPResult CreateScript(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/, e.g. 'Scripts/Enemy.cs'. Class name is derived from the file name.")] string path,
            [MCPParam("Boilerplate to generate: MonoBehaviour, PlainClass, or ScriptableObject.")] MCPScriptTemplate template = MCPScriptTemplate.MonoBehaviour,
            [MCPParam("C# namespace to wrap the generated class in. Omit for no namespace.")] string namespaceName = null)
        {
            if (!PathLooksLikeScript(path, out var pathError)) return MCPResult.Fail(pathError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' already exists. Use update_script to modify it.");

            var className = Path.GetFileNameWithoutExtension(fullPath);
            if (!IsValidIdentifier(className))
                return MCPResult.Fail($"'{className}' (derived from the file name) is not a valid C# class identifier.");

            var content = MCPScriptTemplates.Render(template, className, namespaceName);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return MCPResult.Success(new { path, className });
        }

        [MCPTool("read_script", "Reads the full text content of an existing C# script under Assets/.", group: "scripting")]
        public static MCPResult ReadScript(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the script to read, e.g. 'Scripts/Enemy.cs'.")] string path)
        {
            if (!PathLooksLikeScript(path, out var pathError)) return MCPResult.Fail(pathError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' does not exist.");

            return MCPResult.Success(new { path, content = File.ReadAllText(fullPath) });
        }

        [MCPTool(
            "update_script",
            "Overwrites the full contents of an existing C# script under Assets/ with new content. Fails if the file does not " +
            "exist — use create_script for new files. Triggers a domain reload.",
            MCPLatencyTier.Slow,
            group: "scripting")]
        public static MCPResult UpdateScript(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the existing script to overwrite, e.g. 'Scripts/Enemy.cs'.")] string path,
            [MCPParam("Full new file content, replacing everything currently in the file.")] string content)
        {
            if (!PathLooksLikeScript(path, out var pathError)) return MCPResult.Fail(pathError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' does not exist. Use create_script to create it.");

            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return MCPResult.Success();
        }

        [MCPTool(
            "delete_script",
            "Deletes a C# script (and its .meta file) under Assets/, via AssetDatabase so Unity's asset bookkeeping stays " +
            "consistent. Not undoable via Ctrl+Z — Unity does not route asset deletion through the Undo system. Triggers a " +
            "domain reload if the script was in use.",
            MCPLatencyTier.Slow,
            destructive: true,
            group: "scripting")]
        public static MCPResult DeleteScript(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the script to delete, e.g. 'Scripts/Enemy.cs'.")] string path)
        {
            if (!PathLooksLikeScript(path, out var pathError)) return MCPResult.Fail(pathError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, path, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{path}' does not exist.");

            var assetPath = "Assets/" + path.Replace('\\', '/');
            if (!AssetDatabase.DeleteAsset(assetPath))
                return MCPResult.Fail($"AssetDatabase failed to delete '{assetPath}'.");

            return MCPResult.Success();
        }

        [MCPTool(
            "list_scripts",
            "Lists C# script paths (relative to Assets/) under an optional subfolder filter. Omit underPath to list every " +
            "script in the project.",
            group: "scripting")]
        public static MCPResult ListScripts(
            MCPToolContext ctx,
            [MCPParam("Subfolder under Assets/ to search, e.g. 'Scripts/Enemies'. Omit to search the whole project.")] string underPath = null)
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

            var scripts = Directory.GetFiles(searchRoot, "*.cs", SearchOption.AllDirectories)
                .Select(f => MCPProjectUtil.MakeRelativeToAssets(assetsRoot, f))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            return MCPResult.Success(new { scripts });
        }

        [MCPTool(
            "get_compile_status",
            "Returns whether the Editor is currently compiling scripts, plus structured errors/warnings from the most recent " +
            "compilation. Poll this after create_script/update_script/delete_script before relying on the change having taken " +
            "effect.",
            group: "scripting")]
        public static MCPResult GetCompileStatus(MCPToolContext ctx)
        {
            var messages = MCPCompileStatus.GetMessages();
            var errors = messages.Where(m => m.type == "Error").ToList();
            var warnings = messages.Where(m => m.type == "Warning").ToList();
            var lastFinished = MCPCompileStatus.LastCompileFinishedAt;

            return MCPResult.Success(new
            {
                isCompiling = EditorApplication.isCompiling,
                errorCount = errors.Count,
                warningCount = warnings.Count,
                errors,
                warnings,
                lastCompileFinishedAt = lastFinished == DateTime.MinValue ? null : lastFinished.ToString("o")
            });
        }

        private static bool PathLooksLikeScript(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = "path must end with '.cs' — these tools are script-specific, not a general file-write primitive.";
                return false;
            }
            return true;
        }

        private static bool IsValidIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            for (int i = 1; i < s.Length; i++)
                if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
            return true;
        }
    }
}
