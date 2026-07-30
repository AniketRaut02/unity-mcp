using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityMCP;
using UnityMCP.Support;

namespace UnityMCP.Tools
{
    public static class InspectionTools
    {
        [MCPTool("capture_scene_view", "Renders the active Scene view camera to a PNG for visual inspection — use to check object placement, lighting, and composition after editor-side changes. Returns the image inline as base64.", group: "inspection", readOnly: true)]
        public static MCPResult CaptureSceneView(
            MCPToolContext ctx,
            [MCPParam("Output width in pixels. Defaults to 1280.")] int width = 1280,
            [MCPParam("Output height in pixels. Defaults to 720.")] int height = 720,
            [MCPParam("Optional short label used in the saved filename (letters/digits/underscore/hyphen only). Omit for a generic timestamped name.")] string fileNameHint = null)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                return MCPResult.Fail("No active Scene view found. Open a Scene view window in the Editor first.");

            var result = MCPScreenshotUtil.CaptureCamera(sceneView.camera, width, height, fileNameHint);
            if (!result.success)
                return MCPResult.Fail(result.error);

            return MCPResult.Success(new
            {
                path = result.absolutePath,
                width,
                height,
                imageBase64 = result.base64Png
            });
        }

        [MCPTool("capture_game_view", "Renders the primary game camera (Camera.main, falling back to the highest-depth enabled camera) to a PNG. Works in both Edit and Play mode. Does NOT include Screen Space - Overlay Canvas UI, which renders directly to the screen rather than to any camera — once HUD tools exist, prefer a full editor-window capture for UI-inclusive verification.", group: "inspection", readOnly: true)]
        public static MCPResult CaptureGameView(
            MCPToolContext ctx,
            [MCPParam("Output width in pixels. Defaults to 1280.")] int width = 1280,
            [MCPParam("Output height in pixels. Defaults to 720.")] int height = 720,
            [MCPParam("Optional short label used in the saved filename (letters/digits/underscore/hyphen only). Omit for a generic timestamped name.")] string fileNameHint = null)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                camera = Camera.allCameras
                    .Where(c => c.enabled)
                    .OrderByDescending(c => c.depth)
                    .FirstOrDefault();
            }

            if (camera == null)
                return MCPResult.Fail("No enabled camera found in the scene to represent the game view.");

            var result = MCPScreenshotUtil.CaptureCamera(camera, width, height, fileNameHint);
            if (!result.success)
                return MCPResult.Fail(result.error);

            return MCPResult.Success(new
            {
                path = result.absolutePath,
                cameraName = camera.name,
                width,
                height,
                imageBase64 = result.base64Png
            });
        }

        [MCPTool(
            "capture_from_camera",
            "Renders a specific camera (by hierarchy path) to a PNG, regardless of whether it's the main/game camera -- " +
            "use to frame-check a security camera, cutscene camera, or any secondary camera. Returns the image inline as base64.",
            group: "inspection", readOnly: true)]
        public static MCPResult CaptureFromCamera(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject holding the Camera component.")] string path,
            [MCPParam("Output width in pixels. Defaults to 1280.")] int width = 1280,
            [MCPParam("Output height in pixels. Defaults to 720.")] int height = 720,
            [MCPParam("Optional short label used in the saved filename (letters/digits/underscore/hyphen only). Omit for a generic timestamped name.")] string fileNameHint = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var camera = go.GetComponent<Camera>();
            if (camera == null) return MCPResult.Fail($"GameObject at '{path}' has no Camera component.");

            var result = MCPScreenshotUtil.CaptureCamera(camera, width, height, fileNameHint);
            if (!result.success)
                return MCPResult.Fail(result.error);

            return MCPResult.Success(new
            {
                path = result.absolutePath,
                cameraPath = path,
                width,
                height,
                imageBase64 = result.base64Png
            });
        }

        [MCPTool(
            "draw_debug_gizmo",
            "Draws a temporary debug shape (Line, Ray, or wireframe Box) via Debug.DrawLine/DrawRay for spatial reasoning -- " +
            "e.g. visualize a raycast, a bounds check, or a planned path. Visible live in an open Scene view window, and in " +
            "the Game view during Play mode if its Gizmos toggle is on -- but NOT included in capture_scene_view / " +
            "capture_game_view's rendered output, since those render the raw camera image without the editor's gizmo overlay " +
            "pass. Use to help yourself reason about positions/directions while looking at the live Editor, not as part of " +
            "an automated screenshot-based check.",
            group: "inspection")]
        public static MCPResult DrawDebugGizmo(
            MCPToolContext ctx,
            [MCPParam("Shape to draw.")] MCPGizmoShape shape,
            [MCPParam("Line: start X. Ray: origin X. Box: center X.")] float originX,
            [MCPParam("Line: start Y. Ray: origin Y. Box: center Y.")] float originY,
            [MCPParam("Line: start Z. Ray: origin Z. Box: center Z.")] float originZ,
            [MCPParam("Line: end X. Ray: direction X (scaled by the ray's length, not normalized). Box: full size X. Defaults to 1.")] float endDirOrSizeX = 1f,
            [MCPParam("Line: end Y. Ray: direction Y. Box: full size Y. Defaults to 1.")] float endDirOrSizeY = 1f,
            [MCPParam("Line: end Z. Ray: direction Z. Box: full size Z. Defaults to 1.")] float endDirOrSizeZ = 1f,
            [MCPParam("Color name: red, green, blue, yellow, white, cyan, or magenta. Defaults to yellow.")] string color = "yellow",
            [MCPParam("How long the gizmo stays visible, in seconds. Defaults to 5.")] float duration = 5f)
        {
            if (!TryParseColor(color, out var parsedColor))
                return MCPResult.Fail($"Unknown color '{color}'. Use one of: red, green, blue, yellow, white, cyan, magenta.");

            var origin = new Vector3(originX, originY, originZ);

            switch (shape)
            {
                case MCPGizmoShape.Line:
                    Debug.DrawLine(origin, new Vector3(endDirOrSizeX, endDirOrSizeY, endDirOrSizeZ), parsedColor, duration);
                    break;

                case MCPGizmoShape.Ray:
                    Debug.DrawRay(origin, new Vector3(endDirOrSizeX, endDirOrSizeY, endDirOrSizeZ), parsedColor, duration);
                    break;

                case MCPGizmoShape.Box:
                    DrawWireBox(origin, new Vector3(endDirOrSizeX, endDirOrSizeY, endDirOrSizeZ), parsedColor, duration);
                    break;

                default:
                    return MCPResult.Fail($"Unhandled shape '{shape}'.");
            }

            SceneView.RepaintAll();
            return MCPResult.Success(new { shape = shape.ToString(), duration });
        }

        /// <summary>Debug has no DrawWireCube, so a box is 12 DrawLine calls along its edges -- centered at `center` with full extents `size`.</summary>
        private static void DrawWireBox(Vector3 center, Vector3 size, Color color, float duration)
        {
            var half = size * 0.5f;
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                corners[i] = center + new Vector3(
                    (i & 1) == 0 ? -half.x : half.x,
                    (i & 2) == 0 ? -half.y : half.y,
                    (i & 4) == 0 ? -half.z : half.z);
            }

            // Bottom face, top face, then the four vertical edges connecting them.
            int[][] edges = { new[] { 0, 1 }, new[] { 1, 3 }, new[] { 3, 2 }, new[] { 2, 0 },
                               new[] { 4, 5 }, new[] { 5, 7 }, new[] { 7, 6 }, new[] { 6, 4 },
                               new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 } };

            foreach (var edge in edges)
                Debug.DrawLine(corners[edge[0]], corners[edge[1]], color, duration);
        }

        private static bool TryParseColor(string name, out Color color)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "red": color = Color.red; return true;
                case "green": color = Color.green; return true;
                case "blue": color = Color.blue; return true;
                case "yellow": color = Color.yellow; return true;
                case "white": color = Color.white; return true;
                case "cyan": color = Color.cyan; return true;
                case "magenta": color = Color.magenta; return true;
                default: color = Color.white; return false;
            }
        }

        [MCPTool(
            "get_frame_debugger_info",
            "Returns a best-effort list of this frame's render events (draw calls, clears, compute dispatches) by name, via " +
            "the Editor's internal Frame Debugger. Each entry is a short label (e.g. a GameObject/shader/pass name) in " +
            "submission order -- a coarse per-event breakdown, not the full per-event vertex/shader/texture detail the Frame " +
            "Debugger window itself shows (that data lives on an internal struct not safely reflectable across Unity " +
            "versions). Returns an empty list rather than failing if nothing was rendered this frame (e.g. in batchmode) " +
            "or if the internal API's shape has changed on this Unity version.",
            group: "inspection")]
        public static MCPResult GetFrameDebuggerInfo(
            MCPToolContext ctx,
            [MCPParam("Maximum number of events to return. Defaults to 200.")] int limit = 200)
        {
            // FrameDebuggerUtility is an internal type (UnityEditorInternal.FrameDebuggerInternal
            // in current Unity versions, plain UnityEditorInternal in older ones) -- its
            // individual methods are public, but the TYPE itself isn't, so it can't be
            // referenced directly in source without breaking compilation against whichever
            // Unity version doesn't have it at that exact namespace. Found by simple name
            // instead, which is resilient to that namespace move.
            var utilityType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(t => t.Name == "FrameDebuggerUtility");

            if (utilityType == null)
                return MCPResult.Success(new { events = new string[0], note = "FrameDebuggerUtility not found on this Unity version." });

            try
            {
                var setEnabled = utilityType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                var countProp = utilityType.GetProperty("count", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                var getName = utilityType.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

                if (setEnabled == null || countProp == null || getName == null)
                    return MCPResult.Success(new { events = new string[0], note = "FrameDebuggerUtility's expected members were not found on this Unity version." });

                object instance = setEnabled.IsStatic ? null : Activator.CreateInstance(utilityType);

                setEnabled.Invoke(instance, new object[] { true, 0 });
                try
                {
                    int count = (int)countProp.GetValue(instance);
                    var events = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < count && events.Count < limit; i++)
                    {
                        var eventName = getName.Invoke(instance, new object[] { i }) as string;
                        events.Add(eventName ?? $"event {i}");
                    }

                    return MCPResult.Success(new { eventCount = count, events });
                }
                finally
                {
                    setEnabled.Invoke(instance, new object[] { false, 0 });
                }
            }
            catch (Exception e)
            {
                return MCPResult.Success(new { events = new string[0], note = $"FrameDebuggerUtility reflection failed on this Unity version: {e.Message}" });
            }
        }

        private static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch { return Type.EmptyTypes; }
        }

        [MCPTool(
            "capture_editor_window",
            "Screenshots a specific open Editor window (by its exact title, e.g. 'Console', 'Inspector', 'Game') by reading " +
            "actual screen pixels at that window's position -- unlike capture_scene_view/capture_game_view, this captures " +
            "whatever is really on screen, including UI/Overlay canvases and the window's own chrome. Requires the Editor " +
            "to actually be visible on a display; will fail or return a blank/garbage image if the Editor is running fully " +
            "headless (e.g. batchmode without a real display) -- verify with a real, visible Editor session.",
            group: "inspection", readOnly: true)]
        public static MCPResult CaptureEditorWindow(
            MCPToolContext ctx,
            [MCPParam("Exact title of the open window, as shown in its tab, e.g. 'Console' or 'Game'.")] string windowTitle,
            [MCPParam("Optional short label used in the saved filename.")] string fileNameHint = null)
        {
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var window = allWindows.FirstOrDefault(w => w.titleContent.text == windowTitle);
            if (window == null)
                return MCPResult.Fail($"No open window titled '{windowTitle}' was found. Open windows: {string.Join(", ", allWindows.Select(w => w.titleContent.text).Distinct())}");

            var rect = window.position;
            int width = Mathf.Max(1, (int)rect.width);
            int height = Mathf.Max(1, (int)rect.height);

            Color[] pixels;
            try
            {
                pixels = InternalEditorUtility.ReadScreenPixel(new Vector2(rect.x, rect.y), width, height);
            }
            catch (Exception e)
            {
                return MCPResult.Fail($"Failed to read screen pixels for window '{windowTitle}': {e.Message}");
            }

            if (pixels == null || pixels.Length != width * height)
                return MCPResult.Fail($"ReadScreenPixel returned an unexpected pixel count for window '{windowTitle}' -- the Editor may not be visible on a real display.");

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels);
            tex.Apply();
            var pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            var fileName = MCPScreenshotUtil.BuildFileName(fileNameHint ?? windowTitle);
            var fullPath = System.IO.Path.Combine(MCPProjectUtil.ProjectRoot, "MCPScreenshots", fileName);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            System.IO.File.WriteAllBytes(fullPath, pngBytes);

            return MCPResult.Success(new
            {
                path = fullPath,
                windowTitle,
                width,
                height,
                imageBase64 = Convert.ToBase64String(pngBytes)
            });
        }

        [MCPTool(
            "get_object_screen_bounds",
            "Returns where a GameObject's renderer projects onto screen space (in the given or main camera), as a " +
            "min/max pixel rectangle -- use to verify aim/crosshair alignment, HUD marker placement, or whether an object " +
            "is actually visible on screen. Fails if the GameObject has no Renderer, or if no camera is available.",
            group: "inspection", readOnly: true)]
        public static MCPResult GetObjectScreenBounds(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to check.")] string path,
            [MCPParam("Hierarchy path of the camera to project through. Omit to use Camera.main.")] string cameraPath = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return MCPResult.Fail($"GameObject at '{path}' has no Renderer.");

            Camera camera;
            if (!string.IsNullOrEmpty(cameraPath))
            {
                var camGo = MCPSceneUtil.ResolvePath(cameraPath);
                if (camGo == null) return MCPResult.Fail($"Camera path '{cameraPath}' not found.");
                camera = camGo.GetComponent<Camera>();
                if (camera == null) return MCPResult.Fail($"GameObject at '{cameraPath}' has no Camera component.");
            }
            else
            {
                camera = Camera.main;
                if (camera == null) return MCPResult.Fail("No cameraPath given and no Camera.main found.");
            }

            var bounds = renderer.bounds;
            var center = bounds.center;
            var extents = bounds.extents;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool anyInFront = false;

            for (int i = 0; i < 8; i++)
            {
                var corner = center + new Vector3(
                    (i & 1) == 0 ? -extents.x : extents.x,
                    (i & 2) == 0 ? -extents.y : extents.y,
                    (i & 4) == 0 ? -extents.z : extents.z);

                var screenPoint = camera.WorldToScreenPoint(corner);
                if (screenPoint.z > 0) anyInFront = true;

                minX = Mathf.Min(minX, screenPoint.x);
                minY = Mathf.Min(minY, screenPoint.y);
                maxX = Mathf.Max(maxX, screenPoint.x);
                maxY = Mathf.Max(maxY, screenPoint.y);
            }

            return MCPResult.Success(new
            {
                minX,
                minY,
                maxX,
                maxY,
                width = maxX - minX,
                height = maxY - minY,
                isInFrontOfCamera = anyInFront,
                cameraName = camera.name
            });
        }
    }

    public enum MCPGizmoShape { Line, Ray, Box }
}
