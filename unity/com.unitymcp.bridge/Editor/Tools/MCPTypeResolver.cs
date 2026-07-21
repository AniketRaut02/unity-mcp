using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Shared reflection-based type-by-name resolution, used by any tool that accepts
    /// a type name as a string. Extracted here once a second module (AssetTools'
    /// create_scriptable_object) needed the same logic ComponentTools already had,
    /// rather than maintaining two copies that could drift.
    ///
    /// Two real problems fixed in this revision, found in a code review after
    /// production use:
    ///
    ///   1. No caching -- every call re-enumerated every loaded assembly and called
    ///      GetTypes() on each one, even for the same typeName looked up repeatedly
    ///      (e.g. set_component_field called many times against the same component
    ///      type). Now cached per typeName, safely invalidated for free on every domain
    ///      reload since the cache is plain static state that gets reset along with
    ///      everything else when Unity reloads.
    ///
    ///   2. Non-deterministic short-name resolution -- AppDomain.CurrentDomain.GetAssemblies()
    ///      has no guaranteed stable order, and the old code took the FIRST type whose
    ///      short Name matched across ALL assemblies. Two unrelated types sharing a
    ///      short name (very plausible: your own "Enemy" class vs. a package's "Enemy"
    ///      class) could resolve to different, wrong types across calls with no error
    ///      or warning. Now: an exact FullName match is preferred whenever available
    ///      (deterministic, since a fully-qualified name should be unique in a
    ///      well-formed program), and if resolution would otherwise be genuinely
    ///      ambiguous, it FAILS LOUDLY with every candidate's full name listed, rather
    ///      than silently picking one.
    /// </summary>
    internal static class MCPTypeResolver
    {
        private static readonly Dictionary<string, Type> _cache = new Dictionary<string, Type>();
        private static readonly Dictionary<string, string> _errorCache = new Dictionary<string, string>();

        /// <summary>Convenience wrapper for callers that just want a Type or null, without the distinction between "not found" and "ambiguous".</summary>
        public static Type Resolve(string typeName)
        {
            TryResolve(typeName, out var type, out _);
            return type;
        }

        public static bool TryResolve(string typeName, out Type type, out string error)
        {
            type = null;
            error = null;

            if (string.IsNullOrEmpty(typeName))
            {
                error = "Type name must not be empty.";
                return false;
            }

            if (_cache.TryGetValue(typeName, out var cachedType))
            {
                type = cachedType;
                return true;
            }
            if (_errorCache.TryGetValue(typeName, out var cachedError))
            {
                error = cachedError;
                return false;
            }

            // Fast paths: a fully-qualified name, or a short UnityEngine type name.
            // Both are deterministic regardless of assembly enumeration order, since
            // Type.GetType with a specific string always resolves the same way.
            var direct = Type.GetType(typeName) ?? Type.GetType($"UnityEngine.{typeName}, UnityEngine");
            if (direct != null)
            {
                return CacheAndReturn(typeName, direct, out type, out error);
            }

            var allTypes = EnumerateAllTypes().ToList();

            // Prefer an exact FullName match -- inherently deterministic, since a
            // fully-qualified name collision across DISTINCT types would mean two
            // different assemblies (e.g. two versions of the same one, loaded side by
            // side) define an identically-named type, which is unusual enough to
            // surface as an ambiguity error rather than quietly guess.
            var fullNameMatches = allTypes.Where(t => t.FullName == typeName).Distinct().ToList();
            if (fullNameMatches.Count == 1)
            {
                return CacheAndReturn(typeName, fullNameMatches[0], out type, out error);
            }
            if (fullNameMatches.Count > 1)
            {
                return CacheErrorAndReturn(typeName, AmbiguousMessage(typeName, fullNameMatches), out type, out error);
            }

            // Fall back to short-name matches, deduplicated by actual type identity so
            // the SAME type reachable through more than one assembly-enumeration path
            // doesn't look like a false ambiguity.
            var shortNameMatches = allTypes.Where(t => t.Name == typeName).Distinct().ToList();
            if (shortNameMatches.Count == 1)
            {
                return CacheAndReturn(typeName, shortNameMatches[0], out type, out error);
            }
            if (shortNameMatches.Count > 1)
            {
                return CacheErrorAndReturn(typeName, AmbiguousMessage(typeName, shortNameMatches), out type, out error);
            }

            return CacheErrorAndReturn(typeName, $"Type '{typeName}' not found.", out type, out error);
        }

        private static bool CacheAndReturn(string typeName, Type resolved, out Type type, out string error)
        {
            _cache[typeName] = resolved;
            type = resolved;
            error = null;
            return true;
        }

        private static bool CacheErrorAndReturn(string typeName, string errorMessage, out Type type, out string error)
        {
            _errorCache[typeName] = errorMessage;
            type = null;
            error = errorMessage;
            return false;
        }

        private static string AmbiguousMessage(string typeName, List<Type> matches)
        {
            var names = string.Join(", ", matches.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal));
            return $"Type name '{typeName}' is ambiguous — matches multiple types: {names}. Use the fully-qualified name to disambiguate.";
        }

        private static IEnumerable<Type> EnumerateAllTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types) yield return t;
            }
        }

        /// <summary>
        /// Test-only: clears the cache so a test can exercise fresh-resolution behavior
        /// repeatedly (e.g. testing both the "not found" and "found" paths for the same
        /// typeName across different assertions) without an earlier assertion's cached
        /// result silently short-circuiting a later one.
        /// </summary>
        internal static void ClearCacheForTest()
        {
            _cache.Clear();
            _errorCache.Clear();
        }
    }
}
