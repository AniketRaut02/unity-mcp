using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP;
using UnityMCP.Support;

namespace UnityMCP.Tools
{
    public static class InspectionTools
    {
        [MCPTool("capture_scene_view", "Renders the active Scene view camera to a PNG for visual inspection — use to check object placement, lighting, and composition after editor-side changes. Returns the image inline as base64.", group: "inspection")]
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

        [MCPTool("capture_game_view", "Renders the primary game camera (Camera.main, falling back to the highest-depth enabled camera) to a PNG. Works in both Edit and Play mode. Does NOT include Screen Space - Overlay Canvas UI, which renders directly to the screen rather than to any camera — once HUD tools exist, prefer a full editor-window capture for UI-inclusive verification.", group: "inspection")]
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
    }
}
