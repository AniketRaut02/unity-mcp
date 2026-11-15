using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private const long MaxSizeBytes = 10 * 1024 * 1024;
        private const int KeepRotatedFiles = 3;
        public const int MaxValueChars = 1024;

        private static string LogDir => Path.Combine(Application.dataPath, "..", "Library", "MCP");
        private static string LogPath => Path.Combine(LogDir, "audit.log");
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
                argsJson ?? "", // each value already cut to MaxValueChars by the caller (TruncateValues)
                Truncate(error, MaxValueChars)
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
        // audit.log -> audit.log.1 -> audit.log.2 -> audit.log.3 -> deleted
        private static void RotateIfNeeded()
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxSizeBytes) return;

            var oldest = LogPath + "." + KeepRotatedFiles;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = KeepRotatedFiles - 1; i >= 1; i--)
            {
                var from = LogPath + "." + i;
                if (File.Exists(from)) File.Move(from, LogPath + "." + (i + 1));
            }
            File.Move(LogPath, LogPath + ".1");
            // Any failure here is caught by Record()'s own try/catch like any other write failure.
        }

        /// <summary>
        /// Serializes tool arguments for the log with every string value cut to MaxValueChars, so
        /// a whole script body never lands in the log but every argument name still does.
        /// </summary>
        public static string TruncateValues(object args)
        {
            try
            {
                var token = JToken.FromObject(args ?? new object());
                var values = token is JContainer container ? container.Descendants().OfType<JValue>().ToList() : new System.Collections.Generic.List<JValue>();
                foreach (var v in values)
                {
                    if (v.Type == JTokenType.String)
                    {
                        var text = (string)v.Value;
                        if (text.Length > MaxValueChars) v.Value = text.Substring(0, MaxValueChars) + "...(truncated)";
                    }
                }
                return token.ToString(Formatting.None);
            }
            catch
            {
                return "<unserializable args>";
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...(truncated)";
        }
    }
}
