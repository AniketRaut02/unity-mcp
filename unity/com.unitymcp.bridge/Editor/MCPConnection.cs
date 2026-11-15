using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityMCP.Security;
using Debug = UnityEngine.Debug;

namespace UnityMCP
{
    /// <summary>
    /// One outbound WebSocket from this Editor to one Python MCP server (protocol 2).
    ///
    /// Threading model (read this before touching this file):
    ///   - RunAsync (connect, hello, receive loop) runs on a thread-pool thread. It may only
    ///     do socket I/O, answer pings, and enqueue tool requests via MCPBridge.Enqueue.
    ///     It must NEVER call UnityEngine/UnityEditor APIs (Debug.Log is the one exception:
    ///     it is documented thread-safe).
    ///   - Tool requests run on the main thread (MCPCommandDispatcher), which then calls
    ///     Send() with the answer.
    ///   - Send() is synchronous on purpose: an async send awaited from the main thread
    ///     would capture Unity's SynchronizationContext and deadlock while we block on it.
    /// </summary>
    public class MCPConnection
    {
        public enum ConnState { Connecting, Open, Closed }

        private const int MaxMessageBytes = 16 * 1024 * 1024;
        private const int SendTimeoutMs = 10000;
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

        public readonly int Pid;
        public readonly int Port;

        private volatile ConnState _state = ConnState.Connecting;
        public ConnState State => _state;

        /// <summary>Set once the hello succeeds; the main thread then pushes the tool list and clears it.</summary>
        public volatile bool NeedsToolPush;

        /// <summary>The server refused us in a way retrying cannot fix (protocol version mismatch).</summary>
        public volatile bool Fatal;

        public volatile string LastError;

        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // 100 tool calls per 10 s per connection: guards throughput against a runaway agent
        // loop. MCPCommandDispatcher's per-tick cap separately protects frame time.
        private readonly MCPRateLimiter _rateLimiter = new MCPRateLimiter(maxPerWindow: 100, window: TimeSpan.FromSeconds(10));

        public MCPConnection(int pid, int port)
        {
            Pid = pid;
            Port = port;
        }

        public void Start(string helloJson)
        {
            Task.Run(() => RunAsync(helloJson));
        }

        private async Task RunAsync(string helloJson)
        {
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    connectCts.CancelAfter(ConnectTimeout);
                    await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/unity"), connectCts.Token).ConfigureAwait(false);
                }

                if (!Send(helloJson)) throw new IOException("Could not send hello.");

                var reply = JObject.Parse(await ReceiveTextAsync(HelloTimeout).ConfigureAwait(false) ?? "{}");
                if (reply["error"] is JObject error)
                {
                    LastError = (string)error["message"] ?? "Hello rejected.";
                    if ((int?)error["code"] == MCPRpc.VersionMismatch)
                    {
                        Fatal = true;
                        Debug.LogError($"[MCP] Server on port {Port} refused this Editor: {LastError}");
                    }
                    return;
                }

                LastError = null;
                _state = ConnState.Open;
                NeedsToolPush = true;

                while (!_cts.IsCancellationRequested)
                {
                    var text = await ReceiveTextAsync(null).ConfigureAwait(false);
                    if (text == null) break; // server closed the connection
                    HandleIncoming(text);
                }
            }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested) LastError = e.Message;
            }
            finally
            {
                _state = ConnState.Closed;
                try { _ws.Abort(); } catch { /* already gone */ }
            }
        }

        private void HandleIncoming(string text)
        {
            JObject msg;
            try { msg = JObject.Parse(text); }
            catch { return; } // malformed frame: ignore rather than drop the connection

            var method = (string)msg["method"];
            var idToken = msg["id"];
            var id = idToken == null || idToken.Type == JTokenType.Null ? null : idToken.ToString();
            if (method == null) return; // a response: we send no requests after hello

            if (method == "ping")
            {
                // Answered right here on the socket thread: proves the Editor process is alive
                // even during a long main-thread stall (import, compile). A hung main thread is
                // caught by tool-call timeouts instead.
                Send(MCPRpc.Result(id, new JObject()));
                return;
            }

            if (method == "tools/call" || method == "tools/batch")
            {
                var parameters = msg["params"] as JObject ?? new JObject();

                // A batch is charged as N calls, so it can't bypass the limit. A re-sent id that
                // is already answered (after a reload) costs nothing: it will not run again.
                int charge = method == "tools/batch" ? ((parameters["calls"] as JArray)?.Count ?? 0) : 1;
                if (!MCPAnswerCache.Contains(id) && !_rateLimiter.TryAcquire(charge))
                {
                    Send(MCPRpc.Error(id, MCPRpc.RateLimited,
                        $"Rate limit exceeded (max 100 calls / 10s per connection; this {method} would use {charge}). Slow down, retry, or use smaller batches."));
                    return;
                }

                MCPBridge.Enqueue(new MCPBridge.PendingCommand { Connection = this, Id = id, Method = method, Params = parameters });
                return;
            }

            if (id != null) Send(MCPRpc.Error(id, MCPRpc.MethodNotFound, $"Unknown method '{method}'."));
        }

        /// <summary>Sends one text frame. Safe from any thread; returns false if the connection is gone.</summary>
        public bool Send(string json, int timeoutMs = SendTimeoutMs)
        {
            if (_ws.State != WebSocketState.Open) return false;
            if (!_sendLock.Wait(timeoutMs)) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                if (_ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).Wait(timeoutMs))
                    return true;

                // The send is still in flight and a WebSocket allows only one at a time: drop
                // the connection rather than risk overlapping sends. The bridge reconnects.
                LastError = $"Send timed out after {timeoutMs} ms.";
                try { _ws.Abort(); } catch { /* already gone */ }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>Main thread only. Optionally tells the server a domain reload is starting, then drops the socket immediately.</summary>
        public void Close(bool announceReload)
        {
            if (announceReload && _state == ConnState.Open)
            {
                Send(MCPRpc.Notification("unity/reloading", new JObject()), timeoutMs: 500);
            }
            _cts.Cancel();
            try { _ws.Abort(); } catch { /* already gone */ }
            _state = ConnState.Closed;
        }

        private async Task<string> ReceiveTextAsync(TimeSpan? timeout)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            using (var buffer = new MemoryStream())
            {
                if (timeout.HasValue) cts.CancelAfter(timeout.Value);
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(chunk), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return null;

                    buffer.Write(chunk, 0, result.Count);
                    if (buffer.Length > MaxMessageBytes)
                        throw new IOException($"Message from server exceeds {MaxMessageBytes} bytes.");
                    if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
            }
        }
    }

    /// <summary>JSON-RPC 2.0 message builders for the Unity link (protocol 2).</summary>
    internal static class MCPRpc
    {
        public const int ToolError = -32000;
        public const int VersionMismatch = -32002;
        public const int RateLimited = -32003;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        private class ResultMessage
        {
            public string jsonrpc = "2.0";
            public string id;
            public object result; // always written, even when null: JSON-RPC requires result or error
        }

        private class ErrorMessage
        {
            public string jsonrpc = "2.0";
            public string id;
            public ErrorBody error;
        }

        private class ErrorBody
        {
            public int code;
            public string message;
        }

        private class OutgoingMessage
        {
            public string jsonrpc = "2.0";
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string id;
            public string method;
            [JsonProperty("params")] public object parameters;
        }

        public static string Result(string id, object result) =>
            JsonConvert.SerializeObject(new ResultMessage { id = id, result = result });

        public static string Error(string id, int code, string message) =>
            JsonConvert.SerializeObject(new ErrorMessage { id = id, error = new ErrorBody { code = code, message = message } });

        public static string Request(string id, string method, object parameters) =>
            JsonConvert.SerializeObject(new OutgoingMessage { id = id, method = method, parameters = parameters });

        public static string Notification(string method, object parameters) =>
            JsonConvert.SerializeObject(new OutgoingMessage { method = method, parameters = parameters });
    }
}
