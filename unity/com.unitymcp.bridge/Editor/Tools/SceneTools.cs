using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    public static class SceneTools
    {
        [MCPTool("create_gameobject", "Creates a new empty GameObject in the active scene, optionally under a parent hierarchy path.")]
        public static MCPResult CreateGameObject(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject.")] string name,
            [MCPParam("Hierarchy path of an existing GameObject to parent the new one under. Omit to create at scene root.")] string parentPath = null)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create GameObject");

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

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool(
            "delete_gameobject",
            "Deletes a GameObject by hierarchy path.",
            MCPLatencyTier.Fast,
            destructive: true)]
        public static MCPResult DeleteGameObject(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to delete, e.g. 'Root/Child'.")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            Undo.DestroyObjectImmediate(go);
            return MCPResult.Success();
        }

        [MCPTool("duplicate_gameobject", "Duplicates a GameObject (and its children) by hierarchy path.")]
        public static MCPResult DuplicateGameObject(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to duplicate.")] string path,
            [MCPParam("Name for the duplicate. Omit to use the original's name with ' (Copy)' appended.")] string newName = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var copy = UnityEngine.Object.Instantiate(go, go.transform.parent);
            copy.name = string.IsNullOrEmpty(newName) ? go.name + " (Copy)" : newName;
            Undo.RegisterCreatedObjectUndo(copy, "MCP: Duplicate GameObject");

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(copy) });
        }

        [MCPTool("set_transform", "Sets local position/rotation(euler)/scale on a GameObject by path. Omitted axes are left unchanged.")]
        public static MCPResult SetTransform(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to move/rotate/scale.")] string path,
            [MCPParam("Local X position. Omit to leave unchanged.")] float? posX = null,
            [MCPParam("Local Y position. Omit to leave unchanged.")] float? posY = null,
            [MCPParam("Local Z position. Omit to leave unchanged.")] float? posZ = null,
            [MCPParam("Local X rotation in degrees (Euler). Omit to leave unchanged.")] float? rotX = null,
            [MCPParam("Local Y rotation in degrees (Euler). Omit to leave unchanged.")] float? rotY = null,
            [MCPParam("Local Z rotation in degrees (Euler). Omit to leave unchanged.")] float? rotZ = null,
            [MCPParam("Local X scale. Omit to leave unchanged.")] float? scaleX = null,
            [MCPParam("Local Y scale. Omit to leave unchanged.")] float? scaleY = null,
            [MCPParam("Local Z scale. Omit to leave unchanged.")] float? scaleZ = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var t = go.transform;
            Undo.RecordObject(t, "MCP: Set Transform");

            var pos = t.localPosition;
            if (posX.HasValue) pos.x = posX.Value;
            if (posY.HasValue) pos.y = posY.Value;
            if (posZ.HasValue) pos.z = posZ.Value;
            t.localPosition = pos;

            var euler = t.localEulerAngles;
            if (rotX.HasValue) euler.x = rotX.Value;
            if (rotY.HasValue) euler.y = rotY.Value;
            if (rotZ.HasValue) euler.z = rotZ.Value;
            t.localEulerAngles = euler;

            var scale = t.localScale;
            if (scaleX.HasValue) scale.x = scaleX.Value;
            if (scaleY.HasValue) scale.y = scaleY.Value;
            if (scaleZ.HasValue) scale.z = scaleZ.Value;
            t.localScale = scale;

            return MCPResult.Success();
        }

        [MCPTool("reparent_gameobject", "Moves a GameObject under a new parent path, or to the scene root if newParentPath is omitted.")]
        public static MCPResult ReparentGameObject(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to move.")] string path,
            [MCPParam("Hierarchy path of the new parent. Omit to move to the scene root.")] string newParentPath = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            Transform newParent = null;
            if (!string.IsNullOrEmpty(newParentPath))
            {
                var parentGo = MCPSceneUtil.ResolvePath(newParentPath);
                if (parentGo == null) return MCPResult.Fail($"New parent path '{newParentPath}' not found.");
                newParent = parentGo.transform;
            }

            Undo.SetTransformParent(go.transform, newParent, "MCP: Reparent");
            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool("find_gameobjects", "Finds GameObjects in the active scene by exact name and/or tag. Omit both to list everything (use sparingly).", readOnly: true)]
        public static MCPResult FindGameObjects(
            MCPToolContext ctx,
            [MCPParam("Exact GameObject name to match. Omit to match any name.")] string name = null,
            [MCPParam("Unity tag to match, e.g. 'Player'. Omit to match any tag.")] string tag = null)
        {
            GameObject[] candidates = !string.IsNullOrEmpty(tag)
                ? GameObject.FindGameObjectsWithTag(tag)
                : UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            var matches = new List<string>();
            foreach (var go in candidates)
            {
                if (!string.IsNullOrEmpty(name) && go.name != name) continue;
                matches.Add(MCPSceneUtil.GetPath(go));
            }

            return MCPResult.Success(new { paths = matches });
        }

        [MCPTool("rename_gameobject", "Renames a GameObject by hierarchy path.")]
        public static MCPResult RenameGameObject(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to rename.")] string path,
            [MCPParam("New name for the GameObject.")] string newName)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            if (string.IsNullOrWhiteSpace(newName)) return MCPResult.Fail("newName must not be empty.");

            Undo.RecordObject(go, "MCP: Rename GameObject");
            go.name = newName;

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool("set_gameobject_active", "Enables or disables a GameObject (GameObject.SetActive).")]
        public static MCPResult SetGameObjectActive(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject.")] string path,
            [MCPParam("true to enable, false to disable.")] bool active)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            Undo.RecordObject(go, "MCP: Set GameObject Active");
            go.SetActive(active);

            return MCPResult.Success(new { active = go.activeSelf });
        }

        [MCPTool(
            "set_gameobject_static",
            "Sets static flags on a GameObject (batching/navigation/occlusion/reflection-probe/GI-contribution -- the " +
            "same flags as the Inspector's 'Static' dropdown). Pass allStatic for the common all-on/all-off case (same " +
            "as the top-level 'Static' checkbox), or flags for granular per-flag control; allStatic takes precedence " +
            "if both are given.")]
        public static MCPResult SetGameObjectStatic(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject.")] string path,
            [MCPParam("Set every static flag on (true) or off (false).")] bool? allStatic = null,
            [MCPParam("Specific flag names to set, replacing the existing flag set entirely. Valid: BatchingStatic, NavigationStatic, OccluderStatic, OccludeeStatic, OffMeshLinkGeneration, ReflectionProbeStatic, ContributeGI.")] string[] flags = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            StaticEditorFlags newFlags;
            if (allStatic.HasValue)
            {
                newFlags = 0;
                if (allStatic.Value)
                {
                    foreach (StaticEditorFlags f in Enum.GetValues(typeof(StaticEditorFlags)))
                        newFlags |= f;
                }
            }
            else if (flags != null && flags.Length > 0)
            {
                newFlags = 0;
                foreach (var f in flags)
                {
                    if (!Enum.TryParse<StaticEditorFlags>(f, out var parsed))
                        return MCPResult.Fail($"Unknown static flag '{f}'. Valid: {string.Join(", ", Enum.GetNames(typeof(StaticEditorFlags)))}");
                    newFlags |= parsed;
                }
            }
            else
            {
                return MCPResult.Fail("Provide either allStatic or a non-empty flags array.");
            }

            Undo.RegisterCompleteObjectUndo(go, "MCP: Set Static Flags");
            GameObjectUtility.SetStaticEditorFlags(go, newFlags);

            return MCPResult.Success(new { flags = GameObjectUtility.GetStaticEditorFlags(go).ToString() });
        }

        [MCPTool("get_transform", "Reads a GameObject's transform in both local and world space.", readOnly: true)]
        public static MCPResult GetTransform(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject.")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var t = go.transform;
            return MCPResult.Success(new
            {
                localPosition = new { x = t.localPosition.x, y = t.localPosition.y, z = t.localPosition.z },
                localEulerAngles = new { x = t.localEulerAngles.x, y = t.localEulerAngles.y, z = t.localEulerAngles.z },
                localScale = new { x = t.localScale.x, y = t.localScale.y, z = t.localScale.z },
                worldPosition = new { x = t.position.x, y = t.position.y, z = t.position.z },
                worldEulerAngles = new { x = t.eulerAngles.x, y = t.eulerAngles.y, z = t.eulerAngles.z }
            });
        }

        [MCPTool(
            "translate_gameobject",
            "Moves a GameObject by a delta vector. In local space (the default), the delta is added directly to " +
            "localPosition (not rotated by the object's own orientation -- a predictable, axis-aligned move, not " +
            "Transform.Translate's move-along-local-axes behavior). In world space, the delta is added to the " +
            "object's world position, correct regardless of its parent's transform.")]
        public static MCPResult TranslateGameObject(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to move.")] string path,
            [MCPParam("Delta X.")] float deltaX = 0f,
            [MCPParam("Delta Y.")] float deltaY = 0f,
            [MCPParam("Delta Z.")] float deltaZ = 0f,
            [MCPParam("Add the delta to world position instead of local position. Defaults to false.")] bool worldSpace = false)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var delta = new Vector3(deltaX, deltaY, deltaZ);
            Undo.RecordObject(go.transform, "MCP: Translate GameObject");

            if (worldSpace) go.transform.position += delta;
            else go.transform.localPosition += delta;

            return MCPResult.Success(new { path });
        }
    }
}
