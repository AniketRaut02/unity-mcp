using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityMCP.Groups
{
    /// <summary>
    /// Library/MCP/tool_groups_config.json -- the single source of truth for two Unity-
    /// side, human-only controls exposed by the Tool Groups window (Window > Unity MCP >
    /// Tool Groups): disabled groups and default-active groups. Unity is the sole writer;
    /// Python's groups.py is the sole reader, re-checking this file's mtime on a short
    /// throttle rather than needing any live push over the bridge socket, since a live
    /// RPC channel from Unity into an already-running Python process doesn't exist in
    /// this protocol (Python is always the one that initiates a request; Unity only ever
    /// responds) -- see the file for the read side and why disabling gets applied
    /// immediately to already-running sessions while default-active groups only seed a
    /// fresh one.
    ///
    /// "core" can never be disabled -- enforced here defensively (SetDisabled silently
    /// refuses it) in addition to the window's UI graying out the button, and Python's
    /// groups.py refuses it a third time on its side. No single one of those three is
    /// trusted alone.
    /// </summary>
    internal class MCPToolGroupConfigData
    {
        public List<string> disabledGroups = new List<string>();
        public List<string> defaultActiveGroups = new List<string>();

        /// <summary>
        /// When true, every tool call whose ReadOnly attribute isn't literally true is
        /// refused (both atomic, in MCPToolRegistry.Invoke, and composite, in Python's
        /// server.py call_tool) -- a blanket "look but don't touch" switch for handing an
        /// AI a project when a human wants to observe without risking any mutation. See
        /// docs/tool-scaling-strategy.md section 9.
        /// </summary>
        public bool readOnlyMode = false;

        /// <summary>
        /// Package identifier patterns manage_packages(action:"add") is allowed to install
        /// (exact match, or a "prefix.*" wildcard, e.g. "com.unity.*"). Empty (the default)
        /// means no restriction -- today's behavior, so nobody is surprised by this unless
        /// they explicitly opt in by populating it from the Tool Groups window. See
        /// docs/tool-scaling-strategy.md section 9's "secondary, lower-priority hardening".
        /// </summary>
        public List<string> packageAllowlist = new List<string>();
    }

    public static class MCPToolGroupConfig
    {
        public const string CoreGroup = "core";

        private static string ConfigDir => Path.Combine(Application.dataPath, "..", "Library", "MCP");
        private static string ConfigFilePath => Path.Combine(ConfigDir, "tool_groups_config.json");

        private static MCPToolGroupConfigData _cache;

        private static MCPToolGroupConfigData Load()
        {
            if (_cache != null) return _cache;

            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    _cache = JsonConvert.DeserializeObject<MCPToolGroupConfigData>(json) ?? new MCPToolGroupConfigData();
                }
                else
                {
                    _cache = new MCPToolGroupConfigData();
                }
            }
            catch
            {
                // Malformed/unreadable file -- fall back to "nothing disabled, no default
                // overrides" rather than let a corrupt file break every tool call.
                _cache = new MCPToolGroupConfigData();
            }

            _cache.disabledGroups ??= new List<string>();
            _cache.defaultActiveGroups ??= new List<string>();
            _cache.packageAllowlist ??= new List<string>();
            _cache.disabledGroups.Remove(CoreGroup);
            return _cache;
        }

        private static void Save(MCPToolGroupConfigData data)
        {
            _cache = data;
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigFilePath, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        /// <summary>Invalidates the in-memory cache so the next read picks up any external edit (e.g. a hand-edited file) -- the window calls this on focus/refresh.</summary>
        public static void InvalidateCache() => _cache = null;

        public static bool IsDisabled(string group) =>
            group != CoreGroup && Load().disabledGroups.Contains(group);

        public static IReadOnlyList<string> DisabledGroups => Load().disabledGroups;

        public static IReadOnlyList<string> DefaultActiveGroups => Load().defaultActiveGroups;

        /// <summary>Silently refuses to disable "core" -- callers (the window) should also gray out the button, this is the defensive backstop.</summary>
        public static void SetDisabled(string group, bool disabled)
        {
            if (group == CoreGroup) return;

            var data = Load();
            bool changed;
            if (disabled)
            {
                changed = !data.disabledGroups.Contains(group);
                if (changed) data.disabledGroups.Add(group);
                data.defaultActiveGroups.Remove(group); // a disabled group can't also be a default-active one
            }
            else
            {
                changed = data.disabledGroups.Remove(group);
            }

            if (changed) Save(data);
        }

        public static bool IsReadOnlyMode() => Load().readOnlyMode;

        public static IReadOnlyList<string> PackageAllowlist => Load().packageAllowlist;

        /// <summary>Empty allowlist means unrestricted (today's default behavior).
        /// Otherwise packageId must exactly match an entry, or a "prefix.*" entry's prefix.</summary>
        public static bool IsPackageAllowed(string packageId)
        {
            var allowlist = Load().packageAllowlist;
            if (allowlist.Count == 0) return true;
            if (string.IsNullOrEmpty(packageId)) return false;

            // Strip an "@version" suffix before matching, e.g. "com.unity.cinemachine@2.9.7".
            var name = packageId.Split('@')[0];

            foreach (var pattern in allowlist)
            {
                if (string.IsNullOrEmpty(pattern)) continue;
                if (pattern.EndsWith(".*", StringComparison.Ordinal))
                {
                    var prefix = pattern.Substring(0, pattern.Length - 1); // keep trailing '.'
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
                }
                else if (string.Equals(name, pattern, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static void SetPackageAllowlist(IEnumerable<string> patterns)
        {
            var data = Load();
            data.packageAllowlist = patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            Save(data);
        }

        public static void SetReadOnlyMode(bool enabled)
        {
            var data = Load();
            if (data.readOnlyMode == enabled) return;
            data.readOnlyMode = enabled;
            Save(data);
        }

        public static void SetDefaultActive(string group, bool active)
        {
            var data = Load();
            bool changed;
            if (active && !data.disabledGroups.Contains(group))
            {
                changed = !data.defaultActiveGroups.Contains(group);
                if (changed) data.defaultActiveGroups.Add(group);
            }
            else
            {
                changed = data.defaultActiveGroups.Remove(group);
            }

            if (changed) Save(data);
        }
    }
}
