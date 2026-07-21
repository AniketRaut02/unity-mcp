using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    public static class ComponentTools
    {
        [MCPTool("add_component", "Adds a component to a GameObject by path. typeName may be a short name (e.g. 'Rigidbody') or a full type name.")]
        public static MCPResult AddComponent(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Component type to add, e.g. 'Rigidbody', 'BoxCollider', or a full type name for custom scripts.")] string typeName)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            Undo.AddComponent(go, type);
            return MCPResult.Success();
        }

        [MCPTool("remove_component", "Removes a component from a GameObject by path and type name.")]
        public static MCPResult RemoveComponent(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Component type to remove, e.g. 'Rigidbody'.")] string typeName)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            var component = go.GetComponent(type);
            if (component == null) return MCPResult.Fail($"GameObject has no component of type '{typeName}'.");

            Undo.DestroyObjectImmediate(component);
            return MCPResult.Success();
        }

        [MCPTool("list_components", "Lists the full type names of every component attached to a GameObject by path.")]
        public static MCPResult ListComponents(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var names = go.GetComponents<Component>().Select(c => c.GetType().FullName).ToList();
            return MCPResult.Success(new { components = names });
        }

        [MCPTool("get_component_field", "Reads a public field or property value from a component by path, type name, and member name.")]
        public static MCPResult GetComponentField(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Component type that owns the field/property, e.g. 'Rigidbody'.")] string typeName,
            [MCPParam("Public field or property name to read, e.g. 'mass'.")] string fieldName)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            var component = go.GetComponent(type);
            if (component == null) return MCPResult.Fail($"GameObject has no component of type '{typeName}'.");

            var value = ReadMember(component, fieldName, out var error);
            if (error != null) return MCPResult.Fail(error);

            return MCPResult.Success(new { value = value?.ToString() });
        }

        [MCPTool(
            "set_component_field",
            "Sets a public field or property on a component by path, type name, and member name. " +
            "value is a string and is coerced to the member's actual type (int/float/bool/string/enum in Phase 1).")]
        public static MCPResult SetComponentField(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Component type that owns the field/property, e.g. 'Rigidbody'.")] string typeName,
            [MCPParam("Public field or property name to set, e.g. 'mass'.")] string fieldName,
            [MCPParam("New value as a string, e.g. '5' or 'true' — coerced to the member's real type (int/float/bool/string/enum).")] string value)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            var component = go.GetComponent(type);
            if (component == null) return MCPResult.Fail($"GameObject has no component of type '{typeName}'.");

            Undo.RecordObject(component, "MCP: Set Component Field");
            var error = WriteMember(component, fieldName, value);
            if (error != null) return MCPResult.Fail(error);

            return MCPResult.Success();
        }

        private static object ReadMember(object target, string memberName, out string error)
        {
            error = null;
            var type = target.GetType();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(target);

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead) return prop.GetValue(target);

            error = $"No public readable field or property named '{memberName}' on {type.Name}.";
            return null;
        }

        private static string WriteMember(object target, string memberName, string rawValue)
        {
            var type = target.GetType();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(target, ConvertTo(rawValue, field.FieldType));
                return null;
            }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(target, ConvertTo(rawValue, prop.PropertyType));
                return null;
            }

            return $"No public writable field or property named '{memberName}' on {type.Name}.";
        }

        private static object ConvertTo(string raw, Type targetType)
        {
            if (targetType == typeof(string)) return raw;
            if (targetType == typeof(int)) return int.Parse(raw);
            if (targetType == typeof(float)) return float.Parse(raw);
            if (targetType == typeof(double)) return double.Parse(raw);
            if (targetType == typeof(bool)) return bool.Parse(raw);
            if (targetType.IsEnum) return Enum.Parse(targetType, raw);

            throw new NotSupportedException(
                $"Unsupported field type '{targetType.Name}' for set_component_field in Phase 1 (primitives and enums only — " +
                "Vector3/Color/asset-reference support lands with the Assets/Physics/UI tool modules).");
        }
    }
}
