using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace UnityMCP.Security
{
    /// <summary>
    /// Append-only log of every tool call: who asked for what, whether it succeeded,
    /// how long it took. This is both a debugging aid during development and the
    /// accountability trail called for in the architecture plan — every AI-driven
    /// action against the Editor is on the record in Library/MCP/audit.log.
    /// </summary>
    internal static class MCPAuditLog
    {
        private static string LogPath => Path.Combine(Application.dataPath, "..", "Library", "MCP", "audit.log");
        private static readonly object _lock = new object();

        public static void Record(string requestId, string tool, string argsJson, bool ok, string error, long elapsedMs)
        {
            var line = string.Join(
                "\t",
                DateTime.UtcNow.ToString("o"),
                requestId,
                tool,
                ok ? "OK" : "FAIL",
                elapsedMs + "ms",
                Truncate(argsJson, 500),
                Truncate(error, 500)
            );

            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception e)
                {
                    // Never let audit logging itself break a tool call — surface the failure
                    // to the Console and move on.
                    Debug.LogWarning($"[MCP] Failed to write audit log entry: {e.Message}");
                }
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...(truncated)";
        }
    }
}
