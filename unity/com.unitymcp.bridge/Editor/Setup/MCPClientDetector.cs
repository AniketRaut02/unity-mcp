using System.Collections.Generic;

namespace UnityMCP.Setup
{
    public enum MCPClientKind
    {
        ClaudeCode,
        Codex,
        Cursor,
        Antigravity
    }

    /// <summary>
    /// Client identity/naming logic only. This used to also build CLI commands
    /// (claude/codex mcp add) — that approach was replaced with writing each client's
    /// config file directly (see MCPMcpServersJsonWriter / MCPCodexTomlWriter /
    /// MCPMcpConfigTargets), which removes the dependency on the claude/codex CLIs
    /// being installed and avoids the CLI's own spawn-time working-directory behavior
    /// (the actual root cause of a ModuleNotFoundError this project hit in production).
    /// </summary>
    public static class MCPClientDetector
    {
        /// <summary>
        /// The server key name used in every generated config entry. Kept
        /// project-derived (not just "unity-mcp") so multiple Unity projects on one
        /// machine, all writing to their own project-scoped config files, still end up
        /// with distinct, recognizable names if a user ever compares configs side by
        /// side or merges one into a global config file by hand.
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
        }

        public static string DisplayName(MCPClientKind kind)
        {
            switch (kind)
            {
                case MCPClientKind.ClaudeCode: return "Claude Code";
                case MCPClientKind.Codex: return "Codex";
                case MCPClientKind.Cursor: return "Cursor";
                case MCPClientKind.Antigravity: return "Antigravity";
                default: return kind.ToString();
            }
        }
    }
}
