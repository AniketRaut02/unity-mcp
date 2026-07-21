using System;

namespace UnityMCP
{
    /// <summary>
    /// Attach to an [MCPTool] method's parameter to give it a human-readable
    /// description in the generated JSON schema. This is what an agent actually reads
    /// when deciding what value to pass — a good description here directly affects
    /// tool-call accuracy, especially for parameters whose purpose isn't obvious from
    /// the name alone (e.g. which axis a float represents, what units it's in, what a
    /// null/omitted value means).
    ///
    /// Optional: a parameter with no [MCPParam] still gets a schema entry (just
    /// {"type": ...}, no "description") exactly as before this attribute existed —
    /// nothing about adding new tools requires using it, it's purely additive polish.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class MCPParamAttribute : Attribute
    {
        public string Description { get; }

        public MCPParamAttribute(string description)
        {
            Description = description;
        }
    }
}
