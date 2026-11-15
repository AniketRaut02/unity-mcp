using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.UI;

namespace UnityMCP.ToolBuilder
{
    /// <summary>
    /// Window -> Unity MCP -> Tool Builder. Lets you assemble a new composite tool by
    /// chaining existing atomic tools together, then generates a real Python workflow
    /// function into &lt;project&gt;/UnityMCP/custom_workflows.py (outside Assets/, so Unity never
    /// imports it; committed with the project; loaded by the server at startup). This is the
    /// "tools that build tools" endpoint:
    /// no C# and no hand-written Python are required to add a new composite tool from here
    /// on — just pick tools, wire up their arguments, and click Generate.
    ///
    /// Kept thin on purpose: this file owns UI state and file I/O only. Every real decision
    /// (is this spec valid, what does the generated Python actually look like) lives in
    /// MCPCompositeToolGenerator, which is what's actually unit-tested.
    /// </summary>
    public class MCPToolBuilderWindow : EditorWindow
    {
        [MenuItem("Window/Unity MCP/Tool Builder")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPToolBuilderWindow>();
            window.titleContent = new GUIContent("Unity MCP Tool Builder");
            window.minSize = new Vector2(540, 480);
        }

        private MCPCompositeToolSpec _spec = new MCPCompositeToolSpec();
        private string _statusMessage = "";
        private MessageType _statusType = MessageType.Info;

        private static string CustomWorkflowsPath => Path.Combine(MCPProjectUtil.ProjectRoot, "UnityMCP", "custom_workflows.py");

        private const string FileHeader =
            "\"\"\"\n" +
            "This project's own composite tools for Unity MCP Bridge, loaded by the MCP server at startup.\n" +
            "Written by Window > Unity MCP > Tool Builder; you can also add @workflow functions by hand,\n" +
            "following unity_mcp_server/workflows.py in the package's Server~ folder.\n" +
            "\"\"\"\n" +
            "from unity_mcp_server.workflows import workflow  # noqa: F401\n";

        private Vector2 _scroll;

        private void OnGUI()
        {
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Space(4);
            MCPGui.Header("Tool Builder", "Chain existing tools into a new one, without writing code. Fields marked * are required.",
                "UnityMCP/custom_workflows.py", MCPGui.Neutral,
                "Generated tools are saved to this file in your project (outside Assets/) and load when the MCP server starts.");

            MCPGui.Feedback(_statusMessage, _statusType);

            MCPGui.BeginCard("Tool", MCPGui.Info, "How AI clients will see the new tool.");
            _spec.Name = EditorGUILayout.TextField(MCPGui.Label("Name",
                "Unique tool name in lowercase snake_case, e.g. spawn_enemy_wave. This is what the AI calls.", required: true), _spec.Name);
            _spec.Description = EditorGUILayout.TextField(MCPGui.Label("Description",
                "One or two sentences saying what the tool does and when to use it. The AI reads this to decide when to call it.", required: true), _spec.Description);
            _spec.Group = EditorGUILayout.TextField(MCPGui.Label("Group",
                "Tool group it belongs to, e.g. core or gameplay. Tools outside 'core' are hidden until that group is activated or found with search."), _spec.Group);
            MCPGui.EndCard();

            DrawParameters();
            DrawSteps();

            GUILayout.Space(4);
            if (MCPGui.AccentButton(MCPGui.Button("Generate tool",
                    "Validate the tool and append it to UnityMCP/custom_workflows.py. Restart your AI client session to use it."),
                    MCPGui.Good, GUILayout.Height(30)))
            {
                GenerateAndAppend();
            }
            GUILayout.Space(8);
            GUILayout.EndScrollView();
        }

        private void DrawParameters()
        {
            MCPGui.BeginCard("Parameters", MCPGui.Neutral, "Inputs the AI passes to your tool. Optional: a tool can take none.");

            if (_spec.Parameters.Count > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(MCPGui.Label("Name", "Parameter name, a valid identifier, e.g. enemyCount.", required: true), EditorStyles.miniBoldLabel, GUILayout.Width(140));
                GUILayout.Label(MCPGui.Label("Type", "Value type the AI must send."), EditorStyles.miniBoldLabel, GUILayout.Width(80));
                GUILayout.Label(MCPGui.Label("Description", "Tells the AI what to pass here."), EditorStyles.miniBoldLabel);
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("No parameters yet.", MCPGui.Muted);
            }

            for (int i = 0; i < _spec.Parameters.Count; i++)
            {
                var p = _spec.Parameters[i];
                GUILayout.BeginHorizontal();
                p.Name = EditorGUILayout.TextField(p.Name, GUILayout.Width(140));
                p.Type = (MCPCompositeParamType)EditorGUILayout.EnumPopup(p.Type, GUILayout.Width(80));
                p.Description = EditorGUILayout.TextField(p.Description);
                if (GUILayout.Button(MCPGui.Button("×", "Remove this parameter."), GUILayout.Width(24)))
                {
                    _spec.Parameters.RemoveAt(i);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(2);
            if (GUILayout.Button(MCPGui.Button("+ Add parameter", "Add an input the AI provides when it calls your tool."), GUILayout.Width(130)))
            {
                _spec.Parameters.Add(new MCPCompositeParam { Name = "param" + _spec.Parameters.Count });
            }
            MCPGui.EndCard();
        }

        private void DrawSteps()
        {
            MCPGui.BeginCard("Steps *", _spec.Steps.Count > 0 ? MCPGui.Good : MCPGui.Warn,
                "The existing tools your tool runs, in order. At least one step is required.");
            GUILayout.Label("Argument values can be a literal (Box, 5), {paramName} for one of your parameters, or " +
                            "{stepN.field} for a field from an earlier step's result, e.g. {step0.path}.", MCPGui.Muted);

            for (int i = 0; i < _spec.Steps.Count; i++)
            {
                var step = _spec.Steps[i];
                MCPGui.Separator();

                GUILayout.BeginHorizontal();
                MCPGui.Pill($"step{i}", MCPGui.Info, $"Refer to this step's result as {{step{i}.field}} in later steps.", 52);
                GUILayout.Space(4);
                step.ToolName = EditorGUILayout.TextField(GUIContent.none, step.ToolName);
                bool known = !string.IsNullOrEmpty(step.ToolName) && MCPToolRegistry.TryGet(step.ToolName, out _);
                if (!string.IsNullOrEmpty(step.ToolName))
                    MCPGui.Pill(known ? "found" : "unknown", known ? MCPGui.Good : MCPGui.Warn,
                        known ? "A Unity tool with this name exists." : "No Unity tool has this name. Check the spelling (composite tools can't be steps).");
                if (GUILayout.Button(MCPGui.Button("×", "Remove this step."), GUILayout.Width(24)))
                {
                    _spec.Steps.RemoveAt(i);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
                GUILayout.Label(new GUIContent("Tool name *", "Exact name of an existing Unity tool, e.g. create_gameobject. Required."), MCPGui.Muted);

                DrawKnownParams(step.ToolName);

                for (int a = 0; a < step.Args.Count; a++)
                {
                    var arg = step.Args[a];
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(56);
                    arg.ArgName = EditorGUILayout.TextField(new GUIContent("", "Argument name of the tool above (see its parameters)."), arg.ArgName, GUILayout.Width(140));
                    GUILayout.Label("=", GUILayout.Width(12));
                    arg.ValueTemplate = EditorGUILayout.TextField(new GUIContent("", "Literal value, {paramName} or {stepN.field}."), arg.ValueTemplate);
                    if (GUILayout.Button(MCPGui.Button("×", "Remove this argument."), GUILayout.Width(24)))
                    {
                        step.Args.RemoveAt(a);
                        GUILayout.EndHorizontal();
                        break;
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                GUILayout.Space(56);
                if (GUILayout.Button(MCPGui.Button("+ Add argument", "Pass a value to this step's tool."), GUILayout.Width(120)))
                {
                    step.Args.Add(new MCPCompositeStepArg());
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            if (GUILayout.Button(MCPGui.Button("+ Add step", "Add another existing tool to run after the previous steps."), GUILayout.Width(110)))
            {
                _spec.Steps.Add(new MCPCompositeStep());
            }
            MCPGui.EndCard();
        }

        /// <summary>Shows the real parameter names of the currently-typed tool, purely as a hint.</summary>
        private void DrawKnownParams(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return;
            if (!MCPToolRegistry.TryGet(toolName, out var entry)) return;

            var properties = (Dictionary<string, object>)entry.Schema["properties"];
            var names = string.Join(", ", properties.Keys.Where(k => k != "confirm"));
            GUILayout.Label(new GUIContent($"Its parameters: {names}", entry.Description), MCPGui.Muted);
        }

        private void GenerateAndAppend()
        {
            string existingContent = "";
            if (File.Exists(CustomWorkflowsPath))
            {
                existingContent = File.ReadAllText(CustomWorkflowsPath);
            }

            var generated = MCPCompositeToolGenerator.Generate(_spec, existingContent, out var error);

            if (error != null)
            {
                _statusType = MessageType.Error;
                _statusMessage = error;
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CustomWorkflowsPath));
            if (!File.Exists(CustomWorkflowsPath)) File.WriteAllText(CustomWorkflowsPath, FileHeader);
            File.AppendAllText(CustomWorkflowsPath, generated);

            _statusType = MessageType.Info;
            _statusMessage = $"Generated '{_spec.Name}' into UnityMCP/custom_workflows.py. " +
                              "Restart your MCP client session (or reconnect) to pick it up.";
        }
    }
}
