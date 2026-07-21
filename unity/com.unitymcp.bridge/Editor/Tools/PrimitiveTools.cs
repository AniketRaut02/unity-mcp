using UnityEditor;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    public static class PrimitiveTools
    {
        [MCPTool(
            "create_primitive", 
            "Creates a standard 3D primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad) directly in the scene.", 
            latencyTier: MCPLatencyTier.Fast, 
            destructive: false, 
            group: "core"
        )]
        public static MCPResult CreatePrimitive(
            MCPToolContext ctx,
            [MCPParam("The type of 3D primitive to create. Provides a dropdown of exact valid values.")] PrimitiveType type,
            [MCPParam("World-space X position. Omit to leave at origin (0).")] float? x = null,
            [MCPParam("World-space Y position. Omit to leave at origin (0).")] float? y = null,
            [MCPParam("World-space Z position. Omit to leave at origin (0).")] float? z = null)
        {
            // Create the requested 3D primitive
            GameObject go = GameObject.CreatePrimitive(type);

            // Apply optional position overrides using the nullable parameter pattern
            Vector3 currentPos = go.transform.position;
            if (x.HasValue) currentPos.x = x.Value;
            if (y.HasValue) currentPos.y = y.Value;
            if (z.HasValue) currentPos.z = z.Value;
            go.transform.position = currentPos;

            // Register the undo action so the user can revert the creation in the Editor
            Undo.RegisterCreatedObjectUndo(go, $"MCP: Create {type}");

            // Return success with the hierarchy path to the newly created object
            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }
    }
}