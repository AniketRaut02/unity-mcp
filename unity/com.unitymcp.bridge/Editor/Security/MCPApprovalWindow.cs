using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityMCP.UI;

namespace UnityMCP.Security
{
    /// <summary>
    /// Lists danger-tier calls waiting for a human decision (see MCPApprovalGate). Opens by
    /// itself when an agent asks for one; nothing here blocks the Editor or the agent.
    /// </summary>
    public class MCPApprovalWindow : EditorWindow
    {
        private const int MaxArgsShown = 2000;

        private readonly Dictionary<string, bool> _allowForSession = new Dictionary<string, bool>();
        private Vector2 _scroll;

        [MenuItem("Window/Unity MCP/Approvals")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPApprovalWindow>(false, "MCP Approvals", true);
            window.minSize = new Vector2(440, 260);
            window.Repaint();
        }

        private void OnEnable() => MCPApprovalGate.Changed += Repaint;
        private void OnDisable() => MCPApprovalGate.Changed -= Repaint;

        private void OnGUI()
        {
            var pending = MCPApprovalGate.Pending;

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Space(4);
            MCPGui.Header("Approvals", "Danger-tier actions an AI client asked to run. Nothing runs until you approve it.",
                pending.Count == 0 ? "nothing waiting" : $"{pending.Count} waiting",
                pending.Count == 0 ? MCPGui.Good : MCPGui.Warn,
                "Requests waiting for your decision.");

            if (pending.Count == 0)
            {
                MCPGui.BeginCard("All clear", MCPGui.Good);
                GUILayout.Label("No tool calls are waiting for approval. This window opens by itself when one is.", MCPGui.Muted);
                MCPGui.EndCard();
                GUILayout.EndScrollView();
                return;
            }

            string resolveKey = null;
            bool resolveApprove = false;

            foreach (var request in pending)
            {
                MCPGui.BeginCard(request.tool, MCPGui.Warn, "The tool the AI client wants to run.");

                GUILayout.BeginHorizontal();
                MCPGui.Pill("waiting", MCPGui.Warn, "Not run yet.");
                GUILayout.Space(6);
                GUILayout.Label($"Requested at {request.requestedAt}", MCPGui.Muted);
                GUILayout.EndHorizontal();

                GUILayout.Space(4);
                GUILayout.Label(new GUIContent("Arguments", "Exactly what the tool will run with. The AI's retry must match these to use your approval."), EditorStyles.miniBoldLabel);
                var args = request.argsJson.Length > MaxArgsShown
                    ? request.argsJson.Substring(0, MaxArgsShown) + "...(truncated)"
                    : request.argsJson;
                EditorGUILayout.SelectableLabel(args, MCPGui.Code,
                    GUILayout.MinHeight(EditorGUIUtility.singleLineHeight * 2), GUILayout.ExpandHeight(false));

                _allowForSession.TryGetValue(request.key, out var allow);
                _allowForSession[request.key] = EditorGUILayout.ToggleLeft(
                    new GUIContent($"Also allow '{request.tool}' without asking until the Editor closes",
                        "Only applies if you Approve. Other danger-tier tools still ask."),
                    allow);

                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (MCPGui.AccentButton(MCPGui.Button("Approve", "Let the AI client's next identical call run once."), MCPGui.Good,
                        GUILayout.Width(110), GUILayout.Height(24)))
                { resolveKey = request.key; resolveApprove = true; }
                if (MCPGui.AccentButton(MCPGui.Button("Deny", "Tell the AI client you said no. It won't run."), MCPGui.Bad,
                        GUILayout.Width(110), GUILayout.Height(24)))
                { resolveKey = request.key; resolveApprove = false; }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                MCPGui.EndCard();
            }
            GUILayout.EndScrollView();

            // Applied after drawing so the layout isn't changed mid-draw.
            if (resolveKey != null)
            {
                _allowForSession.TryGetValue(resolveKey, out var allowSession);
                MCPApprovalGate.Resolve(resolveKey, resolveApprove, resolveApprove && allowSession);
                _allowForSession.Remove(resolveKey);
            }
        }
    }
}
