using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Application.logMessageReceivedThreaded can fire from any thread, so this buffer
    /// is lock-protected independently of the MCP command queue rather than assuming
    /// main-thread-only access.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPConsoleCapture
    {
        private const int MaxBuffered = 500;
        private static readonly LinkedList<LogEntry> _buffer = new LinkedList<LogEntry>();
        private static readonly object _lock = new object();

        public struct LogEntry
        {
            public string message;
            public string stackTrace;
            public string type;
        }

        static MCPConsoleCapture()
        {
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            lock (_lock)
            {
                _buffer.AddLast(new LogEntry { message = message, stackTrace = stackTrace, type = type.ToString() });
                if (_buffer.Count > MaxBuffered) _buffer.RemoveFirst();
            }
        }

        public static List<LogEntry> GetRecent(int count)
        {
            lock (_lock)
            {
                var result = new List<LogEntry>();
                var node = _buffer.Last;
                while (node != null && result.Count < count)
                {
                    result.Add(node.Value);
                    node = node.Previous;
                }
                result.Reverse();
                return result;
            }
        }
    }
}
