using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityMCP.Support
{
    /// <summary>
    /// Caches C# compiler diagnostics from CompilationPipeline, independent
    /// of the Editor Console log buffer (which can be cleared, overflow, or
    /// have "Clear on Recompile" enabled). Backs the get_compilation_errors
    /// and wait_for_compile tools.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPCompilationCache
    {
        internal struct CachedMessage
        {
            public string assembly;
            public string file;
            public int line;
            public int column;
            public CompilerMessageType type;
            public string message;
        }

        private static readonly List<CachedMessage> _messages = new List<CachedMessage>();
        private static bool _isCompiling;

        internal static bool IsCompiling => _isCompiling;
        internal static DateTime LastCompileUtc { get; private set; } = DateTime.MinValue;
        internal static IReadOnlyList<CachedMessage> Messages => _messages;

        static MCPCompilationCache()
        {
            CompilationPipeline.assemblyCompilationStarted += OnAssemblyCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationStarted += _ => _isCompiling = true;
            CompilationPipeline.compilationFinished += _ =>
            {
                _isCompiling = false;
                LastCompileUtc = DateTime.UtcNow;
            };
        }

        private static void OnAssemblyCompilationStarted(string assemblyPath)
        {
            // A fresh compile of this assembly supersedes its prior messages.
            _messages.RemoveAll(m => m.assembly == assemblyPath);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            foreach (var m in messages)
            {
                _messages.Add(new CachedMessage
                {
                    assembly = assemblyPath,
                    file = m.file,
                    line = m.line,
                    column = m.column,
                    type = m.type,
                    message = m.message
                });
            }
        }
    }
}
