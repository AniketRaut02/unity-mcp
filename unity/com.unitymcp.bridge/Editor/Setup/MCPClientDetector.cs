using System.Collections.Generic;
using System.IO;

namespace UnityMCP.Setup
{
    public enum MCPClientKind
    {
        ClaudeCode,
        Codex,
        Cursor,
        Antigravity,
        ClaudeDesktop,
        VSCode
    }

    public static class MCPClientDetector
    {
        /// <summary>
        /// The config entry name for this project: "unity-&lt;project folder&gt;", with anything but
        /// letters, digits and '-' replaced by '-'. Entries with this name are the ones the
        /// Setup window owns and keeps up to date (MCPClientConfigUpdater).
        /// </summary>
        public static string ServerName(string projectFolderName)
        {
            var sanitized = Sanitize(projectFolderName);
            return string.IsNullOrEmpty(sanitized) ? "unity" : $"unity-{sanitized}";
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var chars = new char[name.Length];
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                chars[i] = char.IsLetterOrDigit(c) || c == '-' ? c : '-';
            }
            return new string(chars);
        }

        public static IEnumerable<MCPClientKind> AllKinds()
        {
            yield return MCPClientKind.ClaudeCode;
            yield return MCPClientKind.Codex;
            yield return MCPClientKind.Cursor;
            yield return MCPClientKind.Antigravity;
            yield return MCPClientKind.VSCode;
            yield return MCPClientKind.ClaudeDesktop;
        }

        public static string DisplayName(MCPClientKind kind)
        {
            switch (kind)
            {
                case MCPClientKind.ClaudeCode: return "Claude Code";
                case MCPClientKind.Codex: return "Codex";
                case MCPClientKind.Cursor: return "Cursor";
                case MCPClientKind.Antigravity: return "Antigravity";
                case MCPClientKind.VSCode: return "VS Code (Copilot)";
                case MCPClientKind.ClaudeDesktop: return "Claude Desktop";
                default: return kind.ToString();
            }
        }

        /// <summary>
        /// Best-effort "is this client installed for this user": the folder each client creates
        /// in the user's home on first run. Used only to pick what "Configure all" touches;
        /// any client can still be configured by hand.
        /// </summary>
        public static bool IsDetected(MCPClientKind kind)
        {
            var home = MCPMcpConfigTargets.Home();
            switch (kind)
            {
                case MCPClientKind.ClaudeCode: return Directory.Exists(Path.Combine(home, ".claude"));
                case MCPClientKind.Codex: return Directory.Exists(Path.Combine(home, ".codex"));
                case MCPClientKind.Cursor: return Directory.Exists(Path.Combine(home, ".cursor"));
                case MCPClientKind.Antigravity: return Directory.Exists(Path.Combine(home, ".gemini"));
                case MCPClientKind.VSCode: return Directory.Exists(Path.Combine(home, ".vscode"));
                case MCPClientKind.ClaudeDesktop: return Directory.Exists(MCPMcpConfigTargets.ClaudeDesktopConfigDir());
                default: return false;
            }
        }

        /// <summary>Clients that read AGENTS.md for project guidance (Claude Code gets the SKILL.md instead; Claude Desktop has no project).</summary>
        public static bool ReadsAgentsMd(MCPClientKind kind) =>
            kind == MCPClientKind.Codex || kind == MCPClientKind.Cursor ||
            kind == MCPClientKind.Antigravity || kind == MCPClientKind.VSCode;
    }
}
