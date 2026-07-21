using System.Threading;
using UnityEditor;

namespace UnityMCP.Support
{
    /// <summary>
    /// Waits for a Play mode transition to fully settle.
    /// EditorApplication.isPlaying flips as soon as the transition is
    /// *requested*, not once it's actually complete — playModeStateChanged
    /// fires EnteredPlayMode / EnteredEditMode only once the transition
    /// (including any domain reload) has settled.
    ///
    /// KNOWN RISK: this polls via Thread.Sleep on the calling thread. If
    /// tool invocation happens synchronously on Unity's main thread per
    /// dispatcher tick, this blocks the very thread the transition needs to
    /// progress — a likely deadlock, not just a slow call. Verify with the
    /// enter_play_mode smoke test before trusting this in an agent loop; if
    /// Unity visibly freezes rather than smoothly entering Play mode, this
    /// needs a non-blocking two-phase redesign instead.
    /// </summary>
    internal static class MCPPlayModeUtil
    {
        internal static bool WaitForState(bool targetIsPlaying, float timeoutSeconds)
        {
            var settled = false;

            void Handler(PlayModeStateChange change)
            {
                if ((targetIsPlaying && change == PlayModeStateChange.EnteredPlayMode)
                    || (!targetIsPlaying && change == PlayModeStateChange.EnteredEditMode))
                {
                    settled = true;
                }
            }

            EditorApplication.playModeStateChanged += Handler;
            try
            {
                var start = EditorApplication.timeSinceStartup;
                while (!settled)
                {
                    if (EditorApplication.timeSinceStartup - start > timeoutSeconds)
                        return false;
                    Thread.Sleep(50);
                }
                return true;
            }
            finally
            {
                EditorApplication.playModeStateChanged -= Handler;
            }
        }
    }
}
