using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group V of the tool catalog -- Timeline &amp; Cutscenes. `PlayableDirector`/`Playable` are core Unity
    /// (UnityEngine.DirectorModule/CoreModule, confirmed via live spike, always present) and used directly, but
    /// everything Timeline-authoring-specific (TimelineAsset, tracks, clips, signals) lives in the optional
    /// com.unity.timeline package and is resolved via reflection, the same pattern as Cinemachine/Animation
    /// Rigging/URP. `add_camera_cut_track` additionally reflects into Cinemachine's CinemachineTrack/CinemachineShot
    /// -- confirmed via live spike that CinemachineShot.VirtualCamera is an `ExposedReference&lt;T&gt;` field,
    /// wired not through the track's generic binding but through `PlayableDirector.SetReferenceValue(PropertyName,
    /// Object)` against a freshly-generated `PropertyName`, the same mechanism the Timeline Editor UI uses
    /// internally when you drag a camera onto a shot clip.
    /// </summary>
    public static class TimelineTools
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static Type TimelineType(string shortName) => Type.GetType($"UnityEngine.Timeline.{shortName}, Unity.Timeline");

        private static bool TryGetTimelineType(string shortName, out Type type, out string error)
        {
            type = TimelineType(shortName);
            if (type == null)
            {
                error = $"Could not find Timeline type '{shortName}' -- the Timeline package (com.unity.timeline) doesn't appear to be installed in this project.";
                return false;
            }
            error = null;
            return true;
        }

        private static MCPResult LoadTimeline(string assetPath, out object timeline, out Type timelineType, out string unityPath)
        {
            timeline = null;
            unityPath = null;
            if (!TryGetTimelineType("TimelineAsset", out timelineType, out var typeError)) return MCPResult.Fail(typeError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath)) return MCPResult.Fail($"'{assetPath}' does not exist.");

            unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            timeline = AssetDatabase.LoadAssetAtPath(unityPath, timelineType);
            if (timeline == null) return MCPResult.Fail($"Could not load a TimelineAsset at '{assetPath}'.");
            return null;
        }

        private static object FindTrack(object timeline, Type timelineType, string trackName, out string error)
        {
            error = null;
            var getOutputTracksMethod = timelineType.GetMethod("GetOutputTracks", AllInstance);
            var tracks = ((System.Collections.IEnumerable)getOutputTracksMethod.Invoke(timeline, null)).Cast<object>();
            var nameProp = TimelineType("TrackAsset").GetProperty("name", AllInstance);
            var track = tracks.FirstOrDefault(t => (string)nameProp.GetValue(t) == trackName);
            if (track == null) error = $"Track '{trackName}' not found on this TimelineAsset.";
            return track;
        }

        [MCPTool("create_timeline", "Creates a new TimelineAsset, optionally attaching it to a PlayableDirector on an existing or new GameObject.", group: "timeline")]
        public static MCPResult CreateTimeline(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Timelines/IntroCutscene.playable'.")] string assetPath,
            [MCPParam("Hierarchy path for a PlayableDirector -- created if it doesn't exist. Omit to only create the asset.")] string directorPath = null)
        {
            if (!TryGetTimelineType("TimelineAsset", out var timelineType, out var typeError)) return MCPResult.Fail(typeError);
            if (!assetPath.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.playable'.");
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (File.Exists(fullPath)) return MCPResult.Fail($"'{assetPath}' already exists.");

            var timeline = ScriptableObject.CreateInstance(timelineType);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.CreateAsset(timeline, unityPath);
            AssetDatabase.SaveAssets();

            string resolvedDirectorPath = null;
            if (directorPath != null)
            {
                var go = MCPSceneUtil.ResolvePath(directorPath);
                if (go == null)
                {
                    go = new GameObject(directorPath.Contains("/") ? directorPath.Substring(directorPath.LastIndexOf('/') + 1) : directorPath);
                    Undo.RegisterCreatedObjectUndo(go, "MCP: Create Timeline Director");
                }
                var director = go.GetComponent<PlayableDirector>();
                if (director == null) director = go.AddComponent<PlayableDirector>();
                director.playableAsset = (PlayableAsset)timeline;
                resolvedDirectorPath = MCPSceneUtil.GetPath(go);
            }

            return MCPResult.Success(new { assetPath = unityPath, directorPath = resolvedDirectorPath });
        }

        [MCPTool("add_timeline_track", "Adds an Animation, Audio, Activation, or Signal track to a TimelineAsset.", group: "timeline")]
        public static MCPResult AddTimelineTrack(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the TimelineAsset.")] string timelineAssetPath,
            [MCPParam("Track type: Animation, Audio, Activation, or Signal.")] string trackType,
            [MCPParam("Name for the new track.")] string trackName)
        {
            var fail = LoadTimeline(timelineAssetPath, out var timeline, out var timelineType, out _);
            if (fail != null) return fail;

            string shortTypeName = trackType switch
            {
                "Animation" => "AnimationTrack",
                "Audio" => "AudioTrack",
                "Activation" => "ActivationTrack",
                "Signal" => "SignalTrack",
                _ => null,
            };
            if (shortTypeName == null) return MCPResult.Fail($"Unknown trackType '{trackType}'. Valid values: Animation, Audio, Activation, Signal.");
            if (!TryGetTimelineType(shortTypeName, out var trackType_, out var trackTypeError)) return MCPResult.Fail(trackTypeError);

            var createTrackGeneric = timelineType.GetMethods(AllInstance)
                .First(m => m.Name == "CreateTrack" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
            var track = createTrackGeneric.MakeGenericMethod(trackType_).Invoke(timeline, new object[] { null, trackName });

            EditorUtility.SetDirty((UnityEngine.Object)timeline);
            AssetDatabase.SaveAssets();

            return MCPResult.Success(new { trackName = (string)TimelineType("TrackAsset").GetProperty("name", AllInstance).GetValue(track) });
        }

        [MCPTool(
            "add_timeline_clip",
            "Places a clip on an Animation/Audio/Activation track at a given start time and duration. Animation/" +
            "Audio tracks need clipAssetPath (an AnimationClip/AudioClip); Activation tracks ignore it (they just " +
            "toggle their bound GameObject's active state for the clip's duration).",
            group: "timeline")]
        public static MCPResult AddTimelineClip(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the TimelineAsset.")] string timelineAssetPath,
            [MCPParam("Name of an existing track on this TimelineAsset.")] string trackName,
            [MCPParam("Start time in seconds.")] double start,
            [MCPParam("Duration in seconds.")] double duration,
            [MCPParam("Path relative to Assets/ of an AnimationClip (for Animation tracks) or AudioClip (for Audio tracks). Ignored for Activation tracks.")] string clipAssetPath = null)
        {
            var fail = LoadTimeline(timelineAssetPath, out var timeline, out var timelineType, out _);
            if (fail != null) return fail;

            var track = FindTrack(timeline, timelineType, trackName, out var trackError);
            if (track == null) return MCPResult.Fail(trackError);

            var trackAssetType = TimelineType("TrackAsset");
            var createClipGeneric = trackAssetType.GetMethods(AllInstance)
                .First(m => m.Name == "CreateClip" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

            object clip;
            var trackTypeName = track.GetType().Name;
            if (trackTypeName == "AnimationTrack")
            {
                var animAssetType = TimelineType("AnimationPlayableAsset");
                clip = createClipGeneric.MakeGenericMethod(animAssetType).Invoke(track, null);
                if (clipAssetPath != null)
                {
                    if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, clipAssetPath, out var clipFullPath, out var clipGuardError)) return MCPResult.Fail(clipGuardError);
                    if (!File.Exists(clipFullPath)) return MCPResult.Fail($"'{clipAssetPath}' does not exist.");
                    var clipUnityPath = "Assets/" + clipAssetPath.Replace('\\', '/').TrimStart('/');
                    var animClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipUnityPath);
                    if (animClip == null) return MCPResult.Fail($"Could not load an AnimationClip at '{clipAssetPath}'.");
                    var clipAssetObj = GetTimelineClipAsset(clip);
                    animAssetType.GetProperty("clip", AllInstance).SetValue(clipAssetObj, animClip);
                }
            }
            else if (trackTypeName == "AudioTrack")
            {
                var audioAssetType = TimelineType("AudioPlayableAsset");
                clip = createClipGeneric.MakeGenericMethod(audioAssetType).Invoke(track, null);
                if (clipAssetPath != null)
                {
                    if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, clipAssetPath, out var clipFullPath, out var clipGuardError)) return MCPResult.Fail(clipGuardError);
                    if (!File.Exists(clipFullPath)) return MCPResult.Fail($"'{clipAssetPath}' does not exist.");
                    var clipUnityPath = "Assets/" + clipAssetPath.Replace('\\', '/').TrimStart('/');
                    var audioClip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipUnityPath);
                    if (audioClip == null) return MCPResult.Fail($"Could not load an AudioClip at '{clipAssetPath}'.");
                    var clipAssetObj = GetTimelineClipAsset(clip);
                    audioAssetType.GetProperty("clip", AllInstance).SetValue(clipAssetObj, audioClip);
                }
            }
            else if (trackTypeName == "ActivationTrack")
            {
                var activationAssetType = TimelineType("ActivationPlayableAsset");
                clip = createClipGeneric.MakeGenericMethod(activationAssetType).Invoke(track, null);
            }
            else
            {
                return MCPResult.Fail($"Track '{trackName}' is a {trackTypeName}, which add_timeline_clip doesn't support (only Animation/Audio/Activation).");
            }

            var timelineClipType = TimelineType("TimelineClip");
            timelineClipType.GetProperty("start", AllInstance).SetValue(clip, start);
            timelineClipType.GetProperty("duration", AllInstance).SetValue(clip, duration);

            EditorUtility.SetDirty((UnityEngine.Object)timeline);
            AssetDatabase.SaveAssets();

            return MCPResult.Success();
        }

        private static object GetTimelineClipAsset(object timelineClip) =>
            TimelineType("TimelineClip").GetProperty("asset", AllInstance).GetValue(timelineClip);

        [MCPTool(
            "add_timeline_signal",
            "Adds a SignalEmitter marker to a Signal track at a given time, creating (or reusing) a SignalAsset. " +
            "If receiverPath/targetTypeName/methodName are given, ensures a SignalReceiver on receiverPath and " +
            "wires a real reaction to it (dynamic listener by default, same semantics as wire_unity_event).",
            group: "timeline")]
        public static MCPResult AddTimelineSignal(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the TimelineAsset.")] string timelineAssetPath,
            [MCPParam("Name of an existing Signal track on this TimelineAsset.")] string trackName,
            [MCPParam("Time in seconds to place the signal emitter.")] double time,
            [MCPParam("Path relative to Assets/ for the SignalAsset -- created if missing, reused if it already exists.")] string signalAssetPath,
            [MCPParam("Hierarchy path of a GameObject to receive this signal. Omit to only create the emitter.")] string receiverPath = null,
            [MCPParam("Component type on receiverPath that owns the reaction method.")] string targetTypeName = null,
            [MCPParam("Public method name to call when this signal fires.")] string methodName = null)
        {
            var fail = LoadTimeline(timelineAssetPath, out var timeline, out var timelineType, out _);
            if (fail != null) return fail;

            var track = FindTrack(timeline, timelineType, trackName, out var trackError);
            if (track == null) return MCPResult.Fail(trackError);
            if (track.GetType().Name != "SignalTrack") return MCPResult.Fail($"Track '{trackName}' is a {track.GetType().Name}, not a SignalTrack.");

            if (!TryGetTimelineType("SignalAsset", out var signalAssetType, out var signalTypeError)) return MCPResult.Fail(signalTypeError);
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, signalAssetPath, out var signalFullPath, out var signalGuardError))
                return MCPResult.Fail(signalGuardError);
            var signalUnityPath = "Assets/" + signalAssetPath.Replace('\\', '/').TrimStart('/');

            UnityEngine.Object signalAsset;
            if (File.Exists(signalFullPath))
            {
                signalAsset = AssetDatabase.LoadAssetAtPath(signalUnityPath, signalAssetType);
                if (signalAsset == null) return MCPResult.Fail($"Could not load a SignalAsset at '{signalAssetPath}'.");
            }
            else
            {
                signalAsset = (UnityEngine.Object)ScriptableObject.CreateInstance(signalAssetType);
                Directory.CreateDirectory(Path.GetDirectoryName(signalFullPath));
                AssetDatabase.CreateAsset(signalAsset, signalUnityPath);
            }

            var createMarkerGeneric = TimelineType("TrackAsset").GetMethods(AllInstance)
                .First(m => m.Name == "CreateMarker" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (!TryGetTimelineType("SignalEmitter", out var signalEmitterType, out var emitterTypeError)) return MCPResult.Fail(emitterTypeError);
            var emitter = createMarkerGeneric.MakeGenericMethod(signalEmitterType).Invoke(track, new object[] { time });
            signalEmitterType.GetProperty("asset", AllInstance).SetValue(emitter, signalAsset);

            EditorUtility.SetDirty((UnityEngine.Object)timeline);
            AssetDatabase.SaveAssets();

            if (receiverPath != null)
            {
                if (!TryGetTimelineType("SignalReceiver", out var receiverType, out var receiverTypeError)) return MCPResult.Fail(receiverTypeError);
                var receiverGo = MCPSceneUtil.ResolvePath(receiverPath);
                if (receiverGo == null) return MCPResult.Fail($"Path '{receiverPath}' not found.");
                var receiver = receiverGo.GetComponent(receiverType);
                if (receiver == null) receiver = receiverGo.AddComponent(receiverType);

                if (targetTypeName == null || methodName == null)
                    return MCPResult.Fail("receiverPath was given but targetTypeName/methodName are missing.");
                if (!MCPTypeResolver.TryResolve(targetTypeName, out var targetType, out var targetTypeErr)) return MCPResult.Fail(targetTypeErr);
                var targetComponent = receiverGo.GetComponent(targetType);
                if (targetComponent == null) return MCPResult.Fail($"GameObject at '{receiverPath}' has no component of type '{targetTypeName}'.");

                var getReaction = receiverType.GetMethod("GetReaction", AllInstance);
                var addReaction = receiverType.GetMethod("AddReaction", AllInstance);
                var reaction = (UnityEvent)getReaction.Invoke(receiver, new object[] { signalAsset });
                if (reaction == null)
                {
                    reaction = new UnityEvent();
                    addReaction.Invoke(receiver, new object[] { signalAsset, reaction });
                }

                var wireError = MCPUnityEventWiring.AddListener(reaction, typeof(UnityEvent), targetComponent, targetType, methodName, dynamic: true, stringArgument: null, floatArgument: 0f, intArgument: 0, boolArgument: false);
                if (wireError != null) return MCPResult.Fail(wireError);
                reaction.SetPersistentListenerState(reaction.GetPersistentEventCount() - 1, UnityEventCallState.EditorAndRuntime);

                EditorUtility.SetDirty(receiverGo);
            }

            return MCPResult.Success(new { signalAssetPath = signalUnityPath });
        }

        [MCPTool("bind_timeline_track", "Binds a track on a TimelineAsset to a scene object via the PlayableDirector's generic bindings (e.g. an Animation track to an Animator, an Audio track to an AudioSource).", group: "timeline")]
        public static MCPResult BindTimelineTrack(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the PlayableDirector.")] string directorPath,
            [MCPParam("Path relative to Assets/ of the TimelineAsset assigned to that director.")] string timelineAssetPath,
            [MCPParam("Name of the track to bind.")] string trackName,
            [MCPParam("Hierarchy path of the GameObject to bind.")] string targetPath,
            [MCPParam("Component type on targetPath to bind (e.g. 'Animator', 'AudioSource'). Omit to bind the GameObject itself.")] string targetTypeName = null)
        {
            var directorGo = MCPSceneUtil.ResolvePath(directorPath);
            if (directorGo == null) return MCPResult.Fail($"Path '{directorPath}' not found.");
            var director = directorGo.GetComponent<PlayableDirector>();
            if (director == null) return MCPResult.Fail($"GameObject at '{directorPath}' has no PlayableDirector component.");

            var fail = LoadTimeline(timelineAssetPath, out var timeline, out var timelineType, out _);
            if (fail != null) return fail;
            var track = FindTrack(timeline, timelineType, trackName, out var trackError);
            if (track == null) return MCPResult.Fail(trackError);

            var targetGo = MCPSceneUtil.ResolvePath(targetPath);
            if (targetGo == null) return MCPResult.Fail($"Path '{targetPath}' not found.");

            UnityEngine.Object binding = targetGo;
            if (targetTypeName != null)
            {
                if (!MCPTypeResolver.TryResolve(targetTypeName, out var targetType, out var targetTypeError)) return MCPResult.Fail(targetTypeError);
                binding = targetGo.GetComponent(targetType);
                if (binding == null) return MCPResult.Fail($"GameObject at '{targetPath}' has no component of type '{targetTypeName}'.");
            }

            var setBindingMethod = typeof(PlayableDirector).GetMethod("SetGenericBinding", AllInstance);
            setBindingMethod.Invoke(director, new object[] { track, binding });

            return MCPResult.Success();
        }

        [MCPTool(
            "play_timeline",
            "Sets a PlayableDirector's time and evaluates it immediately -- confirmed via live spike this really " +
            "re-samples the timeline outside Play Mode, for verifying a sequence looks right at a given moment " +
            "without needing an actual Play Mode session.",
            group: "timeline", latencyTier: MCPLatencyTier.Fast)]
        public static MCPResult PlayTimeline(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the PlayableDirector.")] string path,
            [MCPParam("Time in seconds to evaluate at.")] double time)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return MCPResult.Fail($"GameObject at '{path}' has no PlayableDirector component.");
            if (director.playableAsset == null) return MCPResult.Fail($"PlayableDirector at '{path}' has no playableAsset assigned.");

            director.time = time;
            director.Evaluate();

            return MCPResult.Success(new { time = director.time, duration = director.duration });
        }

        [MCPTool(
            "add_camera_cut_track",
            "Adds a Cinemachine track with camera-cut shots for cutscene coverage -- each shot references a " +
            "CinemachineVirtualCamera via Timeline's ExposedReference/PlayableDirector.SetReferenceValue mechanism " +
            "(the same one the Timeline Editor UI uses when you drag a camera onto a shot clip). Requires both " +
            "com.unity.timeline and com.unity.cinemachine.",
            group: "timeline")]
        public static MCPResult AddCameraCutTrack(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the PlayableDirector that will resolve the camera references.")] string directorPath,
            [MCPParam("Path relative to Assets/ of the TimelineAsset assigned to that director.")] string timelineAssetPath,
            [MCPParam("Name for the new Cinemachine track.")] string trackName,
            [MCPParam("Shots, each as \"vcamPath,start,duration\", e.g. [\"Vcam1,0,3\", \"Vcam2,3,2\"].")] string[] shots)
        {
            if (shots == null || shots.Length == 0) return MCPResult.Fail("shots must contain at least one \"vcamPath,start,duration\" entry.");

            var directorGo = MCPSceneUtil.ResolvePath(directorPath);
            if (directorGo == null) return MCPResult.Fail($"Path '{directorPath}' not found.");
            var director = directorGo.GetComponent<PlayableDirector>();
            if (director == null) return MCPResult.Fail($"GameObject at '{directorPath}' has no PlayableDirector component.");

            var fail = LoadTimeline(timelineAssetPath, out var timeline, out var timelineType, out _);
            if (fail != null) return fail;

            var cinemachineTrackType = Type.GetType("CinemachineTrack, Cinemachine");
            if (cinemachineTrackType == null) return MCPResult.Fail("Could not find CinemachineTrack -- the Cinemachine package (com.unity.cinemachine) doesn't appear to be installed in this project.");
            var cinemachineShotType = Type.GetType("CinemachineShot, Cinemachine");
            if (cinemachineShotType == null) return MCPResult.Fail("Could not find CinemachineShot via reflection -- this Cinemachine version's Timeline integration may have changed.");

            var createTrackGeneric = timelineType.GetMethods(AllInstance)
                .First(m => m.Name == "CreateTrack" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
            var track = createTrackGeneric.MakeGenericMethod(cinemachineTrackType).Invoke(timeline, new object[] { null, trackName });

            var trackAssetType = TimelineType("TrackAsset");
            var createClipGeneric = trackAssetType.GetMethods(AllInstance)
                .First(m => m.Name == "CreateClip" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var timelineClipType = TimelineType("TimelineClip");
            var vcamField = cinemachineShotType.GetField("VirtualCamera", AllInstance);
            var propertyNameCtor = typeof(PropertyName).GetConstructor(new[] { typeof(string) });

            foreach (var entry in shots)
            {
                var parts = entry.Split(',');
                if (parts.Length != 3 || !double.TryParse(parts[1], out var start) || !double.TryParse(parts[2], out var duration))
                    return MCPResult.Fail($"Invalid shot entry '{entry}' -- expected \"vcamPath,start,duration\".");

                var vcamGo = MCPSceneUtil.ResolvePath(parts[0]);
                if (vcamGo == null) return MCPResult.Fail($"Path '{parts[0]}' not found.");

                var clip = createClipGeneric.MakeGenericMethod(cinemachineShotType).Invoke(track, null);
                timelineClipType.GetProperty("start", AllInstance).SetValue(clip, start);
                timelineClipType.GetProperty("duration", AllInstance).SetValue(clip, duration);

                var shotAsset = GetTimelineClipAsset(clip);
                object exposedRef = vcamField.GetValue(shotAsset);
                var propertyName = propertyNameCtor.Invoke(new object[] { GUID.Generate().ToString() });
                exposedRef.GetType().GetField("exposedName", AllInstance).SetValue(exposedRef, propertyName);
                vcamField.SetValue(shotAsset, exposedRef);

                var setRefMethod = typeof(PlayableDirector).GetMethod("SetReferenceValue", new[] { typeof(PropertyName), typeof(UnityEngine.Object) });
                setRefMethod.Invoke(director, new object[] { propertyName, vcamGo });
            }

            EditorUtility.SetDirty((UnityEngine.Object)timeline);
            AssetDatabase.SaveAssets();

            return MCPResult.Success(new { shotCount = shots.Length });
        }
    }
}
