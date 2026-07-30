using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group J of the tool catalog -- Rendering &amp; Post-Processing. Scoped entirely to URP: this project's
    /// verification spikes confirmed the core SRP `Volume`/`VolumeProfile` types (usable without any specific
    /// pipeline installed) but every actual effect override (Vignette, Bloom, DepthOfField, etc.) lives in
    /// `UnityEngine.Rendering.Universal.*` (com.unity.render-pipelines.universal) -- HDRP has its own,
    /// differently-shaped equivalents this doesn't attempt to support. Like Cinemachine/Shader Graph/AudioMixer
    /// before it, every URP-specific type is resolved via reflection (`Type.GetType(..., "Unity.RenderPipelines.
    /// Universal.Runtime")`) rather than a compile-time package reference, so the bridge still compiles in
    /// Built-in-only projects. Every VolumeComponent override field (e.g. Vignette.intensity) is a
    /// `VolumeParameter&lt;T&gt;`-derived object, not a plain value -- confirmed via live spike that setting it
    /// means reflecting into a nested `value`/`overrideState` property pair, not the field itself.
    /// </summary>
    public static class RenderingTools
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static Type UrpType(string shortName) => Type.GetType($"UnityEngine.Rendering.Universal.{shortName}, Unity.RenderPipelines.Universal.Runtime");

        private static bool TryGetUrpType(string shortName, out Type type, out string error)
        {
            type = UrpType(shortName);
            if (type == null)
            {
                error = $"Could not find URP type '{shortName}' -- the Universal Render Pipeline package (com.unity.render-pipelines.universal) doesn't appear to be installed in this project.";
                return false;
            }
            error = null;
            return true;
        }

        [MCPTool("get_render_pipeline", "Reports the active render pipeline: BuiltIn, Universal, HighDefinition, or Custom (with its asset type name).", group: "rendering", readOnly: true)]
        public static MCPResult GetRenderPipeline(MCPToolContext ctx)
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return MCPResult.Success(new { pipeline = "BuiltIn", typeName = (string)null });

            var typeName = asset.GetType().FullName;
            string pipeline = typeName.Contains("Universal") ? "Universal"
                : typeName.Contains("HighDefinition") ? "HighDefinition"
                : "Custom";
            return MCPResult.Success(new { pipeline, typeName });
        }

        [MCPTool(
            "create_post_process_volume",
            "Creates a new GameObject with a Volume component (global or local) for post-processing overrides. " +
            "Assign/create the actual VolumeProfile asset via set_volume_profile or by passing profileAssetPath here.",
            group: "rendering")]
        public static MCPResult CreatePostProcessVolume(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'PostProcessVolume'.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new volume under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("Whether this volume affects the whole scene (true) or only a local blend region around it via a Collider (false). Defaults to true.")] bool isGlobal = true,
            [MCPParam("Blend priority when multiple volumes overlap; higher wins. Defaults to 0.")] float priority = 0f,
            [MCPParam("Overall blend weight (0-1). Defaults to 1.")] float weight = 1f,
            [MCPParam("Distance over which a local volume blends in/out, local only. Defaults to 0.")] float blendDistance = 0f,
            [MCPParam("Path relative to Assets/ of an existing VolumeProfile asset to assign. Omit to leave unassigned (assign later via set_volume_profile).")] string profileAssetPath = null)
        {
            if (!TryGetCoreSrpType("Volume", out var volumeType, out var volumeError))
                return MCPResult.Fail(volumeError);
            if (!TryGetCoreSrpType("VolumeProfile", out var profileType, out var profileError))
                return MCPResult.Fail(profileError);

            UnityEngine.Object profileAsset = null;
            if (profileAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, profileAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{profileAssetPath}' does not exist.");
                var unityPath = "Assets/" + profileAssetPath.Replace('\\', '/').TrimStart('/');
                profileAsset = AssetDatabase.LoadAssetAtPath(unityPath, profileType);
                if (profileAsset == null) return MCPResult.Fail($"Could not load a VolumeProfile at '{profileAssetPath}'.");
            }

            var go = new GameObject(string.IsNullOrEmpty(name) ? "PostProcessVolume" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Post Process Volume");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = MCPSceneUtil.ResolvePath(parentPath);
                if (parent == null) { UnityEngine.Object.DestroyImmediate(go); return MCPResult.Fail($"Parent path '{parentPath}' not found."); }
                go.transform.SetParent(parent.transform, worldPositionStays: false);
            }

            // isGlobal/profile are properties, but priority/weight/blendDistance are plain public fields on
            // Volume -- confirmed by reading the actual core SRP source, not assumed from the property pattern
            // the other two use.
            var volume = go.AddComponent(volumeType);
            volumeType.GetProperty("isGlobal").SetValue(volume, isGlobal);
            volumeType.GetField("priority").SetValue(volume, priority);
            volumeType.GetField("weight").SetValue(volume, weight);
            volumeType.GetField("blendDistance").SetValue(volume, blendDistance);
            if (profileAsset != null) volumeType.GetProperty("profile").SetValue(volume, profileAsset);

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool(
            "set_volume_profile",
            "Assigns an existing VolumeProfile asset to an existing Volume component, optionally creating a new blank profile asset first.",
            group: "rendering")]
        public static MCPResult SetVolumeProfile(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Volume component.")] string path,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset, e.g. 'Settings/PostFX.asset'.")] string profileAssetPath,
            [MCPParam("If true and profileAssetPath doesn't exist yet, create a new blank VolumeProfile there. Defaults to false.")] bool createIfMissing = false)
        {
            if (!TryGetCoreSrpType("Volume", out var volumeType, out var volumeError))
                return MCPResult.Fail(volumeError);
            if (!TryGetCoreSrpType("VolumeProfile", out var profileType, out var profileError))
                return MCPResult.Fail(profileError);

            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            var volume = go.GetComponent(volumeType);
            if (volume == null) return MCPResult.Fail($"GameObject at '{path}' has no Volume component.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, profileAssetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            var unityPath = "Assets/" + profileAssetPath.Replace('\\', '/').TrimStart('/');

            UnityEngine.Object profileAsset;
            if (File.Exists(fullPath))
            {
                profileAsset = AssetDatabase.LoadAssetAtPath(unityPath, profileType);
                if (profileAsset == null) return MCPResult.Fail($"Could not load a VolumeProfile at '{profileAssetPath}'.");
            }
            else
            {
                if (!createIfMissing) return MCPResult.Fail($"'{profileAssetPath}' does not exist. Pass createIfMissing: true to create it.");
                var newProfile = ScriptableObject.CreateInstance(profileType);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                AssetDatabase.CreateAsset(newProfile, unityPath);
                AssetDatabase.SaveAssets();
                profileAsset = newProfile;
            }

            volumeType.GetProperty("profile").SetValue(volume, profileAsset);
            return MCPResult.Success(new { path, profileAssetPath = unityPath });
        }

        // -----------------------------------------------------------------
        // Volume override helpers -- shared by every add_* effect tool below.
        // -----------------------------------------------------------------

        private static bool TryGetCoreSrpType(string shortName, out Type type, out string error)
        {
            type = Type.GetType($"UnityEngine.Rendering.{shortName}, Unity.RenderPipelines.Core.Runtime");
            if (type == null)
            {
                error = $"Could not find core SRP type '{shortName}' -- neither URP nor HDRP (com.unity.render-pipelines.core) appears to be installed in this project.";
                return false;
            }
            error = null;
            return true;
        }

        private static MCPResult TryLoadProfile(string profileAssetPath, out object profile, out Type profileType)
        {
            profile = null;
            if (!TryGetCoreSrpType("VolumeProfile", out profileType, out var coreError))
                return MCPResult.Fail(coreError);

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, profileAssetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{profileAssetPath}' does not exist. Create one first via create_post_process_volume/set_volume_profile.");

            var unityPath = "Assets/" + profileAssetPath.Replace('\\', '/').TrimStart('/');
            var loaded = AssetDatabase.LoadAssetAtPath(unityPath, profileType);
            if (loaded == null) return MCPResult.Fail($"Could not load a VolumeProfile at '{profileAssetPath}'.");

            profile = loaded;
            return null;
        }

        private static object EnsureOverride(object profile, Type profileType, Type overrideType)
        {
            var tryGet = profileType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "TryGet" && m.IsGenericMethod && m.GetParameters().Length == 1)
                .MakeGenericMethod(overrideType);
            var args = new object[] { null };
            bool found = (bool)tryGet.Invoke(profile, args);
            if (found) return args[0];

            var add = profileType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1)
                .MakeGenericMethod(overrideType);
            return add.Invoke(profile, new object[] { true });
        }

        /// <summary>Sets a VolumeComponent override field's nested VolumeParameter&lt;T&gt;.value/.overrideState. No-op if rawValue is null.</summary>
        private static void SetParam(object overrideInstance, string fieldName, object rawValue)
        {
            if (rawValue == null) return;
            var field = overrideInstance.GetType().GetField(fieldName);
            var param = field.GetValue(overrideInstance);
            param.GetType().GetProperty("value").SetValue(param, rawValue);
            param.GetType().GetProperty("overrideState").SetValue(param, true);
        }

        private static void SetColorChannels(object overrideInstance, string fieldName, float? r, float? g, float? b)
        {
            if (!r.HasValue && !g.HasValue && !b.HasValue) return;
            var field = overrideInstance.GetType().GetField(fieldName);
            var param = field.GetValue(overrideInstance);
            var valueProp = param.GetType().GetProperty("value");
            var current = (Color)valueProp.GetValue(param);
            if (r.HasValue) current.r = r.Value;
            if (g.HasValue) current.g = g.Value;
            if (b.HasValue) current.b = b.Value;
            valueProp.SetValue(param, current);
            param.GetType().GetProperty("overrideState").SetValue(param, true);
        }

        private static void SaveProfile(object profile)
        {
            var obj = (UnityEngine.Object)profile;
            EditorUtility.SetDirty(obj);
            AssetDatabase.SaveAssets();
        }

        [MCPTool("add_vignette", "Adds/tunes a Vignette override on a VolumeProfile: darkened edges for claustrophobic framing.", group: "rendering")]
        public static MCPResult AddVignette(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("Vignette strength (0-1). Omit to leave at default/unchanged.")] float? intensity = null,
            [MCPParam("Softness of the vignette's inner edge (0-1). Omit to leave unchanged.")] float? smoothness = null,
            [MCPParam("Vignette color red component (0-1). Omit to leave unchanged.")] float? colorR = null,
            [MCPParam("Vignette color green component (0-1). Omit to leave unchanged.")] float? colorG = null,
            [MCPParam("Vignette color blue component (0-1). Omit to leave unchanged.")] float? colorB = null,
            [MCPParam("Round the vignette to the screen aspect ratio instead of a perfect circle. Omit to leave unchanged.")] bool? rounded = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("Vignette", out var vignetteType, out var typeError)) return MCPResult.Fail(typeError);

            var vignette = EnsureOverride(profile, profileType, vignetteType);
            SetParam(vignette, "intensity", intensity);
            SetParam(vignette, "smoothness", smoothness);
            SetColorChannels(vignette, "color", colorR, colorG, colorB);
            SetParam(vignette, "rounded", rounded);
            SaveProfile(profile);

            return MCPResult.Success();
        }

        [MCPTool("add_bloom", "Adds/tunes a Bloom override on a VolumeProfile: light bleed and dread glow.", group: "rendering")]
        public static MCPResult AddBloom(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("Brightness threshold above which pixels start blooming. Omit to leave unchanged.")] float? threshold = null,
            [MCPParam("Bloom strength. Omit to leave unchanged.")] float? intensity = null,
            [MCPParam("How much bloom is scattered/spread out (0-1). Omit to leave unchanged.")] float? scatter = null,
            [MCPParam("Bloom tint red component (0-1). Omit to leave unchanged.")] float? tintR = null,
            [MCPParam("Bloom tint green component (0-1). Omit to leave unchanged.")] float? tintG = null,
            [MCPParam("Bloom tint blue component (0-1). Omit to leave unchanged.")] float? tintB = null,
            [MCPParam("Maximum brightness the bloom effect can reach. Omit to leave unchanged.")] float? clamp = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("Bloom", out var bloomType, out var typeError)) return MCPResult.Fail(typeError);

            var bloom = EnsureOverride(profile, profileType, bloomType);
            SetParam(bloom, "threshold", threshold);
            SetParam(bloom, "intensity", intensity);
            SetParam(bloom, "scatter", scatter);
            SetColorChannels(bloom, "tint", tintR, tintG, tintB);
            SetParam(bloom, "clamp", clamp);
            SaveProfile(profile);

            return MCPResult.Success();
        }

        [MCPTool("add_depth_of_field", "Adds/tunes a Depth of Field override on a VolumeProfile: blur to hide or reveal threats at a distance.", group: "rendering")]
        public static MCPResult AddDepthOfField(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("Blur technique: 'Gaussian' (cheap, background-only) or 'Bokeh' (physically-based, both directions). Defaults to 'Gaussian'.")] string mode = "Gaussian",
            [MCPParam("Distance from the camera that's in focus, Bokeh mode. Omit to leave unchanged.")] float? focusDistance = null,
            [MCPParam("Aperture (f-stop), Bokeh mode -- lower is blurrier. Omit to leave unchanged.")] float? aperture = null,
            [MCPParam("Focal length in mm, Bokeh mode. Omit to leave unchanged.")] float? focalLength = null,
            [MCPParam("Distance where background blur starts, Gaussian mode. Omit to leave unchanged.")] float? gaussianStart = null,
            [MCPParam("Distance where background blur reaches max radius, Gaussian mode. Omit to leave unchanged.")] float? gaussianEnd = null,
            [MCPParam("Maximum blur radius in screen percent, Gaussian mode. Omit to leave unchanged.")] float? gaussianMaxRadius = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("DepthOfField", out var dofType, out var typeError)) return MCPResult.Fail(typeError);

            var modeEnumType = UrpType("DepthOfFieldMode");
            if (modeEnumType == null) return MCPResult.Fail("Could not find URP type 'DepthOfFieldMode' via reflection.");
            if (!Enum.IsDefined(modeEnumType, mode)) return MCPResult.Fail($"Unknown mode '{mode}'. Valid values: {string.Join(", ", Enum.GetNames(modeEnumType))}.");

            var dof = EnsureOverride(profile, profileType, dofType);
            SetParam(dof, "mode", Enum.Parse(modeEnumType, mode));
            SetParam(dof, "focusDistance", focusDistance);
            SetParam(dof, "aperture", aperture);
            SetParam(dof, "focalLength", focalLength);
            SetParam(dof, "gaussianStart", gaussianStart);
            SetParam(dof, "gaussianEnd", gaussianEnd);
            SetParam(dof, "gaussianMaxRadius", gaussianMaxRadius);
            SaveProfile(profile);

            return MCPResult.Success();
        }

        [MCPTool("add_chromatic_aberration", "Adds/tunes a Chromatic Aberration override on a VolumeProfile: lens color fringing for unease.", group: "rendering")]
        public static MCPResult AddChromaticAberration(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("Effect strength (0-1). Omit to leave unchanged.")] float? intensity = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("ChromaticAberration", out var caType, out var typeError)) return MCPResult.Fail(typeError);

            var ca = EnsureOverride(profile, profileType, caType);
            SetParam(ca, "intensity", intensity);
            SaveProfile(profile);

            return MCPResult.Success();
        }

        [MCPTool("add_motion_blur", "Adds/tunes a Motion Blur override on a VolumeProfile: camera and/or object motion blur.", group: "rendering")]
        public static MCPResult AddMotionBlur(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("'CameraOnly' or 'CameraAndObjects'. Defaults to 'CameraOnly'.")] string mode = "CameraOnly",
            [MCPParam("Effect strength (0-1). Omit to leave unchanged.")] float? intensity = null,
            [MCPParam("Maximum velocity contribution, in screen percent. Omit to leave unchanged.")] float? clamp = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("MotionBlur", out var mbType, out var typeError)) return MCPResult.Fail(typeError);

            var modeEnumType = UrpType("MotionBlurMode");
            if (modeEnumType == null) return MCPResult.Fail("Could not find URP type 'MotionBlurMode' via reflection.");
            if (!Enum.IsDefined(modeEnumType, mode)) return MCPResult.Fail($"Unknown mode '{mode}'. Valid values: {string.Join(", ", Enum.GetNames(modeEnumType))}.");

            var mb = EnsureOverride(profile, profileType, mbType);
            SetParam(mb, "mode", Enum.Parse(modeEnumType, mode));
            SetParam(mb, "intensity", intensity);
            SetParam(mb, "clamp", clamp);
            SaveProfile(profile);

            return MCPResult.Success();
        }

        [MCPTool("add_lens_distortion", "Adds/tunes a Lens Distortion override on a VolumeProfile: screen warp for disorientation stingers.", group: "rendering")]
        public static MCPResult AddLensDistortion(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("Overall distortion strength (-1 to 1). Omit to leave unchanged.")] float? intensity = null,
            [MCPParam("Horizontal distortion multiplier (0-1). Omit to leave unchanged.")] float? xMultiplier = null,
            [MCPParam("Vertical distortion multiplier (0-1). Omit to leave unchanged.")] float? yMultiplier = null,
            [MCPParam("Screen scale to compensate for edge stretching (0.01-5). Omit to leave unchanged.")] float? scale = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("LensDistortion", out var ldType, out var typeError)) return MCPResult.Fail(typeError);

            var ld = EnsureOverride(profile, profileType, ldType);
            SetParam(ld, "intensity", intensity);
            SetParam(ld, "xMultiplier", xMultiplier);
            SetParam(ld, "yMultiplier", yMultiplier);
            SetParam(ld, "scale", scale);
            SaveProfile(profile);

            return MCPResult.Success();
        }

        [MCPTool("add_film_grain", "Adds/tunes a Film Grain override on a VolumeProfile: grain for a gritty, found-footage look.", group: "rendering")]
        public static MCPResult AddFilmGrain(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("Grain strength (0-1). Omit to leave unchanged.")] float? intensity = null,
            [MCPParam("How much the grain responds to image luminance (0-1). Omit to leave unchanged.")] float? response = null,
            [MCPParam("Built-in grain pattern: Thin1/Thin2/Medium1-6/Large01/Large02/Custom. Omit to leave unchanged.")] string type = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("FilmGrain", out var fgType, out var typeError)) return MCPResult.Fail(typeError);

            var fg = EnsureOverride(profile, profileType, fgType);
            SetParam(fg, "intensity", intensity);
            SetParam(fg, "response", response);
            if (type != null)
            {
                var lookupEnumType = UrpType("FilmGrainLookup");
                if (lookupEnumType == null) return MCPResult.Fail("Could not find URP type 'FilmGrainLookup' via reflection.");
                if (!Enum.IsDefined(lookupEnumType, type)) return MCPResult.Fail($"Unknown type '{type}'. Valid values: {string.Join(", ", Enum.GetNames(lookupEnumType))}.");
                SetParam(fg, "type", Enum.Parse(lookupEnumType, type));
            }
            SaveProfile(profile);

            return MCPResult.Success();
        }

        [MCPTool(
            "add_color_grading",
            "Adds/tunes color grading on a VolumeProfile for a sickly/desaturated palette: ColorAdjustments " +
            "(exposure/contrast/filter/hue/saturation) always, plus WhiteBalance (temperature/tint) and Tonemapping " +
            "mode when those specific parameters are given.",
            group: "rendering")]
        public static MCPResult AddColorGrading(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the VolumeProfile asset.")] string profileAssetPath,
            [MCPParam("Exposure adjustment in EV, applied after tonemapping. Omit to leave unchanged.")] float? postExposure = null,
            [MCPParam("Contrast (-100 to 100). Omit to leave unchanged.")] float? contrast = null,
            [MCPParam("Color filter red component (0-1). Omit to leave unchanged.")] float? colorFilterR = null,
            [MCPParam("Color filter green component (0-1). Omit to leave unchanged.")] float? colorFilterG = null,
            [MCPParam("Color filter blue component (0-1). Omit to leave unchanged.")] float? colorFilterB = null,
            [MCPParam("Hue shift (-180 to 180). Omit to leave unchanged.")] float? hueShift = null,
            [MCPParam("Saturation (-100 to 100). Omit to leave unchanged.")] float? saturation = null,
            [MCPParam("White balance temperature (-100 to 100, negative = cooler/blue, positive = warmer/orange). Adds a WhiteBalance override if given. Omit to skip.")] float? temperature = null,
            [MCPParam("White balance tint (-100 to 100, green/magenta). Adds a WhiteBalance override if given. Omit to skip.")] float? tint = null,
            [MCPParam("Tonemapping curve: 'None', 'Neutral', or 'ACES'. Adds a Tonemapping override if given. Omit to skip.")] string tonemappingMode = null)
        {
            var failResult = TryLoadProfile(profileAssetPath, out var profile, out var profileType);
            if (failResult != null) return failResult;
            if (!TryGetUrpType("ColorAdjustments", out var caType, out var typeError)) return MCPResult.Fail(typeError);

            var colorAdjustments = EnsureOverride(profile, profileType, caType);
            SetParam(colorAdjustments, "postExposure", postExposure);
            SetParam(colorAdjustments, "contrast", contrast);
            SetColorChannels(colorAdjustments, "colorFilter", colorFilterR, colorFilterG, colorFilterB);
            SetParam(colorAdjustments, "hueShift", hueShift);
            SetParam(colorAdjustments, "saturation", saturation);

            if (temperature.HasValue || tint.HasValue)
            {
                if (!TryGetUrpType("WhiteBalance", out var wbType, out var wbError)) return MCPResult.Fail(wbError);
                var whiteBalance = EnsureOverride(profile, profileType, wbType);
                SetParam(whiteBalance, "temperature", temperature);
                SetParam(whiteBalance, "tint", tint);
            }

            if (tonemappingMode != null)
            {
                if (!TryGetUrpType("Tonemapping", out var tmType, out var tmError)) return MCPResult.Fail(tmError);
                var tonemappingModeEnumType = UrpType("TonemappingMode");
                if (tonemappingModeEnumType == null) return MCPResult.Fail("Could not find URP type 'TonemappingMode' via reflection.");
                if (!Enum.IsDefined(tonemappingModeEnumType, tonemappingMode))
                    return MCPResult.Fail($"Unknown tonemappingMode '{tonemappingMode}'. Valid values: {string.Join(", ", Enum.GetNames(tonemappingModeEnumType))}.");
                var tonemapping = EnsureOverride(profile, profileType, tmType);
                SetParam(tonemapping, "mode", Enum.Parse(tonemappingModeEnumType, tonemappingMode));
            }

            SaveProfile(profile);
            return MCPResult.Success();
        }

        [MCPTool(
            "set_camera_clear_and_fog",
            "Ties a camera's clear flags/background color to RenderSettings fog for seamless darkness -- so distance " +
            "fog fades into the same color the camera clears to, rather than a visible horizon line.",
            group: "rendering")]
        public static MCPResult SetCameraClearAndFog(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Camera.")] string path,
            [MCPParam("Camera clear flags. Omit to leave unchanged.")] CameraClearFlags? clearFlags = null,
            [MCPParam("Background color red component (0-1). Ignored if syncBackgroundToFogColor is true. Omit to leave unchanged.")] float? backgroundColorR = null,
            [MCPParam("Background color green component (0-1). Ignored if syncBackgroundToFogColor is true. Omit to leave unchanged.")] float? backgroundColorG = null,
            [MCPParam("Background color blue component (0-1). Ignored if syncBackgroundToFogColor is true. Omit to leave unchanged.")] float? backgroundColorB = null,
            [MCPParam("If true, sets the camera's background color to RenderSettings.fogColor instead of the explicit backgroundColor* params. Defaults to false.")] bool syncBackgroundToFogColor = false)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            var camera = go.GetComponent<Camera>();
            if (camera == null) return MCPResult.Fail($"GameObject at '{path}' has no Camera component.");

            if (clearFlags.HasValue) camera.clearFlags = clearFlags.Value;

            if (syncBackgroundToFogColor)
            {
                camera.backgroundColor = RenderSettings.fogColor;
            }
            else if (backgroundColorR.HasValue || backgroundColorG.HasValue || backgroundColorB.HasValue)
            {
                var c = camera.backgroundColor;
                if (backgroundColorR.HasValue) c.r = backgroundColorR.Value;
                if (backgroundColorG.HasValue) c.g = backgroundColorG.Value;
                if (backgroundColorB.HasValue) c.b = backgroundColorB.Value;
                camera.backgroundColor = c;
            }

            return MCPResult.Success();
        }

        [MCPTool(
            "toggle_ssao",
            "Enables/disables a Screen Space Ambient Occlusion renderer feature on a URP Renderer Data asset, for " +
            "grounded contact shadows -- adds the feature if missing (real, native URP mechanism confirmed via " +
            "live spike: the same SerializedObject/m_RendererFeatures array manipulation URP's own inspector uses, " +
            "not a fragile internal-view-state hack). Its per-effect settings are internal-but-serialized, reached " +
            "via SerializedProperty paths (m_Settings.Intensity etc.) rather than reflection on private fields.",
            group: "rendering")]
        public static MCPResult ToggleSsao(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of the URP Renderer Data asset (e.g. a UniversalRendererData created via 'Create > Rendering > URP Renderer').")] string rendererDataAssetPath,
            [MCPParam("Whether SSAO should be active. Defaults to true.")] bool enabled = true,
            [MCPParam("AO strength. Omit to leave at default/unchanged.")] float? intensity = null,
            [MCPParam("Sample radius in world units. Omit to leave unchanged.")] float? radius = null,
            [MCPParam("Render AO at half resolution for performance. Omit to leave unchanged.")] bool? downsample = null)
        {
            if (!TryGetUrpType("ScreenSpaceAmbientOcclusion", out var ssaoType, out var ssaoTypeError))
                return MCPResult.Fail(ssaoTypeError);

            var rendererDataType = Type.GetType("UnityEngine.Rendering.Universal.ScriptableRendererData, Unity.RenderPipelines.Universal.Runtime");
            if (rendererDataType == null) return MCPResult.Fail("Could not find URP type 'ScriptableRendererData' via reflection.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, rendererDataAssetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (!File.Exists(fullPath))
                return MCPResult.Fail($"'{rendererDataAssetPath}' does not exist.");

            var unityPath = "Assets/" + rendererDataAssetPath.Replace('\\', '/').TrimStart('/');
            var rendererData = AssetDatabase.LoadAssetAtPath(unityPath, rendererDataType);
            if (rendererData == null) return MCPResult.Fail($"Could not load a URP Renderer Data asset at '{rendererDataAssetPath}'.");

            var featuresProp = rendererDataType.GetProperty("rendererFeatures", BindingFlags.Public | BindingFlags.Instance);
            var featuresList = ((System.Collections.IEnumerable)featuresProp.GetValue(rendererData)).Cast<UnityEngine.Object>().ToList();
            var feature = featuresList.FirstOrDefault(f => f != null && ssaoType.IsInstanceOfType(f));

            if (feature == null)
            {
                feature = (UnityEngine.Object)ScriptableObject.CreateInstance(ssaoType);
                feature.name = ssaoType.Name;
                Undo.RegisterCreatedObjectUndo(feature, "MCP: Add SSAO Renderer Feature");
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

                var so = new SerializedObject(rendererData);
                so.Update();
                var featuresArrayProp = so.FindProperty("m_RendererFeatures");
                var featuresMapProp = so.FindProperty("m_RendererFeatureMap");
                featuresArrayProp.arraySize++;
                featuresArrayProp.GetArrayElementAtIndex(featuresArrayProp.arraySize - 1).objectReferenceValue = feature;
                featuresMapProp.arraySize++;
                featuresMapProp.GetArrayElementAtIndex(featuresMapProp.arraySize - 1).longValue = localId;
                so.ApplyModifiedProperties();
            }

            var setActiveMethod = ssaoType.GetMethod("SetActive", BindingFlags.Public | BindingFlags.Instance);
            setActiveMethod.Invoke(feature, new object[] { enabled });

            var featureSo = new SerializedObject(feature);
            bool changedSettings = false;
            if (intensity.HasValue) { featureSo.FindProperty("m_Settings.Intensity").floatValue = intensity.Value; changedSettings = true; }
            if (radius.HasValue) { featureSo.FindProperty("m_Settings.Radius").floatValue = radius.Value; changedSettings = true; }
            if (downsample.HasValue) { featureSo.FindProperty("m_Settings.Downsample").boolValue = downsample.Value; changedSettings = true; }
            if (changedSettings) featureSo.ApplyModifiedProperties();

            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();

            return MCPResult.Success(new { added = feature != null, active = enabled });
        }
    }
}
