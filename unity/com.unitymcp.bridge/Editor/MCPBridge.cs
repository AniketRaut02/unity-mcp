using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Security;
using Debug = UnityEngine.Debug;

namespace UnityMCP
{
    /// <summary>
    /// The Editor side of the MCP link (protocol 2, package 2.0.0).
    ///
    /// Direction: the Editor is the CLIENT. Each Python MCP server (one per AI client session)
    /// listens on its own loopback port and advertises it in Library/MCP/servers/*.json. This
    /// class polls that folder once a second on the main thread, keeps one MCPConnection per
    /// live server, and reconnects on a bounded schedule (0, 1, 3, 5, 10, 30 s, then gives up
    /// until Reconnect is clicked in the Setup window).
    ///
    /// Domain reloads: everything here is static and is wiped by a reload. Before the reload
    /// we tell every server "unity/reloading", drop the sockets and persist the answer cache;
    /// after it, the static constructor runs again and reconnects at once. The servers hold
    /// tool calls meanwhile and re-send them with the same ids (see MCPAnswerCache).
    ///
    /// Main thread only, except Enqueue (called from socket threads).
    /// </summary>
    [InitializeOnLoad]
    public static class MCPBridge
    {
        public const int ProtocolVersion = 2;

        private static readonly double[] ReconnectDelaysSeconds = { 0, 1, 3, 5, 10, 30 };
        private const double PollIntervalSeconds = 1.0;

        public class PendingCommand
        {
            public MCPConnection Connection;
            public string Id;
            public string Method;
            public JObject Params;
        }

        /// <summary>A snapshot of one advertised server, for the Setup window.</summary>
        public struct ServerStatus
        {
            public int Pid;
            public int Port;
            public string State; // "connected" | "connecting" | "waiting to retry" | "gave up"
            public double RetryInSeconds;
            public string LastError;
        }

        private class ServerEntry
        {
            public string FileName;
            public int Pid;
            public int Port;
            public MCPConnection Connection;
            public int Attempts;
            public double NextAttemptTime;
            public bool GaveUp;
            public string LastError;
        }

        private static readonly ConcurrentQueue<PendingCommand> _fastQueue = new ConcurrentQueue<PendingCommand>();
        private static readonly ConcurrentQueue<PendingCommand> _slowQueue = new ConcurrentQueue<PendingCommand>();
        private static readonly Dictionary<string, ServerEntry> _servers = new Dictionary<string, ServerEntry>();

        private static bool _running;
        private static double _lastPollTime = double.NegativeInfinity;
        private static bool _toolPushRequested;
        private static string _token;
        private static string _packageVersion;

        static MCPBridge()
        {
            EditorApplication.update += MCPCommandDispatcher.DrainQueue;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;

            Start();
        }

        public static bool IsRunning => _running;

        /// <summary>
        /// True when the last Start() bailed out because another live Unity process holds
        /// this project's MCPInstanceLock. The Setup window shows a distinct error for it.
        /// </summary>
        public static bool LastStartBlockedByInstanceLock { get; private set; }

        public static int ConnectedServerCount =>
            _servers.Values.Count(s => s.Connection != null && s.Connection.State == MCPConnection.ConnState.Open);

        public static IReadOnlyList<ServerStatus> Servers
        {
            get
            {
                double now = EditorApplication.timeSinceStartup;
                return _servers.Values.OrderBy(s => s.Pid).Select(s => new ServerStatus
                {
                    Pid = s.Pid,
                    Port = s.Port,
                    State = s.GaveUp ? "gave up"
                        : s.Connection == null ? "waiting to retry"
                        : s.Connection.State == MCPConnection.ConnState.Open ? "connected"
                        : "connecting",
                    RetryInSeconds = s.Connection == null && !s.GaveUp ? Math.Max(0, s.NextAttemptTime - now) : 0,
                    LastError = s.Connection?.LastError ?? s.LastError
                }).ToList();
            }
        }

        public static void Start()
        {
            if (_running) return;
            LastStartBlockedByInstanceLock = false;

            // Headless Editors (CI, build machines) get no bridge unless explicitly opted in:
            // nobody is there to see what an agent does, or to answer an approval request.
            if (Application.isBatchMode && Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCHMODE") != "1")
            {
                Debug.Log("[MCP] Batch mode: the MCP bridge is not started. Set UNITY_MCP_ALLOW_BATCHMODE=1 to allow it.");
                return;
            }

            MCPToolRegistry.EnsureScanned();

            // Only the one process holding this lock may publish unity.json for this project.
            if (!MCPInstanceLock.TryAcquire(MCPProjectUtil.ProjectRoot, out var lockError))
            {
                LastStartBlockedByInstanceLock = true;
                Debug.LogError(
                    $"[MCP] {lockError} Close the other Unity instance for this project, then click Retry in " +
                    "Window > Unity MCP > Setup (or reopen this project).");
                return;
            }

            _token = MCPAuthHandshake.EnsureToken();
            _packageVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPBridge).Assembly)?.version ?? "unknown";
            MCPUnityInfoFile.Write(_token, ProtocolVersion, _packageVersion);
            MCPUnityInfoFile.DeleteLegacySessionFile();
            MCPAnswerCache.Restore();

            _running = true;
            _lastPollTime = double.NegativeInfinity; // poll on the next tick
        }

        /// <summary>Clears every "gave up" state and retries all advertised servers now (Setup window button).</summary>
        public static void Reconnect()
        {
            foreach (var entry in _servers.Values)
            {
                entry.GaveUp = false;
                entry.Attempts = 0;
                entry.NextAttemptTime = 0;
            }
            _lastPollTime = double.NegativeInfinity;
        }

        /// <summary>Re-sends the tool list to every server, e.g. after a group is enabled or disabled.</summary>
        public static void RequestToolPush() => _toolPushRequested = true;

        /// <summary>
        /// Called from socket threads. Slow-tier tools (domain-reload triggering) get their own
        /// queue so a burst of quick queries never waits behind them.
        /// </summary>
        public static void Enqueue(PendingCommand cmd)
        {
            var tool = cmd.Method == "tools/call" ? (string)cmd.Params?["tool"] : null;
            bool slow = tool != null
                        && MCPToolRegistry.TryGet(tool, out var entry)
                        && entry.LatencyTier == MCPLatencyTier.Slow;
            (slow ? _slowQueue : _fastQueue).Enqueue(cmd);
        }

        public static bool TryDequeueFast(out PendingCommand cmd) => _fastQueue.TryDequeue(out cmd);
        public static bool TryDequeueSlow(out PendingCommand cmd) => _slowQueue.TryDequeue(out cmd);
        public static bool HasPendingSlow() => !_slowQueue.IsEmpty;

        private static void Tick()
        {
            if (!_running) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastPollTime >= PollIntervalSeconds)
            {
                _lastPollTime = now;
                PollServerFiles(now);
            }

            bool pushAll = _toolPushRequested;
            _toolPushRequested = false;

            foreach (var entry in _servers.Values)
            {
                var connection = entry.Connection;
                if (connection != null)
                {
                    if (connection.State == MCPConnection.ConnState.Open)
                    {
                        entry.Attempts = 0; // a successful connection resets the retry schedule
                        if (connection.NeedsToolPush || pushAll) PushTools(connection);
                        continue;
                    }
                    if (connection.State == MCPConnection.ConnState.Connecting) continue;

                    // Closed: remember why, then schedule the next attempt (or give up).
                    entry.LastError = connection.LastError;
                    entry.Connection = null;
                    if (connection.Fatal) entry.GaveUp = true;
                    else ScheduleNextAttempt(entry, now);
                }

                if (!entry.GaveUp && now >= entry.NextAttemptTime)
                {
                    entry.Connection = new MCPConnection(entry.Pid, entry.Port);
                    entry.Connection.Start(BuildHello());
                }
            }
        }

        private static void ScheduleNextAttempt(ServerEntry entry, double now)
        {
            if (entry.Attempts >= ReconnectDelaysSeconds.Length)
            {
                entry.GaveUp = true;
                Debug.LogWarning($"[MCP] Gave up connecting to the MCP server on port {entry.Port} (pid {entry.Pid}): " +
                                 $"{entry.LastError ?? "no answer"}. Click Reconnect in Window > Unity MCP > Setup to try again.");
                return;
            }
            entry.NextAttemptTime = now + ReconnectDelaysSeconds[entry.Attempts];
            entry.Attempts++;
        }

        private static void PollServerFiles(double now)
        {
            var dir = Path.Combine(MCPProjectUtil.ProjectRoot, "Library", "MCP", "servers");
            var seen = new HashSet<string>();

            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.GetFiles(dir, "*.json"))
                {
                    var name = Path.GetFileName(path);
                    if (!TryReadServerFile(path, out int pid, out int port))
                    {
                        // Unreadable right now (being replaced): keep what we had, re-read next poll.
                        if (_servers.ContainsKey(name)) seen.Add(name);
                        continue;
                    }

                    if (!IsProcessAlive(pid))
                    {
                        try { File.Delete(path); } catch { /* best-effort cleanup of a dead server's entry */ }
                        continue;
                    }

                    seen.Add(name);
                    if (!_servers.ContainsKey(name))
                    {
                        var entry = new ServerEntry { FileName = name, Pid = pid, Port = port };
                        ScheduleNextAttempt(entry, now);
                        _servers[name] = entry;
                    }
                }
            }

            foreach (var gone in _servers.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _servers[gone].Connection?.Close(announceReload: false);
                _servers.Remove(gone);
            }
        }

        private static bool TryReadServerFile(string path, out int pid, out int port)
        {
            pid = port = 0;
            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                pid = (int?)json["pid"] ?? 0;
                port = (int?)json["port"] ?? 0;
                return pid > 0 && port > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { return false; } // no such process

            try { return !process.HasExited; }
            catch { return true; } // exists but we may not query it (another user's process)
        }

        private static string BuildHello()
        {
            return MCPRpc.Request("hello-" + Guid.NewGuid().ToString("N"), "hello", new
            {
                token = _token,
                protocolVersion = ProtocolVersion,
                packageVersion = _packageVersion,
                unityVersion = Application.unityVersion,
                projectPath = MCPProjectUtil.ProjectRoot,
                pid = Process.GetCurrentProcess().Id
            });
        }

        private static void PushTools(MCPConnection connection)
        {
            connection.NeedsToolPush = false;
            var tools = MCPCommandDispatcher.BuildToolDescriptors();
            connection.Send(MCPRpc.Notification("tools/register", new { tools }));
        }

        private static void OnBeforeAssemblyReload()
        {
            if (!_running) return;

            MCPAnswerCache.Persist();
            foreach (var entry in _servers.Values)
                entry.Connection?.Close(announceReload: true);
            _servers.Clear();
            _running = false;

            // unity.json stays (same token after the reload). The lock is released so this
            // same process can take it again once the reloaded domain calls Start().
            MCPInstanceLock.Release();
        }

        private static void OnQuitting()
        {
            if (!_running) return;

            foreach (var entry in _servers.Values)
                entry.Connection?.Close(announceReload: false);
            _servers.Clear();
            _running = false;

            MCPUnityInfoFile.Delete(); // tells servers "Unity is closed", not "reloading"
            MCPInstanceLock.Release();
        }
    }
}
