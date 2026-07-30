using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Support
{
    /// <summary>
    /// Shared render-to-PNG logic for the visual inspection tools
    /// (capture_scene_view, capture_game_view). Writes to a fixed
    /// &lt;ProjectRoot&gt;/MCPScreenshots/ folder, deliberately outside
    /// Assets/, so debug captures never enter the asset database — no
    /// importer overhead, no AssetDatabase.Refresh() needed, no polluting
    /// the actual game project. Because the directory is fixed and
    /// filenames are sanitized/timestamped internally, callers never supply
    /// a path — there's no traversal surface to guard against.
    ///
    /// Every capture also prunes this folder down to MaxRetainedScreenshots
    /// (oldest first): an agent that repeatedly captures the scene/game view
    /// over a long session had no natural cap here before, and each frame can
    /// be several MB at typical capture sizes — left unpruned, that is a real,
    /// unbounded source of disk growth over a long-running session.
    ///
    /// Assumes MCPProjectUtil (used elsewhere in this project for
    /// MCPPathGuard.TryResolveWithinAssets) lives in the UnityMCP namespace
    /// — flag if that's wrong and I'll fix the using.
    /// </summary>
    internal static class MCPScreenshotUtil
    {
        private const int MaxRetainedScreenshots = 50;

        internal struct CaptureResult
        {
            public bool success;
            public string error;
            public string absolutePath;
            public string base64Png;
        }

        private static string ScreenshotDirectory =>
            Path.Combine(MCPProjectUtil.ProjectRoot, "MCPScreenshots");

        internal static string BuildFileName(string hint)
        {
            var safeHint = string.IsNullOrWhiteSpace(hint)
                ? "capture"
                : new string(hint.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());

            if (string.IsNullOrEmpty(safeHint))
                safeHint = "capture";

            return $"{safeHint}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.png";
        }

        internal static CaptureResult CaptureCamera(Camera camera, int width, int height, string fileNameHint)
        {
            if (camera == null)
                return new CaptureResult { success = false, error = "Camera is null." };

            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);

            var previousTargetTexture = camera.targetTexture;
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);

            try
            {
                camera.targetTexture = rt;
                camera.Render();

                var previousActive = RenderTexture.active;
                RenderTexture.active = rt;

                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                RenderTexture.active = previousActive;

                var pngBytes = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);

                Directory.CreateDirectory(ScreenshotDirectory);
                var fileName = BuildFileName(fileNameHint);
                var fullPath = Path.Combine(ScreenshotDirectory, fileName);
                File.WriteAllBytes(fullPath, pngBytes);
                PruneOldScreenshots();

                return new CaptureResult
                {
                    success = true,
                    absolutePath = fullPath,
                    base64Png = Convert.ToBase64String(pngBytes)
                };
            }
            catch (Exception e)
            {
                return new CaptureResult { success = false, error = e.Message };
            }
            finally
            {
                camera.targetTexture = previousTargetTexture;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Deletes the oldest files in ScreenshotDirectory beyond MaxRetainedScreenshots. Best-effort -- a failure here should never fail the capture that just succeeded.</summary>
        private static void PruneOldScreenshots()
        {
            try
            {
                var files = new DirectoryInfo(ScreenshotDirectory).GetFiles("*.png");
                if (files.Length <= MaxRetainedScreenshots) return;

                Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
                for (int i = 0; i < files.Length - MaxRetainedScreenshots; i++)
                {
                    try { files[i].Delete(); } catch { /* best-effort */ }
                }
            }
            catch
            {
                // Directory listing failed for some reason -- not worth failing the
                // capture that just succeeded over a cleanup step.
            }
        }
    }
}
