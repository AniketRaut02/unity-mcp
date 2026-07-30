using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using UnityMCP.Groups;
using UnityMCP.Protocol;
using UnityMCP.Security;

namespace UnityMCP
{
    /// <summary>
    /// Runs on the Editor main thread via EditorApplication.update. This is the only
    /// place where [MCPTool] methods are actually invoked, which is what keeps every
    /// tool implementation free to call UnityEngine/UnityEditor APIs without worrying
    /// about threading.
    /// </summary>
    public static class MCPCommandDispatcher
    {
        // Bounded per tick so a burst of queued commands (e.g. a client that queued 40
        // calls) can't freeze Editor UI responsiveness for one long frame — the rest
        // just get picked up on subsequent ticks.
        private const int MaxPerTick = 20;

        // Batches run entirely within one DrainQueue tick (no cross-tick yielding), so
        // this bounds the worst-case single-frame stall from one oversized batch.
        private const int MaxBatchSize = 25;

        public static void DrainQueue()
        {
            int processed = 0;

            // Fast queue gets priority, but reserve at least one slot per tick for the
            // slow queue when it's non-empty, so a steady stream of quick queries can
            // never fully starve a pending Slow-tier call -- just make it slower, not stuck.
            int fastBudget = MCPServer.HasPendingSlow() ? MaxPerTick - 1 : MaxPerTick;

            while (processed < fastBudget && MCPServer.TryDequeueFast(out var fastPending))
            {
                processed++;
                fastPending.Respond(Process(fastPending.Message));
            }

            while (processed < MaxPerTick && MCPServer.TryDequeueSlow(out var slowPending))
            {
                processed++;
                slowPending.Respond(Process(slowPending.Message));
            }
        }

        private static MCPMessage Process(MCPMessage msg)
        {
            switch (msg.type)
            {
                case "list_tools":
                    return HandleListTools(msg);

                case "call":
                    return HandleCall(msg);

                case "batch":
                    return HandleBatch(msg);

                default:
                    return new MCPMessage { type = "result", id = msg.id, ok = false, error = $"Unknown message type '{msg.type}'." };
            }
        }

        private static MCPMessage HandleListTools(MCPMessage msg)
        {
            MCPToolRegistry.EnsureScanned();

            // A disabled group's tools never appear here at all -- not just "inactive"
            // (Python's groups.py-level activation), an actual hard exclusion so a
            // client can't discover the tool exists in the first place. See
            // MCPToolRegistry.Invoke for the matching refusal on a direct-by-name call.
            var tools = MCPToolRegistry.Tools.Values
                .Where(t => !MCPToolGroupConfig.IsDisabled(t.Group))
                .Select(t => new MCPToolDescriptor
                {
                    name = t.Name,
                    description = t.Description,
                    latency_tier = t.LatencyTier == MCPLatencyTier.Slow ? "slow" : "fast",
                    group = t.Group,
                    schema = t.Schema,
                    destructive = t.Destructive,
                    read_only = t.ReadOnly
                }).ToList();

            return new MCPMessage { type = "tools_list", id = msg.id, tools = tools };
        }

        private static MCPMessage HandleCall(MCPMessage msg)
        {
            var ctx = new MCPToolContext { RequestId = msg.id };

            var stopwatch = Stopwatch.StartNew();
            var result = MCPToolRegistry.Invoke(msg.tool, msg.args, ctx);
            stopwatch.Stop();

            AuditRecord(msg.id, msg.tool, msg.args, result, stopwatch.ElapsedMilliseconds);

            return new MCPMessage
            {
                type = "result",
                id = msg.id,
                ok = result.Ok,
                result = result.Data,
                error = result.Error
            };
        }

        /// <summary>
        /// Runs every sub-call through the exact same MCPToolRegistry.Invoke path a
        /// single "call" message uses -- which means the destructive/confirm gate,
        /// audit logging, and argument coercion all apply uniformly with zero special
        /// casing. The one thing batches can't do is run a Slow-tier (domain-reload)
        /// tool: rejected per-item rather than failing the whole batch, since domain
        /// reload mid-batch would silently abandon every call queued after it.
        /// </summary>
        private static MCPMessage HandleBatch(MCPMessage msg)
        {
            if (msg.calls == null || msg.calls.Count == 0)
                return new MCPMessage { type = "batch_result", id = msg.id, ok = false, error = "Batch contains no calls." };

            if (msg.calls.Count > MaxBatchSize)
                return new MCPMessage
                {
                    type = "batch_result",
                    id = msg.id,
                    ok = false,
                    error = $"Batch of {msg.calls.Count} exceeds the max of {MaxBatchSize} -- split into smaller batches."
                };

            var results = new List<MCPBatchResultItem>(msg.calls.Count);

            foreach (var item in msg.calls)
            {
                if (MCPToolRegistry.TryGet(item.tool, out var entry) && entry.LatencyTier == MCPLatencyTier.Slow)
                {
                    var rejection = MCPResult.Fail(
                        $"'{item.tool}' is a Slow-tier (domain-reload-triggering) tool and cannot run inside a batch -- call it directly.");
                    AuditRecord(msg.id, item.tool, item.args, rejection, 0);
                    results.Add(new MCPBatchResultItem { ok = false, error = rejection.Error });
                    continue;
                }

                var ctx = new MCPToolContext { RequestId = msg.id };
                var stopwatch = Stopwatch.StartNew();
                var result = MCPToolRegistry.Invoke(item.tool, item.args, ctx);
                stopwatch.Stop();

                AuditRecord(msg.id, item.tool, item.args, result, stopwatch.ElapsedMilliseconds);
                results.Add(new MCPBatchResultItem { ok = result.Ok, result = result.Data, error = result.Error });
            }

            return new MCPMessage { type = "batch_result", id = msg.id, ok = true, results = results };
        }

        // Every tool invocation is on the record -- name, args, outcome, timing --
        // regardless of success or failure, and regardless of whether it arrived as a
        // single "call" or as one item within a "batch" (Phase 2 audit requirement,
        // extended to cover batches rather than let them bypass it).
        private static void AuditRecord(string requestId, string tool, System.Collections.Generic.Dictionary<string, object> args, MCPResult result, long elapsedMs)
        {
            string argsJson;
            try { argsJson = JsonConvert.SerializeObject(args); }
            catch { argsJson = "<unserializable args>"; }

            MCPAuditLog.Record(requestId, tool, argsJson, result.Ok, result.Error, elapsedMs);
        }
    }
}
