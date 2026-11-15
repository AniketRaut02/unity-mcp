using System;
using System.IO;
using UnityEngine;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Writes one client's config entry for this project (plus its agent guidance). Shared by the
    /// Setup window's Configure / Configure all buttons and by MCPClientConfigUpdater, so a manual
    /// configure and an automatic refresh can never disagree about what gets written.
    /// Main thread only.
    /// </summary>
    public static class MCPClientConfigurator
    {
        public struct Outcome
        {
            public bool Ok;
            public bool Changed;
            public string Message;
        }

        public static bool IsWindows => Application.platform == RuntimePlatform.WindowsEditor;

        public static string ServerName() => MCPClientDetector.ServerName(new DirectoryInfo(MCPProjectUtil.ProjectRoot).Name);

        public static bool HasOurEntry(MCPClientKind kind)
        {
            var configPath = MCPMcpConfigTargets.AbsolutePath(kind, MCPProjectUtil.ProjectRoot);
            if (configPath == null || !File.Exists(configPath)) return false;

            string content;
            try { content = File.ReadAllText(configPath); }
            catch { return false; }

            return MCPMcpConfigTargets.Format(kind) == MCPConfigFormat.Toml
                ? MCPCodexTomlWriter.IsConfigured(content, ServerName())
                : MCPMcpServersJsonWriter.IsConfigured(content, ServerName(), MCPMcpConfigTargets.JsonRootKey(kind));
        }

        public static Outcome Configure(MCPClientKind kind, bool recordAsLastConfigured = true)
        {
            var displayName = MCPClientDetector.DisplayName(kind);
            var uv = MCPUvLocator.Find(IsWindows);
            if (uv == null)
                return Fail($"{displayName}: uv is not installed. Install it with the command shown above, then try again.");

            var serverDir = MCPServerEntryBuilder.BundledServerDirectory();
            if (serverDir == null)
                return Fail($"{displayName}: the bundled server (Server~ in the Unity MCP Bridge package) was not found. Reinstall the package.");

            var projectRoot = MCPProjectUtil.ProjectRoot;
            var serverName = ServerName();
            var configPath = MCPMcpConfigTargets.AbsolutePath(kind, projectRoot);
            var shownPath = MCPMcpConfigTargets.RelativePathDisplay(kind);

            string existing = null;
            if (File.Exists(configPath))
            {
                try { existing = File.ReadAllText(configPath); }
                catch (Exception e) { return Fail($"{displayName}: could not read {shownPath} — {e.Message}"); }
            }

            var args = MCPServerEntryBuilder.Args(serverDir);
            var env = MCPServerEntryBuilder.Env(projectRoot, serverDir);

            string updated;
            if (MCPMcpConfigTargets.Format(kind) == MCPConfigFormat.Toml)
            {
                var section = MCPCodexTomlWriter.BuildServerSection(serverName, uv, args, env);
                updated = MCPCodexTomlWriter.Merge(existing, serverName, section);
            }
            else
            {
                var entry = MCPMcpServersJsonWriter.BuildServerEntryJson(uv, args, env, MCPMcpConfigTargets.EntryType(kind));
                if (!MCPMcpServersJsonWriter.TryMerge(existing, serverName, entry, out updated, out var mergeError, MCPMcpConfigTargets.JsonRootKey(kind)))
                    return Fail($"{displayName}: {mergeError}");
            }

            bool changed = updated != existing;
            try
            {
                if (changed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                    File.WriteAllText(configPath, updated);
                }

                var packageRoot = Path.GetDirectoryName(serverDir);
                if (kind == MCPClientKind.ClaudeCode) MCPAgentGuidance.InstallClaudeSkill(packageRoot, projectRoot);
                if (MCPClientDetector.ReadsAgentsMd(kind)) MCPAgentGuidance.UpdateAgentsMd(projectRoot);
            }
            catch (Exception e)
            {
                return Fail($"{displayName}: failed to write {shownPath} — {e.Message}");
            }

            if (recordAsLastConfigured) MCPClientConfigTracker.RecordConfigured(kind, DateTime.UtcNow);

            var message = $"{displayName}: wrote '{serverName}' to {shownPath}. Restart your {displayName} session to pick it up.";
            if (kind == MCPClientKind.Codex)
                message += " Codex only loads project config for directories you've marked as trusted.";
            if (!MCPMcpConfigTargets.IsGlobal(kind))
                message += $" The file holds machine-specific paths: add {shownPath} to .gitignore rather than committing it.";
            return new Outcome { Ok = true, Changed = changed, Message = message };
        }

        private static Outcome Fail(string message) => new Outcome { Ok = false, Message = message };
    }
}
