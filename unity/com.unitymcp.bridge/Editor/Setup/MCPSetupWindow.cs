using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityMCP.Security;
using UnityMCP.ToolBuilder;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Window -> Unity MCP -> Setup. Live bridge status, multi-instance conflict
    /// detection, and one-click client configuration for four clients (Claude Code,
    /// Codex, Cursor, Antigravity).
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
            window.minSize = new Vector2(460, 480);
        }

        private string _statusMessage = "";
        private MessageType _statusType = MessageType.Info;

        private const double ConflictCheckIntervalSeconds = 2.0;
        private double _lastConflictCheckTime = -1;
        private MCPInstanceConflictInfo _conflictInfo;

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
            DrawBridgeStatus();
            GUILayout.Space(8);
            DrawConflictStatus();
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

        private void DrawBridgeStatus()
        {
            GUILayout.Label("Bridge Status", EditorStyles.boldLabel);

            if (MCPServer.BoundPort > 0)
            {
                GUILayout.Label($"\u25CF Running on port {MCPServer.BoundPort}");
                GUILayout.Label($"Connected clients: {MCPServer.ConnectedClientCount}");
            }
            else
            {
                GUILayout.Label("\u25CB Not running — check the Console for a bind error.");
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

        private void DrawClientSection(MCPClientKind kind)
        {
            GUILayout.Label(MCPClientDetector.DisplayName(kind), EditorStyles.boldLabel);
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
            var serverName = CurrentServerName();
            var configPath = MCPMcpConfigTargets.AbsolutePath(kind, MCPProjectUtil.ProjectRoot);
            var displayName = MCPClientDetector.DisplayName(kind);
            var relativePath = MCPMcpConfigTargets.RelativePathDisplay(kind);

            if (!File.Exists(configPath))
            {
                _statusType = MessageType.Info;
                _statusMessage = $"{displayName}: no {relativePath} found yet — click Configure.";
                return;
            }

            string content;
            try
            {
                content = File.ReadAllText(configPath);
            }
            catch (Exception e)
            {
                _statusType = MessageType.Warning;
                _statusMessage = $"{displayName}: could not read {relativePath} — {e.Message}";
                return;
            }

            bool configured = MCPMcpConfigTargets.Format(kind) == MCPConfigFormat.Toml
                ? MCPCodexTomlWriter.IsConfigured(content, serverName)
                : MCPMcpServersJsonWriter.IsConfigured(content, serverName);

            _statusType = MessageType.Info;
            _statusMessage = configured
                ? $"{displayName}: '{serverName}' is configured in {relativePath}."
                : $"{displayName}: {relativePath} exists but '{serverName}' is not in it yet — click Configure.";
        }

        private void Configure(MCPClientKind kind)
        {
            var displayName = MCPClientDetector.DisplayName(kind);

            var pythonServerPath = MCPToolBuilderSettings.PythonServerPath;
            if (string.IsNullOrEmpty(pythonServerPath))
            {
                _statusType = MessageType.Error;
                _statusMessage = "Set the Python server location in the Python Server or Tool Builder window before configuring a client.";
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

            _statusType = MessageType.Info;
            _statusMessage = $"{displayName}: wrote {relativePath}. Restart your {displayName} session to pick it up." +
                (kind == MCPClientKind.Codex
                    ? " Codex only loads project-scoped config for directories you've marked as trusted — check that in Codex if it doesn't show up."
                    : "") +
                $" This file contains an absolute, machine-specific path — consider adding {relativePath} to .gitignore rather than committing it, so teammates each generate their own.";
        }
    }
}
