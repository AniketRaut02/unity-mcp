using System.Collections.Generic;
using System.Text;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Reads/merges/writes the { "mcpServers": { ... } } JSON shape used by Claude Code
    /// (.mcp.json), Cursor, Antigravity and Claude Desktop, and VS Code's { "servers": { ... } }
    /// variant (rootKey "servers", plus "type": "stdio" on each entry).
    ///
    /// Only the key matching THIS server's name is added or replaced. Everything else is kept
    /// byte-for-byte: other servers, and every other top-level setting in the file (Claude
    /// Desktop's preferences, VS Code's "inputs", ...). If the existing file doesn't parse as
    /// the expected shape, this refuses to touch it rather than risk destroying content it
    /// can't safely understand.
    /// </summary>
    public static class MCPMcpServersJsonWriter
    {
        private const string DefaultRootKey = "mcpServers";

        public static string BuildServerEntryJson(string command, List<string> args, Dictionary<string, string> env, string type = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            if (!string.IsNullOrEmpty(type))
                sb.Append("      \"type\": \"" + EscapeJsonString(type) + "\",\n");
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

        public static bool IsConfigured(string existingContent, string serverName, string rootKey = DefaultRootKey)
        {
            if (!TryParse(existingContent, rootKey, out _, out var servers, out _)) return false;
            return servers.ContainsKey(serverName);
        }

        public static bool TryMerge(string existingContent, string serverName, string newEntryJson,
            out string newContent, out string error, string rootKey = DefaultRootKey)
        {
            newContent = null;

            if (!TryParse(existingContent, rootKey, out var topLevel, out var servers, out error))
            {
                return false;
            }

            servers[serverName] = newEntryJson;

            var serversJson = new StringBuilder();
            serversJson.Append("{\n");
            var keys = new List<string>(servers.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                serversJson.Append("    \"" + EscapeJsonString(keys[i]) + "\": " + servers[keys[i]].Trim());
                serversJson.Append(i < keys.Count - 1 ? ",\n" : "\n");
            }
            serversJson.Append("  }");
            topLevel[rootKey] = serversJson.ToString();

            var sb = new StringBuilder();
            sb.Append("{\n");
            var topKeys = new List<string>(topLevel.Keys);
            for (int i = 0; i < topKeys.Count; i++)
            {
                sb.Append("  \"" + EscapeJsonString(topKeys[i]) + "\": " + topLevel[topKeys[i]].Trim());
                sb.Append(i < topKeys.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("}\n");

            newContent = sb.ToString();
            return true;
        }

        private static bool TryParse(string existingContent, string rootKey,
            out Dictionary<string, string> topLevel, out Dictionary<string, string> servers, out string error)
        {
            topLevel = new Dictionary<string, string>();
            servers = new Dictionary<string, string>();
            error = null;

            if (string.IsNullOrWhiteSpace(existingContent))
            {
                return true; // nothing on disk yet -- empty maps, valid starting point
            }

            if (!MCPMiniJson.TryExtractObjectEntries(existingContent, out topLevel, out var topLevelError))
            {
                error = $"existing file is not valid JSON ({topLevelError}) — refusing to modify it automatically. Fix or remove it, then try again.";
                topLevel = new Dictionary<string, string>();
                return false;
            }

            if (!topLevel.TryGetValue(rootKey, out var serversRaw))
            {
                return true; // valid JSON, just no servers key yet -- empty map is correct
            }

            if (!MCPMiniJson.TryExtractObjectEntries(serversRaw, out servers, out var serversError))
            {
                error = $"existing '{rootKey}' value is not a valid JSON object ({serversError}) — refusing to modify it automatically. Fix or remove it, then try again.";
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
