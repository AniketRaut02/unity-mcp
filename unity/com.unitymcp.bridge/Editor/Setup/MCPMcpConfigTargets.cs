using System;
using System.IO;

namespace UnityMCP.Setup
{
    public enum MCPConfigFormat
    {
        Json,
        Toml
    }

    /// <summary>
    /// Where each client's MCP config file lives, and its format. Project-scoped for every
    /// client except Claude Desktop, which only has one global config (so each Unity project
    /// gets its own "unity-&lt;Project&gt;" entry in it).
    ///
    /// Antigravity is new enough that its file location varies across sources; this uses the
    /// most consistently cited one (.agents/mcp_config.json) and is worth rechecking if its
    /// docs move it. Claude Desktop's Windows path is the standard installer's (%APPDATA%\Claude).
    /// </summary>
    public static class MCPMcpConfigTargets
    {
        public static string[] RelativePathSegments(MCPClientKind kind)
        {
            switch (kind)
            {
                case MCPClientKind.ClaudeCode: return new[] { ".mcp.json" };
                case MCPClientKind.Cursor: return new[] { ".cursor", "mcp.json" };
                case MCPClientKind.Antigravity: return new[] { ".agents", "mcp_config.json" };
                case MCPClientKind.Codex: return new[] { ".codex", "config.toml" };
                case MCPClientKind.VSCode: return new[] { ".vscode", "mcp.json" };
                default: return null; // Claude Desktop: global, see AbsolutePath
            }
        }

        public static bool IsGlobal(MCPClientKind kind) => kind == MCPClientKind.ClaudeDesktop;

        public static MCPConfigFormat Format(MCPClientKind kind)
        {
            return kind == MCPClientKind.Codex ? MCPConfigFormat.Toml : MCPConfigFormat.Json;
        }

        /// <summary>The JSON object that holds server entries: VS Code calls it "servers", everyone else "mcpServers".</summary>
        public static string JsonRootKey(MCPClientKind kind) => kind == MCPClientKind.VSCode ? "servers" : "mcpServers";

        /// <summary>VS Code requires an explicit transport type on each entry; the others infer stdio from "command".</summary>
        public static string EntryType(MCPClientKind kind) => kind == MCPClientKind.VSCode ? "stdio" : null;

        public static string AbsolutePath(MCPClientKind kind, string projectRoot)
        {
            if (kind == MCPClientKind.ClaudeDesktop) return ClaudeDesktopConfigPath();

            var segments = RelativePathSegments(kind);
            if (segments == null) return null;

            var combined = projectRoot;
            foreach (var segment in segments)
            {
                combined = Path.Combine(combined, segment);
            }
            return combined;
        }

        /// <summary>The path as a display string (e.g. ".cursor/mcp.json", or the full path for a global config).</summary>
        public static string RelativePathDisplay(MCPClientKind kind)
        {
            if (kind == MCPClientKind.ClaudeDesktop) return ClaudeDesktopConfigPath();
            var segments = RelativePathSegments(kind);
            return segments == null ? null : string.Join("/", segments);
        }

        /// <summary>Claude Desktop's own config folder (its location doubles as the "installed" signal).</summary>
        public static string ClaudeDesktopConfigDir()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
                case PlatformID.MacOSX:
                    return Path.Combine(Home(), "Library", "Application Support", "Claude");
                default:
                    // Mono reports macOS as Unix: tell them apart by the macOS-only folder.
                    var mac = Path.Combine(Home(), "Library", "Application Support", "Claude");
                    return Directory.Exists(Path.Combine(Home(), "Library", "Application Support"))
                        ? mac
                        : Path.Combine(Home(), ".config", "Claude");
            }
        }

        private static string ClaudeDesktopConfigPath() => Path.Combine(ClaudeDesktopConfigDir(), "claude_desktop_config.json");

        internal static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
