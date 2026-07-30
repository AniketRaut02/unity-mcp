using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityMCP.Groups;

namespace UnityMCP
{
    public class MCPToolEntry
    {
        public string Name;
        public string Description;
        public MCPLatencyTier LatencyTier;
        public bool Destructive;
        public bool ReadOnly;
        public string Group;
        public MethodInfo Method;
        public ParameterInfo[] Parameters;
        public Dictionary<string, object> Schema;
    }

    /// <summary>
    /// Scans every loaded assembly once for [MCPTool]-decorated static methods, builds a
    /// JSON-Schema-ish description of each one's parameters, and dispatches calls into
    /// them with basic type coercion. This is the single mechanism that both the growth
    /// to 150+ tools and the later GUI tool builder are meant to build on: adding a tool
    /// is "write a method, add an attribute" and nothing here needs to change.
    /// </summary>
    public static class MCPToolRegistry
    {
        private static readonly Dictionary<string, MCPToolEntry> _tools = new Dictionary<string, MCPToolEntry>();
        private static bool _scanned;

        public static void EnsureScanned()
        {
            if (_scanned) return;
            Rescan();
        }

        /// <summary>Forces a fresh reflection scan. Call after domain reload or when tools change at runtime.</summary>
        public static void Rescan()
        {
            _tools.Clear();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var method in methods)
                    {
                        var attr = method.GetCustomAttribute<MCPToolAttribute>();
                        if (attr == null) continue;

                        if (_tools.ContainsKey(attr.Name))
                        {
                            Debug.LogError(
                                $"[MCP] Duplicate tool name '{attr.Name}' on {type.FullName}.{method.Name} — skipping this registration."
                            );
                            continue;
                        }

                        var parameters = method.GetParameters();
                        _tools[attr.Name] = new MCPToolEntry
                        {
                            Name = attr.Name,
                            Description = attr.Description,
                            LatencyTier = attr.LatencyTier,
                            Destructive = attr.Destructive,
                            ReadOnly = attr.ReadOnly,
                            Group = attr.Group,
                            Method = method,
                            Parameters = parameters,
                            Schema = BuildSchema(parameters, attr.Destructive)
                        };
                    }
                }
            }

            _scanned = true;
            Debug.Log($"[MCP] Tool registry scan complete: {_tools.Count} tool(s) registered.");
        }

        public static IReadOnlyDictionary<string, MCPToolEntry> Tools
        {
            get
            {
                EnsureScanned();
                return _tools;
            }
        }

        public static bool TryGet(string name, out MCPToolEntry entry)
        {
            EnsureScanned();
            return _tools.TryGetValue(name, out entry);
        }

        private static Dictionary<string, object> BuildSchema(ParameterInfo[] parameters, bool destructive)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var p in parameters)
            {
                if (p.ParameterType == typeof(MCPToolContext)) continue;

                var underlyingType = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;

                Dictionary<string, object> propertySchema;
                if (underlyingType == typeof(string[]))
                {
                    // A JSON array of strings, not a bare type -- string[] is the one
                    // list-shaped parameter type the registry supports (tags, references,
                    // bindings, inventories, ... -- overwhelmingly the common "list of
                    // things" need across tools), coerced from the wire's JArray in
                    // ConvertArg below.
                    propertySchema = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object> { ["type"] = "string" }
                    };
                }
                else
                {
                    propertySchema = new Dictionary<string, object>
                    {
                        ["type"] = MapJsonType(p.ParameterType)
                    };

                    // Enum-typed parameters get an explicit "enum" list of valid string
                    // values, rather than making the caller guess them from prose in the
                    // tool description — this is what lets an agent (or a schema-validating
                    // client) know the exact valid set instead of pattern-matching examples.
                    if (underlyingType.IsEnum)
                    {
                        propertySchema["enum"] = Enum.GetNames(underlyingType);
                    }
                }

                var paramAttr = p.GetCustomAttribute<MCPParamAttribute>();
                if (paramAttr != null)
                {
                    propertySchema["description"] = paramAttr.Description;
                }

                properties[p.Name] = propertySchema;

                // A default value (including nullable types used as "optional") means
                // the caller doesn't have to supply it.
                bool isOptional = p.HasDefaultValue || IsNullable(p.ParameterType);
                if (!isOptional) required.Add(p.Name);
            }

            if (destructive)
            {
                // Synthetic argument, not a real method parameter — Invoke() strips it
                // out and enforces it centrally before the tool method ever runs.
                properties["confirm"] = new Dictionary<string, object>
                {
                    ["type"] = "boolean",
                    ["description"] = "Must be true to execute this destructive action."
                };
                required.Add("confirm");
            }

            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required
            };
        }

        private static bool IsNullable(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>);

        private static string MapJsonType(Type t)
        {
            var underlying = Nullable.GetUnderlyingType(t) ?? t;

            if (underlying == typeof(string)) return "string";
            if (underlying == typeof(int) || underlying == typeof(long)) return "integer";
            if (underlying == typeof(float) || underlying == typeof(double)) return "number";
            if (underlying == typeof(bool)) return "boolean";
            if (underlying.IsEnum) return "string";

            // Phase 1 supports primitives + enums only; richer types (Vector3, etc.)
            // are a Phase 3+ concern once the Assets/Physics/UI modules need them.
            return "string";
        }

        /// <summary>
        /// Invokes a registered tool by name. `args` comes from deserialized JSON, so values
        /// arrive as loosely-typed CLR objects (long/double/bool/string/Dictionary/List) —
        /// ConvertArg coerces them to the method's actual parameter types.
        /// </summary>
        public static MCPResult Invoke(string name, Dictionary<string, object> args, MCPToolContext ctx)
        {
            EnsureScanned();
            if (!_tools.TryGetValue(name, out var entry))
                return MCPResult.Fail($"Unknown tool '{name}'.");

            // A disabled group (set via Window > Unity MCP > Tool Groups, a human-only
            // control -- there is no MCP tool that can disable/re-enable a group) is
            // refused with the exact same message a genuinely unknown tool gets, not a
            // distinct "disabled" error -- an MCP client must not be able to infer the
            // group's existence from the error text, matching how list_tools already
            // never advertises it (see MCPCommandDispatcher.HandleListTools) and how
            // Python's groups.py treats it the same way for its own composite tools.
            if (MCPToolGroupConfig.IsDisabled(entry.Group))
                return MCPResult.Fail($"Unknown tool '{name}'.");

            // Read-Only Mode (Window > Unity MCP > Tool Groups, human-only toggle): unlike
            // a disabled group, this isn't about hiding a tool's existence -- it's a
            // deliberate "look but don't touch" posture, so the refusal says exactly why
            // instead of pretending the tool doesn't exist.
            if (MCPToolGroupConfig.IsReadOnlyMode() && !entry.ReadOnly)
                return MCPResult.Fail($"Tool '{name}' is not read-only, and Read-Only Mode is enabled for this project.");

            try
            {
                if (entry.Destructive)
                {
                    bool confirmed = args != null
                                      && args.TryGetValue("confirm", out var confirmRaw)
                                      && Convert.ToBoolean(confirmRaw);
                    if (!confirmed)
                        return MCPResult.Fail($"Tool '{name}' is destructive and requires confirm=true.");
                }

                var callArgs = new object[entry.Parameters.Length];
                for (int i = 0; i < entry.Parameters.Length; i++)
                {
                    var p = entry.Parameters[i];

                    if (p.ParameterType == typeof(MCPToolContext))
                    {
                        callArgs[i] = ctx;
                        continue;
                    }

                    if (args != null && args.TryGetValue(p.Name, out var raw) && raw != null)
                    {
                        callArgs[i] = ConvertArg(raw, p.ParameterType);
                    }
                    else if (p.HasDefaultValue)
                    {
                        callArgs[i] = p.DefaultValue;
                    }
                    else if (IsNullable(p.ParameterType))
                    {
                        callArgs[i] = null;
                    }
                    else
                    {
                        return MCPResult.Fail($"Missing required argument '{p.Name}' for tool '{name}'.");
                    }
                }

                var invokeResult = entry.Method.Invoke(null, callArgs);
                if (invokeResult is MCPResult mcpResult) return mcpResult;
                return MCPResult.Success(invokeResult);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Debug.LogError($"[MCP] Tool '{name}' threw: {inner}");
                return MCPResult.Fail(inner.Message);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCP] Tool '{name}' failed: {e}");
                return MCPResult.Fail(e.Message);
            }
        }

        private static object ConvertArg(object raw, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlying.IsInstanceOfType(raw)) return raw;
            if (underlying.IsEnum) return Enum.Parse(underlying, raw.ToString());

            // A JSON array arrives here as Newtonsoft's JArray (deserializing into the
            // Dictionary<string, object>-typed args field keeps nested arrays/objects as
            // JArray/JObject, not plain List<object>/Dictionary<string, object>) -- but
            // this is written against the general IEnumerable contract, not JArray
            // specifically, so it also accepts a real string[]/object[]/List<object> a
            // direct (non-wire) caller -- e.g. a dev-test -- might pass instead.
            if (underlying == typeof(string[]) && raw is System.Collections.IEnumerable enumerable && !(raw is string))
            {
                var items = new List<string>();
                foreach (var item in enumerable) items.Add(item?.ToString());
                return items.ToArray();
            }

            if (underlying == typeof(int)) return Convert.ToInt32(raw);
            if (underlying == typeof(long)) return Convert.ToInt64(raw);
            if (underlying == typeof(float)) return Convert.ToSingle(raw);
            if (underlying == typeof(double)) return Convert.ToDouble(raw);
            if (underlying == typeof(bool)) return Convert.ToBoolean(raw);
            if (underlying == typeof(string)) return raw.ToString();

            return Convert.ChangeType(raw, underlying);
        }
    }
}
