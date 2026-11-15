using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityMCP.UI;

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
            window.minSize = new Vector2(500, 480);
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
        private string _menuAllowlistText;
        private bool _menuAllowlistDirty;
        private double _lastLiveRepaintTime;

        private void OnEnable()
        {
            MCPToolGroupConfig.InvalidateCache();
            _packageAllowlistText = string.Join(", ", MCPToolGroupConfig.PackageAllowlist);
            _packageAllowlistDirty = false;
            _menuAllowlistText = string.Join(", ", MCPToolGroupConfig.MenuAllowlist);
            _menuAllowlistDirty = false;
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
            bool hasLiveState = MCPLiveStateReader.IsAvailable;

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Space(4);
            MCPGui.Header("Tool Groups", "Choose what AI clients can see and do in this project. Changes apply immediately.",
                $"{MCPBridge.ConnectedServerCount} server(s) connected",
                MCPBridge.ConnectedServerCount > 0 ? MCPGui.Good : MCPGui.Neutral,
                "MCP servers (one per AI client session) currently connected to this Editor.");

            DrawCounters(groupRows, hasLiveState);
            DrawSafetyCard();
            DrawAllowlistsCard();

            if (!MCPToolManifestReader.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Composite (Python) tools and full group descriptions appear after an AI client has started once " +
                    "for this project. Until then only groups with Unity-native tools are listed.",
                    MessageType.Info);
                GUILayout.Space(6);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Groups", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(new GUIContent(hasLiveState ? "showing live session state" : "showing defaults for new sessions",
                hasLiveState
                    ? "Active/inactive reflects what a running AI session has active right now."
                    : "No AI session has reported yet: active/inactive is the default a new session starts with."), MCPGui.Muted, GUILayout.ExpandWidth(false));
            if (GUILayout.Button(MCPGui.Button("Refresh", "Re-read the group settings from disk."), GUILayout.Width(70)))
                MCPToolGroupConfig.InvalidateCache();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            foreach (var row in groupRows)
                DrawGroup(row, hasLiveState);

            GUILayout.EndScrollView();
        }

        private void DrawSafetyCard()
        {
            bool readOnly = MCPToolGroupConfig.IsReadOnlyMode();
            bool danger = MCPToolGroupConfig.IsDangerTierEnabled();
            MCPGui.BeginCard("Safety", danger ? MCPGui.Warn : readOnly ? MCPGui.Info : MCPGui.Good,
                "Project-wide switches only a person in the Editor can change. AI clients can't.");

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(new GUIContent("Read-Only Mode", "Look, don't touch."), EditorStyles.boldLabel);
            GUILayout.Label("Refuses every tool that changes something (tools without the RO badge), for every AI client, immediately.", MCPGui.Muted);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            bool newReadOnly = MCPGui.Switch(readOnly, "Turn Read-Only Mode on or off for this project.", MCPGui.Info);
            GUILayout.EndHorizontal();
            if (newReadOnly != readOnly && (!newReadOnly || EditorUtility.DisplayDialog(
                    "Enable Read-Only Mode",
                    "Every tool call that isn't explicitly marked read-only will be refused for this entire project -- " +
                    "for every connected AI client, immediately, including a session already in progress. You can turn " +
                    "it back off here at any time.",
                    "Enable", "Cancel")))
                MCPToolGroupConfig.SetReadOnlyMode(newReadOnly);

            MCPGui.Separator();

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Danger tier", "Actions that reach outside the scene and assets."), EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            if (danger) MCPGui.Pill("approval required", MCPGui.Warn, "Each call waits for you in Window > Unity MCP > Approvals.");
            GUILayout.EndHorizontal();
            GUILayout.Label("Player builds, build and project settings, adding or removing packages, and menu items outside the " +
                            "menu allowlist. Off: hidden or refused. On: each call still needs your approval.", MCPGui.Muted);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            bool newDanger = MCPGui.Switch(danger, "Allow AI clients to request danger-tier actions (each still needs your approval).", MCPGui.Warn);
            GUILayout.EndHorizontal();
            if (newDanger != danger && (!newDanger || EditorUtility.DisplayDialog(
                    "Enable the danger tier",
                    "AI clients will be able to REQUEST Player builds, build and project settings changes, package " +
                    "installs/removals, and any Editor menu item. Nothing in this tier runs without your approval in the " +
                    "MCP Approvals window. You can turn it back off here at any time.",
                    "Enable", "Cancel")))
                MCPToolGroupConfig.SetDangerTierEnabled(newDanger);

            MCPGui.EndCard();
        }

        private void DrawAllowlistsCard()
        {
            MCPGui.BeginCard("Allowlists", MCPGui.Info, "Comma-separated lists. Click Apply to save an edit.");

            GUILayout.Label(MCPGui.Label("Packages",
                "Package IDs manage_packages may install, e.g. \"com.unity.cinemachine\", or \"com.unity.*\" for a prefix. " +
                "Empty = any package (installs are still in the danger tier)."), EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _packageAllowlistText = EditorGUILayout.TextField(_packageAllowlistText);
            if (EditorGUI.EndChangeCheck()) _packageAllowlistDirty = true;
            GUI.enabled = _packageAllowlistDirty;
            if (MCPGui.AccentButton(MCPGui.Button("Apply", "Save the package allowlist."), MCPGui.Good, GUILayout.Width(60)))
            {
                MCPToolGroupConfig.SetPackageAllowlist(_packageAllowlistText.Split(','));
                _packageAllowlistText = string.Join(", ", MCPToolGroupConfig.PackageAllowlist);
                _packageAllowlistDirty = false;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Empty means any package. Example: com.unity.*, com.cysharp.unitask", MCPGui.Muted);

            MCPGui.Separator();

            GUILayout.Label(MCPGui.Label("Menu items",
                "Menu paths execute_menu_item may run without the danger tier. An entry ending in \"/\" allows " +
                "everything under it (\"GameObject/\"); others must match exactly (\"Edit/Play\")."), EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _menuAllowlistText = EditorGUILayout.TextField(_menuAllowlistText);
            if (EditorGUI.EndChangeCheck()) _menuAllowlistDirty = true;
            GUI.enabled = _menuAllowlistDirty;
            if (MCPGui.AccentButton(MCPGui.Button("Apply", "Save the menu allowlist."), MCPGui.Good, GUILayout.Width(60)))
            {
                MCPToolGroupConfig.SetMenuAllowlist(_menuAllowlistText.Split(','));
                _menuAllowlistText = string.Join(", ", MCPToolGroupConfig.MenuAllowlist);
                _menuAllowlistDirty = false;
            }
            GUI.enabled = true;
            if (GUILayout.Button(MCPGui.Button("Defaults", "Restore the built-in menu allowlist."), GUILayout.Width(70)))
            {
                MCPToolGroupConfig.SetMenuAllowlist(null);
                _menuAllowlistText = string.Join(", ", MCPToolGroupConfig.MenuAllowlist);
                _menuAllowlistDirty = false;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Ending in \"/\" allows everything under it; other entries must match exactly.", MCPGui.Muted);

            MCPGui.EndCard();
        }

        /// <summary>
        /// "Active" means live-active when live_tool_state.json is available (a real AI session has
        /// these active right now), else default-active, the same fallback DrawGroup uses per row, so
        /// this summary never contradicts the per-group pills below it.
        /// </summary>
        private void DrawCounters(List<GroupRow> groupRows, bool hasLiveState)
        {
            var liveActive = MCPLiveStateReader.ActiveGroups;

            int totalGroups = groupRows.Count;
            int totalTools = groupRows.Sum(r => r.Tools.Count);
            int activeGroups = 0, activeTools = 0, disabledGroups = 0;
            foreach (var row in groupRows)
            {
                bool isCore = row.Name == MCPToolGroupConfig.CoreGroup;
                if (MCPToolGroupConfig.IsDisabled(row.Name)) { disabledGroups++; continue; }

                bool isActiveNow = isCore || (hasLiveState
                    ? liveActive.Contains(row.Name)
                    : MCPToolGroupConfig.DefaultActiveGroups.Contains(row.Name));
                if (isActiveNow)
                {
                    activeGroups++;
                    activeTools += row.Tools.Count;
                }
            }

            GUILayout.BeginHorizontal();
            DrawCounterStat($"{activeGroups} / {totalGroups}", "groups active", "Groups whose tools an AI client currently sees.");
            DrawCounterStat($"{activeTools} / {totalTools}", "tools visible", "Tools in active groups. Hidden ones can still be found with search.");
            DrawCounterStat(disabledGroups.ToString(), "groups disabled", "Disabled groups are invisible to AI clients entirely.");
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        private static GUIStyle _statValue, _statLabel;

        private void DrawCounterStat(string value, string label, string tooltip)
        {
            _statValue ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
            _statLabel ??= new GUIStyle(MCPGui.Muted) { alignment = TextAnchor.MiddleCenter };
            GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinWidth(110));
            GUILayout.Label(new GUIContent(value, tooltip), _statValue);
            GUILayout.Label(new GUIContent(label, tooltip), _statLabel);
            GUILayout.EndVertical();
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

            // Live state, when available, is the truth; default-active is only a fallback.
            bool activeNow = hasLiveState ? liveActive : defaultActive;

            string statusLabel, statusTip;
            Color statusColor;
            if (disabled) { statusLabel = "Disabled"; statusColor = MCPGui.Bad; statusTip = "Invisible to every AI client; calls fail as unknown."; }
            else if (isCore) { statusLabel = "Always on"; statusColor = MCPGui.Good; statusTip = "The core group is always active."; }
            else if (activeNow) { statusLabel = hasLiveState ? "Active" : "Active by default"; statusColor = MCPGui.Good; statusTip = hasLiveState ? "A running AI session has this group active." : "New AI sessions start with this group active."; }
            else { statusLabel = hasLiveState ? "Inactive" : "Off by default"; statusColor = MCPGui.Neutral; statusTip = "Hidden until an AI activates it with manage_tools (or uses search/describe/call)."; }

            MCPGui.BeginCard(row.Name, statusColor, row.Description);

            GUILayout.BeginHorizontal();
            if (!_expanded.TryGetValue(row.Name, out var isExpanded)) isExpanded = false;
            var newExpanded = EditorGUILayout.Foldout(isExpanded,
                new GUIContent($"{row.Tools.Count} tool{(row.Tools.Count == 1 ? "" : "s")}", "Show or hide this group's tools."), true);
            if (newExpanded != isExpanded) _expanded[row.Name] = newExpanded;
            GUILayout.FlexibleSpace();
            MCPGui.Pill(statusLabel, statusColor, statusTip, 96);
            GUILayout.EndHorizontal();

            GUILayout.Label(row.Description, MCPGui.Muted);

            if (hasLiveState && !isCore && !disabled && liveActive != defaultActive)
                GUILayout.Label($"Default for new sessions: {(defaultActive ? "on" : "off")}", MCPGui.Muted);

            if (!isCore)
            {
                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                if (disabled)
                {
                    if (MCPGui.AccentButton(MCPGui.Button("Enable", "Make this group available to AI clients again."), MCPGui.Good, GUILayout.Width(110)))
                        MCPToolGroupConfig.SetDisabled(row.Name, false);
                }
                else
                {
                    if (GUILayout.Button(MCPGui.Button(defaultActive ? "Make default off" : "Make default on",
                            "Sets whether NEW AI sessions start with this group active. Running sessions keep their own state."),
                            GUILayout.Width(110)))
                        MCPToolGroupConfig.SetDefaultActive(row.Name, !defaultActive);

                    if (MCPGui.AccentButton(MCPGui.Button("Disable...", "Hide this group from every AI client completely, effective immediately."),
                            MCPGui.Bad, GUILayout.Width(90)) &&
                        EditorUtility.DisplayDialog(
                            "Disable Tool Group",
                            $"Disable '{row.Name}'? Its {row.Tools.Count} tool(s) will become completely invisible to every " +
                            "connected AI client -- a direct call to any of them will fail as if the tool never existed. This " +
                            "takes effect immediately, even for a session already in progress. You can re-enable it here at any time.",
                            "Disable", "Cancel"))
                        MCPToolGroupConfig.SetDisabled(row.Name, true);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            if (isExpanded)
            {
                foreach (var tool in row.Tools)
                {
                    MCPGui.Separator();
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(tool.Name, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                    MCPGui.Pill(tool.IsComposite ? "composite" : "unity",
                        tool.IsComposite ? MCPGui.Info : MCPGui.Neutral,
                        tool.IsComposite ? "Python composite tool that chains other tools." : "Native tool running inside the Unity Editor.");
                    if (tool.IsReadOnly) MCPGui.Pill("RO", MCPGui.Good, "Read-only: never changes the project, so it works in Read-Only Mode.");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Label(tool.Description, MCPGui.Muted);
                }
            }

            MCPGui.EndCard();
        }
    }
}
