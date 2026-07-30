using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group Q of the tool catalog -- Animation &amp; Rigging. Every tool except add_ik_constraint uses core Mecanim
    /// APIs (UnityEditor.Animations.*) directly -- no optional package, no reflection needed, unlike most other
    /// GENRE groups. Two real gotchas found via live spike, not guessed: (1) AnimatorController.parameters (and
    /// similarly-shaped array properties) return a fresh deserialized copy on every read -- mutating a
    /// previously-fetched element and reassigning "controller.parameters = controller.parameters" is a silent
    /// no-op; the fetched array's own elements must be mutated and that *same* array instance written back.
    /// (2) Animator.Play()/Update() DOES really change GetCurrentAnimatorStateInfo() outside Play Mode, so
    /// play_animation's state transition is verifiable without an actual Play Mode session (unlike most
    /// Awake()-dependent behavior in earlier batches).
    /// add_ik_constraint uses the optional Animation Rigging package (com.unity.animation.rigging) via reflection,
    /// same pattern as Cinemachine/URP -- confirmed via live spike that TwoBoneIKConstraint/MultiAimConstraint's
    /// data (root/mid/tip/target/hint, sourceObjects, etc.) lives behind a protected "m_Data" FIELD on the generic
    /// RigConstraint&lt;,,&gt; base class, not the public "data" property (a ref-return that throws
    /// NotSupportedException via reflection Invoke) -- and that WeightedTransform.transform is a plain field too,
    /// not a property, the same field-vs-property trap RenderingTools.cs hit with Volume.priority/weight/blendDistance.
    /// </summary>
    public static class AnimationTools
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [MCPTool("create_animator_controller", "Creates a new Animator Controller asset with a default empty state machine.", group: "animation")]
        public static MCPResult CreateAnimatorController(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Animators/Enemy.controller'.")] string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.controller'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            var controller = AnimatorController.CreateAnimatorControllerAtPath(unityPath);
            if (controller == null) return MCPResult.Fail("AnimatorController.CreateAnimatorControllerAtPath returned null.");

            return MCPResult.Success(new { assetPath = unityPath });
        }

        private static MCPResult LoadController(string assetPath, out AnimatorController controller, out string unityPath)
        {
            controller = null;
            unityPath = null;
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' does not exist.");

            unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(unityPath);
            if (controller == null) return MCPResult.Fail($"Could not load an AnimatorController at '{assetPath}'.");
            return null;
        }

        private static AnimatorState FindState(AnimatorStateMachine sm, string name) =>
            sm.states.FirstOrDefault(s => s.state.name == name).state;

        [MCPTool("add_animator_state", "Adds a state to an Animator Controller layer, optionally referencing a clip and/or setting it as the layer's default state.", group: "animation")]
        public static MCPResult AddAnimatorState(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the AnimatorController asset.")] string controllerAssetPath,
            [MCPParam("Name for the new state.")] string stateName,
            [MCPParam("Layer index to add the state to. Defaults to 0 (base layer).")] int layerIndex = 0,
            [MCPParam("Path relative to Assets/ of an AnimationClip to use as the state's motion. Omit to leave unset.")] string clipAssetPath = null,
            [MCPParam("Set this as the layer's default (entry) state. Defaults to false.")] bool setAsDefault = false)
        {
            var fail = LoadController(controllerAssetPath, out var controller, out _);
            if (fail != null) return fail;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) return MCPResult.Fail($"layerIndex {layerIndex} is out of range (controller has {controller.layers.Length} layer(s)).");

            var layer = controller.layers[layerIndex];
            var sm = layer.stateMachine;
            if (FindState(sm, stateName) != null) return MCPResult.Fail($"State '{stateName}' already exists on layer {layerIndex}.");

            var state = sm.AddState(stateName);

            if (clipAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, clipAssetPath, out var clipFullPath, out var clipGuardError))
                    return MCPResult.Fail(clipGuardError);
                if (!File.Exists(clipFullPath)) return MCPResult.Fail($"'{clipAssetPath}' does not exist.");
                var clipUnityPath = "Assets/" + clipAssetPath.Replace('\\', '/').TrimStart('/');
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipUnityPath);
                if (clip == null) return MCPResult.Fail($"Could not load an AnimationClip at '{clipAssetPath}'.");
                state.motion = clip;
            }

            if (setAsDefault) sm.defaultState = state;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return MCPResult.Success();
        }

        [MCPTool(
            "add_animator_transition",
            "Adds a condition-based transition between two states on an Animator Controller layer (fromStateName " +
            "'Any State' adds an any-state transition instead of a normal one).",
            group: "animation")]
        public static MCPResult AddAnimatorTransition(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the AnimatorController asset.")] string controllerAssetPath,
            [MCPParam("Source state name, or 'Any State' for an any-state transition.")] string fromStateName,
            [MCPParam("Destination state name.")] string toStateName,
            [MCPParam("Layer index. Defaults to 0.")] int layerIndex = 0,
            [MCPParam("Whether the transition can only occur after the source state's exit time. Defaults to false.")] bool hasExitTime = false,
            [MCPParam("Normalized exit time (0-1+), only used if hasExitTime is true. Defaults to 0.")] float exitTime = 0f,
            [MCPParam("Transition blend duration in seconds. Defaults to 0.25.")] float duration = 0.25f,
            [MCPParam("Conditions, each as \"parameterName,mode,threshold\", e.g. [\"Speed,Greater,0.1\"]. mode is one of: If, IfNot, Greater, Less, Equals, NotEqual. threshold is ignored for If/IfNot. Omit for an unconditional transition.")] string[] conditions = null)
        {
            var fail = LoadController(controllerAssetPath, out var controller, out _);
            if (fail != null) return fail;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) return MCPResult.Fail($"layerIndex {layerIndex} is out of range (controller has {controller.layers.Length} layer(s)).");

            var sm = controller.layers[layerIndex].stateMachine;
            var toState = FindState(sm, toStateName);
            if (toState == null) return MCPResult.Fail($"Destination state '{toStateName}' not found on layer {layerIndex}.");

            AnimatorStateTransition transition;
            if (fromStateName == "Any State")
            {
                transition = sm.AddAnyStateTransition(toState);
            }
            else
            {
                var fromState = FindState(sm, fromStateName);
                if (fromState == null) return MCPResult.Fail($"Source state '{fromStateName}' not found on layer {layerIndex}.");
                transition = fromState.AddTransition(toState);
            }

            transition.hasExitTime = hasExitTime;
            transition.exitTime = exitTime;
            transition.duration = duration;

            if (conditions != null)
            {
                foreach (var entry in conditions)
                {
                    var parts = entry.Split(',');
                    if (parts.Length < 2) return MCPResult.Fail($"Invalid condition '{entry}' -- expected \"parameterName,mode[,threshold]\".");
                    if (!Enum.TryParse<AnimatorConditionMode>(parts[1], out var mode))
                        return MCPResult.Fail($"Unknown condition mode '{parts[1]}'. Valid values: {string.Join(", ", Enum.GetNames(typeof(AnimatorConditionMode)))}.");
                    float threshold = 0f;
                    if (parts.Length >= 3 && !float.TryParse(parts[2], out threshold))
                        return MCPResult.Fail($"Invalid threshold '{parts[2]}' in condition '{entry}'.");
                    transition.AddCondition(mode, threshold, parts[0]);
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return MCPResult.Success();
        }

        [MCPTool("add_animator_parameter", "Adds a Bool/Float/Int/Trigger parameter to an Animator Controller, optionally with a default value.", group: "animation")]
        public static MCPResult AddAnimatorParameter(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the AnimatorController asset.")] string controllerAssetPath,
            [MCPParam("Parameter name.")] string name,
            [MCPParam("Parameter type: Float, Int, Bool, or Trigger.")] AnimatorControllerParameterType type,
            [MCPParam("Default value. For Float/Int, a numeric string; for Bool, \"true\"/\"false\". Ignored for Trigger. Omit for Unity's own default (0/false).")] string defaultValue = null)
        {
            var fail = LoadController(controllerAssetPath, out var controller, out _);
            if (fail != null) return fail;
            if (controller.parameters.Any(p => p.name == name)) return MCPResult.Fail($"Parameter '{name}' already exists.");

            controller.AddParameter(name, type);

            if (defaultValue != null && type != AnimatorControllerParameterType.Trigger)
            {
                // AnimatorController.parameters returns a fresh copy every read -- mutate the SAME fetched array's
                // element and write that exact array back, confirmed via live spike (reassigning "x.parameters =
                // x.parameters" without holding onto one snapshot is a silent no-op).
                var parameters = controller.parameters;
                var param = parameters.First(p => p.name == name);
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        if (!float.TryParse(defaultValue, out var floatVal)) return MCPResult.Fail($"Invalid float defaultValue '{defaultValue}'.");
                        param.defaultFloat = floatVal;
                        break;
                    case AnimatorControllerParameterType.Int:
                        if (!int.TryParse(defaultValue, out var intVal)) return MCPResult.Fail($"Invalid int defaultValue '{defaultValue}'.");
                        param.defaultInt = intVal;
                        break;
                    case AnimatorControllerParameterType.Bool:
                        if (!bool.TryParse(defaultValue, out var boolVal)) return MCPResult.Fail($"Invalid bool defaultValue '{defaultValue}'.");
                        param.defaultBool = boolVal;
                        break;
                }
                controller.parameters = parameters;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return MCPResult.Success();
        }

        [MCPTool(
            "create_blend_tree",
            "Creates a 1D or 2D BlendTree as a new state on an Animator Controller layer, for locomotion blending.",
            group: "animation")]
        public static MCPResult CreateBlendTree(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the AnimatorController asset.")] string controllerAssetPath,
            [MCPParam("Name for the new state hosting the blend tree.")] string stateName,
            [MCPParam("Layer index. Defaults to 0.")] int layerIndex = 0,
            [MCPParam("Blend tree type: Simple1D, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, or Direct.")] BlendTreeType blendType = BlendTreeType.Simple1D,
            [MCPParam("Parameter driving the X axis (or the only axis, for Simple1D).")] string blendParameter = null,
            [MCPParam("Parameter driving the Y axis. Required for 2D blend types, ignored for Simple1D/Direct.")] string blendParameterY = null,
            [MCPParam("Motions, each as \"clipAssetPath,threshold\" for Simple1D/Direct, or \"clipAssetPath,posX,posY\" for 2D types.")] string[] motions = null)
        {
            var fail = LoadController(controllerAssetPath, out var controller, out _);
            if (fail != null) return fail;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) return MCPResult.Fail($"layerIndex {layerIndex} is out of range (controller has {controller.layers.Length} layer(s)).");

            var sm = controller.layers[layerIndex].stateMachine;
            if (FindState(sm, stateName) != null) return MCPResult.Fail($"State '{stateName}' already exists on layer {layerIndex}.");

            bool is2D = blendType == BlendTreeType.SimpleDirectional2D || blendType == BlendTreeType.FreeformDirectional2D || blendType == BlendTreeType.FreeformCartesian2D;
            if (is2D && string.IsNullOrEmpty(blendParameterY)) return MCPResult.Fail($"blendParameterY is required for blendType '{blendType}'.");

            var blendTree = new BlendTree { name = stateName + "BlendTree" };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            blendTree.blendType = blendType;
            if (blendParameter != null) blendTree.blendParameter = blendParameter;
            if (is2D) blendTree.blendParameterY = blendParameterY;

            if (motions != null)
            {
                foreach (var entry in motions)
                {
                    var parts = entry.Split(',');
                    if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, parts[0], out var clipFullPath, out var clipGuardError))
                        return MCPResult.Fail(clipGuardError);
                    if (!File.Exists(clipFullPath)) return MCPResult.Fail($"'{parts[0]}' does not exist.");
                    var clipUnityPath = "Assets/" + parts[0].Replace('\\', '/').TrimStart('/');
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipUnityPath);
                    if (clip == null) return MCPResult.Fail($"Could not load an AnimationClip at '{parts[0]}'.");

                    if (is2D)
                    {
                        if (parts.Length != 3 || !float.TryParse(parts[1], out var posX) || !float.TryParse(parts[2], out var posY))
                            return MCPResult.Fail($"Invalid 2D motion entry '{entry}' -- expected \"clipAssetPath,posX,posY\".");
                        blendTree.AddChild(clip, new Vector2(posX, posY));
                    }
                    else
                    {
                        if (parts.Length != 2 || !float.TryParse(parts[1], out var threshold))
                            return MCPResult.Fail($"Invalid 1D motion entry '{entry}' -- expected \"clipAssetPath,threshold\".");
                        blendTree.AddChild(clip, threshold);
                    }
                }
            }

            var state = sm.AddState(stateName);
            state.motion = blendTree;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return MCPResult.Success(new { childCount = blendTree.children.Length });
        }

        [MCPTool("assign_animator", "Attaches an Animator to a GameObject with the given controller (and optional avatar/root motion setting).", group: "animation")]
        public static MCPResult AssignAnimator(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Path relative to Assets/ of the AnimatorController asset. Omit to leave unassigned.")] string controllerAssetPath = null,
            [MCPParam("Path relative to Assets/ of an Avatar asset (usually inside an imported model). Omit to leave unassigned.")] string avatarAssetPath = null,
            [MCPParam("Whether the Animator drives the GameObject's position/rotation from the clip's root motion. Omit to leave at Unity's default (true).")] bool? applyRootMotion = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var animator = go.GetComponent<Animator>();
            if (animator == null) animator = go.AddComponent<Animator>();

            if (controllerAssetPath != null)
            {
                var fail = LoadController(controllerAssetPath, out var controller, out _);
                if (fail != null) return fail;
                animator.runtimeAnimatorController = controller;
            }

            if (avatarAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, avatarAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath)) return MCPResult.Fail($"'{avatarAssetPath}' does not exist.");
                var unityPath = "Assets/" + avatarAssetPath.Replace('\\', '/').TrimStart('/');
                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(unityPath);
                if (avatar == null) return MCPResult.Fail($"Could not load an Avatar at '{avatarAssetPath}'.");
                animator.avatar = avatar;
            }

            if (applyRootMotion.HasValue) animator.applyRootMotion = applyRootMotion.Value;

            return MCPResult.Success();
        }

        [MCPTool(
            "play_animation",
            "Plays a state on an existing Animator for verification, then immediately evaluates one frame via " +
            "Animator.Update(0) -- confirmed via live spike to really change GetCurrentAnimatorStateInfo() outside " +
            "Play Mode, unlike most Awake()-dependent runtime behavior elsewhere in this catalog.",
            group: "animation", latencyTier: MCPLatencyTier.Fast)]
        public static MCPResult PlayAnimation(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Animator.")] string path,
            [MCPParam("Name of the state to play.")] string stateName,
            [MCPParam("Layer index. Defaults to 0.")] int layerIndex = 0,
            [MCPParam("Normalized time to start at (0-1). Defaults to 0.")] float normalizedTime = 0f)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) return MCPResult.Fail($"GameObject at '{path}' has no Animator component.");
            if (animator.runtimeAnimatorController == null) return MCPResult.Fail($"Animator at '{path}' has no controller assigned.");

            animator.Play(stateName, layerIndex, normalizedTime);
            animator.Update(0f);

            var info = animator.GetCurrentAnimatorStateInfo(layerIndex);
            if (!info.IsName(stateName))
                return MCPResult.Fail($"Animator.Play('{stateName}') did not result in that state being current -- check the state name and layerIndex.");

            return MCPResult.Success(new { stateName, length = info.length, normalizedTime = info.normalizedTime, loop = info.loop });
        }

        [MCPTool("list_animation_clips", "Lists AnimationClip assets under a folder along with each clip's AnimationEvents.", group: "animation", readOnly: true)]
        public static MCPResult ListAnimationClips(
            MCPToolContext ctx,
            [MCPParam("Folder path relative to Assets/ to search. Defaults to the whole Assets/ folder.")] string searchFolder = "")
        {
            var searchIn = string.IsNullOrEmpty(searchFolder) ? "Assets" : "Assets/" + searchFolder.Replace('\\', '/').TrimStart('/');
            var guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { searchIn });

            var results = guids.Select(guid =>
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                var events = AnimationUtility.GetAnimationEvents(clip).Select(e => new
                {
                    time = e.time,
                    functionName = e.functionName,
                    stringParameter = e.stringParameter,
                    floatParameter = e.floatParameter,
                    intParameter = e.intParameter
                }).ToArray();
                return new { assetPath, length = clip.length, frameRate = clip.frameRate, isLooping = clip.isLooping, events };
            }).ToArray();

            return MCPResult.Success(new { clips = results });
        }

        [MCPTool("add_animation_event", "Adds an event (calling a method by name at a given time) to an existing AnimationClip -- for footstep/hit frames.", group: "animation")]
        public static MCPResult AddAnimationEvent(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the AnimationClip asset.")] string clipAssetPath,
            [MCPParam("Time in seconds within the clip to fire the event.")] float time,
            [MCPParam("Name of the method to call on any script on the GameObject the clip is playing on.")] string functionName,
            [MCPParam("String parameter passed to the method. Omit for none.")] string stringParameter = null,
            [MCPParam("Float parameter passed to the method. Omit for 0.")] float floatParameter = 0f,
            [MCPParam("Int parameter passed to the method. Omit for 0.")] int intParameter = 0)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, clipAssetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath)) return MCPResult.Fail($"'{clipAssetPath}' does not exist.");
            var unityPath = "Assets/" + clipAssetPath.Replace('\\', '/').TrimStart('/');
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(unityPath);
            if (clip == null) return MCPResult.Fail($"Could not load an AnimationClip at '{clipAssetPath}'.");

            var events = AnimationUtility.GetAnimationEvents(clip).ToList();
            events.Add(new AnimationEvent
            {
                time = time,
                functionName = functionName,
                stringParameter = stringParameter,
                floatParameter = floatParameter,
                intParameter = intParameter
            });
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return MCPResult.Success(new { eventCount = events.Count });
        }

        [MCPTool(
            "configure_avatar_mask",
            "Creates (if missing) or edits an AvatarMask asset's humanoid body-part toggles -- e.g. mask out both " +
            "legs for an upper-body-only aim layer while the base layer still drives walking.",
            group: "animation")]
        public static MCPResult ConfigureAvatarMask(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the AvatarMask asset.")] string assetPath,
            [MCPParam("Body parts to deactivate (excluded from this mask), e.g. [\"LeftLeg\", \"RightLeg\"]. Valid values: Root, Body, Head, LeftLeg, RightLeg, LeftArm, RightArm, LeftFingers, RightFingers, LeftFootIK, RightFootIK, LeftHandIK, RightHandIK.")] string[] excludeBodyParts = null,
            [MCPParam("If true and assetPath doesn't exist yet, create a new mask there (all body parts active by default). Defaults to true.")] bool createIfMissing = true)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            var unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');

            AvatarMask mask;
            if (File.Exists(fullPath))
            {
                mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(unityPath);
                if (mask == null) return MCPResult.Fail($"Could not load an AvatarMask at '{assetPath}'.");
            }
            else
            {
                if (!createIfMissing) return MCPResult.Fail($"'{assetPath}' does not exist. Pass createIfMissing: true to create it.");
                mask = new AvatarMask();
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                AssetDatabase.CreateAsset(mask, unityPath);
            }

            if (excludeBodyParts != null)
            {
                foreach (var name in excludeBodyParts)
                {
                    if (!Enum.TryParse<AvatarMaskBodyPart>(name, out var part) || part == AvatarMaskBodyPart.LastBodyPart)
                        return MCPResult.Fail($"Unknown body part '{name}'. Valid values: {string.Join(", ", Enum.GetNames(typeof(AvatarMaskBodyPart)).Where(n => n != "LastBodyPart"))}.");
                    mask.SetHumanoidBodyPartActive(part, false);
                }
            }

            EditorUtility.SetDirty(mask);
            AssetDatabase.SaveAssets();
            return MCPResult.Success(new { assetPath = unityPath });
        }

        [MCPTool("set_root_motion", "Enables/disables root motion (position/rotation driven by the clip) on an existing Animator.", group: "animation")]
        public static MCPResult SetRootMotion(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Animator.")] string path,
            [MCPParam("Whether root motion is applied.")] bool enabled)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) return MCPResult.Fail($"GameObject at '{path}' has no Animator component.");

            animator.applyRootMotion = enabled;
            return MCPResult.Success();
        }

        // -----------------------------------------------------------------
        // add_ik_constraint -- the one tool in this group needing the optional
        // Animation Rigging package, via reflection like every other optional
        // package in this codebase.
        // -----------------------------------------------------------------

        private static bool TryGetRiggingType(string shortName, out Type type, out string error)
        {
            type = Type.GetType($"UnityEngine.Animations.Rigging.{shortName}, Unity.Animation.Rigging");
            if (type == null)
            {
                error = $"Could not find Animation Rigging type '{shortName}' -- the Animation Rigging package (com.unity.animation.rigging) doesn't appear to be installed in this project.";
                return false;
            }
            error = null;
            return true;
        }

        [MCPTool(
            "add_ik_constraint",
            "Adds a TwoBoneIK (hand/foot) or Look (head/eye aim) constraint via the Animation Rigging package, " +
            "auto-creating a Rig + RigBuilder on the given animator root if one doesn't already exist there. " +
            "Requires com.unity.animation.rigging; fails clearly if it isn't installed.",
            group: "animation")]
        public static MCPResult AddIkConstraint(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Animator that root motion/rigging is built on (RigBuilder goes here).")] string animatorRootPath,
            [MCPParam("Hierarchy path for the new constraint GameObject (created if missing).")] string constraintPath,
            [MCPParam("'TwoBoneIK' (hand/foot) or 'Look' (head/eye aim).")] string type,
            [MCPParam("TwoBoneIK only: hierarchy path of the root bone (e.g. upper arm/thigh).")] string rootBonePath = null,
            [MCPParam("TwoBoneIK only: hierarchy path of the mid bone (e.g. elbow/knee).")] string midBonePath = null,
            [MCPParam("TwoBoneIK only: hierarchy path of the tip bone (e.g. hand/foot).")] string tipBonePath = null,
            [MCPParam("TwoBoneIK: hierarchy path of the IK target. Look: hierarchy path of the constrained object (e.g. head bone).")] string targetPath = null,
            [MCPParam("TwoBoneIK only: hierarchy path of the pole/hint target. Omit for none.")] string hintPath = null,
            [MCPParam("Look only: hierarchy path of the object to look at (the aim source).")] string lookAtPath = null,
            [MCPParam("Overall constraint weight (0-1). Defaults to 1.")] float weight = 1f)
        {
            if (!TryGetRiggingType("Rig", out var rigType, out var rigError)) return MCPResult.Fail(rigError);
            if (!TryGetRiggingType("RigBuilder", out var rigBuilderType, out _)) return MCPResult.Fail(rigError);
            if (!TryGetRiggingType("RigLayer", out var rigLayerType, out _)) return MCPResult.Fail(rigError);

            var animatorRoot = MCPSceneUtil.ResolvePath(animatorRootPath);
            if (animatorRoot == null) return MCPResult.Fail($"Path '{animatorRootPath}' not found.");
            if (animatorRoot.GetComponent<Animator>() == null) return MCPResult.Fail($"GameObject at '{animatorRootPath}' has no Animator component.");

            var rigBuilder = animatorRoot.GetComponent(rigBuilderType);
            if (rigBuilder == null) rigBuilder = animatorRoot.AddComponent(rigBuilderType);

            var rigGo = MCPSceneUtil.ResolvePath(animatorRootPath + "/Rig");
            object rig;
            bool rigJustCreated = false;
            if (rigGo == null)
            {
                var newRigGo = new GameObject("Rig");
                Undo.RegisterCreatedObjectUndo(newRigGo, "MCP: Add IK Constraint");
                newRigGo.transform.SetParent(animatorRoot.transform, worldPositionStays: false);
                rig = newRigGo.AddComponent(rigType);
                rigGo = newRigGo;
                rigJustCreated = true;
            }
            else
            {
                rig = rigGo.GetComponent(rigType);
                if (rig == null) rig = rigGo.AddComponent(rigType);
            }

            if (rigJustCreated)
            {
                var layersProp = rigBuilderType.GetProperty("layers", AllInstance);
                var layersList = layersProp.GetValue(rigBuilder);
                var rigLayerCtor = rigLayerType.GetConstructor(new[] { rigType, typeof(bool) });
                var rigLayerInstance = rigLayerCtor.Invoke(new object[] { rig, true });
                var addLayerMethod = layersList.GetType().GetMethod("Add", new[] { rigLayerType });
                addLayerMethod.Invoke(layersList, new object[] { rigLayerInstance });
                layersProp.SetValue(rigBuilder, layersList);
            }

            var constraintGo = MCPSceneUtil.ResolvePath(constraintPath);
            if (constraintGo == null)
            {
                var name = constraintPath.Contains("/") ? constraintPath.Substring(constraintPath.LastIndexOf('/') + 1) : constraintPath;
                constraintGo = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(constraintGo, "MCP: Add IK Constraint");
                constraintGo.transform.SetParent(rigGo.transform, worldPositionStays: false);
            }

            MCPResult result;
            switch (type)
            {
                case "TwoBoneIK":
                    result = ConfigureTwoBoneIk(constraintGo, rootBonePath, midBonePath, tipBonePath, targetPath, hintPath, weight);
                    break;
                case "Look":
                    result = ConfigureLookIk(constraintGo, targetPath, lookAtPath, weight);
                    break;
                default:
                    return MCPResult.Fail($"Unknown type '{type}'. Valid values: TwoBoneIK, Look.");
            }
            if (result != null) return result;

            var buildMethod = rigBuilderType.GetMethod("Build", AllInstance, null, Type.EmptyTypes, null);
            buildMethod.Invoke(rigBuilder, null);

            return MCPResult.Success(new { rigPath = MCPSceneUtil.GetPath(rigGo), constraintPath = MCPSceneUtil.GetPath(constraintGo) });
        }

        private static MCPResult ConfigureTwoBoneIk(GameObject constraintGo, string rootBonePath, string midBonePath, string tipBonePath, string targetPath, string hintPath, float weight)
        {
            if (!TryGetRiggingType("TwoBoneIKConstraint", out var constraintType, out var error)) return MCPResult.Fail(error);
            if (rootBonePath == null || midBonePath == null || tipBonePath == null || targetPath == null)
                return MCPResult.Fail("TwoBoneIK requires rootBonePath, midBonePath, tipBonePath, and targetPath.");

            var rootBone = MCPSceneUtil.ResolvePath(rootBonePath);
            var midBone = MCPSceneUtil.ResolvePath(midBonePath);
            var tipBone = MCPSceneUtil.ResolvePath(tipBonePath);
            var target = MCPSceneUtil.ResolvePath(targetPath);
            if (rootBone == null) return MCPResult.Fail($"Path '{rootBonePath}' not found.");
            if (midBone == null) return MCPResult.Fail($"Path '{midBonePath}' not found.");
            if (tipBone == null) return MCPResult.Fail($"Path '{tipBonePath}' not found.");
            if (target == null) return MCPResult.Fail($"Path '{targetPath}' not found.");

            var constraint = constraintGo.GetComponent(constraintType);
            if (constraint == null) constraint = constraintGo.AddComponent(constraintType);

            var dataField = constraintType.BaseType.GetField("m_Data", AllInstance);
            var data = dataField.GetValue(constraint);
            var dataType = data.GetType();
            dataType.GetProperty("root").SetValue(data, rootBone.transform);
            dataType.GetProperty("mid").SetValue(data, midBone.transform);
            dataType.GetProperty("tip").SetValue(data, tipBone.transform);
            dataType.GetProperty("target").SetValue(data, target.transform);
            if (hintPath != null)
            {
                var hint = MCPSceneUtil.ResolvePath(hintPath);
                if (hint == null) return MCPResult.Fail($"Path '{hintPath}' not found.");
                dataType.GetProperty("hint").SetValue(data, hint.transform);
            }
            dataField.SetValue(constraint, data);

            constraintType.GetProperty("weight", AllInstance).SetValue(constraint, weight);
            return null;
        }

        private static MCPResult ConfigureLookIk(GameObject constraintGo, string targetPath, string lookAtPath, float weight)
        {
            if (!TryGetRiggingType("MultiAimConstraint", out var constraintType, out var error)) return MCPResult.Fail(error);
            if (targetPath == null || lookAtPath == null)
                return MCPResult.Fail("Look requires targetPath (the constrained object, e.g. a head bone) and lookAtPath (what to look at).");

            var constrainedObject = MCPSceneUtil.ResolvePath(targetPath);
            var lookAt = MCPSceneUtil.ResolvePath(lookAtPath);
            if (constrainedObject == null) return MCPResult.Fail($"Path '{targetPath}' not found.");
            if (lookAt == null) return MCPResult.Fail($"Path '{lookAtPath}' not found.");

            if (!TryGetRiggingType("WeightedTransformArray", out var arrayType, out _)) return MCPResult.Fail(error);
            if (!TryGetRiggingType("WeightedTransform", out var weightedTransformType, out _)) return MCPResult.Fail(error);

            var constraint = constraintGo.GetComponent(constraintType);
            if (constraint == null) constraint = constraintGo.AddComponent(constraintType);

            var dataField = constraintType.BaseType.GetField("m_Data", AllInstance);
            var data = dataField.GetValue(constraint);
            var dataType = data.GetType();
            dataType.GetProperty("constrainedObject").SetValue(data, constrainedObject.transform);

            var array = arrayType.GetConstructor(new[] { typeof(int) }).Invoke(new object[] { 0 });
            var weightedTransform = weightedTransformType.GetConstructor(new[] { typeof(Transform), typeof(float) }).Invoke(new object[] { lookAt.transform, 1f });
            arrayType.GetMethod("Add", new[] { weightedTransformType }).Invoke(array, new object[] { weightedTransform });
            dataType.GetProperty("sourceObjects").SetValue(data, array);
            dataField.SetValue(constraint, data);

            constraintType.GetProperty("weight", AllInstance).SetValue(constraint, weight);
            return null;
        }
    }
}
