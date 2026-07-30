using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group K of the tool catalog -- Cameras &amp; Cinemachine. The Cinemachine-dependent tools use reflection
    /// against "Cinemachine.*, Cinemachine" type names rather than a compile-time package reference, the same
    /// pattern MaterialTools.cs uses for the optional Shader Graph package -- this assembly must still compile
    /// and the plain-Camera tools must still work in a project that never installed com.unity.cinemachine.
    /// </summary>
    public static class CameraTools
    {
        [MCPTool("create_camera", "Creates a new GameObject with a plain Camera component.", group: "cameras")]
        public static MCPResult CreateCamera(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'Camera'.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new camera under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("World-space X position. Omit to leave at origin (0).")] float? x = null,
            [MCPParam("World-space Y position. Omit to leave at origin (0).")] float? y = null,
            [MCPParam("World-space Z position. Omit to leave at origin (0).")] float? z = null,
            [MCPParam("Vertical field of view in degrees, perspective only. Defaults to 60.")] float fieldOfView = 60f,
            [MCPParam("Near clip plane distance. Defaults to 0.3.")] float nearClipPlane = 0.3f,
            [MCPParam("Far clip plane distance. Defaults to 1000.")] float farClipPlane = 1000f,
            [MCPParam("Orthographic instead of perspective. Defaults to false.")] bool orthographic = false,
            [MCPParam("Tag this camera 'MainCamera' (only one should exist per scene). Defaults to false.")] bool tagAsMainCamera = false)
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? "Camera" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Camera");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = MCPSceneUtil.ResolvePath(parentPath);
                if (parent == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return MCPResult.Fail($"Parent path '{parentPath}' not found.");
                }
                go.transform.SetParent(parent.transform, false);
            }

            var pos = go.transform.position;
            if (x.HasValue) pos.x = x.Value;
            if (y.HasValue) pos.y = y.Value;
            if (z.HasValue) pos.z = z.Value;
            go.transform.position = pos;

            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = nearClipPlane;
            camera.farClipPlane = farClipPlane;
            camera.orthographic = orthographic;

            if (tagAsMainCamera) go.tag = "MainCamera";

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool("set_camera_properties", "Sets FOV/clip planes/projection/clear behavior/culling mask on an existing Camera. Omitted parameters are left unchanged.", group: "cameras")]
        public static MCPResult SetCameraProperties(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Camera component.")] string path,
            [MCPParam("Vertical field of view in degrees, perspective only. Omit to leave unchanged.")] float? fieldOfView = null,
            [MCPParam("Near clip plane distance. Omit to leave unchanged.")] float? nearClipPlane = null,
            [MCPParam("Far clip plane distance. Omit to leave unchanged.")] float? farClipPlane = null,
            [MCPParam("Orthographic instead of perspective. Omit to leave unchanged.")] bool? orthographic = null,
            [MCPParam("Half-height of the view volume, orthographic only. Omit to leave unchanged.")] float? orthographicSize = null,
            [MCPParam("What to clear before rendering: Skybox, SolidColor, Depth, or Nothing. Omit to leave unchanged.")] CameraClearFlags? clearFlags = null,
            [MCPParam("Background color red component (0-1), used when clearFlags is SolidColor.")] float? backgroundColorR = null,
            [MCPParam("Background color green component (0-1).")] float? backgroundColorG = null,
            [MCPParam("Background color blue component (0-1).")] float? backgroundColorB = null,
            [MCPParam("Layer names this camera renders. Omit to leave unchanged.")] string[] cullingMaskLayerNames = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var camera = go.GetComponent<Camera>();
            if (camera == null) return MCPResult.Fail($"GameObject at '{path}' has no Camera component.");

            Undo.RecordObject(camera, "MCP: Set Camera Properties");

            if (fieldOfView.HasValue) camera.fieldOfView = fieldOfView.Value;
            if (nearClipPlane.HasValue) camera.nearClipPlane = nearClipPlane.Value;
            if (farClipPlane.HasValue) camera.farClipPlane = farClipPlane.Value;
            if (orthographic.HasValue) camera.orthographic = orthographic.Value;
            if (orthographicSize.HasValue) camera.orthographicSize = orthographicSize.Value;
            if (clearFlags.HasValue) camera.clearFlags = clearFlags.Value;

            if (backgroundColorR.HasValue || backgroundColorG.HasValue || backgroundColorB.HasValue)
            {
                var c = camera.backgroundColor;
                if (backgroundColorR.HasValue) c.r = backgroundColorR.Value;
                if (backgroundColorG.HasValue) c.g = backgroundColorG.Value;
                if (backgroundColorB.HasValue) c.b = backgroundColorB.Value;
                camera.backgroundColor = c;
            }

            if (cullingMaskLayerNames != null)
            {
                int mask = 0;
                foreach (var layerName in cullingMaskLayerNames)
                {
                    int layer = LayerMask.NameToLayer(layerName);
                    if (layer < 0) return MCPResult.Fail($"Layer '{layerName}' does not exist.");
                    mask |= 1 << layer;
                }
                camera.cullingMask = mask;
            }

            return MCPResult.Success();
        }

        [MCPTool(
            "set_camera_stack",
            "Orders overlay cameras to render on top of a base camera, oldest-to-front, via Camera.depth ordering with " +
            "'Depth only' clear flags on the overlays -- a render-pipeline-agnostic technique that works without needing " +
            "URP's dedicated camera-stack API. Use for weapon-viewmodel/HUD render layers.",
            group: "cameras")]
        public static MCPResult SetCameraStack(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the base (bottom) camera. Its own depth/clearFlags are left as-is.")] string basePath,
            [MCPParam("Hierarchy paths of overlay cameras, back-to-front. Each gets clearFlags=Depth and an ascending depth value above the base camera's.")] string[] overlayPaths)
        {
            var baseGo = MCPSceneUtil.ResolvePath(basePath);
            if (baseGo == null) return MCPResult.Fail($"Path '{basePath}' not found.");
            var baseCamera = baseGo.GetComponent<Camera>();
            if (baseCamera == null) return MCPResult.Fail($"GameObject at '{basePath}' has no Camera component.");

            if (overlayPaths == null || overlayPaths.Length == 0)
                return MCPResult.Fail("overlayPaths is required and must contain at least one path.");

            var overlayCameras = new List<Camera>();
            foreach (var overlayPath in overlayPaths)
            {
                var overlayGo = MCPSceneUtil.ResolvePath(overlayPath);
                if (overlayGo == null) return MCPResult.Fail($"Overlay path '{overlayPath}' not found.");
                var overlayCamera = overlayGo.GetComponent<Camera>();
                if (overlayCamera == null) return MCPResult.Fail($"GameObject at '{overlayPath}' has no Camera component.");
                overlayCameras.Add(overlayCamera);
            }

            float depth = baseCamera.depth;
            foreach (var overlayCamera in overlayCameras)
            {
                depth += 1f;
                Undo.RecordObject(overlayCamera, "MCP: Set Camera Stack");
                overlayCamera.clearFlags = CameraClearFlags.Depth;
                overlayCamera.depth = depth;
            }

            return MCPResult.Success(new { basePath, overlayCount = overlayCameras.Count });
        }

        [MCPTool(
            "create_cinemachine_camera",
            "Creates a new GameObject with a CinemachineVirtualCamera. Requires the Cinemachine package " +
            "(com.unity.cinemachine) to be installed; fails clearly if it isn't.",
            group: "cameras")]
        public static MCPResult CreateCinemachineCamera(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'CM vcam'.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new vcam under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("World-space X position. Omit to leave at origin (0).")] float? x = null,
            [MCPParam("World-space Y position. Omit to leave at origin (0).")] float? y = null,
            [MCPParam("World-space Z position. Omit to leave at origin (0).")] float? z = null,
            [MCPParam("Hierarchy path of a GameObject for the vcam to Follow. Omit to leave unset.")] string followPath = null,
            [MCPParam("Hierarchy path of a GameObject for the vcam to LookAt. Omit to leave unset.")] string lookAtPath = null)
        {
            if (!TryGetCinemachineType("CinemachineVirtualCamera", out var vcamType, out var typeError))
                return MCPResult.Fail(typeError);

            var go = new GameObject(string.IsNullOrEmpty(name) ? "CM vcam" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Cinemachine Camera");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = MCPSceneUtil.ResolvePath(parentPath);
                if (parent == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return MCPResult.Fail($"Parent path '{parentPath}' not found.");
                }
                go.transform.SetParent(parent.transform, false);
            }

            var pos = go.transform.position;
            if (x.HasValue) pos.x = x.Value;
            if (y.HasValue) pos.y = y.Value;
            if (z.HasValue) pos.z = z.Value;
            go.transform.position = pos;

            var vcam = go.AddComponent(vcamType);

            if (followPath != null && !TrySetTransformProperty(vcam, "Follow", followPath, out var followError))
                return MCPResult.Fail(followError);
            if (lookAtPath != null && !TrySetTransformProperty(vcam, "LookAt", lookAtPath, out var lookAtError))
                return MCPResult.Fail(lookAtError);

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        private static readonly Dictionary<string, string> BodyTypeMap = new Dictionary<string, string>
        {
            ["Transposer"] = "CinemachineTransposer",
            ["FramingTransposer"] = "CinemachineFramingTransposer",
            ["ThirdPersonFollow"] = "Cinemachine3rdPersonFollow",
            ["HardLockToTarget"] = "CinemachineHardLockToTarget",
            ["TrackedDolly"] = "CinemachineTrackedDolly",
            ["OrbitalTransposer"] = "CinemachineOrbitalTransposer",
        };

        private static readonly Dictionary<string, string> AimTypeMap = new Dictionary<string, string>
        {
            ["Composer"] = "CinemachineComposer",
            ["GroupComposer"] = "CinemachineGroupComposer",
            ["POV"] = "CinemachinePOV",
            ["HardLookAt"] = "CinemachineHardLookAt",
            ["SameAsFollowTarget"] = "CinemachineSameAsFollowTarget",
        };

        [MCPTool(
            "set_cinemachine_body",
            "Configures a CinemachineVirtualCamera's Body stage (how it follows its target) via the official " +
            "AddCinemachineComponent<T>() API -- body/aim components live on a hidden child GameObject Cinemachine " +
            "manages itself, not on the vcam's own GameObject, so this can't be done with a plain add_component call.",
            group: "cameras")]
        public static MCPResult SetCinemachineBody(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the CinemachineVirtualCamera.")] string path,
            [MCPParam("Body algorithm: Transposer, FramingTransposer, ThirdPersonFollow, HardLockToTarget, TrackedDolly, or OrbitalTransposer.")] string bodyType,
            [MCPParam("Hierarchy path of a GameObject for the vcam to Follow. Omit to leave the current Follow target unchanged.")] string followPath = null)
        {
            return ConfigureCinemachinePipelineStage(path, bodyType, BodyTypeMap, "bodyType", followPath, targetPropertyName: "Follow");
        }

        [MCPTool(
            "set_cinemachine_aim",
            "Configures a CinemachineVirtualCamera's Aim stage (how it points at its target) via the official " +
            "AddCinemachineComponent<T>() API -- see set_cinemachine_body for why this can't be a plain add_component call.",
            group: "cameras")]
        public static MCPResult SetCinemachineAim(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the CinemachineVirtualCamera.")] string path,
            [MCPParam("Aim algorithm: Composer, GroupComposer, POV, HardLookAt, or SameAsFollowTarget.")] string aimType,
            [MCPParam("Hierarchy path of a GameObject for the vcam to LookAt. Omit to leave the current LookAt target unchanged.")] string lookAtPath = null)
        {
            return ConfigureCinemachinePipelineStage(path, aimType, AimTypeMap, "aimType", lookAtPath, targetPropertyName: "LookAt");
        }

        private static MCPResult ConfigureCinemachinePipelineStage(
            string path, string requestedType, Dictionary<string, string> typeMap, string paramName,
            string targetPath, string targetPropertyName)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!TryGetCinemachineType("CinemachineVirtualCamera", out var vcamType, out var typeError))
                return MCPResult.Fail(typeError);

            var vcam = go.GetComponent(vcamType);
            if (vcam == null) return MCPResult.Fail($"GameObject at '{path}' has no CinemachineVirtualCamera component.");

            if (!typeMap.TryGetValue(requestedType, out var className))
                return MCPResult.Fail($"Unknown {paramName} '{requestedType}'. Valid values: {string.Join(", ", typeMap.Keys)}.");

            if (!TryGetCinemachineType(className, out var componentType, out var componentTypeError))
                return MCPResult.Fail(componentTypeError);

            var method = vcamType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "AddCinemachineComponent" && m.IsGenericMethodDefinition);
            if (method == null)
                return MCPResult.Fail("Could not find CinemachineVirtualCamera.AddCinemachineComponent<T>() via reflection -- this Cinemachine version's API may have changed.");

            method.MakeGenericMethod(componentType).Invoke(vcam, null);

            if (targetPath != null && !TrySetTransformProperty(vcam, targetPropertyName, targetPath, out var targetError))
                return MCPResult.Fail(targetError);

            return MCPResult.Success(new { path, appliedType = className });
        }

        [MCPTool(
            "trigger_camera_impulse",
            "Fires a one-shot Cinemachine impulse (camera shake) from a GameObject -- for scares/impacts. Adds a " +
            "CinemachineImpulseSource automatically if the target doesn't already have one (see also add_camera_shake, " +
            "which additionally wires up a CinemachineImpulseListener on the vcam/brain so it actually reacts).",
            group: "cameras")]
        public static MCPResult TriggerCameraImpulse(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to generate the impulse from (its position becomes the impulse's source point).")] string path,
            [MCPParam("Impulse force/magnitude. Defaults to 1.")] float force = 1f)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            if (!TryGetCinemachineType("CinemachineImpulseSource", out var impulseSourceType, out var typeError))
                return MCPResult.Fail(typeError);

            var impulseSource = go.GetComponent(impulseSourceType);
            if (impulseSource == null)
                impulseSource = Undo.AddComponent(go, impulseSourceType);

            var method = impulseSourceType.GetMethod("GenerateImpulseWithForce", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null);
            if (method == null)
                return MCPResult.Fail("Could not find CinemachineImpulseSource.GenerateImpulseWithForce(float) via reflection -- this Cinemachine version's API may have changed.");

            method.Invoke(impulseSource, new object[] { force });

            return MCPResult.Success(new { path, force });
        }

        private static bool TrySetTransformProperty(Component component, string propertyName, string targetPath, out string error)
        {
            error = null;
            var target = MCPSceneUtil.ResolvePath(targetPath);
            if (target == null)
            {
                error = $"Target path '{targetPath}' not found.";
                return false;
            }

            var property = component.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                error = $"Could not find a public '{propertyName}' property via reflection -- this Cinemachine version's API may have changed.";
                return false;
            }

            property.SetValue(component, target.transform);
            return true;
        }

        private static bool TryGetCinemachineType(string shortName, out Type type, out string error)
        {
            type = Type.GetType($"Cinemachine.{shortName}, Cinemachine");
            if (type == null)
            {
                error = $"Could not find Cinemachine type '{shortName}' -- the Cinemachine package (com.unity.cinemachine) doesn't appear to be installed in this project.";
                return false;
            }
            error = null;
            return true;
        }
    }
}
