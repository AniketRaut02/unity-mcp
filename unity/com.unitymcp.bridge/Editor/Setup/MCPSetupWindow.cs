using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityMCP.Security;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Window -> Unity MCP -> Setup. Live bridge status, multi-instance conflict
    /// detection, the Python server location, and one-click client configuration for
    /// four clients (Claude Code, Codex, Cursor, Antigravity) — the single home for
    /// every piece of Editor-side MCP configuration now that the visual Tool Builder
    /// (which used to own the Python server location setting) has been removed.
    ///
    /// Configure writes each client's config file directly (.mcp.json, .codex/config.toml,
    /// etc.) rather than shelling out to a CLI (claude mcp add / codex mcp add) —
    /// deliberately replacing the earlier CLI-based approach, which depended on those
    /// CLIs being installed correctly AND on their own spawn-time working-directory
    /// behavior, which was the actual root cause of a ModuleNotFoundError this project
    /// hit in production (the CLI recorded a command it later couldn't correctly
    /// resolve from wherever IT chose to spawn from). Writing the file directly removes
    /// that whole class of failure: every value (the absolute venv python path,
    /// PYTHONPATH, the project root) is baked into the file itself, so there's nothing
    /// left to guess about at spawn time.
    ///
    /// Deliberately thin: every real decision (file format, merge safety, path
    /// resolution) lives in MCPMcpServersJsonWriter / MCPCodexTomlWriter /
    /// MCPMcpConfigTargets / MCPServerEntryBuilder, all unit-tested. This file is UI
    /// glue that calls them and displays the result.
    /// </summary>
    public class MCPSetupWindow : EditorWindow
    {
        [MenuItem("Window/Unity MCP/Setup")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPSetupWindow>();
            window.titleContent = new GUIContent("Unity MCP Setup");
            window.minSize = new Vector2(460, 560);
        }

        private string _statusMessage = "";
        private MessageType _statusType = MessageType.Info;

        private const double ConflictCheckIntervalSeconds = 2.0;
        private double _lastConflictCheckTime = -1;
        private MCPInstanceConflictInfo _conflictInfo;

        // Whether each client's config file currently has this project's server entry
        // in it -- refreshed on the same throttle as the conflict check (cheap, but no
        // reason to hit disk for four files on every single repaint).
        private double _lastClientStatusCheckTime = -1;
        private readonly Dictionary<MCPClientKind, bool> _configuredCache = new Dictionary<MCPClientKind, bool>();

        // Soft, theme-agnostic tints applied via GUI.backgroundColor around a
        // GUI.skin.box — multiplies with the skin's own neutral gray box texture, so
        // these read as pastel accents rather than solid color blocks in both the
        // light and dark Editor skins.
        private static readonly Color ColorGood = new Color(0.72f, 0.92f, 0.72f);
        private static readonly Color ColorBad = new Color(0.95f, 0.68f, 0.68f);
        private static readonly Color ColorWarn = new Color(0.97f, 0.90f, 0.55f);
        private static readonly Color ColorNeutral = Color.white;

        private void OnEnable()
        {
            // Standard Unity pattern for a window that should keep showing live state
            // (bridge status, conflict detection) even with zero mouse/keyboard activity —
            // without this, OnGUI only reruns on an actual repaint-triggering event, so a
            // conflict appearing while the window just sits open wouldn't show up until
            // the user did something to it.
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void OnGUI()
        {
            RefreshClientConfiguredStatusIfDue();

            DrawBridgeStatus();
            GUILayout.Space(8);
            DrawConflictStatus();
            GUILayout.Space(8);
            DrawPythonServerSection();
            GUILayout.Space(8);
            DrawLastConfiguredSection();
            GUILayout.Space(12);

            foreach (var kind in MCPClientDetector.AllKinds())
            {
                DrawClientSection(kind);
                GUILayout.Space(8);
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        /// <summary>Tints the background of a GUI.skin.box for the duration of `drawContent` -- the one real trick IMGUI offers for a colored panel that still looks native in both the light and dark Editor skins.</summary>
        private static void DrawColoredBox(Color tint, Action drawContent)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            GUILayout.BeginVertical(GUI.skin.box);
            GUI.backgroundColor = previous; // reset immediately so child controls (buttons, fields) aren't also tinted
            drawContent();
            GUILayout.EndVertical();
        }

        private void DrawBridgeStatus()
        {
            var tint = MCPServer.BoundPort > 0
                ? ColorGood
                : MCPServer.LastStartBlockedByInstanceLock ? ColorBad : ColorNeutral;

            DrawColoredBox(tint, DrawBridgeStatusContent);
        }

        private void DrawBridgeStatusContent()
        {
            GUILayout.Label("Bridge Status", EditorStyles.boldLabel);

            if (MCPServer.BoundPort > 0)
            {
                GUILayout.Label($"\u25CF Running on port {MCPServer.BoundPort}");
                GUILayout.Label($"Connected clients: {MCPServer.ConnectedClientCount}");

                // Distinguishes "0 clients because a domain reload just cycled the
                // bridge (very likely from entering/exiting Play Mode -- Unity does
                // this by default on every transition unless Enter Play Mode Options
                // has domain reload disabled) and a reconnect just hasn't happened
                // yet" from "0 clients because nobody has ever connected" or "0
                // clients and something is actually wrong". Without this, both looked
                // identical in this window, with nothing here to explain why a client
                // that was working a second ago now reads as disconnected even though
                // the AI tool itself never saw an interruption (it talks to the
                // long-lived Python process, not directly to this socket) and will
                // reconnect transparently on its own next tool call.
                if (MCPServer.ConnectedClientCount == 0 && MCPServer.HadClientBeforeLastRestart && MCPServer.SecondsSinceStart < 20)
                {
                    EditorGUILayout.HelpBox(
                        "0 clients right now, but one WAS connected just before this restart -- most likely a " +
                        "Play Mode transition or script recompile triggered a domain reload, which always drops " +
                        "the bridge's live socket connections. This is expected, not an error: any MCP client " +
                        "(Claude Code, Codex, etc.) reconnects automatically the next time it calls a tool, using " +
                        "the fresh session.json this restart just wrote.",
                        MessageType.Info);
                }
            }
            else if (MCPServer.LastStartBlockedByInstanceLock)
            {
                EditorGUILayout.HelpBox(
                    "Not running -- another live Unity process already owns the MCP bridge for this " +
                    "project (see the Console for details). This instance deliberately did not start a " +
                    "second bridge to avoid silently fighting the other one over session.json. Close the " +
                    "other Unity instance for this project, then click Retry.",
                    MessageType.Error);

                if (GUILayout.Button("Retry", GUILayout.Width(80)))
                {
                    MCPServer.Start();
                    RefreshConflictStatus();
                    Repaint();
                }
            }
            else
            {
                GUILayout.Label("\u25CB Not running — check the Console for a bind error.");

                if (GUILayout.Button("Retry", GUILayout.Width(80)))
                {
                    MCPServer.Start();
                    RefreshConflictStatus();
                    Repaint();
                }
            }

            GUILayout.Label($"Project: {MCPProjectUtil.ProjectRoot}");

            if (GUILayout.Button("Refresh", GUILayout.Width(80)))
            {
                RefreshConflictStatus();
                Repaint();
            }
        }

        private void DrawConflictStatus()
        {
            // Throttled re-check rather than every OnGUI call (which can fire many times
            // per second) — the check itself is cheap (one file read + one process lookup)
            // but there's no reason to do it that often for something that only changes on
            // the order of seconds, if ever.
            double now = EditorApplication.timeSinceStartup;
            if (_conflictInfo == null || now - _lastConflictCheckTime > ConflictCheckIntervalSeconds)
            {
                RefreshConflictStatus();
            }

            if (_conflictInfo == null) return;

            var tint = _conflictInfo.HasConflict ? ColorWarn : ColorGood;
            DrawColoredBox(tint, DrawConflictStatusContent);
        }

        private void DrawConflictStatusContent()
        {
            if (_conflictInfo.HasConflict)
            {
                EditorGUILayout.HelpBox(
                    "Multiple Unity instances detected for this project!\n\n" + _conflictInfo.Message,
                    MessageType.Warning);
            }
            else
            {
                GUILayout.Label("\u2713 No conflicting Unity instance detected for this project.", EditorStyles.miniLabel);
            }
        }

        private void RefreshConflictStatus()
        {
            _conflictInfo = MCPInstanceConflictDetector.DetectReal();
            _lastConflictCheckTime = EditorApplication.timeSinceStartup;
        }

        private void RefreshClientConfiguredStatusIfDue()
        {
            // Same throttle as the conflict check -- cheap (up to four small file
            // reads), but no reason to hit disk on every single repaint when this only
            // changes when the user clicks Configure or edits a config file by hand.
            double now = EditorApplication.timeSinceStartup;
            if (_configuredCache.Count > 0 && now - _lastClientStatusCheckTime <= ConflictCheckIntervalSeconds) return;

            foreach (var kind in MCPClientDetector.AllKinds())
            {
                _configuredCache[kind] = IsConfigured(kind);
            }
            _lastClientStatusCheckTime = now;
        }

        private static bool IsConfigured(MCPClientKind kind)
        {
            var configPath = MCPMcpConfigTargets.AbsolutePath(kind, MCPProjectUtil.ProjectRoot);
            if (!File.Exists(configPath)) return false;

            string content;
            try { content = File.ReadAllText(configPath); }
            catch { return false; }

            var serverName = CurrentServerName();
            return MCPMcpConfigTargets.Format(kind) == MCPConfigFormat.Toml
                ? MCPCodexTomlWriter.IsConfigured(content, serverName)
                : MCPMcpServersJsonWriter.IsConfigured(content, serverName);
        }

        private void DrawClientSection(MCPClientKind kind)
        {
            bool configured = _configuredCache.TryGetValue(kind, out var c) && c;
            bool isLastConfigured = MCPClientConfigTracker.TryGetLastConfigured(out var lastKind, out _) && lastKind == kind;

            var tint = configured ? ColorGood : ColorNeutral;
            DrawColoredBox(tint, () => DrawClientSectionContent(kind, configured, isLastConfigured));
        }

        private void DrawClientSectionContent(MCPClientKind kind, bool configured, bool isLastConfigured)
        {
            GUILayout.BeginHorizontal();
            var dotColor = configured ? new Color(0.15f, 0.6f, 0.15f) : Color.gray;
            var previousColor = GUI.color;
            GUI.color = dotColor;
            GUILayout.Label(configured ? "●" : "○", GUILayout.Width(14));
            GUI.color = previousColor;

            GUILayout.Label(MCPClientDetector.DisplayName(kind), EditorStyles.boldLabel);

            if (isLastConfigured)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("★ Last configured", EditorStyles.miniLabel);
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(MCPMcpConfigTargets.RelativePathDisplay(kind), EditorStyles.miniLabel);

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Check status", GUILayout.Width(120)))
            {
                CheckStatus(kind);
            }

            if (GUILayout.Button("Configure", GUILayout.Width(120)))
            {
                Configure(kind);
            }

            GUILayout.EndHorizontal();
        }

        private static string CurrentServerName()
        {
            var projectFolderName = new DirectoryInfo(MCPProjectUtil.ProjectRoot).Name;
            return MCPClientDetector.ServerName(projectFolderName);
        }

        private static bool IsWindows() => Application.platform == RuntimePlatform.WindowsEditor;

        private void CheckStatus(MCPClientKind kind)
        {
            var displayName = MCPClientDetector.DisplayName(kind);
            var configPath = MCPMcpConfigTargets.AbsolutePath(kind, MCPProjectUtil.ProjectRoot);
            var relativePath = MCPMcpConfigTargets.RelativePathDisplay(kind);
            var serverName = CurrentServerName();

            if (!File.Exists(configPath))
            {
                _statusType = MessageType.Info;
                _statusMessage = $"{displayName}: no {relativePath} found yet — click Configure.";
                return;
            }

            bool configured = IsConfigured(kind);
            _configuredCache[kind] = configured;

            _statusType = MessageType.Info;
            _statusMessage = configured
                ? $"{displayName}: '{serverName}' is configured in {relativePath}."
                : $"{displayName}: {relativePath} exists but '{serverName}' is not in it yet — click Configure.";
        }

        private void Configure(MCPClientKind kind)
        {
            var displayName = MCPClientDetector.DisplayName(kind);

            var pythonServerPath = MCPPythonServerSettings.PythonServerPath;
            if (string.IsNullOrEmpty(pythonServerPath))
            {
                _statusType = MessageType.Error;
                _statusMessage = "Set the Python server location above before configuring a client.";
                return;
            }

            bool isWindows = IsWindows();
            var venvPython = MCPServerEntryBuilder.AbsoluteVenvPythonExecutable(pythonServerPath, isWindows);
            if (!File.Exists(venvPython))
            {
                _statusType = MessageType.Error;
                _statusMessage = $"No venv found at '{venvPython}'. Set it up first (pip install -r requirements.txt in a venv there), then try Configure again.";
                return;
            }

            var serverName = CurrentServerName();
            var projectRoot = MCPProjectUtil.ProjectRoot;
            var configPath = MCPMcpConfigTargets.AbsolutePath(kind, projectRoot);
            var relativePath = MCPMcpConfigTargets.RelativePathDisplay(kind);

            string existingContent = null;
            if (File.Exists(configPath))
            {
                try
                {
                    existingContent = File.ReadAllText(configPath);
                }
                catch (Exception e)
                {
                    _statusType = MessageType.Error;
                    _statusMessage = $"{displayName}: could not read existing {relativePath} — {e.Message}";
                    return;
                }
            }

            var args = MCPServerEntryBuilder.Args();
            var env = MCPServerEntryBuilder.Env(projectRoot, pythonServerPath);

            string newContent;
            if (MCPMcpConfigTargets.Format(kind) == MCPConfigFormat.Toml)
            {
                var section = MCPCodexTomlWriter.BuildServerSection(serverName, venvPython, args, env);
                newContent = MCPCodexTomlWriter.Merge(existingContent, serverName, section);
            }
            else
            {
                var entryJson = MCPMcpServersJsonWriter.BuildServerEntryJson(venvPython, args, env);
                if (!MCPMcpServersJsonWriter.TryMerge(existingContent, serverName, entryJson, out newContent, out var mergeError))
                {
                    _statusType = MessageType.Error;
                    _statusMessage = $"{displayName}: {mergeError}";
                    return;
                }
            }

            try
            {
                var directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(configPath, newContent);
            }
            catch (Exception e)
            {
                _statusType = MessageType.Error;
                _statusMessage = $"{displayName}: failed to write {relativePath} — {e.Message}";
                return;
            }

            MCPClientConfigTracker.RecordConfigured(kind, DateTime.UtcNow);
            _configuredCache[kind] = true;

            _statusType = MessageType.Info;
            _statusMessage = $"{displayName}: wrote {relativePath}. Restart your {displayName} session to pick it up." +
                (kind == MCPClientKind.Codex
                    ? " Codex only loads project-scoped config for directories you've marked as trusted — check that in Codex if it doesn't show up."
                    : "") +
                $" This file contains an absolute, machine-specific path — consider adding {relativePath} to .gitignore rather than committing it, so teammates each generate their own.";
        }

        private void DrawPythonServerSection()
        {
            DrawColoredBox(
                string.IsNullOrEmpty(MCPPythonServerSettings.PythonServerPath) ? ColorWarn : ColorGood,
                DrawPythonServerSectionContent);
        }

        private void DrawPythonServerSectionContent()
        {
            GUILayout.Label("Python Server Location", EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            var current = MCPPythonServerSettings.PythonServerPath;
            var updated = EditorGUILayout.TextField(current);
            if (updated != current) MCPPythonServerSettings.PythonServerPath = updated;

            if (GUILayout.Button("Browse...", GUILayout.Width(80)))
            {
                var chosen = EditorUtility.OpenFolderPanel(
                    "Select the unity_mcp_server folder", MCPPythonServerSettings.PythonServerPath, "");
                if (!string.IsNullOrEmpty(chosen)) MCPPythonServerSettings.PythonServerPath = chosen;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("The folder containing workflows.py and server.py. Needed before Configure can write a client's config.", EditorStyles.miniLabel);
        }

        private void DrawLastConfiguredSection()
        {
            if (!MCPClientConfigTracker.TryGetLastConfigured(out var kind, out var configuredAtUtc)) return;

            var relative = MCPClientConfigTracker.FormatRelativeTime(configuredAtUtc, DateTime.UtcNow);
            GUILayout.Label($"Last configured: {MCPClientDetector.DisplayName(kind)} ({relative})", EditorStyles.miniLabel);
        }
    }
}
