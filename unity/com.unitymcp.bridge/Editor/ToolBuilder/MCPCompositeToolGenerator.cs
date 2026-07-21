using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityMCP.ToolBuilder
{
    /// <summary>
    /// Turns an MCPCompositeToolSpec into real Python workflow source, validating every
    /// step against the LIVE MCPToolRegistry as it goes — this is exactly the validation
    /// that's only possible from inside Unity (knowing which tools actually exist right
    /// now, and what their real parameter types are), which is the whole reason this is a
    /// C# generator producing Python text rather than, say, a Python-side builder.
    ///
    /// Deliberately has zero File I/O and zero UnityEditor GUI dependency — the caller
    /// (MCPToolBuilderWindow) owns writing the result to disk. That split is what makes
    /// this class fully testable outside a real Editor.
    /// </summary>
    public static class MCPCompositeToolGenerator
    {
        private static readonly Regex NamePattern = new Regex(@"^[a-z_][a-z0-9_]*$");
        private static readonly Regex ParamRefPattern = new Regex(@"^\{([a-zA-Z_][a-zA-Z0-9_]*)\}$");
        private static readonly Regex StepRefPattern = new Regex(@"^\{step(\d+)\.([a-zA-Z_][a-zA-Z0-9_]*)\}$");

        /// <summary>
        /// Validates `spec` and, if valid, returns the generated Python source with `error`
        /// null. On any validation failure, returns null with `error` describing exactly
        /// what's wrong — every failure mode names the specific step/arg/param at fault
        /// rather than a generic "invalid spec".
        /// </summary>
        public static string Generate(MCPCompositeToolSpec spec, string existingFileContent, out string error)
        {
            error = ValidateSpec(spec, existingFileContent);
            if (error != null) return null;

            return BuildSource(spec);
        }

        private static string ValidateSpec(MCPCompositeToolSpec spec, string existingFileContent)
        {
            if (spec == null) return "Spec is null.";

            if (string.IsNullOrEmpty(spec.Name) || !NamePattern.IsMatch(spec.Name))
                return $"Tool name '{spec.Name}' must be lowercase snake_case (letters, digits, underscores; can't start with a digit).";

            if (!string.IsNullOrEmpty(existingFileContent))
            {
                var duplicatePattern = new Regex("@workflow\\(\\s*\"" + Regex.Escape(spec.Name) + "\"");
                if (duplicatePattern.IsMatch(existingFileContent))
                    return $"A composite tool named '{spec.Name}' already exists in custom_workflows.py. Rename this one or remove the existing definition first.";
            }

            if (string.IsNullOrWhiteSpace(spec.Description))
                return "Description is required — this is what an agent reads to decide when to use the tool.";

            if (spec.Steps == null || spec.Steps.Count == 0)
                return "At least one step is required.";

            var paramNames = new HashSet<string>();
            foreach (var p in spec.Parameters ?? new List<MCPCompositeParam>())
            {
                if (string.IsNullOrEmpty(p.Name) || !Regex.IsMatch(p.Name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                    return $"Parameter name '{p.Name}' must be a valid identifier.";
                if (!paramNames.Add(p.Name))
                    return $"Duplicate parameter name '{p.Name}'.";
            }

            for (int i = 0; i < spec.Steps.Count; i++)
            {
                var step = spec.Steps[i];

                if (string.IsNullOrEmpty(step.ToolName))
                    return $"Step {i}: no tool selected.";

                if (!MCPToolRegistry.TryGet(step.ToolName, out var entry))
                    return $"Step {i}: '{step.ToolName}' is not a registered tool. Check the exact name (case-sensitive).";

                var properties = (Dictionary<string, object>)entry.Schema["properties"];
                var required = (List<string>)entry.Schema["required"];

                foreach (var arg in step.Args ?? new List<MCPCompositeStepArg>())
                {
                    if (!properties.ContainsKey(arg.ArgName))
                        return $"Step {i} ('{step.ToolName}'): '{arg.ArgName}' is not a real parameter of this tool.";

                    var refError = ValidateValueReference(arg.ValueTemplate, i, paramNames);
                    if (refError != null) return $"Step {i} ('{step.ToolName}'), arg '{arg.ArgName}': {refError}";
                }

                // Every required argument on the target tool must be supplied by this step
                // (confirm is exempt — it's synthetic, added by the registry itself, and
                // the generator always supplies it explicitly for destructive tools; see
                // BuildStepLine).
                var suppliedArgNames = new HashSet<string>((step.Args ?? new List<MCPCompositeStepArg>()).Select(a => a.ArgName));
                foreach (var requiredArg in required)
                {
                    if (requiredArg == "confirm") continue;
                    if (!suppliedArgNames.Contains(requiredArg))
                        return $"Step {i} ('{step.ToolName}'): missing required argument '{requiredArg}'.";
                }
            }

            return null;
        }

        private static string ValidateValueReference(string valueTemplate, int currentStepIndex, HashSet<string> paramNames)
        {
            if (string.IsNullOrEmpty(valueTemplate)) return "value is empty.";

            var paramMatch = ParamRefPattern.Match(valueTemplate);
            if (paramMatch.Success)
            {
                var refName = paramMatch.Groups[1].Value;
                if (!paramNames.Contains(refName))
                    return $"references parameter '{refName}', which isn't declared on this tool.";
                return null;
            }

            var stepMatch = StepRefPattern.Match(valueTemplate);
            if (stepMatch.Success)
            {
                var refStepIndex = int.Parse(stepMatch.Groups[1].Value);
                if (refStepIndex >= currentStepIndex)
                    return $"references step{refStepIndex}, which hasn't run yet at this point in the chain (must reference an earlier step).";
                return null;
            }

            // Anything else is treated as a literal — always valid as a reference, type
            // coercion is checked separately at generation time (BuildStepLine).
            return null;
        }

        private static string BuildSource(MCPCompositeToolSpec spec)
        {
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine($"# Auto-generated by the Unity MCP visual tool builder on {DateTime.UtcNow:yyyy-MM-dd}.");
            sb.AppendLine("# Hand-edits are fine -- this file is yours going forward; the builder only ever");
            sb.AppendLine("# appends new functions here, it never rewrites what's already in this file.");
            sb.AppendLine("@workflow(");
            sb.AppendLine($"    \"{spec.Name}\",");
            sb.AppendLine($"    {PyString(spec.Description)},");
            sb.AppendLine("    " + BuildSchemaLiteral(spec) + ",");
            sb.AppendLine($"    group=\"{spec.Group}\",");
            sb.AppendLine(")");
            sb.AppendLine($"async def _{spec.Name}(bridge, args):");

            for (int i = 0; i < spec.Steps.Count; i++)
            {
                sb.AppendLine("    " + BuildStepLine(spec.Steps[i], i));
            }

            sb.AppendLine($"    return step{spec.Steps.Count - 1}");
            sb.AppendLine();

            return sb.ToString();
        }

        private static string BuildStepLine(MCPCompositeStep step, int index)
        {
            MCPToolRegistry.TryGet(step.ToolName, out var entry);
            var properties = (Dictionary<string, object>)entry.Schema["properties"];

            var argPairs = new List<string>();
            foreach (var arg in step.Args ?? new List<MCPCompositeStepArg>())
            {
                var propSchema = (Dictionary<string, object>)properties[arg.ArgName];
                var pyValue = ResolveValue(arg.ValueTemplate, propSchema);
                argPairs.Add($"\"{arg.ArgName}\": {pyValue}");
            }

            // Destructive tools need confirm=true -- the generator always supplies this
            // explicitly rather than exposing it as a fillable field, since a composite
            // tool built from a destructive step should not silently no-op when called.
            if (entry.Destructive)
            {
                argPairs.Add("\"confirm\": True");
            }

            var argsDict = "{" + string.Join(", ", argPairs) + "}";
            return $"step{index} = await bridge.call(\"{step.ToolName}\", {argsDict})";
        }

        private static string ResolveValue(string valueTemplate, Dictionary<string, object> targetPropSchema)
        {
            var paramMatch = ParamRefPattern.Match(valueTemplate);
            if (paramMatch.Success)
            {
                return $"args[\"{paramMatch.Groups[1].Value}\"]";
            }

            var stepMatch = StepRefPattern.Match(valueTemplate);
            if (stepMatch.Success)
            {
                return $"step{stepMatch.Groups[1].Value}[\"{stepMatch.Groups[2].Value}\"]";
            }

            // Literal — coerce based on the target parameter's declared JSON Schema type.
            var jsonType = targetPropSchema.ContainsKey("type") ? (string)targetPropSchema["type"] : "string";
            switch (jsonType)
            {
                case "boolean":
                    if (bool.TryParse(valueTemplate, out var boolValue)) return boolValue ? "True" : "False";
                    return valueTemplate.Trim().ToLowerInvariant() == "true" ? "True" : "False";

                case "integer":
                    if (int.TryParse(valueTemplate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                        return intValue.ToString(CultureInfo.InvariantCulture);
                    return PyString(valueTemplate); // fall back to a string literal rather than emitting invalid Python

                case "number":
                    if (double.TryParse(valueTemplate, NumberStyles.Float, CultureInfo.InvariantCulture, out var numValue))
                        return numValue.ToString(CultureInfo.InvariantCulture);
                    return PyString(valueTemplate);

                default: // "string" or anything else (enums are represented as strings)
                    return PyString(valueTemplate);
            }
        }

        private static string BuildSchemaLiteral(MCPCompositeToolSpec spec)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\": \"object\", \"properties\": {");

            var propParts = new List<string>();
            foreach (var p in spec.Parameters)
            {
                propParts.Add($"\"{p.Name}\": {{\"type\": {PyString(JsonType(p.Type))}, \"description\": {PyString(p.Description)}}}");
            }
            sb.Append(string.Join(", ", propParts));
            sb.Append("}, \"required\": [");

            var requiredNames = spec.Parameters.Where(p => p.Required).Select(p => $"\"{p.Name}\"");
            sb.Append(string.Join(", ", requiredNames));
            sb.Append("]}");

            return sb.ToString();
        }

        private static string JsonType(MCPCompositeParamType type)
        {
            switch (type)
            {
                case MCPCompositeParamType.Int: return "integer";
                case MCPCompositeParamType.Float: return "number";
                case MCPCompositeParamType.Bool: return "boolean";
                default: return "string";
            }
        }

        /// <summary>Renders a C# string as a double-quoted Python string literal, with escaping.</summary>
        private static string PyString(string s)
        {
            if (s == null) return "\"\"";
            var escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            return $"\"{escaped}\"";
        }
    }
}
