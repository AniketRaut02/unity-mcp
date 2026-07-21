using System.IO;

namespace UnityMCP.Setup
{
    public enum MCPConfigFormat
    {
        Json,
        Toml
    }

    /// <summary>
    /// Where each client's project-scoped MCP config file actually lives, and which
    /// format it's in. Verified against current documentation for each client rather
    /// than assumed — Antigravity in particular is new enough (relaunched May 2026,
    /// replacing Gemini CLI) that its exact file location has some inconsistency across
    /// sources as the product settles; this uses the most consistently-cited current
    /// convention (.agents/mcp_config.json), but it's the one entry in this table worth
    /// rechecking if Antigravity's own docs ever move it.
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
                default: return null;
            }
        }

        public static MCPConfigFormat Format(MCPClientKind kind)
        {
            return kind == MCPClientKind.Codex ? MCPConfigFormat.Toml : MCPConfigFormat.Json;
        }

        public static string AbsolutePath(MCPClientKind kind, string projectRoot)
        {
            var segments = RelativePathSegments(kind);
            if (segments == null) return null;

            var combined = projectRoot;
            foreach (var segment in segments)
            {
                combined = Path.Combine(combined, segment);
            }
            return combined;
        }

        /// <summary>The relative path as a display string (e.g. ".cursor/mcp.json"), for status messages.</summary>
        public static string RelativePathDisplay(MCPClientKind kind)
        {
            var segments = RelativePathSegments(kind);
            return segments == null ? null : string.Join("/", segments);
        }
    }
}
