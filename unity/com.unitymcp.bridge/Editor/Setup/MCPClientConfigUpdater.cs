using System;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Keeps the client configs this package wrote pointing at the current package. A git or
    /// OpenUPM update moves the package folder (Library/PackageCache/...@hash), which would
    /// otherwise leave every config launching a server that no longer exists.
    ///
    /// On Editor start (and after any domain reload in which the package folder or uv changed),
    /// every client config that has this project's "unity-&lt;Project&gt;" entry is regenerated
    /// through the same MCPClientConfigurator a manual Configure uses; files already current are
    /// left untouched. The skill and AGENTS.md section are refreshed with them.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPClientConfigUpdater
    {
        private const string LastSyncKey = "UnityMCP.ClientConfigsSyncedFor";

        static MCPClientConfigUpdater()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += SyncIfNeeded; // after the package database is ready
        }

        private static void SyncIfNeeded()
        {
            string serverDir;
            try { serverDir = MCPServerEntryBuilder.BundledServerDirectory(); }
            catch { return; }
            if (serverDir == null) return;

            var uv = MCPUvLocator.Find(MCPClientConfigurator.IsWindows);
            var signature = serverDir + "|" + uv;
            if (SessionState.GetString(LastSyncKey, "") == signature) return;

            foreach (var kind in MCPClientDetector.AllKinds())
            {
                if (!MCPClientConfigurator.HasOurEntry(kind)) continue;

                if (uv == null)
                {
                    if (SessionState.GetBool("UnityMCP.WarnedNoUv", false)) return;
                    SessionState.SetBool("UnityMCP.WarnedNoUv", true);
                    Debug.LogWarning("[MCP] Your MCP client configs need uv to start the bundled server, but uv was not found. " +
                                     "Open Window > Unity MCP > Setup for the one-line install command.");
                    return; // don't record: retry once uv appears
                }

                try
                {
                    var outcome = MCPClientConfigurator.Configure(kind, recordAsLastConfigured: false);
                    if (outcome.Ok && outcome.Changed)
                        Debug.Log($"[MCP] Updated the {MCPClientDetector.DisplayName(kind)} config to this version of the package. " +
                                  "Restart that client's session to use it.");
                    else if (!outcome.Ok)
                        Debug.LogWarning("[MCP] " + outcome.Message);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MCP] Could not update the {MCPClientDetector.DisplayName(kind)} config: {e.Message}");
                }
            }

            SessionState.SetString(LastSyncKey, signature);
        }
    }
}
