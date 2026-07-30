using UnityEditor;

namespace UnityMCP.Setup
{
    /// <summary>
    /// Where the unity_mcp_server Python package lives on disk -- needed by the Setup
    /// window's Configure button to build each client's config entry (the absolute venv
    /// python path, PYTHONPATH, etc).
    ///
    /// This used to live in the now-removed visual Tool Builder (MCPToolBuilderSettings),
    /// since that was the only other thing that needed it (to know where to write
    /// custom_workflows.py). With the Tool Builder gone, the Setup window is the only
    /// remaining consumer, so the setting moved here directly. The EditorPrefs key is
    /// unchanged from the Tool Builder's own ("UnityMCP.PythonServerPath." + project
    /// root) specifically so anyone upgrading keeps whatever path they'd already
    /// configured, with nothing to re-enter.
    ///
    /// Stored via EditorPrefs, keyed by this project's path, so different Unity
    /// projects on the same machine can each point at their own Python server location.
    /// </summary>
    public static class MCPPythonServerSettings
    {
        private static string PrefsKey => "UnityMCP.PythonServerPath." + MCPProjectUtil.ProjectRoot;

        /// <summary>Absolute path to the unity_mcp_server package directory (containing workflows.py, server.py, ...).</summary>
        public static string PythonServerPath
        {
            get => EditorPrefs.GetString(PrefsKey, "");
            set => EditorPrefs.SetString(PrefsKey, value);
        }
    }
}
