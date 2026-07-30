using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Groups
{
    /// <summary>
    /// Window -> Unity MCP -> Tool Groups. A human-only control panel over the same
    /// group mechanism manage_tools exposes to the AI, plus one the AI has no access to
    /// at all: disabling a group entirely.
    ///
    /// Two very different guarantees live side by side here, and the UI is written to
    /// make that difference legible rather than hide it behind one generic toggle:
    ///   - Disable: the group and its tools become entirely invisible to every AI
    ///     client -- not just hidden from listing, a direct-by-name call fails the same
    ///     way calling a tool that never existed would (see MCPToolRegistry.Invoke and
    ///     MCPCommandDispatcher.HandleListTools). Takes effect immediately, even for an
    ///     AI session already mid-conversation. "core" can never be disabled.
    ///   - Activate/Deactivate: sets the DEFAULT for new AI sessions (seeds
    ///     groups.py's _active_groups on process start / manage_tools reset). Does NOT
    ///     retroactively change an already-running session's own state -- that session
    ///     controls its own active groups via manage_tools, same as always.
    ///
    /// A group's real, live active/inactive state (as opposed to the "default for a new
    /// session" above) comes from Library/MCP/live_tool_state.json, written by
    /// groups.py's _write_live_state every time a live session's manage_tools call
    /// actually changes its active-group set. When that file is available, the window
    /// shows the true live state instead of only the default; see MCPLiveStateReader.
    ///
    /// Group descriptions and the composite (Python @workflow) tool list are read from
    /// Library/MCP/tool_manifest.json, written by the Python server on startup -- Unity
    /// has zero native knowledge of composite tools (they're hand-written Python, not
    /// [MCPTool]-attributed C# methods its own reflection scan can discover), and there
    /// is no live channel to ask an already-running Python process for this on demand.
    /// Until that file exists, only groups with at least one Unity-native atomic tool
    /// are shown, with a banner explaining why the picture is incomplete.
    /// </summary>
    public class MCPToolGroupsWindow : EditorWindow
    {
        [MenuItem("Window/Unity MCP/Tool Groups")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPToolGroupsWindow>();
            window.titleContent = new GUIContent("Unity MCP Tool Groups");
            window.minSize = new Vector2(480, 420);
        }

        private class ToolRow
        {
            public string Name;
            public string Description;
            public bool IsComposite;
            public bool IsReadOnly;
        }

        private class GroupRow
        {
            public string Name;
            public string Description;
            public List<ToolRow> Tools = new List<ToolRow>();
        }

        private readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>();
        private Vector2 _scroll;
        private string _packageAllowlistText;
        private bool _packageAllowlistDirty;
        private double _lastLiveRepaintTime;

        private static readonly Color ColorDisabled = new Color(0.95f, 0.68f, 0.68f);
        private static readonly Color ColorActive = new Color(0.72f, 0.92f, 0.72f);
        private static readonly Color ColorInactive = Color.white;

        private void OnEnable()
        {
            MCPToolGroupConfig.InvalidateCache();
            _packageAllowlistText = string.Join(", ", MCPToolGroupConfig.PackageAllowlist);
            _packageAllowlistDirty = false;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        /// <summary>
        /// live_tool_state.json changes from an external process (the Python server, on
        /// its own schedule whenever manage_tools activates/deactivates a group) -- Unity
        /// has no callback for that, so this window has to actually poll its mtime on a
        /// short interval to notice, the same tradeoff every file-based sync point in this
        /// project already accepts (see MCPToolManifestReader / groups.py's own throttled
        /// re-read of tool_groups_config.json).
        /// </summary>
        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastLiveRepaintTime < 1.0) return;
            _lastLiveRepaintTime = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private void OnGUI()
        {
            var groupRows = BuildGroupRows();

            DrawHeader(groupRows);
            GUILayout.Space(6);

            if (!MCPToolManifestReader.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Composite (Python) tool details and full group descriptions aren't available yet -- start " +
                    "your AI client once (which starts the Python MCP server against this project) to populate " +
                    "them. Only groups with at least one Unity-native tool are shown until then; a few groups " +
                    "(e.g. behavior_tree, enemy_ai, weapons) are entirely composite and won't appear at all yet.",
                    MessageType.Info);
                GUILayout.Space(6);
            }

            bool hasLiveState = MCPLiveStateReader.IsAvailable;
            if (!hasLiveState)
            {
                EditorGUILayout.HelpBox(
                    "No live session data yet -- \"Active\"/\"Inactive\" below reflects only the default for a " +
                    "new session. Start an AI client and it'll switch to showing what's genuinely active right now.",
                    MessageType.None);
                GUILayout.Space(6);
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (var row in groupRows)
            {
                DrawGroup(row, hasLiveState);
                GUILayout.Space(4);
            }
            GUILayout.EndScrollView();
        }

        private void DrawHeader(List<GroupRow> groupRows)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Tool Groups", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Connected AI clients: {MCPServer.ConnectedClientCount}", EditorStyles.miniLabel);
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
            {
                MCPToolGroupConfig.InvalidateCache();
                Repaint();
            }
            GUILayout.EndHorizontal();

            DrawCounters(groupRows);

            GUILayout.Space(4);

            bool readOnlyMode = MCPToolGroupConfig.IsReadOnlyMode();
            var previousBg = GUI.backgroundColor;
            GUI.backgroundColor = readOnlyMode ? ColorActive : previousBg;
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUI.backgroundColor = previousBg;
            GUILayout.Label("Read-Only Mode", EditorStyles.boldLabel, GUILayout.Width(120));
            GUILayout.Label(
                "Blocks every tool call that isn't explicitly marked read-only (tagged \"RO\" below) -- takes " +
                "effect immediately, even mid-session.",
                EditorStyles.wordWrappedMiniLabel);
            GUILayout.FlexibleSpace();
            bool newReadOnlyMode = GUILayout.Toggle(readOnlyMode, readOnlyMode ? "On" : "Off", "Button", GUILayout.Width(50));
            if (newReadOnlyMode != readOnlyMode)
            {
                if (!newReadOnlyMode || EditorUtility.DisplayDialog(
                        "Enable Read-Only Mode",
                        "Every tool call that isn't explicitly marked read-only will be refused for this entire " +
                        "project -- for every connected AI client, immediately, including a session already in " +
                        "progress. Use this when you want an AI to look around without being able to change " +
                        "anything. You can turn it back off here at any time.",
                        "Enable", "Cancel"))
                {
                    MCPToolGroupConfig.SetReadOnlyMode(newReadOnlyMode);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label("Package allowlist", EditorStyles.boldLabel, GUILayout.Width(120));
            EditorGUI.BeginChangeCheck();
            _packageAllowlistText = EditorGUILayout.TextField(_packageAllowlistText);
            if (EditorGUI.EndChangeCheck()) _packageAllowlistDirty = true;
            GUI.enabled = _packageAllowlistDirty;
            if (GUILayout.Button("Apply", GUILayout.Width(60)))
            {
                var patterns = _packageAllowlistText.Split(',');
                MCPToolGroupConfig.SetPackageAllowlist(patterns);
                _packageAllowlistText = string.Join(", ", MCPToolGroupConfig.PackageAllowlist);
                _packageAllowlistDirty = false;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "Comma-separated exact package IDs or \"prefix.*\" wildcards (e.g. \"com.unity.*, com.unitymcp.*\") " +
                "that manage_packages(action:\"add\") is allowed to install. Empty means unrestricted -- today's " +
                "default behavior.",
                MessageType.None);
        }

        /// <summary>
        /// "Active" here means live-active if live_tool_state.json is available (a real AI
        /// session actually has these active right now), falling back to default-active
        /// otherwise -- the same fallback DrawGroup itself uses per row, kept consistent so
        /// this summary line never contradicts the per-group badges below it.
        /// </summary>
        private void DrawCounters(List<GroupRow> groupRows)
        {
            bool hasLiveState = MCPLiveStateReader.IsAvailable;
            var liveActive = MCPLiveStateReader.ActiveGroups;

            int totalGroups = groupRows.Count;
            int totalTools = groupRows.Sum(r => r.Tools.Count);

            int activeGroups = 0;
            int activeTools = 0;
            foreach (var row in groupRows)
            {
                bool isCore = row.Name == MCPToolGroupConfig.CoreGroup;
                bool disabled = MCPToolGroupConfig.IsDisabled(row.Name);
                if (disabled) continue;

                bool isActiveNow = isCore || (hasLiveState
                    ? liveActive.Contains(row.Name)
                    : MCPToolGroupConfig.DefaultActiveGroups.Contains(row.Name));

                if (isActiveNow)
                {
                    activeGroups++;
                    activeTools += row.Tools.Count;
                }
            }

            GUILayout.BeginHorizontal(GUI.skin.box);
            DrawCounterStat("Active groups", $"{activeGroups} / {totalGroups}");
            DrawCounterStat("Active tools", $"{activeTools} / {totalTools}");
            DrawCounterStat("Total groups", totalGroups.ToString());
            DrawCounterStat("Total tools", totalTools.ToString());
            GUILayout.FlexibleSpace();
            GUILayout.Label(hasLiveState ? "(live)" : "(default only -- no live session yet)", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
        }

        private void DrawCounterStat(string label, string value)
        {
            GUILayout.BeginVertical(GUILayout.Width(90));
            GUILayout.Label(value, EditorStyles.boldLabel);
            GUILayout.Label(label, EditorStyles.miniLabel);
            GUILayout.EndVertical();
            GUILayout.Space(8);
        }

        private List<GroupRow> BuildGroupRows()
        {
            MCPToolRegistry.EnsureScanned();

            var rows = new Dictionary<string, GroupRow> { };

            GroupRow GetOrCreate(string name)
            {
                if (!rows.TryGetValue(name, out var row))
                {
                    row = new GroupRow { Name = name };
                    rows[name] = row;
                }
                return row;
            }

            foreach (var entry in MCPToolRegistry.Tools.Values)
            {
                var row = GetOrCreate(entry.Group);
                row.Tools.Add(new ToolRow
                {
                    Name = entry.Name,
                    Description = SimplifyDescription(entry.Description),
                    IsComposite = false,
                    IsReadOnly = entry.ReadOnly,
                });
            }

            foreach (var kv in MCPToolManifestReader.GroupDescriptions)
            {
                GetOrCreate(kv.Key).Description = SimplifyDescription(kv.Value, maxChars: 200);
            }

            foreach (var tool in MCPToolManifestReader.CompositeTools)
            {
                var row = GetOrCreate(tool.group);
                row.Tools.Add(new ToolRow
                {
                    Name = tool.name,
                    Description = SimplifyDescription(tool.description),
                    IsComposite = true,
                    IsReadOnly = tool.read_only,
                });
            }

            foreach (var row in rows.Values)
            {
                row.Tools = row.Tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                if (string.IsNullOrEmpty(row.Description))
                    row.Description = "(description unavailable until the Python server has run once)";
            }

            // "core" first (it's the one group every session always has), then
            // alphabetical -- matches how a human scans a settings list: the one
            // exception up top, everything else predictable.
            return rows.Values
                .OrderBy(r => r.Name == MCPToolGroupConfig.CoreGroup ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Tool/group descriptions in source are written for an AI client and its search
        /// index (docs/tool-scaling-strategy.md) -- thorough, but full of implementation
        /// detail (spike findings, edge cases, internal API names) a human skimming this
        /// window doesn't need just to understand what a tool is for. This keeps the first
        /// plain-language sentence or two and drops everything after the first
        /// " -- "/" — " aside, which is reliably where "what it does" ends and "how/why"
        /// begins in this codebase's own description style.
        /// </summary>
        // Abbreviations whose embedded "X. " would otherwise look like a sentence
        // boundary to the naive split below (e.g. "by its exact title, e.g. 'Console'"
        // must not get cut off right after "e.g." -- found by spot-checking real
        // descriptions before trusting this, not guessed in advance).
        private static readonly string[] AbbreviationPlaceholders = { "e.g.", "i.e.", "etc." };

        private static string SimplifyDescription(string description, int maxChars = 150)
        {
            if (string.IsNullOrEmpty(description)) return description;

            string text = description;

            int dashCut = IndexOfEarliest(text, " -- ", " — ");
            if (dashCut > 15) text = text.Substring(0, dashCut);

            for (int i = 0; i < AbbreviationPlaceholders.Length; i++)
                text = text.Replace(AbbreviationPlaceholders[i], $"{i}");

            var sentences = text.Split(new[] { ". " }, StringSplitOptions.None);
            for (int i = 0; i < sentences.Length; i++)
                for (int j = 0; j < AbbreviationPlaceholders.Length; j++)
                    sentences[i] = sentences[i].Replace($"{j}", AbbreviationPlaceholders[j]);
            var result = new StringBuilder();
            foreach (var raw in sentences)
            {
                var sentence = raw.Trim();
                if (sentence.Length == 0) continue;
                if (result.Length > 0 && result.Length + sentence.Length > maxChars) break;
                result.Append(sentence);
                if (!sentence.EndsWith(".")) result.Append('.');
                result.Append(' ');
                if (result.Length > maxChars * 0.6) break; // one solid sentence is usually enough
            }

            var simplified = result.ToString().Trim();
            if (simplified.Length == 0) simplified = text.Trim();
            if (simplified.Length > maxChars + 20)
            {
                var cut = simplified.Substring(0, maxChars);
                int lastSpace = cut.LastIndexOf(' ');
                if (lastSpace > maxChars / 2) cut = cut.Substring(0, lastSpace);
                simplified = cut.TrimEnd() + "...";
            }

            return simplified;
        }

        private static int IndexOfEarliest(string text, params string[] markers)
        {
            int best = -1;
            foreach (var marker in markers)
            {
                int idx = text.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0 && (best == -1 || idx < best)) best = idx;
            }
            return best;
        }

        private void DrawGroup(GroupRow row, bool hasLiveState)
        {
            bool isCore = row.Name == MCPToolGroupConfig.CoreGroup;
            bool disabled = MCPToolGroupConfig.IsDisabled(row.Name);
            bool defaultActive = isCore || MCPToolGroupConfig.DefaultActiveGroups.Contains(row.Name);
            bool liveActive = isCore || MCPLiveStateReader.ActiveGroups.Contains(row.Name);

            // Live state, when available, is the truth; default-active is only ever a
            // fallback for "no session has told us anything yet" or a secondary hint.
            bool activeNow = hasLiveState ? liveActive : defaultActive;

            var tint = disabled ? ColorDisabled : activeNow ? ColorActive : ColorInactive;
            var previousBg = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            GUILayout.BeginVertical(GUI.skin.box);
            GUI.backgroundColor = previousBg;

            GUILayout.BeginHorizontal();

            if (!_expanded.TryGetValue(row.Name, out var isExpanded)) isExpanded = false;
            var newExpanded = EditorGUILayout.Foldout(isExpanded, $"{row.Name}  ({row.Tools.Count} tool{(row.Tools.Count == 1 ? "" : "s")})", true);
            if (newExpanded != isExpanded) _expanded[row.Name] = newExpanded;

            GUILayout.FlexibleSpace();

            string statusLabel;
            if (disabled) statusLabel = "Disabled";
            else if (isCore) statusLabel = "Always active";
            else if (hasLiveState) statusLabel = liveActive ? "Active now" : "Inactive now";
            else statusLabel = defaultActive ? "Active (default)" : "Inactive (default)";
            GUILayout.Label(statusLabel, EditorStyles.miniBoldLabel, GUILayout.Width(130));

            GUILayout.EndHorizontal();

            // Once there's live data, a group's default-for-new-sessions setting can
            // legitimately differ from what's active right now (e.g. the AI activated it
            // itself mid-session) -- surface that instead of hiding the discrepancy.
            if (hasLiveState && !isCore && !disabled && liveActive != defaultActive)
            {
                GUILayout.Label($"(default for a new session: {(defaultActive ? "on" : "off")})", EditorStyles.miniLabel);
            }

            GUILayout.Label(row.Description, EditorStyles.wordWrappedMiniLabel);

            if (!isCore)
            {
                GUILayout.BeginHorizontal();

                if (disabled)
                {
                    if (GUILayout.Button("Enable", GUILayout.Width(90)))
                    {
                        MCPToolGroupConfig.SetDisabled(row.Name, false);
                    }
                }
                else
                {
                    if (GUILayout.Button("Disable...", GUILayout.Width(90)))
                    {
                        if (EditorUtility.DisplayDialog(
                                "Disable Tool Group",
                                $"Disable '{row.Name}'? Its {row.Tools.Count} tool(s) will become completely " +
                                "invisible to every connected AI client -- not just hidden, a direct call to any " +
                                "of them will fail as if the tool never existed. This takes effect immediately, " +
                                "even for an AI session already in progress. You can re-enable it here at any time.",
                                "Disable", "Cancel"))
                        {
                            MCPToolGroupConfig.SetDisabled(row.Name, true);
                        }
                    }

                    string activateLabel = defaultActive ? "Deactivate (default)" : "Activate (default)";
                    if (GUILayout.Button(activateLabel, GUILayout.Width(140)))
                    {
                        MCPToolGroupConfig.SetDefaultActive(row.Name, !defaultActive);
                    }
                }

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            if (isExpanded)
            {
                GUILayout.Space(4);
                foreach (var tool in row.Tools)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(tool.IsComposite ? "C" : "A", EditorStyles.miniLabel, GUILayout.Width(14));
                    if (tool.IsReadOnly) GUILayout.Label("RO", EditorStyles.miniLabel, GUILayout.Width(20));
                    GUILayout.BeginVertical();
                    GUILayout.Label(tool.Name, EditorStyles.miniBoldLabel);
                    GUILayout.Label(tool.Description, EditorStyles.wordWrappedMiniLabel);
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(2);
                }
            }

            GUILayout.EndVertical();
        }
    }
}
