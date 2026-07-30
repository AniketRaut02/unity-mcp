using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityMCP.Groups
{
    /// <summary>
    /// Reads Library/MCP/live_tool_state.json -- written by the Python server (groups.py's
    /// _write_live_state) every time manage_tools actually activates/deactivates a group in
    /// a live session, plus once at process startup. This is the ONLY way the Tool Groups
    /// window can know what an AI session's active groups genuinely are right now, as
    /// opposed to tool_groups_config.json's defaultActiveGroups, which only seeds a brand
    /// new session and never reflects what a live one did with manage_tools -- there is no
    /// live RPC channel Unity could use to ask an already-running Python process for this on
    /// demand (the bridge protocol is Python-initiates-request/Unity-responds only, the same
    /// reason tool_manifest.json exists as a file rather than a call).
    ///
    /// If more than one AI client is connected to this project at once, each writes its own
    /// process's state to the same file, so only the most recently updated one is visible
    /// here -- acceptable for a human-facing status display (surfaced via LastWriterPid so
    /// the window can at least say "as of session pid N" rather than imply one true answer).
    /// </summary>
    public static class MCPLiveStateReader
    {
        private static string StatePath => Path.Combine(Application.dataPath, "..", "Library", "MCP", "live_tool_state.json");

        private class LiveStateData
        {
            public int pid;
            public List<string> activeGroups;
            public string updatedAt;
        }

        private static LiveStateData _cache;
        private static DateTime _cacheFileWriteTimeUtc;

        private static LiveStateData Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;

                var writeTimeUtc = File.GetLastWriteTimeUtc(StatePath);
                if (_cache != null && writeTimeUtc == _cacheFileWriteTimeUtc) return _cache;

                var json = File.ReadAllText(StatePath);
                _cache = JsonConvert.DeserializeObject<LiveStateData>(json);
                _cacheFileWriteTimeUtc = writeTimeUtc;
                return _cache;
            }
            catch
            {
                // Missing/malformed/mid-write file just means "live state isn't available
                // yet" -- not an error worth surfacing beyond the window falling back to
                // default-active display.
                return null;
            }
        }

        public static bool IsAvailable => Load() != null;

        public static int? LastWriterPid => Load()?.pid;

        public static string UpdatedAt => Load()?.updatedAt;

        private static readonly HashSet<string> Empty = new HashSet<string>();

        public static HashSet<string> ActiveGroups
        {
            get
            {
                var data = Load();
                if (data == null || data.activeGroups == null) return Empty;
                return new HashSet<string>(data.activeGroups);
            }
        }
    }
}
