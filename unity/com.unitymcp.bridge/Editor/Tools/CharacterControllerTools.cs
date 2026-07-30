using UnityEditor;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group M of the tool catalog -- FPS Character Controller. This is the one atomic tool in the group; the
    /// rest (movement/sprint/crouch/jump/look/footsteps/interaction/stamina/flashlight/lean) are Python composites
    /// in workflows.py that scaffold small MonoBehaviour scripts, since Unity has no built-in FPS controller --
    /// CharacterController is just the physics primitive, all movement/look logic has to be hand-written.
    /// </summary>
    public static class CharacterControllerTools
    {
        [MCPTool("add_character_controller", "Adds and configures a CharacterController on a GameObject -- the physics capsule an FPS player moves with.", group: "fps_controller")]
        public static MCPResult AddCharacterController(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Capsule radius. Omit for Unity's default (0.5).")] float? radius = null,
            [MCPParam("Capsule height. Omit for Unity's default (2).")] float? height = null,
            [MCPParam("Capsule center Y offset. Omit for Unity's default.")] float? centerY = null,
            [MCPParam("Maximum walkable slope angle in degrees. Omit for Unity's default (45).")] float? slopeLimit = null,
            [MCPParam("Maximum step height the controller can climb without jumping. Omit for Unity's default (0.3).")] float? stepOffset = null,
            [MCPParam("Skin width -- how far inside its collision volume the controller can penetrate. Omit for Unity's default.")] float? skinWidth = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            // Plain AddComponent, not Undo.AddComponent<T> -- a live spike in batch 9 found Undo.AddComponent<T>
            // leaves NavMeshAgent/NavMeshObstacle as a stale, immediately-inaccessible reference; plain
            // AddComponent<T> was confirmed to work cleanly for those, so it's used consistently here too.
            var controller = go.GetComponent<CharacterController>();
            if (controller == null) controller = go.AddComponent<CharacterController>();

            if (radius.HasValue) controller.radius = radius.Value;
            if (height.HasValue) controller.height = height.Value;
            if (centerY.HasValue)
            {
                var center = controller.center;
                center.y = centerY.Value;
                controller.center = center;
            }
            if (slopeLimit.HasValue) controller.slopeLimit = slopeLimit.Value;
            if (stepOffset.HasValue) controller.stepOffset = stepOffset.Value;
            if (skinWidth.HasValue) controller.skinWidth = skinWidth.Value;

            return MCPResult.Success();
        }
    }
}
