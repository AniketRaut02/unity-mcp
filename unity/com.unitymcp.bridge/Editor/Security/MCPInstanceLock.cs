using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UnityMCP.Security
{
    /// <summary>
    /// The actual fix for the reported "MCP silently fails mid-use, Setup window says
    /// another instance's PID is active" incident.
    ///
    /// MCPInstanceConflictDetector (see that file) only ever produced a WARNING: even
    /// after detecting a live conflict, MCPServer.Start() still went ahead and
    /// overwrote session.json unconditionally. With two live Unity processes for the
    /// same project, both would keep re-winning that race on every domain reload —
    /// whichever one wrote session.json LAST is the one any (re)connecting client
    /// reaches, silently, with no error at all. A tool call succeeding against the
    /// WRONG Unity process is worse than a loud failure: it looks like it worked.
    ///
    /// PID comparison is also inherently racy as a way to detect "is the other side
    /// still alive": a stale session.json can reference a PID that Windows has since
    /// reused for a completely unrelated process, which would read back as "still
    /// alive" forever and misreport a permanent phantom conflict.
    ///
    /// An OS-level exclusive file lock has neither problem: only one process can ever
    /// hold it at a time (true mutual exclusion, not a heuristic), and the OS
    /// guarantees it is released the instant the holding process exits for ANY reason
    /// — clean shutdown, crash, or a hard kill — so there is no PID-reuse window to
    /// misread. Whoever holds the lock is unambiguously the one process allowed to
    /// claim session.json for this project.
    /// </summary>
    public static class MCPInstanceLock
    {
        private static FileStream _handle;

        private static string LockPath(string projectRoot) =>
            Path.Combine(projectRoot, "Library", "MCP", "bridge.lock");

        /// <summary>
        /// Attempts to become the sole owner of this project's MCP bridge. Returns true
        /// and holds the lock open for the remainder of the process's life (release via
        /// Release()) on success. Returns false without throwing if another live
        /// process already holds it -- that is an expected, common outcome (e.g. a
        /// second Editor window on the same project), not an error.
        /// </summary>
        public static bool TryAcquire(string projectRoot, out string error)
        {
            error = null;

            if (_handle != null)
            {
                // Already held by this process (e.g. a Rescan/Start called twice
                // without an intervening Release) -- idempotent success.
                return true;
            }

            var path = LockPath(projectRoot);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // FileShare.None is the actual mutual-exclusion primitive here: the OS
                // refuses to hand out a second handle to this file while this one is
                // open, from any process, including this one's own domain-reloaded
                // successor (which is fine -- that successor calls Release() via
                // Stop() first, in the same OS process, before trying to reacquire).
                _handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _handle.SetLength(0);
                var bytes = Encoding.UTF8.GetBytes(Process.GetCurrentProcess().Id.ToString());
                _handle.Write(bytes, 0, bytes.Length);
                _handle.Flush();
                return true;
            }
            catch (IOException e)
            {
                _handle = null;
                error =
                    "Another live Unity process already holds the MCP bridge lock for this project " +
                    $"(Library/MCP/bridge.lock is in use: {e.Message}). This Editor instance will not " +
                    "start its own bridge to avoid silently fighting the other one over session.json.";
                return false;
            }
        }

        /// <summary>Releases the lock, if held by this process, so another process (or this one, after a domain reload) can acquire it.</summary>
        public static void Release()
        {
            try
            {
                _handle?.Dispose();
            }
            catch
            {
                // Best-effort -- the OS releases the lock on process exit regardless.
            }
            finally
            {
                _handle = null;
            }
        }

        /// <summary>True if THIS process currently holds the lock.</summary>
        public static bool IsHeld => _handle != null;
    }
}
