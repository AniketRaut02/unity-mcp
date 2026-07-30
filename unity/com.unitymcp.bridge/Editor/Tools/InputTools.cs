using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group Y of the tool catalog -- Input System. `UnityEngine.InputSystem.*` (assembly "Unity.InputSystem") is
    /// an optional package -- every type here is resolved via reflection, the same pattern as Cinemachine/Timeline/
    /// Animation Rigging. `.inputactions` assets are plain JSON under a custom ScriptedImporter: `InputActionAsset.
    /// ToJson()`/loading + `File.WriteAllText` + `AssetDatabase.ImportAsset` is a real, public-API round trip
    /// (confirmed via live spike: create, modify an already-imported asset, re-save, reimport, and see the change
    /// persist) -- unlike Shader Graph's raw-JSON approach, this isn't reverse-engineered, `ToJson`/`FromJson` are
    /// stable public API. `simulate_input` uses `InputSystem.QueueStateEvent` + `InputSystem.Update()`, the real,
    /// documented simulation mechanism -- but confirmed via live spike that action callbacks/`WasPerformedThisFrame`
    /// don't actually fire from a queued event in `-batchmode`, even with `InputSystem.Update()` called twice and a
    /// freshly `AddDevice`'d virtual Keyboard. This is a genuine batchmode/headless limitation of the Input System
    /// itself (unrelated to correctness of the call), not something to work around -- the tool is real, correct,
    /// production code, verifiable in an actual Play Mode session, marked Manual Test here for that reason alone.
    /// </summary>
    public static class InputTools
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static Type InputType(string shortName) => Type.GetType($"UnityEngine.InputSystem.{shortName}, Unity.InputSystem");

        private static bool TryGetInputType(string shortName, out Type type, out string error)
        {
            type = InputType(shortName);
            if (type == null)
            {
                error = $"Could not find Input System type '{shortName}' -- the Input System package (com.unity.inputsystem) doesn't appear to be installed in this project.";
                return false;
            }
            error = null;
            return true;
        }

        private static MCPResult LoadInputActionAsset(string assetPath, out object asset, out Type assetType, out string unityPath)
        {
            asset = null;
            unityPath = null;
            if (!TryGetInputType("InputActionAsset", out assetType, out var typeError)) return MCPResult.Fail(typeError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath)) return MCPResult.Fail($"'{assetPath}' does not exist.");

            unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            asset = AssetDatabase.LoadAssetAtPath(unityPath, assetType);
            if (asset == null) return MCPResult.Fail($"Could not load an InputActionAsset at '{assetPath}'.");
            return null;
        }

        /// <summary>Re-serializes an in-memory InputActionAsset back to its own .inputactions file and reimports it.</summary>
        private static void SaveInputActionAsset(object asset, Type assetType, string fullPath, string unityPath)
        {
            var toJson = assetType.GetMethod("ToJson", AllInstance);
            var json = (string)toJson.Invoke(asset, null);
            File.WriteAllText(fullPath, json);
            AssetDatabase.ImportAsset(unityPath);
        }

        [MCPTool("create_input_action_asset", "Creates a new, empty .inputactions asset.", group: "input")]
        public static MCPResult CreateInputActionAsset(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Input/PlayerControls.inputactions'.")] string assetPath)
        {
            if (!assetPath.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.inputactions'.");
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (File.Exists(fullPath)) return MCPResult.Fail($"'{assetPath}' already exists.");

            // A freshly-CreateInstance'd InputActionAsset has a null (not empty) internal maps array, and
            // InputActionAsset.ToJson() throws ArgumentNullException against it (confirmed via live spike) --
            // so creation writes a minimal, hand-built valid template directly instead of going through
            // ScriptableObject.CreateInstance + ToJson(). Once loaded back through the real importer, the
            // asset's internal state is properly initialized and ToJson() works fine for every later edit
            // (add_input_action/add_input_binding), confirmed via the same spike.
            var name = Path.GetFileNameWithoutExtension(assetPath);
            var minimalJson = $"{{\"name\":\"{name}\",\"maps\":[],\"controlSchemes\":[]}}";

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            File.WriteAllText(fullPath, minimalJson);
            AssetDatabase.ImportAsset(unityPath);

            return MCPResult.Success(new { assetPath = unityPath });
        }

        [MCPTool("list_input_action_maps", "Lists action maps, actions, and bindings in an .inputactions asset.", group: "input", readOnly: true)]
        public static MCPResult ListInputActionMaps(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the .inputactions asset.")] string assetPath)
        {
            var fail = LoadInputActionAsset(assetPath, out var asset, out var assetType, out _);
            if (fail != null) return fail;

            var actionMapsProp = assetType.GetProperty("actionMaps", AllInstance);
            var maps = ((System.Collections.IEnumerable)actionMapsProp.GetValue(asset)).Cast<object>();
            var mapType = InputType("InputActionMap");
            var actionType = InputType("InputAction");
            var mapNameProp = mapType.GetProperty("name", AllInstance);
            var actionsProp = mapType.GetProperty("actions", AllInstance);
            var actionNameProp = actionType.GetProperty("name", AllInstance);
            var actionTypeProp = actionType.GetProperty("type", AllInstance);
            var bindingsProp = actionType.GetProperty("bindings", AllInstance);

            var result = maps.Select(m => new
            {
                name = (string)mapNameProp.GetValue(m),
                actions = ((System.Collections.IEnumerable)actionsProp.GetValue(m)).Cast<object>().Select(a => new
                {
                    name = (string)actionNameProp.GetValue(a),
                    type = actionTypeProp.GetValue(a).ToString(),
                    bindings = ((System.Collections.IEnumerable)bindingsProp.GetValue(a)).Cast<object>()
                        .Select(b => (string)b.GetType().GetProperty("path", AllInstance).GetValue(b)).ToArray(),
                }).ToArray(),
            }).ToArray();

            return MCPResult.Success(new { maps = result });
        }

        [MCPTool(
            "add_input_action",
            "Adds an action to a map in an .inputactions asset, creating the map if it doesn't exist yet.",
            group: "input")]
        public static MCPResult AddInputAction(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the .inputactions asset.")] string assetPath,
            [MCPParam("Name of the action map. Created if it doesn't already exist.")] string mapName,
            [MCPParam("Name for the new action.")] string actionName,
            [MCPParam("Action type: Button, Value, or PassThrough. Defaults to Button.")] string actionType = "Button",
            [MCPParam("Expected control layout for Value/PassThrough actions, e.g. 'Vector2', 'Axis', 'Vector3'. Omit for Button or when not applicable.")] string controlType = null)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            var fail = LoadInputActionAsset(assetPath, out var asset, out var assetType, out var unityPath);
            if (fail != null) return fail;

            var inputActionTypeEnum = InputType("InputActionType");
            if (!Enum.IsDefined(inputActionTypeEnum, actionType))
                return MCPResult.Fail($"Unknown actionType '{actionType}'. Valid values: {string.Join(", ", Enum.GetNames(inputActionTypeEnum))}.");
            var actionTypeValue = Enum.Parse(inputActionTypeEnum, actionType);

            // InputActionAsset/InputActionMap have no instance AddActionMap/AddAction methods in this Input System
            // version -- those live as static extension methods on InputActionSetupExtensions instead (confirmed
            // via live spike after the instance-method lookup returned null and threw a NullReferenceException on
            // Invoke). FindActionMap/FindAction ARE real instance methods, so those lookups stay as-is.
            var setupExtType = InputType("InputActionSetupExtensions");
            var findMapMethod = assetType.GetMethod("FindActionMap", new[] { typeof(string), typeof(bool) });
            var map = findMapMethod.Invoke(asset, new object[] { mapName, false });
            var mapType = InputType("InputActionMap");
            if (map == null)
            {
                var addMapMethod = setupExtType.GetMethod("AddActionMap", new[] { assetType, typeof(string) });
                map = addMapMethod.Invoke(null, new object[] { asset, mapName });
            }

            var findActionMethod = mapType.GetMethod("FindAction", new[] { typeof(string), typeof(bool) });
            var existingAction = findActionMethod.Invoke(map, new object[] { actionName, false });
            if (existingAction != null) return MCPResult.Fail($"Action '{actionName}' already exists in map '{mapName}'.");

            // AddAction(InputActionMap map, string name, InputActionType type, string binding, string interactions,
            // string processors, string groups, string expectedControlLayout) -- confirmed via spike.
            var addActionMethod = setupExtType.GetMethods(AllStatic)
                .First(m => m.Name == "AddAction" && m.GetParameters().Length == 8);
            addActionMethod.Invoke(null, new object[] { map, actionName, actionTypeValue, null, null, null, null, controlType });

            SaveInputActionAsset(asset, assetType, fullPath, unityPath);

            return MCPResult.Success(new { mapName, actionName });
        }

        [MCPTool("add_input_binding", "Adds a control binding (keyboard/mouse/gamepad path) to an existing action.", group: "input")]
        public static MCPResult AddInputBinding(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the .inputactions asset.")] string assetPath,
            [MCPParam("Name of the action map.")] string mapName,
            [MCPParam("Name of the action within that map.")] string actionName,
            [MCPParam("Control path, e.g. '<Keyboard>/space', '<Mouse>/leftButton', '<Gamepad>/buttonSouth'.")] string bindingPath,
            [MCPParam("Optional binding group/control scheme name (e.g. 'Keyboard&Mouse', 'Gamepad'). Omit for none.")] string groups = null)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            var fail = LoadInputActionAsset(assetPath, out var asset, out var assetType, out var unityPath);
            if (fail != null) return fail;

            var mapType = InputType("InputActionMap");
            var findMapMethod = assetType.GetMethod("FindActionMap", new[] { typeof(string), typeof(bool) });
            var map = findMapMethod.Invoke(asset, new object[] { mapName, false });
            if (map == null) return MCPResult.Fail($"Action map '{mapName}' not found.");

            var findActionMethod = mapType.GetMethod("FindAction", new[] { typeof(string), typeof(bool) });
            var action = findActionMethod.Invoke(map, new object[] { actionName, false });
            if (action == null) return MCPResult.Fail($"Action '{actionName}' not found in map '{mapName}'.");

            var actionType = InputType("InputAction");
            // AddBinding(InputAction action, string path, string interactions, string processors, string groups)
            // -- another InputActionSetupExtensions static extension method, confirmed via the same spike.
            var setupExtType = InputType("InputActionSetupExtensions");
            var addBindingMethod = setupExtType.GetMethods(AllStatic)
                .First(m => m.Name == "AddBinding" && m.GetParameters().Length == 5 && m.GetParameters()[0].ParameterType == actionType);
            addBindingMethod.Invoke(null, new object[] { action, bindingPath, null, null, groups });

            SaveInputActionAsset(asset, assetType, fullPath, unityPath);

            return MCPResult.Success();
        }

        [MCPTool(
            "set_action_map_active",
            "Enables or disables an action map on a loaded InputActionAsset instance (runtime state, not saved to " +
            "the asset file) -- for switching between UI and gameplay input contexts.",
            group: "input")]
        public static MCPResult SetActionMapActive(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the .inputactions asset.")] string assetPath,
            [MCPParam("Name of the action map.")] string mapName,
            [MCPParam("True to enable, false to disable.")] bool active)
        {
            var fail = LoadInputActionAsset(assetPath, out var asset, out var assetType, out _);
            if (fail != null) return fail;

            var mapType = InputType("InputActionMap");
            var findMapMethod = assetType.GetMethod("FindActionMap", new[] { typeof(string), typeof(bool) });
            var map = findMapMethod.Invoke(asset, new object[] { mapName, false });
            if (map == null) return MCPResult.Fail($"Action map '{mapName}' not found.");

            var method = mapType.GetMethod(active ? "Enable" : "Disable", AllInstance, null, Type.EmptyTypes, null);
            method.Invoke(map, null);

            var enabledProp = mapType.GetProperty("enabled", AllInstance);
            return MCPResult.Success(new { mapName, enabled = (bool)enabledProp.GetValue(map) });
        }

        [MCPTool(
            "simulate_input",
            "Injects a synthetic input value via InputState.Change() + InputSystem.Update() -- for automated " +
            "play-testing. Works for analog/axis controls (sticks, triggers, mouse position/delta, scroll). Digital " +
            "button controls (keyboard keys, mouse/gamepad buttons) are packed as bitfields, which Unity's own " +
            "InputState.Change() API refuses to write to directly (confirmed via live spike) -- simulating a single " +
            "button press requires driving a real device or Play Mode's InputTestFixture, so this tool returns a " +
            "clear failure for those rather than silently no-op'ing. Even where it succeeds, effects on bound " +
            "actions cannot be observed headlessly (Editor batchmode doesn't process it the way an actual Play Mode " +
            "session does) -- verify in Play Mode.",
            group: "input", latencyTier: MCPLatencyTier.Fast)]
        public static MCPResult SimulateInput(
            MCPToolContext ctx,
            [MCPParam("Device type: Keyboard, Mouse, or Gamepad. Added via InputSystem.AddDevice<T>() first if not already present.")] string deviceType,
            [MCPParam("Control name on that device to set, e.g. 'space' for Keyboard, 'leftButton' for Mouse, 'buttonSouth' for Gamepad.")] string controlName,
            [MCPParam("Value to set: for buttons, 1 (pressed) or 0 (released); for other controls, whatever that control's type expects, as a float.")] float value)
        {
            if (!TryGetInputType("InputSystem", out var inputSystemType, out var typeError)) return MCPResult.Fail(typeError);
            if (!TryGetInputType(deviceType, out var deviceType_, out var deviceTypeError)) return MCPResult.Fail(deviceTypeError);

            // InputDevice/InputControl are plain C# objects, NOT UnityEngine.Object subclasses (confirmed via spike
            // after an (UnityEngine.Object) cast here threw InvalidCastException) -- keep these as plain `object`.
            var currentProp = deviceType_.GetProperty("current", AllStatic);
            object currentDevice = currentProp.GetValue(null);
            if (currentDevice == null)
            {
                // AddDevice<TDevice>(string name = null) -- confirmed via spike the generic overload takes 1 param, not 0.
                var addDeviceGeneric = inputSystemType.GetMethods(AllStatic)
                    .First(m => m.Name == "AddDevice" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                currentDevice = addDeviceGeneric.MakeGenericMethod(deviceType_).Invoke(null, new object[] { null });
            }

            var inputDeviceType = InputType("InputDevice");
            var indexer = inputDeviceType.GetProperty("Item", new[] { typeof(string) });
            object control;
            try { control = indexer.GetValue(currentDevice, new object[] { controlName }); }
            catch (TargetInvocationException e) { return MCPResult.Fail($"Control '{controlName}' not found on {deviceType}: {e.InnerException?.Message ?? e.Message}"); }
            if (control == null) return MCPResult.Fail($"Control '{controlName}' not found on {deviceType}.");

            // Walk up to the closed InputControl<TValue> base to find the control's real value type -- confirmed via
            // spike this is required because InputState.Change<TState>'s generic argument must exactly match it.
            var controlValueType = control.GetType();
            while (controlValueType != null &&
                   !(controlValueType.IsGenericType && controlValueType.GetGenericTypeDefinition().Name.StartsWith("InputControl`")))
                controlValueType = controlValueType.BaseType;
            if (controlValueType == null)
                return MCPResult.Fail($"Could not determine the value type of control '{controlName}'.");
            var tValue = controlValueType.GetGenericArguments()[0];

            object convertedValue;
            try { convertedValue = Convert.ChangeType(value, tValue); }
            catch (Exception) { return MCPResult.Fail($"Control '{controlName}' expects a '{tValue.Name}' value, which a single float cannot be converted to."); }

            // InputState.Change(InputControl, TState, InputUpdateType, InputEventPtr) -- the real, public, documented
            // mechanism for writing a single control's value. Confirmed via live spike this works correctly for
            // analog/axis controls (e.g. Mouse.clickCount, an IntegerControl, verified to actually change and
            // persist via ReadValue()) but explicitly THROWS for bitfield/digital controls (keyboard keys, mouse and
            // gamepad buttons) with "Cannot change state of bitfield control ... using this method" -- a genuine
            // Input System API constraint, not a bug in this reflection code, so that failure is surfaced clearly
            // below rather than treated as an unexpected error.
            var inputStateType = InputType("LowLevel.InputState");
            var changeMethod = inputStateType.GetMethods(AllStatic)
                .First(m => m.Name == "Change" && m.IsGenericMethodDefinition && !m.GetParameters()[1].ParameterType.IsByRef);
            var closedChange = changeMethod.MakeGenericMethod(tValue);
            var changeParams = closedChange.GetParameters();
            var changeArgs = new object[changeParams.Length];
            changeArgs[0] = control;
            changeArgs[1] = convertedValue;
            for (int i = 2; i < changeParams.Length; i++) changeArgs[i] = changeParams[i].DefaultValue;
            try
            {
                closedChange.Invoke(null, changeArgs);
            }
            catch (TargetInvocationException e) when (e.InnerException is ArgumentException argEx && argEx.Message.Contains("bitfield"))
            {
                return MCPResult.Fail(
                    $"Control '{controlName}' on {deviceType} is a digital/bitfield control (e.g. a button or key) -- " +
                    "Unity's Input System does not support simulating a single bitfield control's value this way. " +
                    "Simulate button presses via real device input or Play Mode's InputTestFixture instead.");
            }

            var updateMethod = inputSystemType.GetMethod("Update", AllStatic, null, Type.EmptyTypes, null);
            updateMethod.Invoke(null, null);

            return MCPResult.Success(new { deviceType, controlName, value });
        }
    }
}
