using System;
using System.Collections.Generic;

namespace UnityMCP.Security
{
    /// <summary>
    /// Sliding-window rate limiter, one instance per client connection. Guards against
    /// a runaway agent loop hammering the Editor with calls faster than the main thread
    /// can drain them — independent of MCPCommandDispatcher's per-tick cap, which
    /// protects frame time but not overall throughput.
    /// </summary>
    public class MCPRateLimiter
    {
        private readonly int _maxPerWindow;
        private readonly TimeSpan _window;
        private readonly Queue<DateTime> _timestamps = new Queue<DateTime>();
        private readonly object _lock = new object();

        public MCPRateLimiter(int maxPerWindow, TimeSpan window)
        {
            _maxPerWindow = maxPerWindow;
            _window = window;
        }

        /// <summary>Returns true and records the call if under the limit; returns false without recording if not.</summary>
        public bool TryAcquire() => TryAcquire(1);

        /// <summary>
        /// Atomically checks and records `count` calls at once — used by batch_execute so an
        /// N-item batch is charged as N calls against the same limit a single call would be,
        /// rather than bypassing the limit by hiding behind one wire message.
        /// </summary>
        public bool TryAcquire(int count)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                while (_timestamps.Count > 0 && now - _timestamps.Peek() > _window)
                    _timestamps.Dequeue();

                if (_timestamps.Count + count > _maxPerWindow) return false;

                for (int i = 0; i < count; i++)
                    _timestamps.Enqueue(now);
                return true;
            }
        }
    }
}
