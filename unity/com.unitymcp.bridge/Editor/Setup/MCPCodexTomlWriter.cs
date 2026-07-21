using System;
using System.Collections.Generic;
using System.Text;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Builds and merges the [mcp_servers.&lt;name&gt;] section Codex reads from
    /// .codex/config.toml. TOML is genuinely harder to parse/merge robustly than JSON
    /// without a real library, which this project doesn't want to take on as a
    /// dependency for one feature — so instead of a full parser, this only needs to find
    /// the boundaries of OUR OWN section (which has a known, generated shape) and splice
    /// around it, never attempting to understand any other table a human or another tool
    /// added to the file.
    /// </summary>
    public static class MCPCodexTomlWriter
    {
        public static string BuildServerSection(string serverName, string command, List<string> args, Dictionary<string, string> env)
        {
            var sb = new StringBuilder();
            sb.Append($"[mcp_servers.{serverName}]\n");
            sb.Append($"command = \"{EscapeTomlString(command)}\"\n");
            sb.Append("args = [");
            for (int i = 0; i < args.Count; i++)
            {
                sb.Append($"\"{EscapeTomlString(args[i])}\"");
                if (i < args.Count - 1) sb.Append(", ");
            }
            sb.Append("]\n");

            if (env.Count > 0)
            {
                sb.Append("\n");
                sb.Append($"[mcp_servers.{serverName}.env]\n");
                foreach (var kv in env)
                {
                    sb.Append($"{kv.Key} = \"{EscapeTomlString(kv.Value)}\"\n");
                }
            }

            return sb.ToString();
        }

        public static bool IsConfigured(string existingContent, string serverName)
        {
            if (string.IsNullOrEmpty(existingContent)) return false;
            return existingContent.Contains(SectionHeader(serverName));
        }

        /// <summary>
        /// Idempotent: if our section already exists, replaces JUST that bounded span
        /// (from our own header up to the next top-level table header, or EOF) with the
        /// freshly-generated section — so re-running Configure after changing paths
        /// actually updates the file instead of leaving stale values in place. If it
        /// doesn't exist yet, appends it. Never touches content outside that bounded
        /// span, so any OTHER [mcp_servers.*] entries or unrelated tables are left
        /// completely alone.
        /// </summary>
        public static string Merge(string existingContent, string serverName, string newSection)
        {
            if (string.IsNullOrEmpty(existingContent))
            {
                return newSection;
            }

            var header = SectionHeader(serverName);
            int headerIndex = existingContent.IndexOf(header, StringComparison.Ordinal);

            if (headerIndex < 0)
            {
                var separator = existingContent.EndsWith("\n\n") ? "" : (existingContent.EndsWith("\n") ? "\n" : "\n\n");
                return existingContent + separator + newSection;
            }

            int searchFrom = headerIndex + header.Length;
            int nextTopLevelTable = FindNextTopLevelTableStart(existingContent, searchFrom, serverName);
            int spanEnd = nextTopLevelTable >= 0 ? nextTopLevelTable : existingContent.Length;

            var before = existingContent.Substring(0, headerIndex);
            var after = existingContent.Substring(spanEnd);

            return before + newSection + (after.Length == 0 || after.StartsWith("\n") ? after : "\n" + after);
        }

        private static string SectionHeader(string serverName) => $"[mcp_servers.{serverName}]";

        /// <summary>
        /// Scans forward line-by-line from `fromIndex` for the next line that starts a
        /// new top-level TOML table ("[" at the start of a line) — skipping over our own
        /// nested [mcp_servers.name.env] table, which is still part of OUR section, not
        /// a boundary. Returns -1 if nothing else follows (end of file).
        /// </summary>
        private static int FindNextTopLevelTableStart(string content, int fromIndex, string ownServerName)
        {
            var ownEnvHeader = $"[mcp_servers.{ownServerName}.env]";
            int i = fromIndex;
            while (true)
            {
                int newlineIndex = content.IndexOf('\n', i);
                if (newlineIndex < 0) return -1;
                int lineStart = newlineIndex + 1;
                if (lineStart >= content.Length) return -1;

                if (content[lineStart] == '[')
                {
                    bool isOwnEnvTable = content.Length - lineStart >= ownEnvHeader.Length &&
                                          content.Substring(lineStart, ownEnvHeader.Length) == ownEnvHeader;
                    if (isOwnEnvTable)
                    {
                        i = lineStart + ownEnvHeader.Length;
                        continue;
                    }
                    return lineStart;
                }

                i = lineStart;
            }
        }

        private static string EscapeTomlString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
