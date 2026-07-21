using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Subscribes to CompilationPipeline events to capture structured compiler
    /// diagnostics (file, line, column, message) rather than scraping the console log
    /// text, which is fragile and doesn't carry structured location data. Messages are
    /// cleared at the start of each new compilation so a stale error from three
    /// compiles ago is never reported as current.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPCompileStatus
    {
        public struct CompileMessage
        {
            public string assembly;
            public string message;
            public string file;
            public int line;
            public int column;
            public string type; // "Error" | "Warning"
        }

        private static readonly object _lock = new object();
        private static List<CompileMessage> _messages = new List<CompileMessage>();
        private static DateTime _lastCompileFinishedAt = DateTime.MinValue;

        static MCPCompileStatus()
        {
            CompilationPipeline.compilationStarted += _ => ResetForNewCompile();
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        private static void ResetForNewCompile()
        {
            lock (_lock)
            {
                _messages = new List<CompileMessage>();
            }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            lock (_lock)
            {
                foreach (var m in messages)
                {
                    _messages.Add(new CompileMessage
                    {
                        assembly = assemblyPath,
                        message = m.message,
                        file = m.file,
                        line = m.line,
                        column = m.column,
                        type = m.type == CompilerMessageType.Error ? "Error" : "Warning"
                    });
                }
                _lastCompileFinishedAt = DateTime.UtcNow;
            }
        }

        public static List<CompileMessage> GetMessages()
        {
            lock (_lock)
            {
                return new List<CompileMessage>(_messages);
            }
        }

        public static DateTime LastCompileFinishedAt
        {
            get { lock (_lock) return _lastCompileFinishedAt; }
        }
    }
}
