using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityMCP;
using UnityMCP.Support;

namespace UnityMCP.Tools
{
    public static class CompilationTools
    {
        [MCPTool("get_compilation_errors", "Returns C# compiler errors and warnings from the last compile, read directly from CompilationPipeline. Unlike console-based tools, this is unaffected by Console clears or log buffer overflow — use it to check whether a script change actually compiled cleanly.", group: "scripting", readOnly: true)]
        public static MCPResult GetCompilationErrors(
            MCPToolContext ctx,
            [MCPParam("Filter by severity: \"error\", \"warning\", or \"all\". Defaults to \"all\".")] string severity = "all",
            [MCPParam("Maximum number of messages to return, most recent first. Omit for no limit.")] int? limit = null)
        {
            if (MCPCompilationCache.IsCompiling)
                return MCPResult.Fail("Compilation is still in progress. Call wait_for_compile first, then retry.");

            var wanted = severity?.ToLowerInvariant() ?? "all";
            if (wanted != "all" && wanted != "error" && wanted != "warning")
                return MCPResult.Fail($"Invalid severity '{severity}'. Use \"error\", \"warning\", or \"all\".");

            var filtered = MCPCompilationCache.Messages.Where(m =>
                wanted == "all"
                || (wanted == "error" && m.type == CompilerMessageType.Error)
                || (wanted == "warning" && m.type == CompilerMessageType.Warning));

            var results = filtered
                .Select(m => (object)new
                {
                    file = m.file,
                    line = m.line,
                    column = m.column,
                    severity = m.type == CompilerMessageType.Error ? "error" : "warning",
                    message = m.message,
                    assembly = m.assembly
                })
                .ToList();

            if (limit.HasValue)
                results = results.Take(limit.Value).ToList();

            return MCPResult.Success(new
            {
                count = results.Count,
                lastCompileUtc = MCPCompilationCache.LastCompileUtc,
                messages = results
            });
        }

        [MCPTool("wait_for_compile", "Blocks until any in-progress script compile / domain reload finishes, up to a timeout. Call this after any Slow-tier tool that can trigger a recompile, before relying on new types or reading compilation results.", group: "scripting", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult WaitForCompile(
            MCPToolContext ctx,
            [MCPParam("Maximum time to wait, in seconds, before giving up. Defaults to 30.")] float timeoutSeconds = 30f)
        {
            var start = EditorApplication.timeSinceStartup;

            while (EditorApplication.isCompiling || MCPCompilationCache.IsCompiling)
            {
                if (EditorApplication.timeSinceStartup - start > timeoutSeconds)
                    return MCPResult.Fail($"Timed out after {timeoutSeconds}s waiting for compilation to finish.");

                System.Threading.Thread.Sleep(50);
            }

            return MCPResult.Success(new
            {
                waitedSeconds = EditorApplication.timeSinceStartup - start,
                lastCompileUtc = MCPCompilationCache.LastCompileUtc
            });
        }
    }
}
