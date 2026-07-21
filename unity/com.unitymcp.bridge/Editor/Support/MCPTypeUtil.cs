using System;
using System.Linq;
using System.Reflection;

namespace UnityMCP.Support
{
    /// <summary>
    /// Resolves a short or fully-qualified type name to a System.Type by
    /// searching all loaded assemblies. Self-contained because I don't know
    /// whether this project already has an equivalent helper backing
    /// add_component — if it does, point me to it and tools here should use
    /// that instead of duplicating this logic.
    /// </summary>
    internal static class MCPTypeUtil
    {
        internal static Type ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var direct = Type.GetType(typeName);
            if (direct != null)
                return direct;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                var match = types.FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
                if (match != null)
                    return match;
            }

            return null;
        }
    }
}
