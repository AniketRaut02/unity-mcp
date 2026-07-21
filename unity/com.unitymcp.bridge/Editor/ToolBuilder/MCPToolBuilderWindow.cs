using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.ToolBuilder
{
    /// <summary>
    /// Window -> Unity MCP -> Tool Builder. Lets you assemble a new composite tool by
    /// chaining existing atomic tools together, then generates a real Python workflow
    /// function into custom_workflows.py. This is the "tools that build tools" endpoint:
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
            window.minSize = new Vector2(520, 420);
        }

        private MCPCompositeToolSpec _spec = new MCPCompositeToolSpec();
        private string _statusMessage = "";
        private MessageType _statusType = MessageType.Info;

        private static string CustomWorkflowsPath => Path.Combine(MCPToolBuilderSettings.PythonServerPath, "custom_workflows.py");

        private void OnGUI()
        {
            DrawPythonServerPathField();

            GUILayout.Space(10);
            GUILayout.Label("New Composite Tool", EditorStyles.boldLabel);
            _spec.Name = EditorGUILayout.TextField("Name (snake_case)", _spec.Name);
            _spec.Description = EditorGUILayout.TextField("Description", _spec.Description);
            _spec.Group = EditorGUILayout.TextField("Group", _spec.Group);

            GUILayout.Space(10);
            DrawParameters();

            GUILayout.Space(10);
            DrawSteps();

            GUILayout.Space(12);
            if (GUILayout.Button("Generate", GUILayout.Height(28)))
            {
                GenerateAndAppend();
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Space(10);
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        private void DrawPythonServerPathField()
        {
            GUILayout.Label("Python Server Location", EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            var current = MCPToolBuilderSettings.PythonServerPath;
            var updated = EditorGUILayout.TextField(current);
            if (updated != current) MCPToolBuilderSettings.PythonServerPath = updated;

            if (GUILayout.Button("Browse...", GUILayout.Width(80)))
            {
                var chosen = EditorUtility.OpenFolderPanel(
                    "Select the unity_mcp_server folder", MCPToolBuilderSettings.PythonServerPath, "");
                if (!string.IsNullOrEmpty(chosen)) MCPToolBuilderSettings.PythonServerPath = chosen;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("The folder containing workflows.py and server.py.", EditorStyles.miniLabel);
        }

        private void DrawParameters()
        {
            GUILayout.Label("Parameters (what the new tool accepts)", EditorStyles.boldLabel);

            for (int i = 0; i < _spec.Parameters.Count; i++)
            {
                var p = _spec.Parameters[i];
                GUILayout.BeginHorizontal();
                p.Name = EditorGUILayout.TextField(p.Name, GUILayout.Width(140));
                p.Type = (MCPCompositeParamType)EditorGUILayout.EnumPopup(p.Type, GUILayout.Width(80));
                p.Description = EditorGUILayout.TextField(p.Description);
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    _spec.Parameters.RemoveAt(i);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Parameter", GUILayout.Width(140)))
            {
                _spec.Parameters.Add(new MCPCompositeParam { Name = "param" + _spec.Parameters.Count });
            }
        }

        private void DrawSteps()
        {
            GUILayout.Label("Steps (existing tools to chain, in order)", EditorStyles.boldLabel);
            GUILayout.Label(
                "Argument values: a literal (e.g. Box, 5), {paramName} for one of this tool's own " +
                "parameters above, or {stepN.field} for a field from an earlier step's result (e.g. {step0.path}).",
                EditorStyles.wordWrappedLabel);

            for (int i = 0; i < _spec.Steps.Count; i++)
            {
                var step = _spec.Steps[i];
                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.BeginHorizontal();
                GUILayout.Label($"step{i}:", GUILayout.Width(50));
                step.ToolName = EditorGUILayout.TextField(step.ToolName);
                if (GUILayout.Button("Remove step", GUILayout.Width(90)))
                {
                    _spec.Steps.RemoveAt(i);
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    break;
                }
                GUILayout.EndHorizontal();

                DrawKnownParams(step.ToolName);

                for (int a = 0; a < step.Args.Count; a++)
                {
                    var arg = step.Args[a];
                    GUILayout.BeginHorizontal();
                    arg.ArgName = EditorGUILayout.TextField(arg.ArgName, GUILayout.Width(140));
                    arg.ValueTemplate = EditorGUILayout.TextField(arg.ValueTemplate);
                    if (GUILayout.Button("x", GUILayout.Width(24)))
                    {
                        step.Args.RemoveAt(a);
                        GUILayout.EndHorizontal();
                        break;
                    }
                    GUILayout.EndHorizontal();
                }

                if (GUILayout.Button("+ Add Arg", GUILayout.Width(90)))
                {
                    step.Args.Add(new MCPCompositeStepArg());
                }

                GUILayout.EndVertical();
            }

            if (GUILayout.Button("+ Add Step", GUILayout.Width(100)))
            {
                _spec.Steps.Add(new MCPCompositeStep());
            }
        }

        /// <summary>Shows the real parameter names of the currently-typed tool, purely as a hint — read-only.</summary>
        private void DrawKnownParams(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return;
            if (!MCPToolRegistry.TryGet(toolName, out var entry)) return;

            var properties = (Dictionary<string, object>)entry.Schema["properties"];
            var names = string.Join(", ", properties.Keys.Where(k => k != "confirm"));
            GUILayout.Label($"  parameters: {names}", EditorStyles.miniLabel);
        }

        private void GenerateAndAppend()
        {
            var serverPath = MCPToolBuilderSettings.PythonServerPath;
            if (string.IsNullOrEmpty(serverPath) || !Directory.Exists(serverPath))
            {
                _statusType = MessageType.Error;
                _statusMessage = "Set the Python server location above first (the folder containing workflows.py and server.py).";
                return;
            }

            if (MCPToolBuilderSettings.IsInsideProject(serverPath, MCPProjectUtil.ProjectRoot))
            {
                _statusType = MessageType.Error;
                _statusMessage =
                    $"Python server location '{serverPath}' is inside this Unity project ({MCPProjectUtil.ProjectRoot}). " +
                    "Point it at wherever unity_mcp_server actually lives on disk, outside the Unity project entirely. " +
                    "Writing here can trigger an unrelated Unity reimport/recompile and disconnect the bridge mid-generation.";
                return;
            }

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

            File.AppendAllText(CustomWorkflowsPath, generated);

            _statusType = MessageType.Info;
            _statusMessage = $"Generated '{_spec.Name}' into custom_workflows.py. " +
                              "Restart your MCP client session (or reconnect) to pick it up.";
        }
    }
}
