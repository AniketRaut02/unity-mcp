using System.Reflection;
using UnityMCP;
using UnityMCP.Support;

namespace UnityMCP.Tools
{
    public static class AssertionTools
    {
        [MCPTool("assert_scene_state", "Asserts a condition about the scene: that a GameObject exists at a hierarchy path, optionally that it has a given component type, and optionally that a specific field/property on that component equals an expected value (compared as strings). Returns a clear pass/fail rather than throwing.", group: "testing")]
        public static MCPResult AssertSceneState(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to check, e.g. \"Player/Camera\".")] string path,
            [MCPParam("Optional component type name that must be present on the GameObject, e.g. \"Rigidbody\" or \"MyNamespace.MyComponent\".")] string componentType = null,
            [MCPParam("Optional field or property name on that component to check.")] string memberName = null,
            [MCPParam("Optional expected value for memberName, compared via ToString(). Required if memberName is set.")] string expectedValue = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null)
                return MCPResult.Success(new { passed = false, reason = $"No GameObject found at path '{path}'." });

            if (string.IsNullOrEmpty(componentType))
                return MCPResult.Success(new { passed = true, reason = "GameObject exists." });

            var type = MCPTypeUtil.ResolveType(componentType);
            if (type == null)
                return MCPResult.Success(new { passed = false, reason = $"Could not resolve component type '{componentType}'." });

            var component = go.GetComponent(type);
            if (component == null)
                return MCPResult.Success(new { passed = false, reason = $"GameObject '{path}' has no component of type '{componentType}'." });

            if (string.IsNullOrEmpty(memberName))
                return MCPResult.Success(new { passed = true, reason = "GameObject and component both present." });

            if (!TryReadMember(component, memberName, out var actualValue, out var readError))
                return MCPResult.Success(new { passed = false, reason = readError });

            var actualString = actualValue?.ToString() ?? "null";
            var matched = actualString == expectedValue;

            return MCPResult.Success(new
            {
                passed = matched,
                reason = matched
                    ? $"{memberName} == '{expectedValue}' as expected."
                    : $"{memberName} was '{actualString}', expected '{expectedValue}'.",
                actualValue = actualString
            });
        }

        private static bool TryReadMember(object target, string memberName, out object value, out string error)
        {
            var type = target.GetType();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                value = field.GetValue(target);
                error = null;
                return true;
            }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                value = prop.GetValue(target);
                error = null;
                return true;
            }

            value = null;
            error = $"No field or property named '{memberName}' found on {type.Name}.";
            return false;
        }
    }
}
