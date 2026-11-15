using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            int fastBudget = MCPBridge.HasPendingSlow() ? MaxPerTick - 1 : MaxPerTick;

            while (processed < fastBudget && MCPBridge.TryDequeueFast(out var fastPending))
            {
                processed++;
                Execute(fastPending);
            }

            while (processed < MaxPerTick && MCPBridge.TryDequeueSlow(out var slowPending))
            {
                processed++;
                Execute(slowPending);
            }
        }

        /// <summary>
        /// Answers from the cache when this request id already ran (the Python server re-sends
        /// a call after a domain reload with the same id), so no tool ever runs twice for one
        /// request. The answer is cached BEFORE it is sent: if the send is lost to a reload,
        /// the re-sent request still finds it.
        /// </summary>
        public static void Execute(MCPBridge.PendingCommand cmd)
        {
            if (!MCPAnswerCache.TryGet(cmd.Id, out var json))
            {
                json = Process(cmd);
                MCPAnswerCache.Store(cmd.Id, json);
            }
            cmd.Connection?.Send(json);
        }

        public static string Process(MCPBridge.PendingCommand cmd)
        {
            try
            {
                switch (cmd.Method)
                {
                    case "tools/call": return HandleCall(cmd.Id, cmd.Params ?? new JObject());
                    case "tools/batch": return HandleBatch(cmd.Id, cmd.Params ?? new JObject());
                    default: return MCPRpc.Error(cmd.Id, MCPRpc.MethodNotFound, $"Unknown method '{cmd.Method}'.");
                }
            }
            catch (Exception e)
            {
                // e.g. a tool returned an object Newtonsoft can't serialize (self-referencing
                // Unity types). Answer with an error instead of losing the request.
                return MCPRpc.Error(cmd.Id, MCPRpc.InternalError, $"Unity failed to process '{cmd.Method}': {e.Message}");
            }
        }

        /// <summary>
        /// Every registered tool except disabled groups, which are never advertised at all so
        /// a client can't discover they exist. Pushed to each server on connect and whenever
        /// group configuration changes (MCPBridge.RequestToolPush).
        /// </summary>
        public static List<MCPToolDescriptor> BuildToolDescriptors()
        {
            MCPToolRegistry.EnsureScanned();

            return MCPToolRegistry.Tools.Values
                .Where(t => !MCPToolGroupConfig.IsDisabled(t.Group))
                .Where(t => !t.Dangerous || MCPToolGroupConfig.IsDangerTierEnabled())
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
        }

        private static string HandleCall(string id, JObject parameters)
        {
            var tool = (string)parameters["tool"];
            if (string.IsNullOrEmpty(tool))
                return MCPRpc.Error(id, MCPRpc.InvalidParams, "tools/call needs a 'tool' name.");

            var args = (parameters["args"] as JObject)?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
            var ctx = new MCPToolContext { RequestId = id };

            var stopwatch = Stopwatch.StartNew();
            var result = MCPToolRegistry.Invoke(tool, args, ctx);
            stopwatch.Stop();

            AuditRecord(id, tool, args, result, stopwatch.ElapsedMilliseconds);

            return result.Ok
                ? MCPRpc.Result(id, result.Data)
                : MCPRpc.Error(id, MCPRpc.ToolError, result.Error ?? $"Tool '{tool}' failed.");
        }

        /// <summary>
        /// Runs every sub-call through the exact same MCPToolRegistry.Invoke path a
        /// single call uses -- which means the destructive/confirm gate, audit logging,
        /// and argument coercion all apply uniformly with zero special casing. The one
        /// thing batches can't do is run a Slow-tier (domain-reload) tool: rejected
        /// per-item rather than failing the whole batch, since domain reload mid-batch
        /// would silently abandon every call queued after it.
        /// </summary>
        private static string HandleBatch(string id, JObject parameters)
        {
            var calls = (parameters["calls"] as JArray)?.ToObject<List<MCPBatchCallItem>>();
            if (calls == null || calls.Count == 0)
                return MCPRpc.Error(id, MCPRpc.InvalidParams, "Batch contains no calls.");

            if (calls.Count > MaxBatchSize)
                return MCPRpc.Error(id, MCPRpc.InvalidParams,
                    $"Batch of {calls.Count} exceeds the max of {MaxBatchSize} -- split into smaller batches.");

            var results = new List<MCPBatchResultItem>(calls.Count);

            foreach (var item in calls)
            {
                if (MCPToolRegistry.TryGet(item.tool, out var entry) && entry.LatencyTier == MCPLatencyTier.Slow)
                {
                    var rejection = MCPResult.Fail(
                        $"'{item.tool}' is a Slow-tier (domain-reload-triggering) tool and cannot run inside a batch -- call it directly.");
                    AuditRecord(id, item.tool, item.args, rejection, 0);
                    results.Add(new MCPBatchResultItem { ok = false, error = rejection.Error });
                    continue;
                }

                var ctx = new MCPToolContext { RequestId = id };
                var stopwatch = Stopwatch.StartNew();
                var result = MCPToolRegistry.Invoke(item.tool, item.args, ctx);
                stopwatch.Stop();

                AuditRecord(id, item.tool, item.args, result, stopwatch.ElapsedMilliseconds);
                results.Add(new MCPBatchResultItem { ok = result.Ok, result = result.Data, error = result.Error });
            }

            return MCPRpc.Result(id, new { results });
        }

        // Every tool invocation is on the record -- name, args, outcome, timing --
        // regardless of success or failure, and regardless of whether it arrived as a
        // single call or as one item within a batch.
        private static void AuditRecord(string requestId, string tool, Dictionary<string, object> args, MCPResult result, long elapsedMs)
        {
            MCPAuditLog.Record(requestId, tool, MCPAuditLog.TruncateValues(args), result.Ok, result.Error, elapsedMs);
        }
    }
}
