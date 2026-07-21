using System.Collections.Generic;

namespace UnityMCP.ToolBuilder
{
    public enum MCPCompositeParamType
    {
        String,
        Int,
        Float,
        Bool
    }

    /// <summary>One input parameter the generated composite tool will accept.</summary>
    public class MCPCompositeParam
    {
        public string Name;
        public MCPCompositeParamType Type = MCPCompositeParamType.String;
        public string Description = "";
        public bool Required = true;
    }

    /// <summary>
    /// One argument passed to a step's underlying atomic tool call. ValueTemplate is raw
    /// text as entered in the builder UI and is resolved by MCPCompositeToolGenerator into
    /// one of three things:
    ///   - "{paramName}"      -> a reference to one of the composite tool's own parameters
    ///   - "{stepN.field}"    -> a reference to a field of an earlier step's result
    ///   - anything else      -> a literal value, type-coerced against the target tool's
    ///                           own schema (so "2" becomes a Python int if the target
    ///                           parameter is typed as int, a quoted string if it's typed
    ///                           as string, etc.)
    /// </summary>
    public class MCPCompositeStepArg
    {
        public string ArgName;
        public string ValueTemplate;
    }

    /// <summary>One step in the chain: call an existing atomic tool with these arguments.</summary>
    public class MCPCompositeStep
    {
        public string ToolName;
        public List<MCPCompositeStepArg> Args = new List<MCPCompositeStepArg>();
    }

    /// <summary>
    /// The full definition of a new composite tool, as authored in the visual builder.
    /// This is pure data — MCPCompositeToolGenerator turns it into Python source,
    /// MCPToolBuilderWindow is the UI that fills one in. Neither of those two things needs
    /// to know anything about the other's internals, which is what keeps the generator
    /// fully unit-testable without any GUI involved.
    /// </summary>
    public class MCPCompositeToolSpec
    {
        public string Name = "";
        public string Description = "";
        public string Group = "core";
        public List<MCPCompositeParam> Parameters = new List<MCPCompositeParam>();
        public List<MCPCompositeStep> Steps = new List<MCPCompositeStep>();
    }
}
