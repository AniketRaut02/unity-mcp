using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityMCP;
using UnityMCP.Support;

namespace UnityMCP.Tools
{
    public static class ConsoleTools
    {
        [MCPTool("read_console_log", "Reads cached Editor console messages captured since this session's last domain reload (errors, warnings, and logs). Use after an action to check whether it produced any runtime errors or warnings.", group: "inspection")]
        public static MCPResult ReadConsoleLog(
            MCPToolContext ctx,
            [MCPParam("Filter by type: \"error\", \"warning\", \"log\", or \"all\". Defaults to \"all\".")] string severity = "all",
            [MCPParam("Maximum number of entries to return, most recent first. Omit for no limit.")] int? limit = null)
        {
            var wanted = severity?.ToLowerInvariant() ?? "all";
            if (wanted != "all" && wanted != "error" && wanted != "warning" && wanted != "log")
                return MCPResult.Fail($"Invalid severity '{severity}'. Use \"error\", \"warning\", \"log\", or \"all\".");

            IEnumerable<MCPConsoleCache.CachedLog> filtered = MCPConsoleCache.Entries;
            if (wanted != "all")
            {
                filtered = filtered.Where(e =>
                    wanted == "error" ? (e.type == LogType.Error || e.type == LogType.Exception || e.type == LogType.Assert)
                    : wanted == "warning" ? e.type == LogType.Warning
                    : e.type == LogType.Log);
            }

            var results = filtered
                .Reverse()
                .Select(e => (object)new
                {
                    severity = e.type.ToString(),
                    message = e.message,
                    stackTrace = e.stackTrace,
                    timestampUtc = e.timestampUtc
                })
                .ToList();

            if (limit.HasValue)
                results = results.Take(limit.Value).ToList();

            return MCPResult.Success(new { count = results.Count, entries = results });
        }

        [MCPTool("clear_console_log", "Clears the Unity Console window and this tool's cached message buffer, so a subsequent read_console_log only shows messages logged after this point.", group: "inspection")]
        public static MCPResult ClearConsoleLog(MCPToolContext ctx)
        {
            MCPConsoleCache.Clear();
            var realConsoleCleared = MCPConsoleCache.TryClearRealConsole();

            return MCPResult.Success(new { cacheCleared = true, realConsoleCleared });
        }
    }
}
