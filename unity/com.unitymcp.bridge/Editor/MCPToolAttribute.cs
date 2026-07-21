using System;

namespace UnityMCP
{
    /// <summary>
    /// Latency classification so the Python-side caller can pick a sane timeout and so
    /// the Editor-side dispatcher can prioritize fast queries ahead of slow mutations.
    /// Slow = anything that can trigger a domain reload (script creation/edit, some
    /// asset database operations) or otherwise block the Editor for a noticeable time.
    /// </summary>
    public enum MCPLatencyTier
    {
        Fast,
        Slow
    }

    /// <summary>
    /// Decorate a public static method with this to expose it as an MCP tool.
    /// The method is discovered by reflection at Editor load (see MCPToolRegistry) —
    /// no other registration step is needed. This is the mechanism that keeps adding
    /// tool #151 a one-line change instead of a wiring exercise.
    ///
    /// Supported parameter types for now (Phase 1/2): string, int, long, float, double,
    /// bool, nullable versions of the numeric/bool types, and enums (as strings).
    /// The first parameter may optionally be an MCPToolContext, which is not part of
    /// the exposed schema and is supplied by the dispatcher.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class MCPToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        public MCPLatencyTier LatencyTier { get; }

        /// <summary>
        /// Marks a tool as destructive (deletes or irreversibly overwrites something).
        /// Enforcement lives entirely in MCPToolRegistry.Invoke, not in the tool method
        /// itself: a destructive tool automatically gets a synthetic required
        /// `confirm: true` argument added to its advertised schema, and the call is
        /// rejected before the method ever runs if that argument isn't explicitly true.
        /// This is deliberate — the guarantee shouldn't depend on every tool author
        /// remembering to check it themselves.
        /// </summary>
        public bool Destructive { get; }

        /// <summary>
        /// Which tool group this belongs to (e.g. "core", "scripting", "physics").
        /// "core" tools are always visible to an MCP client; everything else is hidden
        /// by default and must be activated per-session via the manage_tools workflow
        /// tool (Python side) — fewer visible tools per call means fewer prompt tokens
        /// and better routing accuracy, and it means a client isn't shown e.g. UI tools
        /// in a session that never touches UI. This is purely a visibility/prompt-economy
        /// mechanism, not a security boundary — see the group-filtering docs for why.
        /// </summary>
        public string Group { get; }

        public MCPToolAttribute(
            string name,
            string description,
            MCPLatencyTier latencyTier = MCPLatencyTier.Fast,
            bool destructive = false,
            string group = "core")
        {
            Name = name;
            Description = description;
            LatencyTier = latencyTier;
            Destructive = destructive;
            Group = group;
        }
    }
}
