using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityMCP.Protocol;
using UnityMCP.Security;

namespace UnityMCP
{
    /// <summary>
    /// Owns the loopback TCP socket and the background threads that read/write it.
    ///
    /// Threading model (read this before touching this file):
    ///   - AcceptLoop and per-client read loops run on background threads. They may
    ///     ONLY do socket I/O and enqueue/dequeue on the thread-safe collections below.
    ///     They must NEVER call into UnityEngine/UnityEditor APIs directly.
    ///   - MCPCommandDispatcher.DrainQueue runs on the main thread via
    ///     EditorApplication.update, and is the ONLY place tool methods (which do call
    ///     Unity APIs) are actually invoked.
    ///   - Responses flow back through the same client's background thread via the
    ///     PendingCommand.Respond callback, which the main thread calls after invoking
    ///     the tool — the callback itself just writes bytes to the socket.
    /// Violating the "no Unity API calls off the main thread" rule is the single most
    /// common way naive Unity-MCP bridges crash the Editor.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPServer
    {
        private const int BasePort = 45678;
        private const int MaxPortAttempts = 50;
        private const int MaxFrameSize = 16 * 1024 * 1024;

        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static int _boundPort;
        private static readonly object _writeLock = new object();

        private static readonly ConcurrentQueue<PendingCommand> _fastQueue = new ConcurrentQueue<PendingCommand>();
        private static readonly ConcurrentQueue<PendingCommand> _slowQueue = new ConcurrentQueue<PendingCommand>();

        public class PendingCommand
        {
            public MCPMessage Message;
            public Action<MCPMessage> Respond;
        }

        static MCPServer()
        {
            EditorApplication.update += MCPCommandDispatcher.DrainQueue;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            AppDomain.CurrentDomain.DomainUnload += (_, __) => Stop();
            EditorApplication.quitting += Stop;

            Start();
        }

        /// <summary>
        /// Routes to the fast or slow queue based on the target tool's declared latency
        /// tier, so a burst of quick queries (get_hierarchy, get_selected_object, ...)
        /// never has to wait behind a domain-reload-triggering tool that's already
        /// queued. batch_execute and list_tools are always fast-tier: batches reject any
        /// Slow-tier sub-call individually (see MCPCommandDispatcher), so a whole batch
        /// can never itself be a multi-second stall the way one Slow tool call can.
        /// </summary>
        public static void Enqueue(PendingCommand cmd)
        {
            if (IsSlowTier(cmd.Message)) _slowQueue.Enqueue(cmd);
            else _fastQueue.Enqueue(cmd);
        }

        private static bool IsSlowTier(MCPMessage msg)
        {
            if (msg.type != "call") return false;
            return MCPToolRegistry.TryGet(msg.tool, out var entry) && entry.LatencyTier == MCPLatencyTier.Slow;
        }

        public static bool TryDequeueFast(out PendingCommand cmd) => _fastQueue.TryDequeue(out cmd);
        public static bool TryDequeueSlow(out PendingCommand cmd) => _slowQueue.TryDequeue(out cmd);
        public static bool HasPendingSlow() => !_slowQueue.IsEmpty;

        /// <summary>The port actually bound this session, or 0 if not currently listening.</summary>
        public static int BoundPort => _running ? _boundPort : 0;

        private static int _connectedClientCount;

        /// <summary>
        /// How many clients currently have an authenticated connection open. Used by the
        /// Setup window to show a real "is anything actually talking to this" signal —
        /// a listening port alone doesn't tell you whether Claude Code/Codex ever
        /// successfully connected, only that the socket exists.
        /// </summary>
        public static int ConnectedClientCount => _connectedClientCount;

        public static void Start()
        {
            if (_running) return;

            string token = MCPAuthHandshake.EnsureToken();
            MCPToolRegistry.EnsureScanned();

            // Search for a free port rather than assuming BasePort is available. This is
            // what makes multiple Unity projects/instances on one machine safe: each
            // binds whatever port it can get, and publishes that actual port alongside
            // its token via MCPSessionFile — a Python client always reads the (port,
            // token) pair for the specific project it's pointed at, so it can never end
            // up talking to a stale or unrelated project's listener.
            int port = BasePort;
            _listener = null;
            for (int attempt = 0; attempt < MaxPortAttempts; attempt++)
            {
                try
                {
                    var candidate = new TcpListener(IPAddress.Loopback, port);
                    candidate.Start();
                    _listener = candidate;
                    break;
                }
                catch (SocketException)
                {
                    port++;
                }
            }

            if (_listener == null)
            {
                Debug.LogError(
                    $"[MCP] Could not bind any port in range {BasePort}-{BasePort + MaxPortAttempts - 1}. " +
                    "Too many bridge instances running? Close stale Unity/Editor processes and reopen this project.");
                return;
            }

            _boundPort = port;

            // Check what's CURRENTLY on disk before we overwrite it -- if it belongs to
            // another still-live Unity process for this same project, that's the exact
            // incident class reported in production (see MCPInstanceConflictDetector):
            // that other process's port ends up being what an MCP client actually
            // connects to, while this Editor's own Setup window shows this process's own
            // port, making the two disagree with no obvious explanation. Logged now,
            // proactively, rather than only being discoverable by opening the Setup
            // window and clicking a button.
            var conflict = MCPInstanceConflictDetector.DetectReal(port);
            if (conflict.HasConflict)
            {
                Debug.LogWarning($"[MCP] Possible multi-instance conflict detected: {conflict.Message}");
            }

            MCPSessionFile.Write(port, token);

            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MCP-Accept" };
            _acceptThread.Start();

            Debug.Log($"[MCP] Listening on 127.0.0.1:{port} (loopback only). Session file written for this project.");
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener?.Stop(); } catch { /* listener already gone */ }

            // Remove the session file immediately so a Python client can never read a
            // (port, token) pair with no live listener behind it — better a clear
            // "connection refused" / "no session file" than a silent stale match.
            MCPSessionFile.Delete();
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    break; // listener was stopped
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var clientThread = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "MCP-Client" };
                clientThread.Start();
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                bool authenticated = false;
                bool countedAsConnected = false;
                // 100 tool calls per 10-second window per connection. This guards overall
                // throughput against a runaway agent loop; MCPCommandDispatcher's
                // per-tick cap separately protects single-frame responsiveness — the two
                // are complementary, not redundant.
                var rateLimiter = new MCPRateLimiter(maxPerWindow: 100, window: TimeSpan.FromSeconds(10));

                try
                {
                    while (_running && client.Connected)
                    {
                        MCPMessage msg;
                        try
                        {
                            msg = ReadMessage(stream);
                        }
                        catch (IOException)
                        {
                            break;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[MCP] Malformed message from client, closing connection: {e.Message}");
                            break;
                        }

                        if (msg == null) break; // clean disconnect

                        if (!authenticated)
                        {
                            if (msg.type == "handshake" && MCPAuthHandshake.Validate(msg.token))
                            {
                                authenticated = true;
                                Interlocked.Increment(ref _connectedClientCount);
                                countedAsConnected = true;
                                WriteMessage(stream, new MCPMessage { type = "handshake_ack", ok = true, id = msg.id });
                            }
                            else
                            {
                                WriteMessage(stream, new MCPMessage
                                {
                                    type = "handshake_ack",
                                    ok = false,
                                    id = msg.id,
                                    error = "Invalid or missing token."
                                });
                                break; // reject and close
                            }
                            continue;
                        }

                        // A batch is charged as N calls against the rate limit, one per
                        // sub-call — otherwise batch_execute would be a way to bypass the
                        // limit by hiding many calls behind one wire message.
                        int chargeCount = msg.type == "batch" ? (msg.calls?.Count ?? 0) : 1;
                        bool needsRateCheck = msg.type == "call" || msg.type == "batch";

                        if (needsRateCheck && !rateLimiter.TryAcquire(chargeCount))
                        {
                            WriteMessage(stream, new MCPMessage
                            {
                                type = msg.type == "batch" ? "batch_result" : "result",
                                id = msg.id,
                                ok = false,
                                error = $"Rate limit exceeded (max 100 calls / 10s on this connection; this {msg.type} would use {chargeCount}). Slow down, retry, or use smaller batches."
                            });
                            continue;
                        }

                        var pending = new PendingCommand
                        {
                            Message = msg,
                            Respond = response => WriteMessage(stream, response)
                        };
                        Enqueue(pending);
                    }
                }
                finally
                {
                    if (countedAsConnected) Interlocked.Decrement(ref _connectedClientCount);
                }
            }
        }

        private static MCPMessage ReadMessage(NetworkStream stream)
        {
            var lengthBytes = ReadExact(stream, 4);
            if (lengthBytes == null) return null;

            int length = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];
            if (length <= 0 || length > MaxFrameSize)
                throw new IOException($"Invalid frame length: {length}");

            var payload = ReadExact(stream, length);
            if (payload == null) return null;

            var json = Encoding.UTF8.GetString(payload);
            return JsonConvert.DeserializeObject<MCPMessage>(json);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) return null; // peer disconnected
                offset += read;
            }
            return buffer;
        }

        private static void WriteMessage(NetworkStream stream, MCPMessage message)
        {
            var json = JsonConvert.SerializeObject(message);
            var payload = Encoding.UTF8.GetBytes(json);
            int length = payload.Length;

            var header = new byte[]
            {
                (byte)((length >> 24) & 0xFF),
                (byte)((length >> 16) & 0xFF),
                (byte)((length >> 8) & 0xFF),
                (byte)(length & 0xFF)
            };

            lock (_writeLock)
            {
                try
                {
                    stream.Write(header, 0, 4);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
                catch (IOException)
                {
                    // Client disconnected between the read that produced this response and now — drop it.
                }
            }
        }
    }
}
