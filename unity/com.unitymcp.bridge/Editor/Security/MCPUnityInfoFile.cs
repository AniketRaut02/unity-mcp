using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityMCP.Security
{
    /// <summary>
    /// Library/MCP/unity.json: what a Python MCP server needs to trust this Editor (protocol 2).
    ///
    /// Written only while this process holds MCPInstanceLock, so exactly one Editor per
    /// project can publish it. It carries the session token this Editor presents in its
    /// hello: new per Editor launch, unchanged across domain reloads (see MCPAuthHandshake).
    /// Kept through domain reloads and deleted when the Editor quits, which is how a Python
    /// server tells "Unity is reloading" (file present) from "Unity is closed" (file gone).
    /// </summary>
    public static class MCPUnityInfoFile
    {
        private static string McpDir => Path.Combine(MCPProjectUtil.ProjectRoot, "Library", "MCP");
        public static string FilePath => Path.Combine(McpDir, "unity.json");

        /// <summary>Main thread (reads Application.* values).</summary>
        public static void Write(string token, int protocolVersion, string packageVersion)
        {
            Directory.CreateDirectory(McpDir);
            var json = JsonConvert.SerializeObject(new
            {
                token,
                pid = Process.GetCurrentProcess().Id,
                projectPath = MCPProjectUtil.ProjectRoot,
                unityVersion = Application.unityVersion,
                packageVersion,
                protocolVersion,
                startedAt = DateTime.UtcNow.ToString("o")
            }, Formatting.Indented);

            // Write-then-swap so a Python server reading mid-write never sees half a file.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        public static void Delete()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { /* best-effort: a stale file only makes a server wait out its hold timeout */ }
        }

        /// <summary>v1 (1.x) wrote session.json for the old Unity-hosted listener; nothing reads it any more.</summary>
        public static void DeleteLegacySessionFile()
        {
            try
            {
                var legacy = Path.Combine(McpDir, "session.json");
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch { /* best-effort */ }
        }
    }
}
