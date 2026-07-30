using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group R of the tool catalog -- Audio. `UnityEditor.Audio.AudioMixerController` (used by create_audio_mixer)
    /// is an internal class in a core Unity assembly (no optional package involved, unlike Cinemachine/Shader
    /// Graph) -- its individual members are public but the class itself isn't, so it has to be driven entirely via
    /// reflection, the same pattern used for the Frame Debugger in InspectionTools.cs. Live spikes against a real
    /// Unity Editor found CreateNewGroup() doesn't reliably attach a new group into the mixer's persisted group
    /// tree without additional internal Editor view-state setup (AddGroupToCurrentView threw
    /// IndexOutOfRangeException on a fresh controller with no existing view list) -- rather than keep
    /// reverse-engineering increasingly fragile internal state, create_audio_mixer is scoped to creating the
    /// mixer asset with its default Master group only; additional groups need the Mixer window's own UI.
    /// </summary>
    public static class AudioTools
    {
        [MCPTool("add_audio_source", "Adds and configures a 3D AudioSource on a GameObject.", group: "audio")]
        public static MCPResult AddAudioSource(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("0 = fully 2D, 1 = fully 3D (spatial). Defaults to 1.")] float spatialBlend = 1f,
            [MCPParam("Minimum distance for full volume, 3D only. Omit for Unity's default (1).")] float? minDistance = null,
            [MCPParam("Maximum distance before the sound is inaudible, 3D only. Omit for Unity's default (500).")] float? maxDistance = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null) source = go.AddComponent<AudioSource>();

            source.spatialBlend = spatialBlend;
            if (minDistance.HasValue) source.minDistance = minDistance.Value;
            if (maxDistance.HasValue) source.maxDistance = maxDistance.Value;

            return MCPResult.Success();
        }

        [MCPTool("set_audio_source_properties", "Sets clip/volume/pitch/loop/priority/mute on an existing AudioSource. Omitted parameters are left unchanged.", group: "audio")]
        public static MCPResult SetAudioSourceProperties(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the AudioSource.")] string path,
            [MCPParam("Path relative to Assets/ of an AudioClip to assign. Omit to leave unchanged.")] string clipAssetPath = null,
            [MCPParam("Volume (0-1). Omit to leave unchanged.")] float? volume = null,
            [MCPParam("Pitch multiplier. Omit to leave unchanged.")] float? pitch = null,
            [MCPParam("Whether the clip loops. Omit to leave unchanged.")] bool? loop = null,
            [MCPParam("Priority (0 = highest, 256 = lowest). Omit to leave unchanged.")] int? priority = null,
            [MCPParam("Whether to play automatically on Awake. Omit to leave unchanged.")] bool? playOnAwake = null,
            [MCPParam("Whether the source is muted. Omit to leave unchanged.")] bool? mute = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null) return MCPResult.Fail($"GameObject at '{path}' has no AudioSource component.");

            if (clipAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, clipAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{clipAssetPath}' does not exist.");

                var unityPath = "Assets/" + clipAssetPath.Replace('\\', '/').TrimStart('/');
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(unityPath);
                if (clip == null) return MCPResult.Fail($"Could not load an AudioClip at '{clipAssetPath}'.");
                source.clip = clip;
            }

            if (volume.HasValue) source.volume = volume.Value;
            if (pitch.HasValue) source.pitch = pitch.Value;
            if (loop.HasValue) source.loop = loop.Value;
            if (priority.HasValue) source.priority = priority.Value;
            if (playOnAwake.HasValue) source.playOnAwake = playOnAwake.Value;
            if (mute.HasValue) source.mute = mute.Value;

            return MCPResult.Success();
        }

        [MCPTool("configure_spatial_audio", "Configures 3D spatialization on an existing AudioSource: rolloff, distance range, spread, and Doppler. Omitted parameters are left unchanged.", group: "audio")]
        public static MCPResult ConfigureSpatialAudio(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the AudioSource.")] string path,
            [MCPParam("Volume falloff curve shape. Omit to leave unchanged.")] AudioRolloffMode? rolloffMode = null,
            [MCPParam("Distance for full volume. Omit to leave unchanged.")] float? minDistance = null,
            [MCPParam("Distance beyond which the sound stops attenuating (Logarithmic) or is inaudible (Linear). Omit to leave unchanged.")] float? maxDistance = null,
            [MCPParam("Spread angle in degrees for 3D stereo/multichannel sources (0-360). Omit to leave unchanged.")] float? spread = null,
            [MCPParam("Doppler pitch-shift strength (0 = none, 1 = full). Omit to leave unchanged.")] float? dopplerLevel = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null) return MCPResult.Fail($"GameObject at '{path}' has no AudioSource component.");

            if (rolloffMode.HasValue) source.rolloffMode = rolloffMode.Value;
            if (minDistance.HasValue) source.minDistance = minDistance.Value;
            if (maxDistance.HasValue) source.maxDistance = maxDistance.Value;
            if (spread.HasValue) source.spread = spread.Value;
            if (dopplerLevel.HasValue) source.dopplerLevel = dopplerLevel.Value;

            return MCPResult.Success();
        }

        [MCPTool(
            "add_audio_listener",
            "Adds an AudioListener to a GameObject (usually the main camera). Reports if other listeners already " +
            "exist elsewhere in the scene -- Unity only uses one at a time and logs a runtime warning if more than " +
            "one is active, but this tool doesn't remove others automatically.",
            group: "audio")]
        public static MCPResult AddAudioListener(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var listener = go.GetComponent<AudioListener>();
            bool alreadyPresent = listener != null;
            if (listener == null) listener = go.AddComponent<AudioListener>();

            var allListeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            var otherPaths = allListeners
                .Where(l => l.gameObject != go)
                .Select(l => MCPSceneUtil.GetPath(l.gameObject))
                .ToList();

            return MCPResult.Success(new { alreadyPresent, otherListenerPaths = otherPaths });
        }

        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        [MCPTool(
            "create_audio_mixer",
            "Creates a new AudioMixer asset with its default Master group. Uses UnityEditor.Audio.AudioMixerController " +
            "via reflection (an internal-but-always-present class, no optional package involved) -- additional child " +
            "groups and exposed parameters need the Mixer window's own UI; a live spike found the internal group-creation " +
            "API doesn't reliably attach new groups into the persisted mixer without further Editor view-state setup " +
            "this tool doesn't attempt to reverse-engineer.",
            group: "audio")]
        public static MCPResult CreateAudioMixer(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Audio/MainMixer.mixer'.")] string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".mixer", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.mixer'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            var controllerType = Type.GetType("UnityEditor.Audio.AudioMixerController, UnityEditor");
            if (controllerType == null)
                return MCPResult.Fail("Could not find UnityEditor.Audio.AudioMixerController via reflection -- this Unity version's internal audio mixer API may have changed.");

            var createMethod = controllerType.GetMethod("CreateMixerControllerAtPath", AllStatic);
            if (createMethod == null)
                return MCPResult.Fail("Could not find AudioMixerController.CreateMixerControllerAtPath via reflection -- this Unity version's internal audio mixer API may have changed.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');

            object controller;
            try
            {
                controller = createMethod.Invoke(null, new object[] { unityAssetPath });
            }
            catch (TargetInvocationException e)
            {
                return MCPResult.Fail($"AudioMixerController.CreateMixerControllerAtPath threw: {e.InnerException?.Message ?? e.Message}");
            }

            if (controller == null)
                return MCPResult.Fail("AudioMixerController.CreateMixerControllerAtPath returned null.");

            AssetDatabase.SaveAssets();

            var masterGroupProp = controllerType.GetProperty("masterGroup", AllInstance);
            var masterGroup = masterGroupProp?.GetValue(controller) as AudioMixerGroup;

            return MCPResult.Success(new { assetPath = unityAssetPath, masterGroupName = masterGroup?.name });
        }

        [MCPTool(
            "set_mixer_parameter",
            "Sets an exposed AudioMixer parameter (via AudioMixer.SetFloat) -- for ducking music, muffling audio " +
            "behind a snapshot transition, etc. The parameter must already be exposed to scripting (right-click a " +
            "group's effect parameter in the Mixer window -> 'Expose ... to script'); this tool doesn't expose new " +
            "parameters itself.",
            group: "audio")]
        public static MCPResult SetMixerParameter(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the AudioMixer asset.")] string mixerAssetPath,
            [MCPParam("Exposed parameter name, e.g. 'MusicVolume'.")] string parameterName,
            [MCPParam("New value (typically a decibel value for volume-type parameters).")] float value)
        {
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, mixerAssetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{mixerAssetPath}' does not exist.");

            var unityPath = "Assets/" + mixerAssetPath.Replace('\\', '/').TrimStart('/');
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(unityPath);
            if (mixer == null) return MCPResult.Fail($"Could not load an AudioMixer at '{mixerAssetPath}'.");

            bool success = mixer.SetFloat(parameterName, value);
            if (!success)
                return MCPResult.Fail($"AudioMixer.SetFloat('{parameterName}', ...) returned false -- '{parameterName}' likely isn't exposed to scripting yet. Expose it via the Mixer window first.");

            return MCPResult.Success();
        }

        [MCPTool("add_reverb_zone", "Adds an AudioReverbZone (environmental reverb for a room/corridor).", group: "audio")]
        public static MCPResult AddReverbZone(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Distance from the center where reverb starts fading in. Omit for Unity's default (10).")] float? minDistance = null,
            [MCPParam("Distance from the center at full falloff. Omit for Unity's default (15).")] float? maxDistance = null,
            [MCPParam("A built-in reverb preset (e.g. Cave, Hallway, Room, Underwater). Omit for Unity's default (Generic).")] AudioReverbPreset? reverbPreset = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var zone = go.GetComponent<AudioReverbZone>();
            if (zone == null) zone = go.AddComponent<AudioReverbZone>();

            if (minDistance.HasValue) zone.minDistance = minDistance.Value;
            if (maxDistance.HasValue) zone.maxDistance = maxDistance.Value;
            if (reverbPreset.HasValue) zone.reverbPreset = reverbPreset.Value;

            return MCPResult.Success();
        }

        [MCPTool(
            "play_sound",
            "Plays a one-shot sound for verification -- adds an AudioSource to the target if missing. If " +
            "clipAssetPath is given, plays that clip via PlayOneShot without disturbing the source's own assigned " +
            "clip; otherwise plays whatever clip is already assigned.",
            group: "audio", latencyTier: MCPLatencyTier.Fast)]
        public static MCPResult PlaySound(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Path relative to Assets/ of an AudioClip to play. Omit to play the AudioSource's already-assigned clip.")] string clipAssetPath = null,
            [MCPParam("Playback volume (0-1). Defaults to 1.")] float volume = 1f)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null) source = go.AddComponent<AudioSource>();

            if (clipAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, clipAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{clipAssetPath}' does not exist.");

                var unityPath = "Assets/" + clipAssetPath.Replace('\\', '/').TrimStart('/');
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(unityPath);
                if (clip == null) return MCPResult.Fail($"Could not load an AudioClip at '{clipAssetPath}'.");

                source.PlayOneShot(clip, volume);
                return MCPResult.Success(new { clipLength = clip.length });
            }

            if (source.clip == null)
                return MCPResult.Fail("No clipAssetPath given and the AudioSource has no clip assigned.");

            source.PlayOneShot(source.clip, volume);
            return MCPResult.Success(new { clipLength = source.clip.length });
        }
    }
}
