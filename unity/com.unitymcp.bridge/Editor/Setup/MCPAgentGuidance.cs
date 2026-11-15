using System.IO;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Puts usage guidance where each client's agent reads it, on Configure:
    ///   - Claude Code: the package's Skills~/unity-mcp/SKILL.md copied to .claude/skills/unity-mcp/.
    ///   - Codex, Cursor, Antigravity, VS Code: a marked section in the project's AGENTS.md. Only the
    ///     text between the markers is ever replaced; everything else in AGENTS.md is the user's.
    /// Both are refreshed when the package updates (MCPClientConfigUpdater).
    /// </summary>
    public static class MCPAgentGuidance
    {
        public const string BeginMarker = "<!-- unity-mcp:begin (managed by Unity MCP Bridge; text between these markers is replaced on update) -->";
        public const string EndMarker = "<!-- unity-mcp:end -->";

        public const string AgentsSection =
            "## Unity Editor (Unity MCP Bridge)\n\n" +
            "This project's Unity Editor is controlled through the Unity MCP Bridge MCP server.\n\n" +
            "- Only the `core` tool group is visible at first. Find tools with `manage_tools(action=\"search\", query=...)`; " +
            "use one or two hidden tools with `manage_tools` `describe` then `call`, or `activate` a group for heavier use.\n" +
            "- Group several quick changes into one `batch_execute`. Script changes recompile Unity: poll `get_compile_status` " +
            "until it is done, then check `get_console_logs`.\n" +
            "- Reads are summary-first (`get_hierarchy` shows the top level; expand with `path`/`depth`; long lists page with `offset`).\n" +
            "- A reply with `status: \"awaiting_approval\"` means the tool has NOT run: ask the user to approve it in Unity " +
            "(Window > Unity MCP > Approvals), then call it again with the same arguments. Never retry a denied call.\n" +
            "- Only pass `confirm: true` to destructive tools when the user asked for that change.\n";

        /// <summary>Returns the path written, or null when the package's skill file can't be found.</summary>
        public static string InstallClaudeSkill(string packageRoot, string projectRoot)
        {
            var source = Path.Combine(packageRoot, "Skills~", "unity-mcp", "SKILL.md");
            if (!File.Exists(source)) return null;

            var target = Path.Combine(projectRoot, ".claude", "skills", "unity-mcp", "SKILL.md");
            var content = File.ReadAllText(source);
            if (!File.Exists(target) || File.ReadAllText(target) != content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, content);
            }
            return target;
        }

        public static string UpdateAgentsMd(string projectRoot)
        {
            var path = Path.Combine(projectRoot, "AGENTS.md");
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            var merged = MergeAgentsMd(existing, AgentsSection);
            if (merged != existing) File.WriteAllText(path, merged);
            return path;
        }

        /// <summary>Pure: replaces the managed section if present, otherwise appends it. Never touches text outside the markers.</summary>
        public static string MergeAgentsMd(string existing, string section)
        {
            var block = BeginMarker + "\n" + section.TrimEnd('\n') + "\n" + EndMarker;
            if (string.IsNullOrEmpty(existing)) return block + "\n";

            int begin = existing.IndexOf(BeginMarker, System.StringComparison.Ordinal);
            int end = begin < 0 ? -1 : existing.IndexOf(EndMarker, begin, System.StringComparison.Ordinal);
            if (begin >= 0 && end >= 0)
                return existing.Substring(0, begin) + block + existing.Substring(end + EndMarker.Length);

            return existing.TrimEnd('\n') + "\n\n" + block + "\n";
        }
    }
}
