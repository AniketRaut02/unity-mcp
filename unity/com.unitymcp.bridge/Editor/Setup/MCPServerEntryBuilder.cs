using System.Collections.Generic;
using System.IO;

namespace UnityMCP.Setup
{
    /// <summary>
    /// The command/args/env every generated client config uses to start the MCP server that
    /// ships inside this package (Server~/), identical across clients; only the file format and
    /// location differ (MCPMcpConfigTargets). Everything is computed from this machine's real
    /// state, nothing is baked in:
    ///
    ///   &lt;uv&gt; run --project &lt;package&gt;/Server~ --frozen python -m unity_mcp_server.server
    ///
    /// with the Python environment in &lt;project&gt;/Library/MCP/.venv (UV_PROJECT_ENVIRONMENT), so
    /// it's per project, built from uv.lock's exact versions, re-synced by uv after a package
    /// update, and never written inside the package folder. PYTHONPATH points at Server~
    /// because the server runs from source (pyproject's tool.uv.package = false).
    ///
    /// The package folder changes on every git/OpenUPM update (PackageCache/...@hash), which is
    /// why MCPClientConfigUpdater rewrites these entries when it moves.
    /// </summary>
    public static class MCPServerEntryBuilder
    {
        /// <summary>Main thread (PackageInfo). The bundled server folder, or null if the package can't be located.</summary>
        public static string BundledServerDirectory()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPServerEntryBuilder).Assembly);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath)) return null;
            var dir = Path.Combine(package.resolvedPath, "Server~");
            return Directory.Exists(dir) ? Path.GetFullPath(dir) : null;
        }

        public static List<string> Args(string serverDirectory)
        {
            return new List<string> { "run", "--project", serverDirectory, "--frozen", "python", "-m", "unity_mcp_server.server" };
        }

        public static Dictionary<string, string> Env(string unityProjectRoot, string serverDirectory)
        {
            return new Dictionary<string, string>
            {
                ["UNITY_MCP_PROJECT_ROOT"] = unityProjectRoot,
                ["UV_PROJECT_ENVIRONMENT"] = Path.Combine(unityProjectRoot, "Library", "MCP", ".venv"),
                ["PYTHONPATH"] = serverDirectory
            };
        }
    }
}
