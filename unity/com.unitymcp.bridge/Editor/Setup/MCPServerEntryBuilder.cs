using System.Collections.Generic;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Builds the actual command/args/env every generated config entry points at —
    /// identical across all four clients (only the file format/location differs, which
    /// MCPMcpConfigTargets handles). Every value here is computed fresh from THIS
    /// machine's actual state (the configured Python server location, the real Unity
    /// project root) — nothing is a baked-in path, since this project ships to users
    /// whose install locations won't match whoever built it.
    /// </summary>
    public static class MCPServerEntryBuilder
    {
        /// <summary>
        /// An ABSOLUTE path, not a relative one — this command is written into a config
        /// file that some other process reads later, potentially spawning from a working
        /// directory this code has no control over. Explicit separator characters
        /// (matching isWindows) rather than Path.Combine, for the same reason every other
        /// isWindows-driven path in this codebase is built that way: Path.Combine uses
        /// the ACTUAL runtime OS's separator regardless of what isWindows says, which
        /// makes the non-matching branch untestable on a machine running the other OS.
        /// </summary>
        public static string AbsoluteVenvPythonExecutable(string pythonServerPath, bool isWindows)
        {
            var trimmed = pythonServerPath.TrimEnd('/', '\\');
            return isWindows ? $"{trimmed}\\.venv\\Scripts\\python.exe" : $"{trimmed}/.venv/bin/python3";
        }

        public static List<string> Args()
        {
            return new List<string> { "-m", "unity_mcp_server.server" };
        }

        public static Dictionary<string, string> Env(string unityProjectRoot, string pythonServerPath)
        {
            return new Dictionary<string, string>
            {
                ["UNITY_MCP_PROJECT_ROOT"] = unityProjectRoot,
                ["PYTHONPATH"] = pythonServerPath
            };
        }
    }
}
