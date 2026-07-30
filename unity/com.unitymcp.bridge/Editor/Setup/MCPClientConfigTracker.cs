using System;
using System.Globalization;
using UnityEditor;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Remembers which client (Claude Code, Codex, Cursor, Antigravity) the Setup
    /// window's Configure button was most recently clicked for, and when -- purely a
    /// convenience so reopening the window after a while shows "what did I last set
    /// up here" instead of nothing, especially useful once there are four clients
    /// listed and it's easy to forget which one you already pointed at this project.
    ///
    /// Persisted via EditorPrefs (survives domain reloads AND Editor restarts, unlike
    /// SessionState -- this is meant to answer "what did I do last session", not just
    /// "what happened since the last reload"), keyed per-project like every other
    /// Setup-window preference.
    /// </summary>
    public static class MCPClientConfigTracker
    {
        private static string ClientPrefsKey => "UnityMCP.LastConfiguredClient." + MCPProjectUtil.ProjectRoot;
        private static string TimePrefsKey => "UnityMCP.LastConfiguredAt." + MCPProjectUtil.ProjectRoot;

        public static void RecordConfigured(MCPClientKind kind, DateTime utcNow)
        {
            EditorPrefs.SetString(ClientPrefsKey, kind.ToString());
            EditorPrefs.SetString(TimePrefsKey, utcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        /// <summary>False if no client has ever been configured for this project (or the stored value is unreadable), rather than throwing.</summary>
        public static bool TryGetLastConfigured(out MCPClientKind kind, out DateTime configuredAtUtc)
        {
            kind = default;
            configuredAtUtc = default;

            var kindString = EditorPrefs.GetString(ClientPrefsKey, "");
            var timeString = EditorPrefs.GetString(TimePrefsKey, "");

            if (string.IsNullOrEmpty(kindString) || string.IsNullOrEmpty(timeString)) return false;
            if (!Enum.TryParse(kindString, out kind)) return false;
            if (!DateTime.TryParse(timeString, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out configuredAtUtc)) return false;

            return true;
        }

        /// <summary>
        /// Human-friendly "how long ago" string ("just now", "5 minutes ago", "3 hours
        /// ago", "2 days ago"), falling back to an absolute date once it's old enough
        /// that a relative phrase stops being more useful than the actual date. A pure
        /// function of two DateTimes (not DateTime.UtcNow internally) so it's testable
        /// without EditorPrefs or real wall-clock time.
        /// </summary>
        public static string FormatRelativeTime(DateTime utcTime, DateTime nowUtc)
        {
            var elapsed = nowUtc - utcTime;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero; // clock-skew guard -- never show a negative age

            if (elapsed < TimeSpan.FromSeconds(60)) return "just now";

            if (elapsed < TimeSpan.FromMinutes(60))
            {
                int m = (int)elapsed.TotalMinutes;
                return $"{m} minute{(m == 1 ? "" : "s")} ago";
            }

            if (elapsed < TimeSpan.FromHours(24))
            {
                int h = (int)elapsed.TotalHours;
                return $"{h} hour{(h == 1 ? "" : "s")} ago";
            }

            if (elapsed < TimeSpan.FromDays(30))
            {
                int d = (int)elapsed.TotalDays;
                return $"{d} day{(d == 1 ? "" : "s")} ago";
            }

            return utcTime.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
