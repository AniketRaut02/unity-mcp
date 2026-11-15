using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityMCP.UI;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Window -> Unity MCP -> Setup. Live bridge status, the bundled server and uv status,
    /// and one-click configuration for Claude Code, Codex, Cursor, Antigravity, VS Code and
    /// Claude Desktop (one at a time, or every detected client at once).
    ///
    /// Configure writes each client's config file directly rather than shelling out to a
    /// client CLI: every value (uv's absolute path, the bundled server folder, the project
    /// root) is baked into the file, so nothing is left to resolve at spawn time.
    ///
    /// Deliberately thin UI glue: what gets written lives in MCPClientConfigurator and the
    /// writers/builders it calls; MCPClientConfigUpdater keeps those files current after
    /// package updates.
    /// </summary>
    public class MCPSetupWindow : EditorWindow
    {
        [MenuItem("Window/Unity MCP/Setup")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPSetupWindow>();
            window.titleContent = new GUIContent("Unity MCP Setup");
            window.minSize = new Vector2(480, 560);
        }

        private string _statusMessage = "";
        private MessageType _statusType = MessageType.Info;

        private const double StatusRefreshIntervalSeconds = 2.0;

        // Whether each client's config file currently has this project's server entry in it,
        // refreshed on a short throttle (no reason to hit disk on every repaint).
        private double _lastClientStatusCheckTime = -1;
        private Vector2 _scroll;
        private string _uvPath;
        private bool _uvChecked;
        private string _packageVersion;
        private readonly Dictionary<MCPClientKind, bool> _configuredCache = new Dictionary<MCPClientKind, bool>();

        private void OnEnable()
        {
            // Keep live state (bridge, servers) current with no mouse activity.
            EditorApplication.update += Repaint;
            _packageVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPSetupWindow).Assembly)?.version;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void OnGUI()
        {
            RefreshClientConfiguredStatusIfDue();
            if (!_uvChecked) RefreshUv();

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Space(4);
            MCPGui.Header("Unity MCP", "Connect AI coding assistants to this Unity project.",
                _packageVersion != null ? "v" + _packageVersion : null, MCPGui.Neutral, "Installed Unity MCP Bridge package version.");

            MCPGui.Feedback(_statusMessage, _statusType);
            DrawBridgeCard();
            DrawServerCard();
            DrawClientsCard();
            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ Bridge

        private void DrawBridgeCard()
        {
            var servers = MCPBridge.Servers;
            bool anyGaveUp = false;
            foreach (var s in servers) anyGaveUp |= s.State == "gave up";

            string pill; Color color; string summary;
            if (!MCPBridge.IsRunning && MCPBridge.LastStartBlockedByInstanceLock)
            { pill = "Blocked"; color = MCPGui.Bad; summary = "Another Unity instance owns this project's bridge."; }
            else if (!MCPBridge.IsRunning)
            { pill = "Stopped"; color = MCPGui.Bad; summary = "The bridge is not running. Check the Console for errors."; }
            else if (MCPBridge.ConnectedServerCount > 0)
            { pill = "Connected"; color = MCPGui.Good; summary = $"Connected to {MCPBridge.ConnectedServerCount} of {servers.Count} MCP server(s)."; }
            else if (anyGaveUp)
            { pill = "Not connected"; color = MCPGui.Warn; summary = "Gave up reconnecting to a server. Click Reconnect to try again."; }
            else
            { pill = "Waiting"; color = MCPGui.Info; summary = "Waiting for an AI client to start. It connects within about a second."; }

            MCPGui.BeginCard("Bridge", color, "The link between this Editor and the MCP servers your AI clients start.");

            GUILayout.BeginHorizontal();
            MCPGui.Pill(pill, color, "Bridge state.", 90);
            GUILayout.Space(6);
            GUILayout.Label(summary, EditorStyles.wordWrappedLabel);
            GUILayout.EndHorizontal();

            if (MCPBridge.IsRunning && servers.Count > 0)
            {
                MCPGui.Separator();
                foreach (var server in servers)
                {
                    GUILayout.BeginHorizontal();
                    var stateColor = server.State == "connected" ? MCPGui.Good
                        : server.State == "gave up" ? MCPGui.Bad
                        : MCPGui.Warn;
                    var stateText = server.State == "waiting to retry" ? $"retry in {server.RetryInSeconds:0}s" : server.State;
                    MCPGui.Pill(stateText, stateColor, "Connection state for this MCP server.", 90);
                    GUILayout.Space(6);
                    GUILayout.Label(new GUIContent($"Server pid {server.Pid}  ·  port {server.Port}",
                        "Each AI client session runs its own MCP server process; this Editor connects to all of them."));
                    GUILayout.EndHorizontal();
                    if (server.State != "connected" && !string.IsNullOrEmpty(server.LastError))
                        GUILayout.Label("Last error: " + server.LastError, MCPGui.Muted);
                }
            }

            MCPGui.Separator();
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Project: " + MCPProjectUtil.ProjectRoot, MCPProjectUtil.ProjectRoot), MCPGui.Muted);
            GUILayout.FlexibleSpace();
            if (MCPBridge.IsRunning)
            {
                if (GUILayout.Button(MCPGui.Button("Reconnect", "Retry every MCP server now, including ones it gave up on."), GUILayout.Width(90)))
                    MCPBridge.Reconnect();
            }
            else if (GUILayout.Button(MCPGui.Button("Retry", "Try to start the bridge again (close any other Unity instance on this project first)."), GUILayout.Width(90)))
            {
                MCPBridge.Start();
            }
            GUILayout.EndHorizontal();

            MCPGui.EndCard();
        }

        // ------------------------------------------------------------------ Server + uv

        private void DrawServerCard()
        {
            var serverDir = MCPServerEntryBuilder.BundledServerDirectory();
            bool ready = _uvPath != null && serverDir != null;
            MCPGui.BeginCard("MCP Server", ready ? MCPGui.Good : MCPGui.Warn,
                "The Python MCP server that AI clients launch. It ships inside this package and runs with uv.");

            GUILayout.BeginHorizontal();
            MCPGui.Pill(serverDir != null ? "Bundled" : "Missing", serverDir != null ? MCPGui.Good : MCPGui.Bad,
                "The server is in this package's Server~ folder.", 90);
            GUILayout.Space(6);
            GUILayout.Label(new GUIContent(serverDir != null
                    ? "Runs from this package. Its Python environment lives in Library/MCP/.venv."
                    : "Server~ folder not found. Reinstall the Unity MCP Bridge package.", serverDir), EditorStyles.wordWrappedLabel);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            MCPGui.Pill(_uvPath != null ? "uv found" : "uv missing", _uvPath != null ? MCPGui.Good : MCPGui.Warn,
                "uv starts the server and downloads a matching Python if needed.", 90);
            GUILayout.Space(6);
            GUILayout.Label(new GUIContent(_uvPath ?? "uv is needed before any client can start the server.", _uvPath), EditorStyles.wordWrappedLabel);
            GUILayout.EndHorizontal();

            if (_uvPath == null)
            {
                GUILayout.Space(6);
                GUILayout.Label("Install uv by running this in a terminal, then click Check again:", MCPGui.Muted);
                var command = MCPUvLocator.InstallCommand(MCPClientConfigurator.IsWindows);
                EditorGUILayout.SelectableLabel(command, MCPGui.Code, GUILayout.Height(EditorGUIUtility.singleLineHeight * 2));
                GUILayout.BeginHorizontal();
                if (MCPGui.AccentButton(MCPGui.Button("Copy command", "Copy the install command to the clipboard."), MCPGui.Info, GUILayout.Width(110)))
                    EditorGUIUtility.systemCopyBuffer = command;
                if (GUILayout.Button(MCPGui.Button("Check again", "Look for uv again after installing it."), GUILayout.Width(100)))
                    RefreshUv();
                if (GUILayout.Button(MCPGui.Button("uv docs", "Open uv's installation guide in your browser."), GUILayout.Width(80)))
                    Application.OpenURL(MCPUvLocator.DocsUrl);
                GUILayout.EndHorizontal();
            }

            MCPGui.EndCard();
        }

        // ------------------------------------------------------------------ Clients

        private void DrawClientsCard()
        {
            int configured = 0;
            foreach (var kind in MCPClientDetector.AllKinds())
                if (_configuredCache.TryGetValue(kind, out var c) && c) configured++;

            MCPGui.BeginCard("AI Clients", configured > 0 ? MCPGui.Good : MCPGui.Info,
                "Writes each client's MCP config so it starts this project's server. Restart a client after configuring it.");

            GUI.enabled = _uvPath != null;
            if (MCPGui.AccentButton(MCPGui.Button("Configure all detected clients",
                    _uvPath != null
                        ? "Configure every supported client installed for your user, in one click."
                        : "Install uv first (see MCP Server above)."),
                    MCPGui.Good, GUILayout.Height(28)))
                ConfigureAllDetected();
            GUI.enabled = true;

            if (MCPClientConfigTracker.TryGetLastConfigured(out var lastKind, out var lastAt))
                GUILayout.Label($"Last configured: {MCPClientDetector.DisplayName(lastKind)} ({MCPClientConfigTracker.FormatRelativeTime(lastAt, DateTime.UtcNow)})", MCPGui.Muted);

            foreach (var kind in MCPClientDetector.AllKinds())
            {
                MCPGui.Separator();
                DrawClientRow(kind);
            }

            MCPGui.EndCard();
        }

        private void DrawClientRow(MCPClientKind kind)
        {
            bool configured = _configuredCache.TryGetValue(kind, out var c) && c;
            bool detected = MCPClientDetector.IsDetected(kind);
            var fullPath = MCPMcpConfigTargets.AbsolutePath(kind, MCPProjectUtil.ProjectRoot);

            GUILayout.BeginHorizontal();
            MCPGui.Pill(configured ? "Configured" : "Not set", configured ? MCPGui.Good : MCPGui.Neutral,
                configured ? "This project's server entry is in the client's config." : "This client isn't configured for this project yet.", 90);
            GUILayout.Space(6);

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label(MCPClientDetector.DisplayName(kind), EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            if (detected) MCPGui.Pill("detected", MCPGui.Info, "Found installed for your user, so 'Configure all' includes it.");
            GUILayout.EndHorizontal();
            GUILayout.Label(new GUIContent(MCPMcpConfigTargets.RelativePathDisplay(kind), fullPath), MCPGui.Muted);
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            if (GUILayout.Button(MCPGui.Button("Check", "Check whether this client's config contains this project's server."), GUILayout.Width(60), GUILayout.Height(20)))
                CheckStatus(kind);
            GUI.enabled = _uvPath != null;
            if (MCPGui.AccentButton(MCPGui.Button(configured ? "Update" : "Configure",
                    $"Write this project's server entry to {fullPath}."), configured ? MCPGui.Neutral : MCPGui.Good,
                    GUILayout.Width(84), GUILayout.Height(20)))
                Configure(kind);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ actions

        private void RefreshClientConfiguredStatusIfDue()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_configuredCache.Count > 0 && now - _lastClientStatusCheckTime <= StatusRefreshIntervalSeconds) return;

            foreach (var kind in MCPClientDetector.AllKinds())
            {
                _configuredCache[kind] = MCPClientConfigurator.HasOurEntry(kind);
            }
            _lastClientStatusCheckTime = now;
        }

        private void CheckStatus(MCPClientKind kind)
        {
            var displayName = MCPClientDetector.DisplayName(kind);
            var configPath = MCPMcpConfigTargets.AbsolutePath(kind, MCPProjectUtil.ProjectRoot);
            var relativePath = MCPMcpConfigTargets.RelativePathDisplay(kind);
            var serverName = MCPClientConfigurator.ServerName();

            if (!File.Exists(configPath))
            {
                _statusType = MessageType.Info;
                _statusMessage = $"{displayName}: no {relativePath} found yet — click Configure.";
                return;
            }

            bool configured = MCPClientConfigurator.HasOurEntry(kind);
            _configuredCache[kind] = configured;

            _statusType = MessageType.Info;
            _statusMessage = configured
                ? $"{displayName}: '{serverName}' is configured in {relativePath}."
                : $"{displayName}: {relativePath} exists but '{serverName}' is not in it yet — click Configure.";
        }

        private void Configure(MCPClientKind kind)
        {
            RefreshUv();
            var outcome = MCPClientConfigurator.Configure(kind);
            if (outcome.Ok) _configuredCache[kind] = true;
            _statusType = outcome.Ok ? MessageType.Info : MessageType.Error;
            _statusMessage = outcome.Message;
        }

        private void ConfigureAllDetected()
        {
            RefreshUv();
            var done = new List<string>();
            var failed = new List<string>();
            foreach (var kind in MCPClientDetector.AllKinds())
            {
                if (!MCPClientDetector.IsDetected(kind)) continue;
                var outcome = MCPClientConfigurator.Configure(kind);
                if (outcome.Ok) { _configuredCache[kind] = true; done.Add(MCPClientDetector.DisplayName(kind)); }
                else failed.Add(outcome.Message);
            }

            if (done.Count == 0 && failed.Count == 0)
            {
                _statusType = MessageType.Warning;
                _statusMessage = "No supported MCP client was detected for your user. Configure one below by hand.";
                return;
            }

            _statusType = failed.Count > 0 ? MessageType.Warning : MessageType.Info;
            _statusMessage = (done.Count > 0 ? $"Configured: {string.Join(", ", done)}. Restart those clients' sessions to pick it up." : "") +
                             (failed.Count > 0 ? "\n" + string.Join("\n", failed) : "");
        }

        private void RefreshUv()
        {
            _uvPath = MCPUvLocator.Find(MCPClientConfigurator.IsWindows);
            _uvChecked = true;
        }
    }
}
