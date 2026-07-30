using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Shared public-field/property reflection used by every ComponentTools.cs method that
    /// reads or writes a component member by name (get/set_component_field, and the newer
    /// get_component_properties / set_component_properties_batch / wire_object_reference /
    /// batch_wire_references). Extracted here once a second and third caller needed the
    /// exact same lookup-and-coerce logic ComponentTools originally had privately to itself,
    /// rather than maintaining drifting copies.
    /// </summary>
    internal static class MCPComponentReflection
    {
        public static object ReadMember(object target, string memberName, out string error)
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

        public static string WriteMember(object target, string memberName, string rawValue)
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

        public static object ConvertTo(string raw, Type targetType)
        {
            if (targetType == typeof(string)) return raw;
            if (targetType == typeof(int)) return int.Parse(raw);
            if (targetType == typeof(float)) return float.Parse(raw);
            if (targetType == typeof(double)) return double.Parse(raw);
            if (targetType == typeof(bool)) return bool.Parse(raw);
            if (targetType.IsEnum) return Enum.Parse(targetType, raw);

            throw new NotSupportedException(
                $"Unsupported field type '{targetType.Name}' for a string-coerced value (primitives and enums only — " +
                "use wire_object_reference for GameObject/Component/asset-reference fields).");
        }

        /// <summary>
        /// Resolves the field/property once, converts `rawValue` once, and hands back a
        /// closure that performs the actual write -- lets a caller (set_component_properties_batch)
        /// validate every entry in a batch BEFORE mutating anything, so a failure partway
        /// through a batch can never leave a component half-updated.
        /// </summary>
        public static bool TryPrepareWrite(object target, string memberName, string rawValue, out Action apply, out string error)
        {
            apply = null;
            error = null;
            var type = target.GetType();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                object converted;
                try { converted = ConvertTo(rawValue, field.FieldType); }
                catch (Exception e) { error = $"Could not convert value '{rawValue}' for field '{memberName}': {e.Message}"; return false; }
                apply = () => field.SetValue(target, converted);
                return true;
            }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                object converted;
                try { converted = ConvertTo(rawValue, prop.PropertyType); }
                catch (Exception e) { error = $"Could not convert value '{rawValue}' for property '{memberName}': {e.Message}"; return false; }
                apply = () => prop.SetValue(target, converted);
                return true;
            }

            error = $"No public writable field or property named '{memberName}' on {type.Name}.";
            return false;
        }

        /// <summary>Every public readable field/property on a component, with object-reference values reduced to a path/name rather than serialized whole (which would recurse through GameObject/Transform/scene graph cycles).</summary>
        public static Dictionary<string, object> GetAllReadableMembers(Component component)
        {
            var result = new Dictionary<string, object>();
            var type = component.GetType();

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try { result[field.Name] = SafeValue(field.GetValue(component)); }
                catch { /* some fields throw on access in certain component states -- skip rather than fail the whole read */ }
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                try { result[prop.Name] = SafeValue(prop.GetValue(component)); }
                catch { /* ditto */ }
            }

            return result;
        }

        private static object SafeValue(object value)
        {
            if (value == null) return null;

            if (value is GameObject go)
                return new { refKind = "gameObject", path = MCPSceneUtil.GetPath(go) };

            if (value is Component comp)
                return new { refKind = "component", path = MCPSceneUtil.GetPath(comp.gameObject), type = comp.GetType().FullName };

            if (value is UnityEngine.Object obj)
                return new { refKind = "asset", path = AssetDatabase.Contains(obj) ? AssetDatabase.GetAssetPath(obj) : null, name = obj.name };

            return value;
        }

        /// <summary>The declared type of a writable member, needed before resolving a scene path or asset path into a compatible value for it.</summary>
        public static bool TryResolveMemberType(object target, string memberName, out Type memberType, out string error)
        {
            error = null;
            memberType = null;
            var type = target.GetType();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) { memberType = field.FieldType; return true; }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) { memberType = prop.PropertyType; return true; }

            error = $"No public writable field or property named '{memberName}' on {type.Name}.";
            return false;
        }

        /// <summary>Assigns an already-resolved UnityEngine.Object (or null) into a field/property, refusing an incompatible type rather than letting SetValue throw an opaque reflection exception.</summary>
        public static string WriteObjectReferenceValue(object target, string memberName, UnityEngine.Object value)
        {
            var type = target.GetType();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                if (value != null && !field.FieldType.IsInstanceOfType(value))
                    return $"Field '{memberName}' is of type {field.FieldType.Name}, not compatible with the resolved value's type {value.GetType().Name}.";
                field.SetValue(target, value);
                return null;
            }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                if (value != null && !prop.PropertyType.IsInstanceOfType(value))
                    return $"Property '{memberName}' is of type {prop.PropertyType.Name}, not compatible with the resolved value's type {value.GetType().Name}.";
                prop.SetValue(target, value);
                return null;
            }

            return $"No public writable field or property named '{memberName}' on {type.Name}.";
        }
    }
}
