using System.Collections.Generic;

namespace UnityMCP.Protocol
{
    // Protocol 2 payload shapes. The JSON-RPC 2.0 envelope around them is built by MCPRpc
    // (MCPConnection.cs): tools/register carries MCPToolDescriptor[], tools/batch carries
    // MCPBatchCallItem[] and answers with MCPBatchResultItem[].

    /// <summary>One sub-call within a batch_execute request.</summary>
    public class MCPBatchCallItem
    {
        public string tool;
        public Dictionary<string, object> args;
    }

    /// <summary>One sub-call's outcome within a batch_execute response, in the same order as the request.</summary>
    public class MCPBatchResultItem
    {
        public bool ok;
        public object result;
        public string error;
    }

    public class MCPToolDescriptor
    {
        public string name;
        public string description;
        public string latency_tier; // "fast" | "slow"
        public string group; // "core" | "scripting" | "physics" | "assets" | "ui" | ...
        public Dictionary<string, object> schema;
        public bool destructive;
        public bool read_only;
    }
}
