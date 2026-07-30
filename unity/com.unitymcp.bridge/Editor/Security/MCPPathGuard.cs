using System;
using System.IO;

namespace UnityMCP.Security
{
    /// <summary>
    /// Confines any filesystem-touching tool (asset creation, script generation, etc. —
    /// arriving with the Phase 3 Assets/Scripting modules) to the project's Assets/
    /// folder. No tool in Phase 1/2 calls this yet since none of them touch the
    /// filesystem — this exists now so every future filesystem tool has to go through
    /// it, rather than each one growing its own ad hoc path check.
    ///
    /// Resolution goes through Path.GetFullPath, which collapses ".." segments, so the
    /// containment check below is what actually catches traversal — it is not just a
    /// string-prefix check on the un-resolved input.
    ///
    /// That lexical check alone is not sufficient, though: Path.GetFullPath only does
    /// string normalization, it never touches the filesystem to check whether any
    /// directory ALONG the resolved path is actually a symlink (or Windows junction)
    /// pointing somewhere else entirely. A relative path that looks like it stays under
    /// Assets/ could still resolve to a real file outside it if, say, someone created
    /// "Assets/Escape" as a symlink to "/etc" — the string containment check would pass
    /// (the lexical path starts with assetsRoot) while the actual write lands wherever
    /// the symlink points. TryCheckNoReparsePoints below closes that gap by walking the
    /// resolved path's real directory chain and refusing if any of it is a reparse point.
    /// </summary>
    public static class MCPPathGuard
    {
        /// <param name="projectRoot">The folder containing Assets/ (one level above Assets/).</param>
        /// <param name="relativeAssetPath">A path relative to Assets/, e.g. "Prefabs/Enemy.prefab".</param>
        public static bool TryResolveWithinAssets(string projectRoot, string relativeAssetPath, out string fullPath, out string error)
        {
            string assetsRoot;
            try { assetsRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets")); }
            catch (Exception e)
            {
                fullPath = null;
                error = $"Could not resolve path: {e.Message}";
                return false;
            }
            return TryResolveWithinRoot(assetsRoot, relativeAssetPath, "Assets/", out fullPath, out error);
        }

        /// <summary>
        /// Same confinement/symlink-safety guarantees as <see cref="TryResolveWithinAssets"/>, but confines to the
        /// whole project root instead of just Assets/ -- for tools whose output legitimately lives outside Assets/
        /// (e.g. build_player's player build output), where confining to Assets/ would be wrong, but leaving the
        /// path unconfined would let a build write anywhere on disk.
        /// </summary>
        /// <param name="projectRoot">The folder containing Assets/ (one level above Assets/).</param>
        /// <param name="relativeProjectPath">A path relative to the project root, e.g. "Builds/Windows/Game.exe".</param>
        public static bool TryResolveWithinProject(string projectRoot, string relativeProjectPath, out string fullPath, out string error)
        {
            string root;
            try { root = Path.GetFullPath(projectRoot); }
            catch (Exception e)
            {
                fullPath = null;
                error = $"Could not resolve path: {e.Message}";
                return false;
            }
            return TryResolveWithinRoot(root, relativeProjectPath, "the project root", out fullPath, out error);
        }

        private static bool TryResolveWithinRoot(string root, string relativePath, string rootDescription, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "Path must not be empty.";
                return false;
            }

            if (Path.IsPathRooted(relativePath))
            {
                error = $"Absolute paths are not allowed — provide a path relative to {rootDescription} (e.g. 'Prefabs/Enemy.prefab').";
                return false;
            }

            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(root, relativePath)); }
            catch (Exception e)
            {
                error = $"Could not resolve path: {e.Message}";
                return false;
            }

            bool withinRoot = candidate.Equals(root, StringComparison.Ordinal)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

            if (!withinRoot)
            {
                error = $"Path '{relativePath}' resolves outside {rootDescription} and is not allowed.";
                return false;
            }

            if (!TryCheckNoReparsePoints(root, candidate, out var reparseError))
            {
                error = reparseError;
                return false;
            }

            fullPath = candidate;
            return true;
        }

        /// <summary>
        /// Walks from assetsRoot down through candidate's real directory chain, refusing
        /// if anything along the way (including the final leaf, if it already exists) is
        /// a symlink or junction. Directories that don't exist yet (the common case for a
        /// create_* tool's target) are skipped rather than treated as an error — there's
        /// nothing to check about a path segment that isn't there, and a symlink can't be
        /// silently introduced by a tool that's about to CREATE that exact path itself.
        /// </summary>
        private static bool TryCheckNoReparsePoints(string assetsRoot, string candidate, out string error)
        {
            error = null;

            if (IsReparsePoint(assetsRoot))
            {
                error = $"'{assetsRoot}' (the project's Assets/ folder itself) is a symlink or junction — refusing to operate through it.";
                return false;
            }

            var relative = candidate.Length > assetsRoot.Length
                ? candidate.Substring(assetsRoot.Length).TrimStart(Path.DirectorySeparatorChar)
                : "";
            var segments = relative.Length > 0 ? relative.Split(Path.DirectorySeparatorChar) : new string[0];

            var current = assetsRoot;
            for (int i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                bool isLastSegment = i == segments.Length - 1;

                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    // Doesn't exist yet -- nothing further along this chain can exist
                    // either, so nothing left to check (this is the normal case for
                    // create_* tools, where only the final leaf is new).
                    break;
                }

                if (IsReparsePoint(current))
                {
                    error = isLastSegment
                        ? $"'{current}' is itself a symlink or junction — refusing to operate on it directly."
                        : $"Path traverses a symlink or junction at '{current}' — refusing to operate through it, since it could " +
                          "resolve outside Assets/ even though the given path looks like it stays inside.";
                    return false;
                }
            }

            return true;
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                // Doesn't exist, or attributes couldn't be read for some other reason --
                // either way, not a symlink concern at this point (a nonexistent path
                // can't redirect anywhere).
                return false;
            }
        }
    }
}
