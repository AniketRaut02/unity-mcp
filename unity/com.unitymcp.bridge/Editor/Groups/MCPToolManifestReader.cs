using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityMCP.Groups
{
    /// <summary>One composite (Python @workflow) tool, as reported by the Python server.</summary>
    public class MCPCompositeToolInfo
    {
        public string name;
        public string description;
        public string group;
        public bool destructive;
        public bool read_only;
    }

    /// <summary>
    /// Reads Library/MCP/tool_manifest.json -- written by the Python server (server.py's
    /// _write_tool_manifest, called once at process startup) the moment it starts, never
    /// by Unity. This is the ONLY way the Tool Groups window can know about composite
    /// tools (generate_grid_layout, create_scare_sequence, ...) and full group
    /// descriptions at all: they're hand-written Python in workflows.py/groups.py, not
    /// [MCPTool]-attributed C# methods MCPToolRegistry's reflection scan can discover,
    /// and there is no live channel for Unity to ask an already-running Python process
    /// for this on demand (the bridge protocol is Python-initiates-request /
    /// Unity-responds only). Until the Python server has run at least once against this
    /// project, this file won't exist yet -- IsAvailable is false, and the window shows
    /// only the groups/tools Unity's own registry already knows about natively.
    /// </summary>
    public static class MCPToolManifestReader
    {
        private static string ManifestPath => Path.Combine(Application.dataPath, "..", "Library", "MCP", "tool_manifest.json");

        private class ManifestData
        {
            public string generatedAt;
            public Dictionary<string, string> groupDescriptions;
            public List<MCPCompositeToolInfo> compositeTools;
        }

        private static ManifestData _cache;
        private static DateTime _cacheFileWriteTimeUtc;

        private static ManifestData Load()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return null;

                var writeTimeUtc = File.GetLastWriteTimeUtc(ManifestPath);
                if (_cache != null && writeTimeUtc == _cacheFileWriteTimeUtc) return _cache;

                var json = File.ReadAllText(ManifestPath);
                _cache = JsonConvert.DeserializeObject<ManifestData>(json);
                _cacheFileWriteTimeUtc = writeTimeUtc;
                return _cache;
            }
            catch
            {
                // Missing/malformed manifest just means "composite tool info isn't
                // available yet" -- not an error worth surfacing anywhere but the window's
                // own "not available" hint.
                return null;
            }
        }

        public static bool IsAvailable => Load() != null;

        public static string GeneratedAt => Load()?.generatedAt;

        public static IReadOnlyDictionary<string, string> GroupDescriptions =>
            Load()?.groupDescriptions ?? new Dictionary<string, string>();

        public static IReadOnlyList<MCPCompositeToolInfo> CompositeTools =>
            Load()?.compositeTools ?? new List<MCPCompositeToolInfo>();
    }
}
