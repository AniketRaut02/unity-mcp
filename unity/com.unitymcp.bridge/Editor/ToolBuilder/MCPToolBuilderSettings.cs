using UnityEditor;

namespace UnityMCP.ToolBuilder
{
    /// <summary>
    /// Where the unity_mcp_server Python package lives on disk, so the Tool Builder knows
    /// where to write custom_workflows.py.
    ///
    /// This is NOT assumed from a relative path off the Unity project — the Unity project
    /// and the Python server are independently located in a real deployment. They only
    /// happen to be siblings (unity/ and python/ under one folder) in this repo's own dev
    /// layout, which is a fact about how this package is developed and distributed, not a
    /// guarantee about where any given user has actually put the Python side. Stored via
    /// EditorPrefs, keyed by this project's path, so different Unity projects on the same
    /// machine can each point at their own Python server location.
    /// </summary>
    public static class MCPToolBuilderSettings
    {
        private static string PrefsKey => "UnityMCP.PythonServerPath." + MCPProjectUtil.ProjectRoot;

        /// <summary>Absolute path to the unity_mcp_server package directory (containing workflows.py, server.py, ...).</summary>
        public static string PythonServerPath
        {
            get => EditorPrefs.GetString(PrefsKey, "");
            set => EditorPrefs.SetString(PrefsKey, value);
        }

        /// <summary>
        /// True if `candidatePath` is the same as, or nested under, `projectRoot`. Used to
        /// refuse configuring a Python server location inside the Unity project: writing
        /// files there (which is all custom_workflows.py generation ever does — plain
        /// System.IO, no Unity API calls) can still land inside Assets/ or another watched
        /// folder, where Unity's own file-watching/import machinery may notice the change
        /// and trigger an unrelated reimport or recompile pass, disconnecting the bridge
        /// with no code in this project having asked for that to happen. Pure string/path
        /// logic — no EditorPrefs, no file I/O — so it's fully unit-testable without a
        /// real Editor.
        /// </summary>
        public static bool IsInsideProject(string candidatePath, string projectRoot)
        {
            if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(projectRoot)) return false;

            string normalizedCandidate, normalizedProjectRoot;
            try
            {
                normalizedCandidate = System.IO.Path.GetFullPath(candidatePath).TrimEnd('/', '\\');
                normalizedProjectRoot = System.IO.Path.GetFullPath(projectRoot).TrimEnd('/', '\\');
            }
            catch
            {
                return false; // malformed path -- let the existing Directory.Exists check in the window catch it
            }

            if (normalizedCandidate.Equals(normalizedProjectRoot, System.StringComparison.OrdinalIgnoreCase))
                return true;

            return normalizedCandidate.StartsWith(
                normalizedProjectRoot + System.IO.Path.DirectorySeparatorChar,
                System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
