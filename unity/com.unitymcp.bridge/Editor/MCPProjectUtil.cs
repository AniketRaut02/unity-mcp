using System.IO;
using UnityEngine;

namespace UnityMCP
{
    /// <summary>
    /// Small shared helper so every tool module that needs the on-disk project root
    /// (for path-guarding filesystem operations) computes it the same way, in one place.
    /// </summary>
    public static class MCPProjectUtil
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// Converts an absolute file path known to be under Assets/ into the "/"-separated,
        /// Assets/-relative form tools use for addressing (e.g. "Scripts/Enemy.cs").
        /// Shared by ScriptingTools.list_scripts and AssetTools.list_assets.
        /// </summary>
        public static string MakeRelativeToAssets(string assetsRoot, string fullPath)
        {
            var rel = fullPath.Substring(assetsRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
