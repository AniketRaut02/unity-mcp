using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group W of the tool catalog -- Gameplay Systems &amp; Data. create_scriptable_object (#242) already exists in
    /// the `assets` group from an earlier batch; not duplicated here. set_scriptable_object_values reuses
    /// MCPComponentReflection's field/property reflection (already generic over any `object`, not GameObject-specific)
    /// against a loaded asset instead of a scene component. wire_event_listener is wire_unity_event's sibling for
    /// SO-based event channels: the listener source is a project asset instead of a scene GameObject, so it can't
    /// share ComponentTools.cs's MCPSceneUtil.ResolvePath-based lookup. save_game_state/load_game_state are a plain,
    /// generic JSON-blob round trip against Application.persistentDataPath -- deliberately NOT tied to any specific
    /// scaffolded save-data shape, so they work as a standalone verification-loop primitive independent of
    /// create_save_system's scaffolded MCPSaveData/MCPSaveSystem scripts.
    /// </summary>
    public static class GameplayTools
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Regex ValidSlotName = new Regex("^[A-Za-z0-9_-]+$");

        [MCPTool(
            "set_scriptable_object_values",
            "Sets multiple fields on a ScriptableObject asset in one call -- the SO-asset equivalent of " +
            "set_component_properties_batch. All-or-nothing: every field is validated and converted before any of " +
            "them are actually written.",
            group: "gameplay")]
        public static MCPResult SetScriptableObjectValues(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the ScriptableObject asset.")] string assetPath,
            [MCPParam("Field/property names to set, paired by index with 'values'.")] string[] fieldNames,
            [MCPParam("New values as strings, paired by index with 'fieldNames'.")] string[] values)
        {
            if (fieldNames == null || values == null || fieldNames.Length == 0 || fieldNames.Length != values.Length)
                return MCPResult.Fail("fieldNames and values must be non-empty arrays of the same length.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath)) return MCPResult.Fail($"'{assetPath}' does not exist.");

            var unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(unityPath);
            if (so == null) return MCPResult.Fail($"Could not load a ScriptableObject at '{assetPath}'.");

            var applies = new List<Action>();
            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (!MCPComponentReflection.TryPrepareWrite(so, fieldNames[i], values[i], out var apply, out var error))
                    return MCPResult.Fail($"Field '{fieldNames[i]}': {error}");
                applies.Add(apply);
            }

            Undo.RecordObject(so, "MCP: Set ScriptableObject Values");
            foreach (var apply in applies) apply();

            EditorUtility.SetDirty(so);
            AssetDatabase.SaveAssets();
            return MCPResult.Success(new { updated = fieldNames });
        }

        [MCPTool(
            "wire_event_listener",
            "Subscribes a component's method to an SO-based event channel asset's UnityEvent -- wire_unity_event's " +
            "sibling for when the event lives on a project asset instead of a scene GameObject. Same real gotchas " +
            "apply and are handled the same way: a null event field is auto-instantiated, the new listener's call " +
            "state defaults to EditorAndRuntime so it can be verified by raising the channel directly in the " +
            "Editor, dynamic:true (the default) forwards the channel's real raised value, and a parameterless " +
            "methodName falls back to a 'static' listener if no overload matches the event's own generic argument.",
            group: "gameplay")]
        public static MCPResult WireEventListener(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the event channel ScriptableObject asset.")] string channelAssetPath,
            [MCPParam("Class name of the event channel asset, e.g. 'MCPVoidEventChannel'.")] string channelTypeName,
            [MCPParam("UnityEvent/UnityEvent<T> field name on the channel. Defaults to 'OnEventRaised'.")] string eventFieldName,
            [MCPParam("Hierarchy path of the GameObject with the listener method's component.")] string targetPath,
            [MCPParam("Component type on the target GameObject that owns the method.")] string targetTypeName,
            [MCPParam("Public method name to call. Its parameter list must match the event's generic argument (none for a plain UnityEvent).")] string methodName,
            [MCPParam("For a UnityEvent<T> listener: forward the channel's real raised argument (true, default) or bake a fixed constant from the *Argument params below (false).")] bool dynamic = true,
            [MCPParam("Static-mode-only (dynamic: false): the fixed string argument baked into this persistent call. Ignored otherwise.")] string stringArgument = null,
            [MCPParam("Static-mode-only (dynamic: false): the fixed float argument. Ignored otherwise.")] float floatArgument = 0f,
            [MCPParam("Static-mode-only (dynamic: false): the fixed int argument. Ignored otherwise.")] int intArgument = 0,
            [MCPParam("Static-mode-only (dynamic: false): the fixed bool argument. Ignored otherwise.")] bool boolArgument = false,
            [MCPParam("Whether the persistent call fires in Play Mode only (RuntimeOnly) or in the Editor too (EditorAndRuntime). Defaults to EditorAndRuntime.")] UnityEventCallState callState = UnityEventCallState.EditorAndRuntime)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, channelAssetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath)) return MCPResult.Fail($"'{channelAssetPath}' does not exist.");
            if (!MCPTypeResolver.TryResolve(channelTypeName, out var channelType, out var channelTypeError)) return MCPResult.Fail(channelTypeError);

            var unityPath = "Assets/" + channelAssetPath.Replace('\\', '/').TrimStart('/');
            var channel = AssetDatabase.LoadAssetAtPath(unityPath, channelType);
            if (channel == null) return MCPResult.Fail($"Could not load a '{channelTypeName}' at '{channelAssetPath}'.");

            var targetGo = MCPSceneUtil.ResolvePath(targetPath);
            if (targetGo == null) return MCPResult.Fail($"Path '{targetPath}' not found.");
            if (!MCPTypeResolver.TryResolve(targetTypeName, out var targetType, out var targetTypeError)) return MCPResult.Fail(targetTypeError);
            var targetComponent = targetGo.GetComponent(targetType);
            if (targetComponent == null) return MCPResult.Fail($"GameObject at '{targetPath}' has no component of type '{targetTypeName}'.");

            string fieldName = string.IsNullOrEmpty(eventFieldName) ? "OnEventRaised" : eventFieldName;
            var field = channelType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            var prop = field == null ? channelType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance) : null;
            if (field == null && prop == null) return MCPResult.Fail($"'{fieldName}' is not a public field or property on '{channelTypeName}'.");

            var eventType = field != null ? field.FieldType : prop.PropertyType;
            if (!typeof(UnityEventBase).IsAssignableFrom(eventType))
                return MCPResult.Fail($"'{fieldName}' is type '{eventType.Name}', not a UnityEvent.");

            object eventInstance = field != null ? field.GetValue(channel) : prop.GetValue(channel);
            if (eventInstance == null)
            {
                eventInstance = Activator.CreateInstance(eventType);
                if (field != null) field.SetValue(channel, eventInstance);
                else prop.SetValue(channel, eventInstance);
            }

            var ueBase = (UnityEventBase)eventInstance;
            var addError = MCPUnityEventWiring.AddListener(ueBase, eventType, targetComponent, targetType, methodName, dynamic, stringArgument, floatArgument, intArgument, boolArgument);
            if (addError != null) return MCPResult.Fail(addError);

            ueBase.SetPersistentListenerState(ueBase.GetPersistentEventCount() - 1, callState);
            EditorUtility.SetDirty(channel);
            AssetDatabase.SaveAssets();

            return MCPResult.Success(new { persistentEventCount = ueBase.GetPersistentEventCount() });
        }

        private static string ResolveSlotPath(string slot)
        {
            if (string.IsNullOrEmpty(slot) || !ValidSlotName.IsMatch(slot)) return null;
            var dir = Path.Combine(Application.persistentDataPath, "MCPSaves");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, slot + ".json");
        }

        [MCPTool(
            "save_game_state",
            "Writes a JSON string to a named save slot under Application.persistentDataPath -- a generic, real " +
            "persistence primitive for a save/load round-trip verification loop, independent of any specific " +
            "scaffolded save-data shape (see create_save_system for a scene-state-aware scaffold built on top of " +
            "this).",
            group: "gameplay", latencyTier: MCPLatencyTier.Fast)]
        public static MCPResult SaveGameState(
            MCPToolContext ctx,
            [MCPParam("Save slot name -- letters, digits, underscore, hyphen only, e.g. 'slot1' or 'autosave'.")] string slot,
            [MCPParam("JSON (or any string) content to write.")] string dataJson)
        {
            var path = ResolveSlotPath(slot);
            if (path == null) return MCPResult.Fail("slot must be non-empty and contain only letters, digits, underscore, or hyphen.");

            File.WriteAllText(path, dataJson);
            return MCPResult.Success(new { slot, path, bytesWritten = System.Text.Encoding.UTF8.GetByteCount(dataJson) });
        }

        [MCPTool("load_game_state", "Reads back the JSON string previously written to a save slot via save_game_state.", group: "gameplay", latencyTier: MCPLatencyTier.Fast)]
        public static MCPResult LoadGameState(
            MCPToolContext ctx,
            [MCPParam("Save slot name to load.")] string slot)
        {
            var path = ResolveSlotPath(slot);
            if (path == null) return MCPResult.Fail("slot must be non-empty and contain only letters, digits, underscore, or hyphen.");
            if (!File.Exists(path)) return MCPResult.Fail($"No save slot '{slot}' found.");

            var dataJson = File.ReadAllText(path);
            return MCPResult.Success(new { slot, dataJson });
        }
    }
}
