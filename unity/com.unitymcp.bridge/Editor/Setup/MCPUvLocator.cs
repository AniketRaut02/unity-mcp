using System;
using System.Collections.Generic;
using System.IO;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Finds the uv executable that client configs will launch the server with. Returns an
    /// absolute path because MCP clients started from a desktop shortcut (Claude Desktop,
    /// Cursor, VS Code) often don't inherit the PATH your terminal has.
    /// </summary>
    public static class MCPUvLocator
    {
        public const string DocsUrl = "https://docs.astral.sh/uv/getting-started/installation/";

        public static string InstallCommand(bool isWindows) => isWindows
            ? "powershell -ExecutionPolicy ByPass -c \"irm https://astral.sh/uv/install.ps1 | iex\""
            : "curl -LsSf https://astral.sh/uv/install.sh | sh";

        /// <summary>Absolute path to uv, or null when it isn't installed (or not where installers put it).</summary>
        public static string Find(bool isWindows)
        {
            var exe = isWindows ? "uv.exe" : "uv";
            foreach (var dir in CandidateDirectories(isWindows))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                    // malformed PATH entry: skip it
                }
            }
            return null;
        }

        private static IEnumerable<string> CandidateDirectories(bool isWindows)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(isWindows ? ';' : ':')) yield return dir;

            // Where the official installers put it, for Editors started without a login shell's PATH.
            var home = MCPMcpConfigTargets.Home();
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".cargo", "bin");
            if (isWindows)
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
                yield return Path.Combine(localAppData, "Programs", "uv");
            }
            else
            {
                yield return "/opt/homebrew/bin";
                yield return "/usr/local/bin";
            }
        }
    }
}
