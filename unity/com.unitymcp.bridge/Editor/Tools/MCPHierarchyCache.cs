using System;
using UnityEditor;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Caches the result of get_hierarchy's tree traversal (the most expensive query
    /// tool -- it walks every GameObject in the active scene).
    ///
    /// Invalidation, revised after a code review found the original approach incomplete:
    /// EditorApplication.hierarchyChanged alone (the only hook this had originally) fires
    /// reliably for structural changes -- create/destroy/reparent -- but NOT reliably for
    /// property mutations like renaming a GameObject. That gap was flagged as a narrow,
    /// mostly-theoretical "manual Editor-UI edit" limitation when this cache was first
    /// built. It isn't theoretical: Transform.name is a real, settable property proxying
    /// to GameObject.name, and the existing generic set_component_field tool can already
    /// reach it today (set_component_field(path, "Transform", "name", "NewName")) --
    /// meaning a rename reachable through a tool this project already ships could go
    /// unnoticed by the cache, not just a rename done by hand in the Editor.
    ///
    /// Fixed by also subscribing to ObjectChangeEvents.changesPublished (Unity 2021.1+,
    /// under this package's own declared minimum of 2021.3 — see package.json), which
    /// covers a superset of changes including property edits and renames, not just
    /// structural ones. Rather than trying to filter down to exactly the change types
    /// that matter (fragile, and exactly the kind of narrowing that produced the original
    /// gap), any published change at all invalidates unconditionally -- this cache exists
    /// purely to avoid repeated tree walks between real changes, not to survive one it
    /// can't fully characterize.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPHierarchyCache
    {
        private static object _cached;
        private static bool _dirty = true;

        static MCPHierarchyCache()
        {
            EditorApplication.hierarchyChanged += Invalidate;
            ObjectChangeEvents.changesPublished += OnObjectChangesPublished;
        }

        private static void Invalidate() => _dirty = true;

        private static void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (stream.length > 0) Invalidate();
        }

        public static object GetOrBuild(Func<object> builder)
        {
            if (_dirty || _cached == null)
            {
                _cached = builder();
                _dirty = false;
            }
            return _cached;
        }
    }
}
