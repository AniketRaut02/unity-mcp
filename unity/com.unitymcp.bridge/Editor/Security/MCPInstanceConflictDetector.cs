using System;
using System.Diagnostics;

namespace UnityMCP.Security
{
    public class MCPInstanceConflictInfo
    {
        public bool HasConflict;
        public int? OtherPid;
        public int? OtherPort;
        public string OtherStartedAt;
        public string Message;
    }

    /// <summary>
    /// Detects the specific incident class reported in production: a second live Unity
    /// process for the SAME project overwrites session.json with ITS port/token, while
    /// the Setup window (reading this process's own live MCPServer.BoundPort) keeps
    /// showing THIS process's port -- an MCP client connecting right now reads
    /// session.json and reaches the OTHER process, which explains both symptoms at once
    /// ("handshake happens on some other port" and "Setup window shows 0 clients", since
    /// whichever process the client actually reached is not the one the human is looking
    /// at). Unity is the only vantage point that can catch this: it's the only place with
    /// access to BOTH truths simultaneously -- this process's own live in-memory state,
    /// and whatever's currently on disk (which might belong to someone else). A Python-side
    /// diagnostic (diagnose_bridge.py) only ever sees the disk file, which is exactly the
    /// thing that can be misleading during a live conflict.
    ///
    /// Evaluate() is pure decision logic with the actual "is this PID alive" check passed
    /// in as a delegate, so it's fully unit-testable with fake alive/dead process states --
    /// checking whether a REAL process is actually running needs the real OS
    /// (System.Diagnostics.Process), which only DetectReal() does for real.
    /// </summary>
    public static class MCPInstanceConflictDetector
    {
        public static MCPInstanceConflictInfo Evaluate(
            int currentPid, int currentPort,
            MCPSessionSnapshot diskSession,
            Func<int, bool> isProcessAlive)
        {
            var info = new MCPInstanceConflictInfo();

            if (diskSession == null)
            {
                info.HasConflict = false;
                info.Message = "No session file found on disk to compare against.";
                return info;
            }

            if (diskSession.Pid == currentPid)
            {
                info.HasConflict = false;
                info.Message = "session.json matches this process -- no conflict.";
                return info;
            }

            bool otherAlive = isProcessAlive(diskSession.Pid);

            if (!otherAlive)
            {
                info.HasConflict = false;
                info.Message = $"session.json references PID {diskSession.Pid}, which is no longer running -- stale, harmless.";
                return info;
            }

            if (diskSession.Port == currentPort)
            {
                // Two live PIDs both actually bound to the same port is impossible -- the
                // OS would have refused the second bind. Seeing this means we caught
                // session.json mid-write or otherwise transiently inconsistent, not a real
                // second listener actually competing on our own port.
                info.HasConflict = false;
                info.Message = "session.json's port matches this process's own bound port -- likely a stale read mid-write, not a real conflict.";
                return info;
            }

            info.HasConflict = true;
            info.OtherPid = diskSession.Pid;
            info.OtherPort = diskSession.Port;
            info.OtherStartedAt = diskSession.StartedAt;
            info.Message =
                $"Another live Unity process (PID {diskSession.Pid}, started {diskSession.StartedAt}) currently owns " +
                $"session.json, listening on port {diskSession.Port} -- not this Editor's own port ({currentPort}). " +
                "An MCP client connecting right now would reach that OTHER process, not this one. Close the other " +
                $"Unity instance (PID {diskSession.Pid}) for this project, or close this one, so only one remains.";
            return info;
        }

        /// <summary>The real check: reads session.json fresh off disk right now and compares against this process's own live state.</summary>
        public static MCPInstanceConflictInfo DetectReal()
        {
            return DetectReal(MCPServer.BoundPort);
        }

        /// <summary>
        /// Same as DetectReal(), but with the current port passed explicitly rather than
        /// read from MCPServer.BoundPort — needed by MCPServer.Start() itself, which wants
        /// to check for a conflict using the port it's ABOUT to bind, before _running flips
        /// true (BoundPort reads as 0 until then, which would make the comparison useless
        /// at exactly the moment it matters most: right before overwriting session.json).
        /// </summary>
        public static MCPInstanceConflictInfo DetectReal(int currentPort)
        {
            int currentPid = Process.GetCurrentProcess().Id;

            MCPSessionFile.TryReadCurrent(out var diskSession);

            return Evaluate(currentPid, currentPort, diskSession, IsProcessAliveReal);
        }

        private static bool IsProcessAliveReal(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch
            {
                // GetProcessById throws ArgumentException if no such PID exists (already
                // exited and reaped by the OS) -- that's a normal "not alive" outcome, not
                // an error worth distinguishing from any other failure to check.
                return false;
            }
        }
    }
}
