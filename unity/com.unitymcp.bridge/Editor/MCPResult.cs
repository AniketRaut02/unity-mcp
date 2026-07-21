namespace UnityMCP
{
    /// <summary>
    /// Standard return type for [MCPTool] methods. Tools may also just return a plain
    /// object (e.g. an anonymous type) instead of wrapping it — MCPToolRegistry.Invoke
    /// treats a non-MCPResult return value as an implicit success with that value as data.
    /// Use MCPResult.Fail explicitly whenever a tool needs to report a handled error
    /// (missing target, invalid path, etc.) rather than throwing.
    /// </summary>
    public class MCPResult
    {
        public bool Ok;
        public object Data;
        public string Error;

        public static MCPResult Success(object data = null) => new MCPResult { Ok = true, Data = data };
        public static MCPResult Fail(string error) => new MCPResult { Ok = false, Error = error };
    }
}
