using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Support
{
    /// <summary>
    /// Caches Editor log messages via Application.logMessageReceived, backing
    /// the read_console_log / clear_console_log tools. Deliberately avoids
    /// reflecting into UnityEditorInternal.LogEntries' entry-enumeration API
    /// (its GetEntryInternal signature is not stable across Unity versions);
    /// the one exception is LogEntries.Clear(), which has been a stable,
    /// argument-less static method for a decade and is low-risk to call via
    /// reflection.
    ///
    /// Known limitation: this cache only holds messages logged after this
    /// static constructor last ran, so it resets on every domain reload — it
    /// will not show Console history from before the bridge (re)loaded.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPConsoleCache
    {
        internal struct CachedLog
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public DateTime timestampUtc;
        }

        private const int MaxEntries = 2000;
        private static readonly List<CachedLog> _entries = new List<CachedLog>();

        internal static IReadOnlyList<CachedLog> Entries => _entries;

        static MCPConsoleCache()
        {
            Application.logMessageReceived += OnLogMessage;
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            _entries.Add(new CachedLog
            {
                message = message,
                stackTrace = stackTrace,
                type = type,
                timestampUtc = DateTime.UtcNow
            });

            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }

        internal static void Clear() => _entries.Clear();

        /// <summary>
        /// Best-effort clear of the real Unity Console window via reflection
        /// into the internal LogEntries.Clear() method. Returns false rather
        /// than throwing if the method can't be found (e.g. a future Unity
        /// version renames it) — callers should treat that as "cache
        /// cleared, visible Console unchanged" rather than a hard failure.
        /// </summary>
        internal static bool TryClearRealConsole()
        {
            try
            {
                var logEntriesType = Type.GetType("UnityEditorInternal.LogEntries,UnityEditor");
                var clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
                if (clearMethod == null) return false;
                clearMethod.Invoke(null, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
