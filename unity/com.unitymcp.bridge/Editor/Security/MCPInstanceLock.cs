using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UnityMCP.Security
{
    /// <summary>
    /// Guarantees one Unity Editor per project publishes Library/MCP/unity.json (and so is the
    /// one the project's MCP servers accept). Two live Editors on one project used to take
    /// turns overwriting the shared rendezvous file, silently redirecting a connected client
    /// to whichever wrote last; a tool call succeeding against the WRONG Editor is worse than
    /// a loud failure.
    ///
    /// An OS-level exclusive file lock is real mutual exclusion, not a PID heuristic: only one
    /// process can hold it, and the OS releases it the instant that process exits for ANY
    /// reason (clean quit, crash, hard kill), so there is no PID-reuse window to misread.
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
                    "start its own bridge, so MCP clients keep talking to the other one.";
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
