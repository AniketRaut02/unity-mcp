using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group AC of the tool catalog -- Build, Project &amp; Packages, the final ship batch. build_player's
    /// BuildPipeline.BuildPlayer() call and its BuildReport contract (result/totalErrors/totalWarnings/totalTime/
    /// totalSize/outputPath) were confirmed via a real end-to-end build in the verify project -- it genuinely
    /// failed (a pre-existing ShaderGraph assembly-resolution issue in that scratch project, unrelated to this
    /// tool), which was itself useful confirmation that failures come back as real, structured BuildReport data
    /// rather than an uncaught exception. manage_packages' Client.List/Add/Remove/Search all return async
    /// *Request objects -- confirmed via live spike that a bounded spin-wait (Thread.Sleep in a loop checking
    /// IsCompleted) resolves correctly in a few hundred ms without deadlocking the Editor's main thread, unlike
    /// the domain-reload-driven tools elsewhere in this codebase that require the agent to poll via a separate
    /// LOOP tool instead. manage_project_settings' tag/layer editing uses the well-known SerializedObject-over-
    /// ProjectSettings/TagManager.asset technique (there's no other public API for it), confirmed via spike that
    /// edits actually persist and are visible via InternalEditorUtility.tags afterward.
    /// </summary>
    public static class BuildTools
    {
        [MCPTool(
            "build_player",
            "Builds a Player via BuildPipeline.BuildPlayer, using every enabled scene already in Build Settings " +
            "(see add_scene_to_build/list_scenes_in_build, scene group) unless scenePaths overrides that. Reports " +
            "real success/failure with error/warning counts, build time, and output size -- a failed build is " +
            "reported as ok:true with succeeded:false and the real error count, not a tool-call failure, since a " +
            "build failing is meaningful data, not a broken tool call.",
            group: "build", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult BuildPlayer(
            MCPToolContext ctx,
            [MCPParam("Output path for the built player, relative to the project root, e.g. 'Builds/Windows/Game.exe'.")] string outputPath,
            [MCPParam("Build target, e.g. 'StandaloneWindows64', 'StandaloneOSX', 'Android'. Omit to use the Editor's currently active build target.")] string target = null,
            [MCPParam("Scene paths relative to Assets/ to include. Omit to use every enabled scene already in Build Settings.")] string[] scenePaths = null,
            [MCPParam("Development build: debug symbols, Profiler support, Development Console. Defaults to false.")] bool development = false)
        {
            BuildTarget buildTarget;
            if (target != null)
            {
                if (!Enum.TryParse(target, out buildTarget))
                    return MCPResult.Fail($"Unknown build target '{target}'.");
            }
            else buildTarget = EditorUserBuildSettings.activeBuildTarget;

            string[] scenes;
            if (scenePaths != null && scenePaths.Length > 0)
            {
                scenes = new string[scenePaths.Length];
                for (int i = 0; i < scenePaths.Length; i++)
                {
                    if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, scenePaths[i], out _, out var guardError))
                        return MCPResult.Fail($"scenePaths[{i}]: {guardError}");
                    scenes[i] = "Assets/" + scenePaths[i].Replace('\\', '/').TrimStart('/');
                }
            }
            else
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length == 0)
                    return MCPResult.Fail("No enabled scenes in Build Settings and no scenePaths given -- add one via add_scene_to_build first, or pass scenePaths explicitly.");
            }

            if (!MCPPathGuard.TryResolveWithinProject(MCPProjectUtil.ProjectRoot, outputPath, out var fullOutputPath, out var outputGuardError))
                return MCPResult.Fail(outputGuardError);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = fullOutputPath,
                target = buildTarget,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };

            BuildReport report;
            try { report = BuildPipeline.BuildPlayer(options); }
            catch (Exception e) { return MCPResult.Fail($"BuildPipeline.BuildPlayer threw: {e.Message}"); }

            var summary = report.summary;
            return MCPResult.Success(new
            {
                result = summary.result.ToString(),
                succeeded = summary.result == BuildResult.Succeeded,
                totalErrors = summary.totalErrors,
                totalWarnings = summary.totalWarnings,
                totalTimeSeconds = summary.totalTime.TotalSeconds,
                totalSizeBytes = (long)summary.totalSize,
                outputPath = summary.outputPath,
            });
        }

        [MCPTool(
            "configure_build_settings",
            "Configures Player Settings relevant to a build: company/product name, bundle version, and scripting " +
            "backend (Mono2x/IL2CPP) for a build target group. Scenes-in-build are configured separately via " +
            "add_scene_to_build/list_scenes_in_build (scene group) -- already covered there, not duplicated here.",
            group: "build")]
        public static MCPResult ConfigureBuildSettings(
            MCPToolContext ctx,
            [MCPParam("New company name. Omit to leave unchanged.")] string companyName = null,
            [MCPParam("New product name. Omit to leave unchanged.")] string productName = null,
            [MCPParam("New bundle version string, e.g. '1.2.0'. Omit to leave unchanged.")] string bundleVersion = null,
            [MCPParam("Scripting backend: 'Mono2x' or 'IL2CPP'. Omit to leave unchanged.")] string scriptingBackend = null,
            [MCPParam("Build target group the scriptingBackend change applies to, e.g. 'Standalone', 'Android'. Omit to use the Editor's currently selected group.")] string buildTargetGroup = null)
        {
            if (companyName != null) PlayerSettings.companyName = companyName;
            if (productName != null) PlayerSettings.productName = productName;
            if (bundleVersion != null) PlayerSettings.bundleVersion = bundleVersion;

            if (scriptingBackend != null)
            {
                if (!Enum.TryParse<ScriptingImplementation>(scriptingBackend, out var impl))
                    return MCPResult.Fail($"Unknown scriptingBackend '{scriptingBackend}'. Valid values: Mono2x, IL2CPP.");

                NamedBuildTarget namedTarget;
                if (buildTargetGroup != null)
                {
                    if (!Enum.TryParse<BuildTargetGroup>(buildTargetGroup, out var group))
                        return MCPResult.Fail($"Unknown buildTargetGroup '{buildTargetGroup}'.");
                    namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
                }
                else namedTarget = NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);

                PlayerSettings.SetScriptingBackend(namedTarget, impl);
            }

            return MCPResult.Success(new
            {
                companyName = PlayerSettings.companyName,
                productName = PlayerSettings.productName,
                bundleVersion = PlayerSettings.bundleVersion,
            });
        }

        private const float DefaultPackageRequestTimeoutSeconds = 60f;

        private static MCPResult WaitFor(Request request, float timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!request.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                    return MCPResult.Fail($"Package Manager request timed out after {timeoutSeconds} seconds.");
                System.Threading.Thread.Sleep(50);
            }
            return null;
        }

        [MCPTool(
            "manage_packages",
            "Lists, adds, removes, or searches UPM packages via the real Package Manager Client API, spin-waiting " +
            "for each request to complete (confirmed via live spike this resolves in well under a second for " +
            "list/search and does not deadlock the Editor's main thread). 'remove' requires confirm:true, the " +
            "same explicit-confirmation intent as this codebase's framework-level destructive gate, checked " +
            "manually here since a single multi-action tool can't be conditionally destructive per-action at the " +
            "attribute level. Adding/removing a package can trigger a domain reload.",
            group: "build", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult ManagePackages(
            MCPToolContext ctx,
            [MCPParam("Action: 'list', 'add', 'remove', or 'search'.")] string action,
            [MCPParam("Package name (e.g. 'com.unity.cinemachine') or name@version for add. Required for add/remove/search.")] string packageId = null,
            [MCPParam("Required 'true' when action is 'remove', to confirm the removal. Ignored otherwise.")] bool confirm = false,
            [MCPParam("Maximum seconds to wait for the Package Manager request to complete. Defaults to 60.")] float timeoutSeconds = DefaultPackageRequestTimeoutSeconds)
        {
            switch (action)
            {
                case "list":
                {
                    var req = Client.List(true, false);
                    var waitError = WaitFor(req, timeoutSeconds);
                    if (waitError != null) return waitError;
                    if (req.Status != StatusCode.Success) return MCPResult.Fail($"Client.List failed: {req.Error?.message}");
                    var packages = req.Result.Select(p => new { name = p.name, version = p.version, source = p.source.ToString() }).ToArray();
                    return MCPResult.Success(new { packages });
                }
                case "search":
                {
                    if (string.IsNullOrEmpty(packageId)) return MCPResult.Fail("packageId is required for action 'search'.");
                    var req = Client.Search(packageId);
                    var waitError = WaitFor(req, timeoutSeconds);
                    if (waitError != null) return waitError;
                    if (req.Status != StatusCode.Success) return MCPResult.Fail($"Client.Search failed: {req.Error?.message}");
                    var packages = req.Result.Select(p => new { name = p.name, version = p.version, description = p.description }).ToArray();
                    return MCPResult.Success(new { packages });
                }
                case "add":
                {
                    if (string.IsNullOrEmpty(packageId)) return MCPResult.Fail("packageId is required for action 'add'.");
                    if (!UnityMCP.Groups.MCPToolGroupConfig.IsPackageAllowed(packageId))
                        return MCPResult.Fail(
                            $"Package '{packageId}' isn't on this project's package allowlist (Window > Unity MCP > " +
                            "Tool Groups). Ask a human with Editor access to add it if this is expected.");
                    var req = Client.Add(packageId);
                    var waitError = WaitFor(req, timeoutSeconds);
                    if (waitError != null) return waitError;
                    if (req.Status != StatusCode.Success) return MCPResult.Fail($"Client.Add failed: {req.Error?.message}");
                    return MCPResult.Success(new { name = req.Result.name, version = req.Result.version });
                }
                case "remove":
                {
                    if (string.IsNullOrEmpty(packageId)) return MCPResult.Fail("packageId is required for action 'remove'.");
                    if (!confirm) return MCPResult.Fail("Removing a package can break the project -- pass confirm: true to proceed.");
                    var req = Client.Remove(packageId);
                    var waitError = WaitFor(req, timeoutSeconds);
                    if (waitError != null) return waitError;
                    if (req.Status != StatusCode.Success) return MCPResult.Fail($"Client.Remove failed: {req.Error?.message}");
                    return MCPResult.Success(new { packageId });
                }
                default:
                    return MCPResult.Fail($"Unknown action '{action}'. Valid actions: list, add, remove, search.");
            }
        }

        [MCPTool(
            "manage_project_settings",
            "Configures project-wide tags, layers, time settings, and quality level. Tags/layers are edited via " +
            "ProjectSettings/TagManager.asset's SerializedObject (there is no other public API for it) -- confirmed " +
            "via live spike that edits persist and are visible via InternalEditorUtility.tags/layers afterward. " +
            "Layer indices 0-7 are Unity's reserved built-in layers and are refused. Physics settings are already " +
            "covered by configure_physics_settings (physics group) and graphics tiers aren't exposed by a stable " +
            "public API, so neither is duplicated here.",
            group: "build")]
        public static MCPResult ManageProjectSettings(
            MCPToolContext ctx,
            [MCPParam("Tag to add, if it doesn't already exist. Omit to skip.")] string addTag = null,
            [MCPParam("Tag to remove, if present. Omit to skip.")] string removeTag = null,
            [MCPParam("User layer index (8-31) to name. Omit to skip.")] int? layerIndex = null,
            [MCPParam("Name to assign to layerIndex. Required if layerIndex is given; pass an empty string to clear it.")] string layerName = null,
            [MCPParam("Project-wide fixed timestep in seconds (Time.fixedDeltaTime). Omit to leave unchanged.")] float? fixedDeltaTime = null,
            [MCPParam("Project-wide maximum timestep in seconds (Time.maximumDeltaTime). Omit to leave unchanged.")] float? maximumDeltaTime = null,
            [MCPParam("Quality level to set by name (see QualitySettings.names) or numeric index as a string. Omit to leave unchanged.")] string qualityLevel = null)
        {
            if (layerIndex.HasValue && (layerIndex.Value < 8 || layerIndex.Value > 31))
                return MCPResult.Fail("layerIndex must be between 8 and 31 -- layers 0-7 are Unity's reserved built-in layers.");
            if (layerIndex.HasValue && layerName == null)
                return MCPResult.Fail("layerName is required when layerIndex is given (pass an empty string to clear it).");

            if (addTag != null || removeTag != null || layerIndex.HasValue)
            {
                var tagManagerAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
                if (tagManagerAsset == null) return MCPResult.Fail("Could not load ProjectSettings/TagManager.asset.");
                var tagManager = new SerializedObject(tagManagerAsset);

                if (addTag != null)
                {
                    var tagsProp = tagManager.FindProperty("tags");
                    bool exists = Enumerable.Range(0, tagsProp.arraySize).Any(i => tagsProp.GetArrayElementAtIndex(i).stringValue == addTag);
                    if (!exists)
                    {
                        tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                        tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = addTag;
                    }
                }

                if (removeTag != null)
                {
                    var tagsProp = tagManager.FindProperty("tags");
                    for (int i = tagsProp.arraySize - 1; i >= 0; i--)
                    {
                        if (tagsProp.GetArrayElementAtIndex(i).stringValue == removeTag)
                            tagsProp.DeleteArrayElementAtIndex(i);
                    }
                }

                if (layerIndex.HasValue)
                {
                    var layersProp = tagManager.FindProperty("layers");
                    layersProp.GetArrayElementAtIndex(layerIndex.Value).stringValue = layerName;
                }

                tagManager.ApplyModifiedProperties();
            }

            if (fixedDeltaTime.HasValue) Time.fixedDeltaTime = fixedDeltaTime.Value;
            if (maximumDeltaTime.HasValue) Time.maximumDeltaTime = maximumDeltaTime.Value;

            if (qualityLevel != null)
            {
                int index = Array.IndexOf(QualitySettings.names, qualityLevel);
                if (index < 0 && !int.TryParse(qualityLevel, out index))
                    return MCPResult.Fail($"Unknown qualityLevel '{qualityLevel}'. Valid names: {string.Join(", ", QualitySettings.names)}.");
                if (index < 0 || index >= QualitySettings.names.Length)
                    return MCPResult.Fail($"qualityLevel index {index} is out of range (0-{QualitySettings.names.Length - 1}).");
                QualitySettings.SetQualityLevel(index, true);
            }

            return MCPResult.Success(new
            {
                tags = UnityEditorInternal.InternalEditorUtility.tags,
                layers = UnityEditorInternal.InternalEditorUtility.layers,
                fixedDeltaTime = Time.fixedDeltaTime,
                maximumDeltaTime = Time.maximumDeltaTime,
                qualityLevel = QualitySettings.names[QualitySettings.GetQualityLevel()],
            });
        }
    }
}
