using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEditor;

namespace UnityMCP
{
    /// <summary>
    /// Recent tool answers by JSON-RPC request id, so a request the Python server re-sends
    /// after a domain reload (same id) gets the stored answer instead of running twice.
    /// Typical case: create_script ran, triggered the reload, and its reply never made it
    /// out; Python re-sends on reconnect and gets "created" back instead of "already exists".
    ///
    /// Bounded both ways: at most 200 answers, none older than 5 minutes. Lives in memory
    /// (thread-safe: socket threads ask Contains()) and is copied into SessionState only
    /// around a reload, so normal calls never pay to re-serialize the whole cache.
    /// </summary>
    internal static class MCPAnswerCache
    {
        private const int MaxEntries = 200;
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
        private const string SessionKey = "UnityMCP.AnswerCache";

        public class Entry // public so Newtonsoft can recreate it from SessionState
        {
            public string id;
            public string json;
            public DateTime storedAtUtc;
        }

        private static readonly LinkedList<Entry> _order = new LinkedList<Entry>();
        private static readonly Dictionary<string, LinkedListNode<Entry>> _byId = new Dictionary<string, LinkedListNode<Entry>>();
        private static readonly object _lock = new object();

        public static bool TryGet(string id, out string json)
        {
            json = null;
            if (id == null) return false;
            lock (_lock)
            {
                Prune(DateTime.UtcNow);
                if (!_byId.TryGetValue(id, out var node)) return false;
                json = node.Value.json;
                return true;
            }
        }

        public static bool Contains(string id) => TryGet(id, out _);

        public static void Store(string id, string json)
        {
            if (id == null) return;
            lock (_lock)
            {
                if (_byId.ContainsKey(id)) return;
                _byId[id] = _order.AddLast(new Entry { id = id, json = json, storedAtUtc = DateTime.UtcNow });
                Prune(DateTime.UtcNow);
            }
        }

        /// <summary>Main thread, from beforeAssemblyReload: the static fields above are about to be wiped.</summary>
        public static void Persist()
        {
            lock (_lock)
            {
                Prune(DateTime.UtcNow);
                SessionState.SetString(SessionKey, JsonConvert.SerializeObject(new List<Entry>(_order)));
            }
        }

        /// <summary>Main thread, on bridge start after a reload.</summary>
        public static void Restore()
        {
            var saved = SessionState.GetString(SessionKey, "");
            SessionState.EraseString(SessionKey);
            if (string.IsNullOrEmpty(saved)) return;

            List<Entry> entries;
            try { entries = JsonConvert.DeserializeObject<List<Entry>>(saved); }
            catch { return; } // a corrupt cache just means no dedupe for this one reload

            lock (_lock)
            {
                foreach (var e in entries ?? new List<Entry>())
                {
                    if (e?.id != null && !_byId.ContainsKey(e.id))
                        _byId[e.id] = _order.AddLast(e);
                }
                Prune(DateTime.UtcNow);
            }
        }

        private static void Prune(DateTime nowUtc)
        {
            while (_order.First != null && (_order.Count > MaxEntries || nowUtc - _order.First.Value.storedAtUtc > MaxAge))
            {
                _byId.Remove(_order.First.Value.id);
                _order.RemoveFirst();
            }
        }
    }
}
