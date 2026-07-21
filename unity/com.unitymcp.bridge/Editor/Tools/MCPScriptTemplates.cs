namespace UnityMCP.Tools
{
    public enum MCPScriptTemplate
    {
        MonoBehaviour,
        PlainClass,
        ScriptableObject
    }

    /// <summary>
    /// Renders minimal, compilable boilerplate for create_script. Deliberately plain —
    /// this is meant to give an agent a valid starting file it then edits via
    /// update_script, not to be a full code-generation system.
    /// </summary>
    internal static class MCPScriptTemplates
    {
        public static string Render(MCPScriptTemplate template, string className, string namespaceName)
        {
            string body;
            switch (template)
            {
                case MCPScriptTemplate.ScriptableObject:
                    body =
$@"using UnityEngine;

[CreateAssetMenu(fileName = ""{className}"", menuName = ""ScriptableObjects/{className}"")]
public class {className} : ScriptableObject
{{
}}
";
                    break;

                case MCPScriptTemplate.PlainClass:
                    body =
$@"public class {className}
{{
}}
";
                    break;

                default: // MonoBehaviour
                    body =
$@"using UnityEngine;

public class {className} : MonoBehaviour
{{
    private void Start()
    {{
    }}

    private void Update()
    {{
    }}
}}
";
                    break;
            }

            if (string.IsNullOrEmpty(namespaceName))
                return body;

            return $"namespace {namespaceName}\n{{\n{Indent(body)}\n}}\n";
        }

        private static string Indent(string text)
        {
            var lines = text.TrimEnd('\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Length > 0) lines[i] = "    " + lines[i];
            return string.Join("\n", lines);
        }
    }
}
