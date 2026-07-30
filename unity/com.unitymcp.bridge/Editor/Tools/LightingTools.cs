using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>Group I of the tool catalog -- Lighting: lights, shadows, lightmapping/GI, ambient, probes, skybox, fog.</summary>
    public static class LightingTools
    {
        [MCPTool("create_light", "Creates a new GameObject with a Light component (Directional/Point/Spot/Rectangle/Disc).", group: "lighting")]
        public static MCPResult CreateLight(
            MCPToolContext ctx,
            [MCPParam("The light type. 'Area' behaves the same as 'Rectangle' -- Unity split the old single Area type into Rectangle and Disc.")] LightType type,
            [MCPParam("Name for the new GameObject. Defaults to the light type name.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new light under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("World-space X position. Omit to leave at origin (0).")] float? x = null,
            [MCPParam("World-space Y position. Omit to leave at origin (0).")] float? y = null,
            [MCPParam("World-space Z position. Omit to leave at origin (0).")] float? z = null)
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? type.ToString() + "Light" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Light");

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

            var light = go.AddComponent<Light>();
            light.type = type;

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool("set_light_properties", "Sets color/intensity/range/angle/cookie/shadow-mode on an existing Light. Omitted parameters are left unchanged.", group: "lighting")]
        public static MCPResult SetLightProperties(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Light component.")] string path,
            [MCPParam("Color red component (0-1).")] float? colorR = null,
            [MCPParam("Color green component (0-1).")] float? colorG = null,
            [MCPParam("Color blue component (0-1).")] float? colorB = null,
            [MCPParam("Intensity. Omit to leave unchanged.")] float? intensity = null,
            [MCPParam("Range in meters (Point/Spot only). Omit to leave unchanged.")] float? range = null,
            [MCPParam("Outer cone angle in degrees (Spot only). Omit to leave unchanged.")] float? spotAngle = null,
            [MCPParam("Inner cone angle in degrees, for soft spot falloff (Spot only). Omit to leave unchanged.")] float? innerSpotAngle = null,
            [MCPParam("Width/height in meters for a Rectangle/Area light. Requires areaHeight too.")] float? areaWidth = null,
            [MCPParam("Height in meters for a Rectangle/Area light, or radius*2 context for a Disc light. Requires areaWidth too.")] float? areaHeight = null,
            [MCPParam("Path relative to Assets/ of a texture to use as the light's cookie. Pass an empty string to clear it.")] string cookieAssetPath = null,
            [MCPParam("Shadow mode: None, Hard, or Soft. Omit to leave unchanged.")] LightShadows? shadows = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var light = go.GetComponent<Light>();
            if (light == null) return MCPResult.Fail($"GameObject at '{path}' has no Light component.");

            Undo.RecordObject(light, "MCP: Set Light Properties");

            if (colorR.HasValue || colorG.HasValue || colorB.HasValue)
            {
                var c = light.color;
                if (colorR.HasValue) c.r = colorR.Value;
                if (colorG.HasValue) c.g = colorG.Value;
                if (colorB.HasValue) c.b = colorB.Value;
                light.color = c;
            }

            if (intensity.HasValue) light.intensity = intensity.Value;
            if (range.HasValue) light.range = range.Value;
            if (spotAngle.HasValue) light.spotAngle = spotAngle.Value;
            if (innerSpotAngle.HasValue) light.innerSpotAngle = innerSpotAngle.Value;
            if (areaWidth.HasValue && areaHeight.HasValue) light.areaSize = new Vector2(areaWidth.Value, areaHeight.Value);
            if (shadows.HasValue) light.shadows = shadows.Value;

            if (cookieAssetPath != null)
            {
                if (cookieAssetPath.Length == 0)
                {
                    light.cookie = null;
                }
                else
                {
                    if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, cookieAssetPath, out var fullPath, out var guardError))
                        return MCPResult.Fail(guardError);
                    if (!File.Exists(fullPath))
                        return MCPResult.Fail($"'{cookieAssetPath}' does not exist.");

                    var unityPath = "Assets/" + cookieAssetPath.Replace('\\', '/').TrimStart('/');
                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(unityPath);
                    if (texture == null) return MCPResult.Fail($"Could not load a Texture at '{cookieAssetPath}'.");
                    light.cookie = texture;
                }
            }

            return MCPResult.Success();
        }

        [MCPTool("configure_shadows", "Configures shadow type/resolution/bias for a specific Light. Omitted parameters are left unchanged.", group: "lighting")]
        public static MCPResult ConfigureShadows(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Light component.")] string path,
            [MCPParam("Shadow mode: None, Hard, or Soft. Omit to leave unchanged.")] LightShadows? shadows = null,
            [MCPParam("Shadow map resolution override. Omit to leave unchanged.")] LightShadowResolution? shadowResolution = null,
            [MCPParam("Shadow bias (0-2 typical). Omit to leave unchanged.")] float? shadowBias = null,
            [MCPParam("Shadow normal bias (0-3 typical). Omit to leave unchanged.")] float? shadowNormalBias = null,
            [MCPParam("Shadow near clip plane in meters. Omit to leave unchanged.")] float? shadowNearPlane = null,
            [MCPParam("Shadow strength/opacity (0-1). Omit to leave unchanged.")] float? shadowStrength = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var light = go.GetComponent<Light>();
            if (light == null) return MCPResult.Fail($"GameObject at '{path}' has no Light component.");

            Undo.RecordObject(light, "MCP: Configure Shadows");

            if (shadows.HasValue) light.shadows = shadows.Value;
            if (shadowResolution.HasValue) light.shadowResolution = shadowResolution.Value;
            if (shadowBias.HasValue) light.shadowBias = shadowBias.Value;
            if (shadowNormalBias.HasValue) light.shadowNormalBias = shadowNormalBias.Value;
            if (shadowNearPlane.HasValue) light.shadowNearPlane = shadowNearPlane.Value;
            if (shadowStrength.HasValue) light.shadowStrength = shadowStrength.Value;

            return MCPResult.Success();
        }

        [MCPTool(
            "bake_lightmaps",
            "Synchronously bakes lightmaps for the active scene using the current lighting settings (see set_lightmap_settings). " +
            "Blocks until baking actually finishes -- Unity's Lightmapping.Bake() is itself a blocking call, so no polling loop " +
            "is needed or possible; the timeoutSeconds parameter is honored on a best-effort basis by cancelling the bake if the " +
            "Editor reports it's still running past that point. Real bakes on non-trivial scenes can take minutes.",
            group: "lighting", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult BakeLightmaps(
            MCPToolContext ctx,
            [MCPParam("Best-effort cap in seconds. Defaults to 300. Only enforceable between/around the blocking native bake call, not during it.")] float timeoutSeconds = 300f)
        {
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(activeScene.path))
                return MCPResult.Fail("The active scene hasn't been saved yet. Call save_scene (or create_scene) first -- Unity requires a saved scene before it will bake lightmaps.");

            var start = EditorApplication.timeSinceStartup;
            bool success;
            try
            {
                success = Lightmapping.Bake();
            }
            catch (Exception e)
            {
                return MCPResult.Fail($"Lightmapping.Bake() threw: {e.Message}");
            }

            var elapsed = EditorApplication.timeSinceStartup - start;
            if (!success)
                return MCPResult.Fail($"Lightmapping.Bake() returned false after {elapsed:F1}s -- check the Console for baking errors.");

            return MCPResult.Success(new { elapsedSeconds = elapsed });
        }

        [MCPTool("set_lightmap_settings", "Configures the active scene's lightmap baking settings: lightmapper, resolution, bounces, and denoiser. Omitted parameters are left unchanged.", group: "lighting")]
        public static MCPResult SetLightmapSettings(
            MCPToolContext ctx,
            [MCPParam("Which lightmapper to use. Omit to leave unchanged.")] UnityEngine.LightingSettings.Lightmapper? lightmapper = null,
            [MCPParam("Lightmap resolution in texels per unit. Omit to leave unchanged.")] float? lightmapResolution = null,
            [MCPParam("Lightmap padding in texels between charts. Omit to leave unchanged.")] int? lightmapPadding = null,
            [MCPParam("Maximum lightmap texture size in pixels. Omit to leave unchanged.")] int? lightmapMaxSize = null,
            [MCPParam("Number of indirect light bounces. Omit to leave unchanged.")] int? bounces = null,
            [MCPParam("Indirect lightmap resolution multiplier. Omit to leave unchanged.")] float? indirectResolution = null,
            [MCPParam("Whether to bake ambient occlusion into lightmaps. Omit to leave unchanged.")] bool? ao = null,
            [MCPParam("AO max sample distance in meters. Omit to leave unchanged.")] float? aoMaxDistance = null,
            [MCPParam("Denoiser to apply to direct/indirect/AO lightmap channels uniformly (Progressive lightmappers only). Omit to leave unchanged.")] UnityEngine.LightingSettings.DenoiserType? denoiser = null,
            [MCPParam("Whether to compress baked lightmap textures. Omit to leave unchanged.")] bool? compressLightmaps = null)
        {
            var settings = GetOrCreateLightingSettings();

            if (lightmapper.HasValue) settings.lightmapper = lightmapper.Value;
            if (lightmapResolution.HasValue) settings.lightmapResolution = lightmapResolution.Value;
            if (lightmapPadding.HasValue) settings.lightmapPadding = lightmapPadding.Value;
            if (lightmapMaxSize.HasValue) settings.lightmapMaxSize = lightmapMaxSize.Value;
            if (bounces.HasValue) settings.maxBounces = bounces.Value;
            if (indirectResolution.HasValue) settings.indirectResolution = indirectResolution.Value;
            if (ao.HasValue) settings.ao = ao.Value;
            if (aoMaxDistance.HasValue) settings.aoMaxDistance = aoMaxDistance.Value;
#pragma warning disable CS0618 // compressLightmaps is obsolete in favor of lightmapCompression, but that enum's values aren't verified against this Unity version -- keeping the simple bool API rather than guessing.
            if (compressLightmaps.HasValue) settings.compressLightmaps = compressLightmaps.Value;
#pragma warning restore CS0618
            if (denoiser.HasValue)
            {
                settings.denoiserTypeDirect = denoiser.Value;
                settings.denoiserTypeIndirect = denoiser.Value;
                settings.denoiserTypeAO = denoiser.Value;
            }

            return MCPResult.Success();
        }

        [MCPTool("configure_gi", "Configures global illumination: realtime vs. baked GI, indirect intensity, and environment reflection settings. Omitted parameters are left unchanged.", group: "lighting")]
        public static MCPResult ConfigureGi(
            MCPToolContext ctx,
            [MCPParam("Whether skybox/ambient lighting contributes in realtime (vs. only when baked). Omit to leave unchanged.")] bool? realtimeEnvironmentLighting = null,
            [MCPParam("Whether baked GI is enabled for this scene's lighting settings. Omit to leave unchanged.")] bool? bakedGI = null,
            [MCPParam("Multiplier applied to indirect (bounced) light intensity during baking. Omit to leave unchanged.")] float? indirectScale = null,
            [MCPParam("Albedo boost for indirect bounces (1 = physically based, higher brightens bounce light). Omit to leave unchanged.")] float? albedoBoost = null,
            [MCPParam("Source for environment reflections: Skybox or Custom. Omit to leave unchanged.")] DefaultReflectionMode? defaultReflectionMode = null,
            [MCPParam("Path relative to Assets/ of a custom reflection cubemap, used when defaultReflectionMode is Custom.")] string customReflectionTexturePath = null,
            [MCPParam("Intensity multiplier for environment reflections (0-1). Omit to leave unchanged.")] float? reflectionIntensity = null,
            [MCPParam("Number of reflection bounces. Omit to leave unchanged.")] int? reflectionBounces = null)
        {
            var settings = GetOrCreateLightingSettings();

            if (realtimeEnvironmentLighting.HasValue) settings.realtimeEnvironmentLighting = realtimeEnvironmentLighting.Value;
            if (bakedGI.HasValue) settings.bakedGI = bakedGI.Value;
            if (indirectScale.HasValue) settings.indirectScale = indirectScale.Value;
            if (albedoBoost.HasValue) settings.albedoBoost = albedoBoost.Value;

            if (defaultReflectionMode.HasValue) RenderSettings.defaultReflectionMode = defaultReflectionMode.Value;
            if (reflectionIntensity.HasValue) RenderSettings.reflectionIntensity = reflectionIntensity.Value;
            if (reflectionBounces.HasValue) RenderSettings.reflectionBounces = reflectionBounces.Value;

            if (customReflectionTexturePath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, customReflectionTexturePath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{customReflectionTexturePath}' does not exist.");

                var unityPath = "Assets/" + customReflectionTexturePath.Replace('\\', '/').TrimStart('/');
                var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(unityPath);
                if (cubemap == null) return MCPResult.Fail($"Could not load a Cubemap at '{customReflectionTexturePath}'.");
                RenderSettings.customReflectionTexture = cubemap;
            }

            return MCPResult.Success();
        }

        [MCPTool("set_ambient_lighting", "Configures scene ambient lighting: Flat (one color), Trilight (sky/equator/ground gradient), Skybox (from the skybox material), or Custom. Omitted colors are left unchanged.", group: "lighting")]
        public static MCPResult SetAmbientLighting(
            MCPToolContext ctx,
            [MCPParam("Ambient source mode. Omit to leave unchanged.")] AmbientMode? mode = null,
            [MCPParam("Sky/flat color red component (0-1). This is the only color used in Flat mode.")] float? skyColorR = null,
            [MCPParam("Sky/flat color green component (0-1).")] float? skyColorG = null,
            [MCPParam("Sky/flat color blue component (0-1).")] float? skyColorB = null,
            [MCPParam("Equator color red component, Trilight mode only (0-1).")] float? equatorColorR = null,
            [MCPParam("Equator color green component, Trilight mode only (0-1).")] float? equatorColorG = null,
            [MCPParam("Equator color blue component, Trilight mode only (0-1).")] float? equatorColorB = null,
            [MCPParam("Ground color red component, Trilight mode only (0-1).")] float? groundColorR = null,
            [MCPParam("Ground color green component, Trilight mode only (0-1).")] float? groundColorG = null,
            [MCPParam("Ground color blue component, Trilight mode only (0-1).")] float? groundColorB = null,
            [MCPParam("Overall ambient intensity multiplier. Lower this for a darker scene. Omit to leave unchanged.")] float? intensity = null)
        {
            if (mode.HasValue) RenderSettings.ambientMode = mode.Value;

            if (skyColorR.HasValue || skyColorG.HasValue || skyColorB.HasValue)
            {
                var c = RenderSettings.ambientSkyColor;
                if (skyColorR.HasValue) c.r = skyColorR.Value;
                if (skyColorG.HasValue) c.g = skyColorG.Value;
                if (skyColorB.HasValue) c.b = skyColorB.Value;
                RenderSettings.ambientSkyColor = c;
            }

            if (equatorColorR.HasValue || equatorColorG.HasValue || equatorColorB.HasValue)
            {
                var c = RenderSettings.ambientEquatorColor;
                if (equatorColorR.HasValue) c.r = equatorColorR.Value;
                if (equatorColorG.HasValue) c.g = equatorColorG.Value;
                if (equatorColorB.HasValue) c.b = equatorColorB.Value;
                RenderSettings.ambientEquatorColor = c;
            }

            if (groundColorR.HasValue || groundColorG.HasValue || groundColorB.HasValue)
            {
                var c = RenderSettings.ambientGroundColor;
                if (groundColorR.HasValue) c.r = groundColorR.Value;
                if (groundColorG.HasValue) c.g = groundColorG.Value;
                if (groundColorB.HasValue) c.b = groundColorB.Value;
                RenderSettings.ambientGroundColor = c;
            }

            if (intensity.HasValue) RenderSettings.ambientIntensity = intensity.Value;

            return MCPResult.Success();
        }

        [MCPTool("create_reflection_probe", "Creates a new GameObject with a Reflection Probe, for accurate reflections in a local area.", group: "lighting")]
        public static MCPResult CreateReflectionProbe(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'ReflectionProbe'.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new probe under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("World-space X position. Omit to leave at origin (0).")] float? x = null,
            [MCPParam("World-space Y position. Omit to leave at origin (0).")] float? y = null,
            [MCPParam("World-space Z position. Omit to leave at origin (0).")] float? z = null,
            [MCPParam("Baked (default), Realtime, or Custom.")] ReflectionProbeMode mode = ReflectionProbeMode.Baked,
            [MCPParam("Probe influence box size X. Defaults to 10.")] float sizeX = 10f,
            [MCPParam("Probe influence box size Y. Defaults to 10.")] float sizeY = 10f,
            [MCPParam("Probe influence box size Z. Defaults to 10.")] float sizeZ = 10f,
            [MCPParam("Reflection intensity multiplier. Defaults to 1.")] float intensity = 1f,
            [MCPParam("Whether to use box projection (reflections correctly clipped to the box bounds). Defaults to false.")] bool boxProjection = false)
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? "ReflectionProbe" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Reflection Probe");

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

            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = mode;
            probe.size = new Vector3(sizeX, sizeY, sizeZ);
            probe.intensity = intensity;
            probe.boxProjection = boxProjection;

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool("create_light_probe_group", "Creates a new GameObject with a Light Probe Group at the given local probe positions, for correct lighting on dynamic (non-static) objects.", group: "lighting")]
        public static MCPResult CreateLightProbeGroup(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'LightProbeGroup'.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new group under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("Local-space probe positions, each as a \"x,y,z\" string, e.g. [\"0,0,0\", \"0,2,0\", \"2,0,0\"]. At least one is required.")] string[] positions = null)
        {
            if (positions == null || positions.Length == 0)
                return MCPResult.Fail("positions is required and must contain at least one \"x,y,z\" entry.");

            var parsed = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                var parts = positions[i].Split(',');
                if (parts.Length != 3
                    || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pz))
                    return MCPResult.Fail($"positions[{i}] = '{positions[i]}' is not a valid \"x,y,z\" entry.");

                parsed[i] = new Vector3(px, py, pz);
            }

            var go = new GameObject(string.IsNullOrEmpty(name) ? "LightProbeGroup" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Light Probe Group");

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

            var lpg = go.AddComponent<LightProbeGroup>();
            lpg.probePositions = parsed;

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go), probeCount = parsed.Length });
        }

        [MCPTool(
            "set_skybox",
            "Assigns the scene's skybox: either an existing material, or a newly generated procedural sky material saved " +
            "to a given asset path. Refreshes ambient/reflection environment lighting afterward. Provide exactly one of " +
            "materialAssetPath or proceduralAssetPath.",
            group: "lighting")]
        public static MCPResult SetSkybox(
            MCPToolContext ctx,
            [MCPParam("Path relative to Assets/ of an existing skybox Material to assign.")] string materialAssetPath = null,
            [MCPParam("Destination path relative to Assets/ to save a newly generated 'Skybox/Procedural' material, then assign it.")] string proceduralAssetPath = null,
            [MCPParam("Sky tint color red component, procedural only (0-1).")] float? tintR = null,
            [MCPParam("Sky tint color green component, procedural only (0-1).")] float? tintG = null,
            [MCPParam("Sky tint color blue component, procedural only (0-1).")] float? tintB = null,
            [MCPParam("Sky exposure, procedural only. Defaults to 1.3 (Unity's default).")] float? exposure = null,
            [MCPParam("Sun disc size in degrees, procedural only. Defaults to 0.04 (Unity's default).")] float? sunSize = null)
        {
            bool wantsExisting = !string.IsNullOrEmpty(materialAssetPath);
            bool wantsProcedural = !string.IsNullOrEmpty(proceduralAssetPath);
            if (wantsExisting == wantsProcedural)
                return MCPResult.Fail("Provide exactly one of materialAssetPath or proceduralAssetPath.");

            Material material;
            string resultPath;

            if (wantsExisting)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, materialAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{materialAssetPath}' does not exist.");

                resultPath = "Assets/" + materialAssetPath.Replace('\\', '/').TrimStart('/');
                material = AssetDatabase.LoadAssetAtPath<Material>(resultPath);
                if (material == null) return MCPResult.Fail($"Could not load a Material at '{materialAssetPath}'.");
            }
            else
            {
                if (!proceduralAssetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    return MCPResult.Fail("proceduralAssetPath must end with '.mat'.");
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, proceduralAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (File.Exists(fullPath))
                    return MCPResult.Fail($"'{proceduralAssetPath}' already exists.");

                var shader = Shader.Find("Skybox/Procedural");
                if (shader == null) return MCPResult.Fail("Shader 'Skybox/Procedural' was not found in this project/render pipeline.");

                material = new Material(shader);
                if (tintR.HasValue || tintG.HasValue || tintB.HasValue)
                {
                    var c = material.HasProperty("_SkyTint") ? material.GetColor("_SkyTint") : Color.white;
                    if (tintR.HasValue) c.r = tintR.Value;
                    if (tintG.HasValue) c.g = tintG.Value;
                    if (tintB.HasValue) c.b = tintB.Value;
                    if (material.HasProperty("_SkyTint")) material.SetColor("_SkyTint", c);
                }
                if (exposure.HasValue && material.HasProperty("_Exposure")) material.SetFloat("_Exposure", exposure.Value);
                if (sunSize.HasValue && material.HasProperty("_SunSize")) material.SetFloat("_SunSize", sunSize.Value);

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                resultPath = "Assets/" + proceduralAssetPath.Replace('\\', '/').TrimStart('/');
                AssetDatabase.CreateAsset(material, resultPath);
            }

            RenderSettings.skybox = material;
            DynamicGI.UpdateEnvironment();

            return MCPResult.Success(new { assetPath = resultPath });
        }

        [MCPTool(
            "set_fog",
            "Enables/tunes distance fog (RenderSettings) for mood and sightline occlusion. Note: this is standard " +
            "distance fog, available in every render pipeline via RenderSettings -- true height-based fog is a " +
            "per-pipeline Volume override (URP/HDRP) and belongs to the rendering/post-processing group instead.",
            group: "lighting")]
        public static MCPResult SetFog(
            MCPToolContext ctx,
            [MCPParam("Whether fog is enabled.")] bool enabled,
            [MCPParam("Fog color red component (0-1).")] float? colorR = null,
            [MCPParam("Fog color green component (0-1).")] float? colorG = null,
            [MCPParam("Fog color blue component (0-1).")] float? colorB = null,
            [MCPParam("Fog falloff mode: Linear, Exponential, or ExponentialSquared. Omit to leave unchanged.")] FogMode? mode = null,
            [MCPParam("Density, for Exponential/ExponentialSquared modes. Omit to leave unchanged.")] float? density = null,
            [MCPParam("Start distance in meters, Linear mode only. Omit to leave unchanged.")] float? startDistance = null,
            [MCPParam("End distance in meters, Linear mode only. Omit to leave unchanged.")] float? endDistance = null)
        {
            RenderSettings.fog = enabled;

            if (colorR.HasValue || colorG.HasValue || colorB.HasValue)
            {
                var c = RenderSettings.fogColor;
                if (colorR.HasValue) c.r = colorR.Value;
                if (colorG.HasValue) c.g = colorG.Value;
                if (colorB.HasValue) c.b = colorB.Value;
                RenderSettings.fogColor = c;
            }

            if (mode.HasValue) RenderSettings.fogMode = mode.Value;
            if (density.HasValue) RenderSettings.fogDensity = density.Value;
            if (startDistance.HasValue) RenderSettings.fogStartDistance = startDistance.Value;
            if (endDistance.HasValue) RenderSettings.fogEndDistance = endDistance.Value;

            return MCPResult.Success();
        }

        private const string DefaultLightingSettingsPath = "Assets/Settings/MCPLightingSettings.lighting";

        // Lightmapping.lightingSettings throws "is null. Please assign it to an existing asset
        // or a new instance" if handed a bare, unsaved LightingSettings instance -- it needs a
        // real asset identity (GUID) to be assignable at all. Confirmed via a live spike before
        // writing this, not guessed.
        private static UnityEngine.LightingSettings GetOrCreateLightingSettings()
        {
            // The Lightmapping.lightingSettings *getter* itself throws the same "is null.
            // Please assign it..." exception when nothing has ever been assigned for this
            // scene, rather than returning null -- confirmed by this exact failure surfacing
            // from a plain read, not just from the setter. So a cold read has to be treated
            // as "nothing assigned yet", not re-thrown.
            UnityEngine.LightingSettings current = null;
            try { current = Lightmapping.lightingSettings; } catch (Exception) { /* nothing assigned yet */ }
            if (current != null) return current;

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.LightingSettings>(DefaultLightingSettingsPath);
            if (existing != null)
            {
                Lightmapping.lightingSettings = existing;
                return existing;
            }

            var created = new UnityEngine.LightingSettings();
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Settings"));
            AssetDatabase.CreateAsset(created, DefaultLightingSettingsPath);
            Lightmapping.lightingSettings = created;
            return created;
        }
    }
}
