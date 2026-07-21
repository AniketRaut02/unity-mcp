namespace UnityMCP
{
    /// <summary>
    /// Optional first parameter for [MCPTool] methods. Not part of the tool's exposed
    /// JSON schema — the dispatcher fills it in. Kept minimal in Phase 1; this is the
    /// natural place to add things like "which client/session made this call" once
    /// multi-client scenarios need it.
    /// </summary>
    public class MCPToolContext
    {
        public string RequestId;
    }
}
