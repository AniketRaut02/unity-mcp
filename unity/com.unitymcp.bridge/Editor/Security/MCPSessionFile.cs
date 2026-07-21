using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityMCP.Security
{
    /// <summary>
    /// The single source of truth a Python client reads to find both which port THIS
    /// project's bridge is actually listening on, and the token to authenticate with.
    ///
    /// Why this exists (Phase 2 fix): with a hardcoded global port, if a second Unity
    /// process (another project, or a stale orphaned instance) grabs that port first,
    /// this project's token will never match what that other listener has cached —
    /// there is no way to tell from the port number alone whose listener you're talking
    /// to. Binding to a self-discovered free port and publishing (port, token) together,
    /// per-project, removes the ambiguity entirely: the Python client for project X reads
    /// project X's session.json and always gets project X's real port + matching token.
    /// </summary>
    internal class MCPSessionInfo
    {
        public string token;
        public int port;
        public int pid;
        public string projectPath;
        public string unityVersion;
        public string startedAt;
    }

    /// <summary>
    /// Read-only view of a session.json's contents, for comparing against this process's
    /// own live state — see MCPInstanceConflictDetector. Deliberately a separate public
    /// type from the internal writer-side MCPSessionInfo, so the on-disk JSON shape isn't
    /// tied to whatever a reader needs.
    /// </summary>
    public class MCPSessionSnapshot
    {
        public string Token;
        public int Port;
        public int Pid;
        public string ProjectPath;
        public string UnityVersion;
        public string StartedAt;
    }

    public static class MCPSessionFile
    {
        private static string SessionDir => Path.Combine(Application.dataPath, "..", "Library", "MCP");
        private static string SessionFilePath => Path.Combine(SessionDir, "session.json");

        public static void Write(int port, string token)
        {
            Directory.CreateDirectory(SessionDir);

            var info = new MCPSessionInfo
            {
                token = token,
                port = port,
                pid = Process.GetCurrentProcess().Id,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                unityVersion = Application.unityVersion,
                startedAt = DateTime.UtcNow.ToString("o")
            };

            File.WriteAllText(SessionFilePath, JsonConvert.SerializeObject(info, Formatting.Indented));
        }

        /// <summary>
        /// Reads whatever is CURRENTLY on disk right now, without assuming it matches this
        /// process's own state — that assumption is exactly what MCPInstanceConflictDetector
        /// exists to check. Returns false (not an exception) for anything short of a clean
        /// read: missing file, mid-write, or malformed JSON all just mean "nothing reliable
        /// to compare against right now", which is a normal transient state (e.g. between a
        /// domain reload's Stop() deleting the file and the next Start() rewriting it), not
        /// an error worth surfacing.
        /// </summary>
        public static bool TryReadCurrent(out MCPSessionSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                if (!File.Exists(SessionFilePath)) return false;

                var json = File.ReadAllText(SessionFilePath);
                var info = JsonConvert.DeserializeObject<MCPSessionInfo>(json);
                if (info == null) return false;

                snapshot = new MCPSessionSnapshot
                {
                    Token = info.token,
                    Port = info.port,
                    Pid = info.pid,
                    ProjectPath = info.projectPath,
                    UnityVersion = info.unityVersion,
                    StartedAt = info.startedAt
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Removes the session file so a Python client can never read a stale (port, token)
        /// pair that no longer has a live listener behind it — called on Stop(), including
        /// domain reload and Editor quit, and rewritten fresh the moment Start() succeeds.
        /// </summary>
        public static void Delete()
        {
            try
            {
                if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath);
            }
            catch
            {
                // Best-effort — a leftover file here just means the next connect attempt
                // gets a clear "connection refused" instead of a silent stale read.
            }
        }
    }
}
