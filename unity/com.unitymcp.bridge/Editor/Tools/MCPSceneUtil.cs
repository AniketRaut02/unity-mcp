using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Tools
{
    /// <summary>
    /// GameObjects are addressed by a "/"-separated hierarchy path (e.g. "Root/Child/Grandchild")
    /// rather than by instance ID, since paths are what an LLM-driven caller can reason about
    /// and reconstruct from get_hierarchy output.
    /// </summary>
    internal static class MCPSceneUtil
    {
        public static GameObject ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var parts = path.Split('/');
            var scene = SceneManager.GetActiveScene();

            Transform current = null;
            foreach (var rootGo in scene.GetRootGameObjects())
            {
                if (rootGo.name == parts[0])
                {
                    current = rootGo.transform;
                    break;
                }
            }

            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null) return null;
            }

            return current.gameObject;
        }

        public static string GetPath(GameObject go)
        {
            if (go == null) return null;

            var t = go.transform;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
