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
    ///
    /// Rotated at MaxSizeBytes rather than left to grow forever: an agent session can
    /// run for hours and easily rack up tens of thousands of tool calls, each adding a
    /// line here with no natural end to the file. Left uncapped, this was exactly the
    /// kind of quiet, unbounded disk growth a long-running MCP session could produce
    /// without the user ever seeing an obvious cause. One rotated backup (audit.log.1)
    /// is kept so recent history survives a rotation instead of being discarded outright.
    /// </summary>
    internal static class MCPAuditLog
    {
        private const long MaxSizeBytes = 5 * 1024 * 1024;

        private static string LogDir => Path.Combine(Application.dataPath, "..", "Library", "MCP");
        private static string LogPath => Path.Combine(LogDir, "audit.log");
        private static string RotatedLogPath => Path.Combine(LogDir, "audit.log.1");
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
                    Directory.CreateDirectory(LogDir);
                    RotateIfNeeded();
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

        /// <summary>
        /// Swaps audit.log out to audit.log.1 (discarding whatever backup was already
        /// there) once it crosses MaxSizeBytes, so the next Record() call starts a fresh,
        /// empty file. Called under _lock, right before the append that would otherwise
        /// keep growing an oversized file indefinitely.
        /// </summary>
        private static void RotateIfNeeded()
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxSizeBytes) return;

            try
            {
                File.Delete(RotatedLogPath);
            }
            catch
            {
                // No pre-existing backup, or couldn't remove it -- Move below will throw
                // if that actually blocks the rotation, which is caught by Record()'s
                // own try/catch same as any other audit-log write failure.
            }

            File.Move(LogPath, RotatedLogPath);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...(truncated)";
        }
    }
}
