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
    /// Group S of the tool catalog -- Particles &amp; VFX. `ParticleSystem`/`TrailRenderer` are core Unity (no
    /// reflection needed); `add_decal` (URP `DecalProjector`) and `create_vfx_graph` (Visual Effect Graph package)
    /// are optional-package types resolved via reflection, same pattern as Cinemachine/RenderingTools.cs.
    /// `create_fog_volume` is a deliberate, documented scope call: this URP version (17.0.4, verified live) has no
    /// native "Local Volumetric Fog" volume type -- that's an HDRP/newer-URP-only feature -- so it's built instead
    /// from a real, working soft-particle cloud technique.
    /// </summary>
    public static class VfxTools
    {
        private const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static bool TryGetUrpType(string shortName, out Type type, out string error)
        {
            type = Type.GetType($"UnityEngine.Rendering.Universal.{shortName}, Unity.RenderPipelines.Universal.Runtime");
            if (type == null)
            {
                error = $"Could not find URP type '{shortName}' -- the Universal Render Pipeline package (com.unity.render-pipelines.universal) doesn't appear to be installed in this project.";
                return false;
            }
            error = null;
            return true;
        }

        [MCPTool("create_particle_system", "Creates a new GameObject with a configured ParticleSystem (main module + shape + emission rate).", group: "vfx")]
        public static MCPResult CreateParticleSystem(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'ParticleSystem'.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new system under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("World-space X position. Omit to leave at origin (0).")] float? x = null,
            [MCPParam("World-space Y position. Omit to leave at origin (0).")] float? y = null,
            [MCPParam("World-space Z position. Omit to leave at origin (0).")] float? z = null,
            [MCPParam("System duration in seconds. Defaults to 5.")] float duration = 5f,
            [MCPParam("Whether the system loops. Defaults to true.")] bool looping = true,
            [MCPParam("Particle lifetime in seconds. Defaults to 5.")] float startLifetime = 5f,
            [MCPParam("Initial particle speed. Defaults to 5.")] float startSpeed = 5f,
            [MCPParam("Initial particle size. Defaults to 1.")] float startSize = 1f,
            [MCPParam("Start color red component (0-1). Defaults to 1.")] float startColorR = 1f,
            [MCPParam("Start color green component (0-1). Defaults to 1.")] float startColorG = 1f,
            [MCPParam("Start color blue component (0-1). Defaults to 1.")] float startColorB = 1f,
            [MCPParam("Start color alpha component (0-1). Defaults to 1.")] float startColorA = 1f,
            [MCPParam("Maximum simultaneous particles. Defaults to 1000.")] int maxParticles = 1000,
            [MCPParam("Local (moves with the GameObject) or World simulation space. Defaults to Local.")] ParticleSystemSimulationSpace simulationSpace = ParticleSystemSimulationSpace.Local,
            [MCPParam("Emitter shape. Defaults to Cone.")] ParticleSystemShapeType shapeType = ParticleSystemShapeType.Cone,
            [MCPParam("Shape radius. Omit for Unity's default.")] float? shapeRadius = null,
            [MCPParam("Particles emitted per second. Defaults to 10.")] float rateOverTime = 10f,
            [MCPParam("Path relative to Assets/ of a Material for the ParticleSystemRenderer. Omit for Unity's default particle material.")] string materialAssetPath = null)
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? "ParticleSystem" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Particle System");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = MCPSceneUtil.ResolvePath(parentPath);
                if (parent == null) { UnityEngine.Object.DestroyImmediate(go); return MCPResult.Fail($"Parent path '{parentPath}' not found."); }
                go.transform.SetParent(parent.transform, worldPositionStays: false);
            }
            go.transform.localPosition = new Vector3(x ?? 0f, y ?? 0f, z ?? 0f);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = duration;
            main.loop = looping;
            main.startLifetime = startLifetime;
            main.startSpeed = startSpeed;
            main.startSize = startSize;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(startColorR, startColorG, startColorB, startColorA));
            main.maxParticles = maxParticles;
            main.simulationSpace = simulationSpace;

            var shape = ps.shape;
            shape.shapeType = shapeType;
            if (shapeRadius.HasValue) shape.radius = shapeRadius.Value;

            var emission = ps.emission;
            emission.rateOverTime = rateOverTime;

            if (materialAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, materialAssetPath, out var fullPath, out var guardError))
                { UnityEngine.Object.DestroyImmediate(go); return MCPResult.Fail(guardError); }
                if (!File.Exists(fullPath))
                { UnityEngine.Object.DestroyImmediate(go); return MCPResult.Fail($"'{materialAssetPath}' does not exist."); }
                var unityPath = "Assets/" + materialAssetPath.Replace('\\', '/').TrimStart('/');
                var material = AssetDatabase.LoadAssetAtPath<Material>(unityPath);
                if (material == null) { UnityEngine.Object.DestroyImmediate(go); return MCPResult.Fail($"Could not load a Material at '{materialAssetPath}'."); }
                go.GetComponent<ParticleSystemRenderer>().material = material;
            }

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool(
            "set_particle_module",
            "Edits Emission/Shape/ColorOverLifetime/Noise modules on an existing ParticleSystem. Omitted parameters are left unchanged.",
            group: "vfx")]
        public static MCPResult SetParticleModule(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the ParticleSystem.")] string path,
            [MCPParam("Enable/disable the Emission module. Omit to leave unchanged.")] bool? emissionEnabled = null,
            [MCPParam("Particles emitted per second. Omit to leave unchanged.")] float? rateOverTime = null,
            [MCPParam("Emitter shape. Omit to leave unchanged.")] ParticleSystemShapeType? shapeType = null,
            [MCPParam("Shape radius. Omit to leave unchanged.")] float? shapeRadius = null,
            [MCPParam("Shape cone/arc angle in degrees. Omit to leave unchanged.")] float? shapeAngle = null,
            [MCPParam("Enable/disable the Color Over Lifetime module. Omit to leave unchanged.")] bool? colorOverLifetimeEnabled = null,
            [MCPParam("Alpha at the start of a particle's life (0-1). Setting this or colorOverLifetimeEndAlpha enables the module.")] float? colorOverLifetimeStartAlpha = null,
            [MCPParam("Alpha at the end of a particle's life (0-1). Setting this or colorOverLifetimeStartAlpha enables the module.")] float? colorOverLifetimeEndAlpha = null,
            [MCPParam("Enable/disable the Noise module. Omit to leave unchanged.")] bool? noiseEnabled = null,
            [MCPParam("Noise strength. Setting this enables the module.")] float? noiseStrength = null,
            [MCPParam("Noise frequency. Setting this enables the module.")] float? noiseFrequency = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) return MCPResult.Fail($"GameObject at '{path}' has no ParticleSystem component.");

            if (emissionEnabled.HasValue || rateOverTime.HasValue)
            {
                var emission = ps.emission;
                if (emissionEnabled.HasValue) emission.enabled = emissionEnabled.Value;
                if (rateOverTime.HasValue) emission.rateOverTime = rateOverTime.Value;
            }

            if (shapeType.HasValue || shapeRadius.HasValue || shapeAngle.HasValue)
            {
                var shape = ps.shape;
                if (shapeType.HasValue) shape.shapeType = shapeType.Value;
                if (shapeRadius.HasValue) shape.radius = shapeRadius.Value;
                if (shapeAngle.HasValue) shape.angle = shapeAngle.Value;
            }

            if (colorOverLifetimeEnabled.HasValue || colorOverLifetimeStartAlpha.HasValue || colorOverLifetimeEndAlpha.HasValue)
            {
                var colorOverLifetime = ps.colorOverLifetime;
                if (colorOverLifetimeStartAlpha.HasValue || colorOverLifetimeEndAlpha.HasValue)
                {
                    colorOverLifetime.enabled = true;
                    var gradient = new Gradient();
                    gradient.SetKeys(
                        new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                        new[] { new GradientAlphaKey(colorOverLifetimeStartAlpha ?? 1f, 0f), new GradientAlphaKey(colorOverLifetimeEndAlpha ?? 1f, 1f) });
                    colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);
                }
                if (colorOverLifetimeEnabled.HasValue) colorOverLifetime.enabled = colorOverLifetimeEnabled.Value;
            }

            if (noiseEnabled.HasValue || noiseStrength.HasValue || noiseFrequency.HasValue)
            {
                var noise = ps.noise;
                if (noiseStrength.HasValue || noiseFrequency.HasValue) noise.enabled = true;
                if (noiseStrength.HasValue) noise.strength = noiseStrength.Value;
                if (noiseFrequency.HasValue) noise.frequency = noiseFrequency.Value;
                if (noiseEnabled.HasValue) noise.enabled = noiseEnabled.Value;
            }

            return MCPResult.Success();
        }

        [MCPTool("play_particle_system", "Plays/stops/pauses/clears an existing ParticleSystem, for verification.", group: "vfx", latencyTier: MCPLatencyTier.Fast)]
        public static MCPResult PlayParticleSystem(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the ParticleSystem.")] string path,
            [MCPParam("'Play', 'Stop', 'Pause', or 'Clear'. Defaults to 'Play'.")] string action = "Play",
            [MCPParam("Apply the action to child particle systems too. Defaults to true.")] bool withChildren = true)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) return MCPResult.Fail($"GameObject at '{path}' has no ParticleSystem component.");

            switch (action)
            {
                case "Play": ps.Play(withChildren); break;
                case "Stop": ps.Stop(withChildren, ParticleSystemStopBehavior.StopEmittingAndClear); break;
                case "Pause": ps.Pause(withChildren); break;
                case "Clear": ps.Clear(withChildren); break;
                default: return MCPResult.Fail($"Unknown action '{action}'. Valid values: Play, Stop, Pause, Clear.");
            }

            return MCPResult.Success(new { isPlaying = ps.isPlaying, particleCount = ps.particleCount });
        }

        [MCPTool(
            "create_vfx_graph",
            "Creates a new, blank VFX Graph asset via the real Editor API (UnityEditor.VisualEffectAssetEditorUtility." +
            "CreateNewAsset, confirmed via live spike to produce a genuinely loadable VisualEffectAsset). Requires the " +
            "Visual Effect Graph package (com.unity.visualeffectgraph); fails clearly if it isn't installed.",
            group: "vfx")]
        public static MCPResult CreateVfxGraph(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'VFX/BloodMist.vfx'.")] string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.vfx'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            var utilType = Type.GetType("UnityEditor.VisualEffectAssetEditorUtility, Unity.VisualEffectGraph.Editor");
            if (utilType == null)
                return MCPResult.Fail("The Visual Effect Graph package (com.unity.visualeffectgraph) is not installed in this project.");
            var createMethod = utilType.GetMethod("CreateNewAsset", AllStatic);
            if (createMethod == null)
                return MCPResult.Fail("Could not find VisualEffectAssetEditorUtility.CreateNewAsset via reflection -- this Unity version's VFX Graph API may have changed.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');

            object asset;
            try
            {
                asset = createMethod.Invoke(null, new object[] { unityAssetPath });
            }
            catch (TargetInvocationException e)
            {
                return MCPResult.Fail($"VisualEffectAssetEditorUtility.CreateNewAsset threw: {e.InnerException?.Message ?? e.Message}");
            }
            if (asset == null) return MCPResult.Fail("VisualEffectAssetEditorUtility.CreateNewAsset returned null.");

            AssetDatabase.SaveAssets();
            return MCPResult.Success(new { assetPath = unityAssetPath });
        }

        [MCPTool(
            "add_decal",
            "Adds/tunes a URP DecalProjector on an existing GameObject (blood, grime, cracks projected onto nearby " +
            "surfaces along -Z). Requires URP; fails clearly if unavailable.",
            group: "vfx")]
        public static MCPResult AddDecal(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject -- position/rotation controls where and which way the decal projects.")] string path,
            [MCPParam("Path relative to Assets/ of a decal Material (typically using the URP Decal shader). Omit to leave unassigned.")] string materialAssetPath = null,
            [MCPParam("Projector box width. Omit to leave unchanged.")] float? sizeX = null,
            [MCPParam("Projector box height. Omit to leave unchanged.")] float? sizeY = null,
            [MCPParam("Projector box depth (projection distance along -Z). Omit to leave unchanged.")] float? sizeZ = null,
            [MCPParam("Distance-based fade-out factor (0-1). Omit to leave unchanged.")] float? fadeFactor = null,
            [MCPParam("Maximum distance the decal is rendered at. Omit to leave unchanged.")] float? drawDistance = null)
        {
            if (!TryGetUrpType("DecalProjector", out var decalType, out var typeError))
                return MCPResult.Fail(typeError);

            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var decal = go.GetComponent(decalType);
            if (decal == null) decal = go.AddComponent(decalType);

            if (materialAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, materialAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{materialAssetPath}' does not exist.");
                var unityPath = "Assets/" + materialAssetPath.Replace('\\', '/').TrimStart('/');
                var material = AssetDatabase.LoadAssetAtPath<Material>(unityPath);
                if (material == null) return MCPResult.Fail($"Could not load a Material at '{materialAssetPath}'.");
                decalType.GetProperty("material").SetValue(decal, material);
            }

            if (sizeX.HasValue || sizeY.HasValue || sizeZ.HasValue)
            {
                var sizeProp = decalType.GetProperty("size");
                var size = (Vector3)sizeProp.GetValue(decal);
                if (sizeX.HasValue) size.x = sizeX.Value;
                if (sizeY.HasValue) size.y = sizeY.Value;
                if (sizeZ.HasValue) size.z = sizeZ.Value;
                sizeProp.SetValue(decal, size);
            }

            if (fadeFactor.HasValue) decalType.GetProperty("fadeFactor").SetValue(decal, fadeFactor.Value);
            if (drawDistance.HasValue) decalType.GetProperty("drawDistance").SetValue(decal, drawDistance.Value);

            return MCPResult.Success();
        }

        [MCPTool(
            "create_fog_volume",
            "Creates a local fog pocket as a dense, slow-drifting soft-particle cloud. This Unity/URP version has no " +
            "native 'Local Volumetric Fog' volume type (an HDRP/newer-URP-only feature, confirmed absent via live " +
            "spike against this project's installed URP package) -- this is a real, working particle-based " +
            "approximation instead, using Unity's built-in alpha-blended particle material.",
            group: "vfx")]
        public static MCPResult CreateFogVolume(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'FogVolume'.")] string name = null,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new fog pocket under. Omit to create at scene root.")] string parentPath = null,
            [MCPParam("World-space X position. Omit to leave at origin (0).")] float? x = null,
            [MCPParam("World-space Y position. Omit to leave at origin (0).")] float? y = null,
            [MCPParam("World-space Z position. Omit to leave at origin (0).")] float? z = null,
            [MCPParam("Radius of the fog pocket in world units. Defaults to 5.")] float radius = 5f,
            [MCPParam("Fog thickness (0-1), drives particle alpha and count. Defaults to 0.5.")] float density = 0.5f,
            [MCPParam("Fog color red component (0-1). Defaults to 0.8 (light grey).")] float colorR = 0.8f,
            [MCPParam("Fog color green component (0-1). Defaults to 0.8 (light grey).")] float colorG = 0.8f,
            [MCPParam("Fog color blue component (0-1). Defaults to 0.8 (light grey).")] float colorB = 0.8f)
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? "FogVolume" : name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Fog Volume");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = MCPSceneUtil.ResolvePath(parentPath);
                if (parent == null) { UnityEngine.Object.DestroyImmediate(go); return MCPResult.Fail($"Parent path '{parentPath}' not found."); }
                go.transform.SetParent(parent.transform, worldPositionStays: false);
            }
            go.transform.localPosition = new Vector3(x ?? 0f, y ?? 0f, z ?? 0f);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.startLifetime = 8f;
            main.startSpeed = 0.15f;
            main.startSize = Mathf.Max(1f, radius * 0.6f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(colorR, colorG, colorB, Mathf.Clamp01(density)));
            main.maxParticles = 200;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = radius;

            var emission = ps.emission;
            emission.rateOverTime = Mathf.Clamp(density * 12f, 1f, 40f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.material = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Particle.mat");

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool("create_trail", "Adds/tunes a TrailRenderer on an existing GameObject, for projectiles/entities.", group: "vfx")]
        public static MCPResult CreateTrail(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("How long, in seconds, the trail persists. Defaults to 1.")] float time = 1f,
            [MCPParam("Trail width at the emitting end. Defaults to 0.2.")] float startWidth = 0.2f,
            [MCPParam("Trail width at the fading end. Defaults to 0.")] float endWidth = 0f,
            [MCPParam("Path relative to Assets/ of a Material for the trail. Omit for Unity's default.")] string materialAssetPath = null,
            [MCPParam("Trail color red component (0-1), applied to both start and end. Omit to leave unchanged.")] float? colorR = null,
            [MCPParam("Trail color green component (0-1), applied to both start and end. Omit to leave unchanged.")] float? colorG = null,
            [MCPParam("Trail color blue component (0-1), applied to both start and end. Omit to leave unchanged.")] float? colorB = null,
            [MCPParam("Trail color alpha component (0-1), applied to both start and end. Omit to leave unchanged.")] float? colorA = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var trail = go.GetComponent<TrailRenderer>();
            if (trail == null) trail = go.AddComponent<TrailRenderer>();

            trail.time = time;
            trail.startWidth = startWidth;
            trail.endWidth = endWidth;

            if (materialAssetPath != null)
            {
                if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, materialAssetPath, out var fullPath, out var guardError))
                    return MCPResult.Fail(guardError);
                if (!File.Exists(fullPath))
                    return MCPResult.Fail($"'{materialAssetPath}' does not exist.");
                var unityPath = "Assets/" + materialAssetPath.Replace('\\', '/').TrimStart('/');
                var material = AssetDatabase.LoadAssetAtPath<Material>(unityPath);
                if (material == null) return MCPResult.Fail($"Could not load a Material at '{materialAssetPath}'.");
                trail.material = material;
            }

            if (colorR.HasValue || colorG.HasValue || colorB.HasValue || colorA.HasValue)
            {
                var start = trail.startColor;
                var end = trail.endColor;
                if (colorR.HasValue) { start.r = colorR.Value; end.r = colorR.Value; }
                if (colorG.HasValue) { start.g = colorG.Value; end.g = colorG.Value; }
                if (colorB.HasValue) { start.b = colorB.Value; end.b = colorB.Value; }
                if (colorA.HasValue) { start.a = colorA.Value; end.a = colorA.Value; }
                trail.startColor = start;
                trail.endColor = end;
            }

            return MCPResult.Success();
        }
    }
}
