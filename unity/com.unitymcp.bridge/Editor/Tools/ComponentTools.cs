using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
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

        [MCPTool("list_components", "Lists the full type names of every component attached to a GameObject by path.", readOnly: true)]
        public static MCPResult ListComponents(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var names = go.GetComponents<Component>().Select(c => c.GetType().FullName).ToList();
            return MCPResult.Success(new { components = names });
        }

        [MCPTool("get_component_field", "Reads a public field or property value from a component by path, type name, and member name.", readOnly: true)]
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

            var value = MCPComponentReflection.ReadMember(component, fieldName, out var error);
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
            var error = MCPComponentReflection.WriteMember(component, fieldName, value);
            if (error != null) return MCPResult.Fail(error);

            return MCPResult.Success();
        }

        [MCPTool(
            "get_component_properties",
            "Reads every public field/property on a component (superset of get_component_field, one call instead of " +
            "many). Object-reference values (GameObject/Component/asset fields) are reduced to a path/name rather than " +
            "the whole referenced object, to keep the response bounded and JSON-safe.",
            group: "core", readOnly: true)]
        public static MCPResult GetComponentProperties(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Component type to read, e.g. 'Rigidbody'.")] string typeName)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            var component = go.GetComponent(type);
            if (component == null) return MCPResult.Fail($"GameObject has no component of type '{typeName}'.");

            return MCPResult.Success(new { properties = MCPComponentReflection.GetAllReadableMembers(component) });
        }

        [MCPTool(
            "set_component_properties_batch",
            "Sets multiple fields on one component in a single call -- fieldNames and values are paired by index, " +
            "each value coerced the same way set_component_field does (primitives/enums only). All-or-nothing: every " +
            "field is validated and converted before any of them are actually written, so a bad entry never leaves the " +
            "component half-updated.",
            group: "core")]
        public static MCPResult SetComponentPropertiesBatch(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Component type that owns the fields, e.g. 'Rigidbody'.")] string typeName,
            [MCPParam("Field/property names to set, paired by index with 'values'.")] string[] fieldNames,
            [MCPParam("New values as strings, paired by index with 'fieldNames'.")] string[] values)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            var component = go.GetComponent(type);
            if (component == null) return MCPResult.Fail($"GameObject has no component of type '{typeName}'.");

            if (fieldNames == null || values == null || fieldNames.Length == 0 || fieldNames.Length != values.Length)
                return MCPResult.Fail("fieldNames and values must be non-empty arrays of the same length.");

            var applies = new List<Action>();
            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (!MCPComponentReflection.TryPrepareWrite(component, fieldNames[i], values[i], out var apply, out var err))
                    return MCPResult.Fail($"'{fieldNames[i]}': {err}");
                applies.Add(apply);
            }

            Undo.RecordObject(component, "MCP: Set Component Properties Batch");
            foreach (var apply in applies) apply();

            return MCPResult.Success(new { updated = fieldNames });
        }

        [MCPTool(
            "wire_object_reference",
            "Assigns a scene GameObject/component or a project asset into a component's object-reference field (e.g. " +
            "a public Transform/GameObject/Material field), by path. Exactly one of targetGameObjectPath or " +
            "targetAssetPath must be given; which one is valid depends on the field's actual type.",
            group: "core")]
        public static MCPResult WireObjectReference(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject whose component field will be set.")] string path,
            [MCPParam("Component type that owns the field, e.g. 'MyNamespace.EnemyAI'.")] string typeName,
            [MCPParam("Field/property name to assign, e.g. 'target'.")] string fieldName,
            [MCPParam("Hierarchy path of a scene GameObject to assign. Used if the field is a GameObject or Component type. Omit if using targetAssetPath.")] string targetGameObjectPath = null,
            [MCPParam("Asset path relative to Assets/ to assign (e.g. a Material, Prefab, or ScriptableObject). Omit if using targetGameObjectPath.")] string targetAssetPath = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            var component = go.GetComponent(type);
            if (component == null) return MCPResult.Fail($"GameObject has no component of type '{typeName}'.");

            if (!TryResolveReferenceTarget(component, fieldName, targetGameObjectPath, targetAssetPath, out var value, out var resolveError))
                return MCPResult.Fail(resolveError);

            Undo.RecordObject(component, "MCP: Wire Object Reference");
            var writeError = MCPComponentReflection.WriteObjectReferenceValue(component, fieldName, value);
            if (writeError != null) return MCPResult.Fail(writeError);

            return MCPResult.Success();
        }

        [MCPTool(
            "batch_wire_references",
            "Wires multiple object-reference fields, potentially across different components/GameObjects, in a single " +
            "call. Each entry in 'wireSpecs' is a JSON object string with keys 'path', 'typeName', 'fieldName', and " +
            "exactly one of 'targetGameObjectPath'/'targetAssetPath' -- the same shape as one wire_object_reference " +
            "call. All-or-nothing: every entry is resolved before any of them are actually written.",
            group: "core")]
        public static MCPResult BatchWireReferences(
            MCPToolContext ctx,
            [MCPParam("Array of JSON object strings, each like '{\"path\":\"Enemy\",\"typeName\":\"EnemyAI\",\"fieldName\":\"target\",\"targetGameObjectPath\":\"Player\"}'.")] string[] wireSpecs)
        {
            if (wireSpecs == null || wireSpecs.Length == 0)
                return MCPResult.Fail("wireSpecs must contain at least one entry.");

            var prepared = new List<(Component component, string fieldName, UnityEngine.Object value)>();

            for (int i = 0; i < wireSpecs.Length; i++)
            {
                Newtonsoft.Json.Linq.JObject spec;
                try { spec = Newtonsoft.Json.Linq.JObject.Parse(wireSpecs[i]); }
                catch (Exception e) { return MCPResult.Fail($"wireSpecs[{i}] is not valid JSON: {e.Message}"); }

                var path = (string)spec["path"];
                var typeName = (string)spec["typeName"];
                var fieldName = (string)spec["fieldName"];
                var targetGoPath = (string)spec["targetGameObjectPath"];
                var targetAssetPath = (string)spec["targetAssetPath"];

                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(fieldName))
                    return MCPResult.Fail($"wireSpecs[{i}] must have non-empty 'path', 'typeName', and 'fieldName'.");

                var go = MCPSceneUtil.ResolvePath(path);
                if (go == null) return MCPResult.Fail($"wireSpecs[{i}]: path '{path}' not found.");

                if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError))
                    return MCPResult.Fail($"wireSpecs[{i}]: {typeError}");

                var component = go.GetComponent(type);
                if (component == null) return MCPResult.Fail($"wireSpecs[{i}]: GameObject has no component of type '{typeName}'.");

                if (!TryResolveReferenceTarget(component, fieldName, targetGoPath, targetAssetPath, out var value, out var resolveError))
                    return MCPResult.Fail($"wireSpecs[{i}]: {resolveError}");

                prepared.Add((component, fieldName, value));
            }

            foreach (var (component, fieldName, value) in prepared)
            {
                Undo.RecordObject(component, "MCP: Batch Wire References");
                MCPComponentReflection.WriteObjectReferenceValue(component, fieldName, value);
            }

            return MCPResult.Success(new { wiredCount = prepared.Count });
        }

        [MCPTool(
            "wire_unity_event",
            "Adds a persistent listener to a UnityEvent/UnityEvent<T> field on a component, calling a public method " +
            "on another component by name -- the same thing the Inspector's own '+' button does, reachable by " +
            "path/name instead of clicking. Creates the event instance first if the field is currently null. " +
            "Defaults callState to EditorAndRuntime (not RuntimeOnly) so wiring can be verified in the Editor " +
            "without entering Play Mode. For a UnityEvent<T>, defaults to a dynamic listener forwarding the " +
            "event's real runtime argument to methodName; pass dynamic: false with a *Argument value to bake a " +
            "fixed constant instead. Falls back to a static, parameterless call if methodName has no overload " +
            "matching the event's generic argument.",
            group: "core")]
        public static MCPResult WireUnityEvent(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the UnityEvent field.")] string path,
            [MCPParam("Component type that owns the event field, e.g. 'MCPInteractionRaycaster'.")] string typeName,
            [MCPParam("UnityEvent/UnityEvent<T> field name, e.g. 'onInteractableFound'.")] string eventFieldName,
            [MCPParam("Hierarchy path of the GameObject with the listener method's component.")] string targetPath,
            [MCPParam("Component type on the target GameObject that owns the method.")] string targetTypeName,
            [MCPParam("Public method name to call, e.g. 'ShowPrompt'. Its parameter list must match the event's generic argument (none for a plain UnityEvent).")] string methodName,
            [MCPParam("For a UnityEvent<T> listener: forward the event's real runtime argument (true, default) or bake a fixed constant from the *Argument params below (false).")] bool dynamic = true,
            [MCPParam("Static-mode-only (dynamic: false): the fixed string argument baked into this persistent call. Ignored otherwise.")] string stringArgument = null,
            [MCPParam("Static-mode-only (dynamic: false): the fixed float argument. Ignored otherwise.")] float floatArgument = 0f,
            [MCPParam("Static-mode-only (dynamic: false): the fixed int argument. Ignored otherwise.")] int intArgument = 0,
            [MCPParam("Static-mode-only (dynamic: false): the fixed bool argument. Ignored otherwise.")] bool boolArgument = false,
            [MCPParam("Whether the persistent call fires in Play Mode only (RuntimeOnly) or in the Editor too (EditorAndRuntime). Defaults to EditorAndRuntime.")] UnityEventCallState callState = UnityEventCallState.EditorAndRuntime)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);
            var component = go.GetComponent(type);
            if (component == null) return MCPResult.Fail($"GameObject at '{path}' has no component of type '{typeName}'.");

            var targetGo = MCPSceneUtil.ResolvePath(targetPath);
            if (targetGo == null) return MCPResult.Fail($"Path '{targetPath}' not found.");
            if (!MCPTypeResolver.TryResolve(targetTypeName, out var targetType, out var targetTypeError)) return MCPResult.Fail(targetTypeError);
            var targetComponent = targetGo.GetComponent(targetType);
            if (targetComponent == null) return MCPResult.Fail($"GameObject at '{targetPath}' has no component of type '{targetTypeName}'.");

            const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
            var field = type.GetField(eventFieldName, publicInstance);
            var prop = field == null ? type.GetProperty(eventFieldName, publicInstance) : null;
            if (field == null && prop == null) return MCPResult.Fail($"'{eventFieldName}' is not a public field or property on '{typeName}'.");

            var eventType = field != null ? field.FieldType : prop.PropertyType;
            if (!typeof(UnityEventBase).IsAssignableFrom(eventType))
                return MCPResult.Fail($"'{eventFieldName}' is type '{eventType.Name}', not a UnityEvent.");

            object eventInstance = field != null ? field.GetValue(component) : prop.GetValue(component);
            if (eventInstance == null)
            {
                eventInstance = Activator.CreateInstance(eventType);
                if (field != null) field.SetValue(component, eventInstance);
                else prop.SetValue(component, eventInstance);
            }

            var ueBase = (UnityEventBase)eventInstance;
            var addError = MCPUnityEventWiring.AddListener(ueBase, eventType, targetComponent, targetType, methodName, dynamic, stringArgument, floatArgument, intArgument, boolArgument);
            if (addError != null) return MCPResult.Fail(addError);

            ueBase.SetPersistentListenerState(ueBase.GetPersistentEventCount() - 1, callState);
            EditorUtility.SetDirty(component);

            return MCPResult.Success(new { persistentEventCount = ueBase.GetPersistentEventCount() });
        }

        [MCPTool(
            "copy_component",
            "Copies a component's full field state (via Unity's own Copy Component / Paste Component Values " +
            "mechanism, so private [SerializeField] fields and complex types are handled correctly, not just public " +
            "primitives) from one GameObject onto a component of the same type on another GameObject. Adds the " +
            "component to the target first if it doesn't already have one.",
            group: "core")]
        public static MCPResult CopyComponent(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to copy FROM.")] string sourcePath,
            [MCPParam("Component type to copy, e.g. 'Rigidbody'.")] string typeName,
            [MCPParam("Hierarchy path of the GameObject to copy TO.")] string targetPath)
        {
            var sourceGo = MCPSceneUtil.ResolvePath(sourcePath);
            if (sourceGo == null) return MCPResult.Fail($"Source path '{sourcePath}' not found.");

            var targetGo = MCPSceneUtil.ResolvePath(targetPath);
            if (targetGo == null) return MCPResult.Fail($"Target path '{targetPath}' not found.");

            if (!MCPTypeResolver.TryResolve(typeName, out var type, out var typeError)) return MCPResult.Fail(typeError);

            var sourceComponent = sourceGo.GetComponent(type);
            if (sourceComponent == null) return MCPResult.Fail($"Source GameObject has no component of type '{typeName}'.");

            var targetComponent = targetGo.GetComponent(type);
            if (targetComponent == null)
                targetComponent = Undo.AddComponent(targetGo, type);
            else
                Undo.RecordObject(targetComponent, "MCP: Copy Component");

            UnityEditorInternal.ComponentUtility.CopyComponent(sourceComponent);
            UnityEditorInternal.ComponentUtility.PasteComponentValues(targetComponent);

            return MCPResult.Success();
        }

        [MCPTool(
            "find_missing_components",
            "Scans a loaded scene for GameObjects with missing/broken component scripts (a script asset that was " +
            "deleted or renamed after being attached, leaving a 'Missing (Mono Script)' placeholder in the Inspector).",
            group: "core", readOnly: true)]
        public static MCPResult FindMissingComponents(
            MCPToolContext ctx,
            [MCPParam("Name of the loaded scene to scan. Omit to use the active scene.")] string sceneName = null)
        {
            var scene = string.IsNullOrEmpty(sceneName)
                ? UnityEngine.SceneManagement.SceneManager.GetActiveScene()
                : UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid()) return MCPResult.Fail($"Scene '{sceneName}' is not currently loaded.");

            var results = new List<object>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                    if (missingCount > 0)
                        results.Add(new { path = MCPSceneUtil.GetPath(go), missingScriptCount = missingCount });
                }
            }

            return MCPResult.Success(new { gameObjectsWithMissingScripts = results });
        }

        private static bool TryResolveReferenceTarget(
            Component component, string fieldName, string targetGameObjectPath, string targetAssetPath,
            out UnityEngine.Object value, out string error)
        {
            value = null;
            error = null;

            bool hasGoTarget = !string.IsNullOrEmpty(targetGameObjectPath);
            bool hasAssetTarget = !string.IsNullOrEmpty(targetAssetPath);
            if (hasGoTarget == hasAssetTarget)
            {
                error = "Provide exactly one of targetGameObjectPath or targetAssetPath.";
                return false;
            }

            if (!MCPComponentReflection.TryResolveMemberType(component, fieldName, out var memberType, out var memberError))
            {
                error = memberError;
                return false;
            }

            if (hasGoTarget)
            {
                var targetGo = MCPSceneUtil.ResolvePath(targetGameObjectPath);
                if (targetGo == null)
                {
                    error = $"Target path '{targetGameObjectPath}' not found.";
                    return false;
                }

                if (memberType == typeof(GameObject))
                {
                    value = targetGo;
                    return true;
                }

                if (typeof(Component).IsAssignableFrom(memberType))
                {
                    var targetComponent = targetGo.GetComponent(memberType);
                    if (targetComponent == null)
                    {
                        error = $"'{targetGameObjectPath}' has no component of type {memberType.Name}, needed for field '{fieldName}'.";
                        return false;
                    }
                    value = targetComponent;
                    return true;
                }

                error = $"Field '{fieldName}' is of type {memberType.Name}, which isn't a GameObject or Component -- can't assign a scene object path to it.";
                return false;
            }

            var fullAssetPath = "Assets/" + targetAssetPath.Replace('\\', '/').TrimStart('/');
            value = AssetDatabase.LoadAssetAtPath(fullAssetPath, memberType);
            if (value == null)
            {
                error = $"Could not load an asset of type {memberType.Name} at '{targetAssetPath}'.";
                return false;
            }
            return true;
        }
    }
}
