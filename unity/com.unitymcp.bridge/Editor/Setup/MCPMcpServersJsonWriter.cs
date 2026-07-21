using System.Collections.Generic;
using System.Text;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Reads/merges/writes the { "mcpServers": { ... } } JSON shape shared by Claude
    /// Code (.mcp.json), Cursor (.cursor/mcp.json), and Antigravity (.agents/mcp_config.json)
    /// — one implementation for all three, since they use the identical schema, confirmed
    /// against current documentation for each rather than assumed.
    ///
    /// Other servers already present in the file (for other tools the user has
    /// configured) are preserved byte-for-byte, untouched — only the key matching THIS
    /// server's name is added or replaced. If the existing file doesn't parse as the
    /// expected shape, this refuses to touch it rather than risk destroying content it
    /// can't safely understand.
    /// </summary>
    public static class MCPMcpServersJsonWriter
    {
        private const string RootKey = "mcpServers";

        public static string BuildServerEntryJson(string command, List<string> args, Dictionary<string, string> env)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("      \"command\": \"" + EscapeJsonString(command) + "\",\n");
            sb.Append("      \"args\": [");
            for (int i = 0; i < args.Count; i++)
            {
                sb.Append("\"" + EscapeJsonString(args[i]) + "\"");
                if (i < args.Count - 1) sb.Append(", ");
            }
            sb.Append("],\n");
            sb.Append("      \"env\": {\n");
            var envKeys = new List<string>(env.Keys);
            for (int i = 0; i < envKeys.Count; i++)
            {
                sb.Append("        \"" + EscapeJsonString(envKeys[i]) + "\": \"" + EscapeJsonString(env[envKeys[i]]) + "\"");
                sb.Append(i < envKeys.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("      }\n");
            sb.Append("    }");
            return sb.ToString();
        }

        /// <summary>
        /// True if `serverName` already has an entry under mcpServers in `existingContent`.
        /// A parse failure is reported as "not configured" (false) rather than throwing —
        /// callers that need to distinguish "not configured" from "can't tell, file is
        /// malformed" should use TryMerge instead, which surfaces that distinction.
        /// </summary>
        public static bool IsConfigured(string existingContent, string serverName)
        {
            if (!TryGetServersMap(existingContent, out var servers, out _)) return false;
            return servers.ContainsKey(serverName);
        }

        /// <summary>
        /// Merges `newEntryJson` into `existingContent` under mcpServers[serverName],
        /// preserving every other existing entry untouched. `existingContent` may be
        /// null/empty (starts fresh). Returns false with a message in `error` — and
        /// leaves `newContent` null — if the existing content doesn't parse as the
        /// expected shape, rather than risk overwriting something this code can't
        /// safely understand.
        /// </summary>
        public static bool TryMerge(string existingContent, string serverName, string newEntryJson, out string newContent, out string error)
        {
            newContent = null;

            if (!TryGetServersMap(existingContent, out var servers, out error))
            {
                return false;
            }

            servers[serverName] = newEntryJson;

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"mcpServers\": {\n");
            var keys = new List<string>(servers.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                sb.Append("    \"" + EscapeJsonString(key) + "\": " + servers[key].Trim());
                sb.Append(i < keys.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  }\n");
            sb.Append("}\n");

            newContent = sb.ToString();
            return true;
        }

        private static bool TryGetServersMap(string existingContent, out Dictionary<string, string> servers, out string error)
        {
            servers = new Dictionary<string, string>();
            error = null;

            if (string.IsNullOrWhiteSpace(existingContent))
            {
                return true; // nothing on disk yet -- empty map, valid starting point
            }

            if (!MCPMiniJson.TryExtractObjectEntries(existingContent, out var topLevel, out var topLevelError))
            {
                error = $"existing file is not valid JSON ({topLevelError}) — refusing to modify it automatically. Fix or remove it, then try again.";
                return false;
            }

            if (!topLevel.TryGetValue(RootKey, out var serversRaw))
            {
                return true; // valid JSON, just no mcpServers key yet -- empty map is correct
            }

            if (!MCPMiniJson.TryExtractObjectEntries(serversRaw, out servers, out var serversError))
            {
                error = $"existing 'mcpServers' value is not a valid JSON object ({serversError}) — refusing to modify it automatically. Fix or remove it, then try again.";
                servers = new Dictionary<string, string>();
                return false;
            }

            return true;
        }

        private static string EscapeJsonString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
