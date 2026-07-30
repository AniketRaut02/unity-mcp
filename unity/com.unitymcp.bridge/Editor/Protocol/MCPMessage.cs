using System.Collections.Generic;

namespace UnityMCP.Protocol
{
    /// <summary>
    /// Single wire message shape used for every message type (handshake, handshake_ack,
    /// list_tools, tools_list, call, result). Fields not relevant to a given `type` are
    /// simply left null/default — kept deliberately flat rather than polymorphic so
    /// JSON (de)serialization on both the C# and Python side stays trivial.
    /// </summary>
    public class MCPMessage
    {
        public string type;

        // Correlation id, present on every request and echoed back on its response.
        public string id;

        // handshake
        public string token;

        // handshake_ack / result / batch_result
        public bool ok;
        public string error;

        // call
        public string tool;
        public Dictionary<string, object> args;

        // result
        public object result;

        // tools_list
        public List<MCPToolDescriptor> tools;

        // batch (request)
        public List<MCPBatchCallItem> calls;

        // batch_result (response)
        public List<MCPBatchResultItem> results;
    }

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
