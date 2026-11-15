using UnityEditor;
using UnityEngine;

namespace UnityMCP.UI
{
    /// <summary>
    /// The shared look of every Unity MCP window: a header banner, cards with a coloured status
    /// stripe, coloured status pills, tinted primary buttons, and labels that carry tooltips.
    /// Plain IMGUI, works in both the light and dark Editor skins. Call only from OnGUI.
    /// </summary>
    public static class MCPGui
    {
        public static readonly Color Good = new Color(0.30f, 0.69f, 0.31f);
        public static readonly Color Warn = new Color(0.95f, 0.62f, 0.14f);
        public static readonly Color Bad = new Color(0.86f, 0.26f, 0.24f);
        public static readonly Color Info = new Color(0.26f, 0.55f, 0.93f);
        public static readonly Color Neutral = new Color(0.55f, 0.55f, 0.55f);

        private static Color HeaderBackground => EditorGUIUtility.isProSkin ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.80f, 0.80f, 0.80f);
        private static Color Divider => EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.12f);

        private static GUIStyle _title, _subtitle, _cardTitle, _pill, _muted, _code, _card;

        private static GUIStyle TitleStyle => _title ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 };
        private static GUIStyle SubtitleStyle => _subtitle ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
        private static GUIStyle CardTitleStyle => _cardTitle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        private static GUIStyle CardStyle => _card ??= new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(12, 10, 8, 10) };

        private static GUIStyle PillStyle => _pill ??= new GUIStyle(EditorStyles.miniBoldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(7, 7, 1, 1),
            normal = { textColor = Color.white }
        };

        /// <summary>Secondary text: small, word-wrapped, slightly dimmed.</summary>
        public static GUIStyle Muted => _muted ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel)
        {
            normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.68f, 0.68f, 0.68f) : new Color(0.32f, 0.32f, 0.32f) }
        };

        /// <summary>Monospace-looking selectable block for commands, paths and arguments.</summary>
        public static GUIStyle Code => _code ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true, fontSize = 11 };

        /// <summary>Top banner: window title, one-line purpose, and an optional right-aligned pill (e.g. the version).</summary>
        public static void Header(string title, string subtitle, string pillText = null, Color? pillColor = null, string pillTooltip = null)
        {
            var rect = EditorGUILayout.BeginVertical();
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.x - 4, rect.y - 4, rect.width + 8, rect.height + 8), HeaderBackground);
                EditorGUI.DrawRect(new Rect(rect.x - 4, rect.yMax + 3, rect.width + 8, 2), Info);
            }
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, TitleStyle);
            GUILayout.FlexibleSpace();
            if (pillText != null) Pill(pillText, pillColor ?? Neutral, pillTooltip);
            GUILayout.EndHorizontal();
            GUILayout.Label(subtitle, SubtitleStyle);
            GUILayout.Space(4);
            EditorGUILayout.EndVertical();
            GUILayout.Space(12);
        }

        /// <summary>Starts a card: a padded box with a coloured stripe on its left edge and a bold title row.</summary>
        public static void BeginCard(string title, Color stripe, string tooltip = null)
        {
            var rect = EditorGUILayout.BeginVertical(CardStyle);
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 3, rect.height), stripe);
            GUILayout.Label(new GUIContent(title, tooltip), CardTitleStyle);
            GUILayout.Space(2);
        }

        public static void EndCard()
        {
            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
        }

        /// <summary>A small coloured status label ("Connected", "Off", "Required" ...).</summary>
        public static void Pill(string text, Color color, string tooltip = null, float minWidth = 0)
        {
            var content = new GUIContent(text, tooltip);
            var size = PillStyle.CalcSize(content);
            var rect = GUILayoutUtility.GetRect(Mathf.Max(size.x, minWidth), size.y + 2, PillStyle,
                GUILayout.Width(Mathf.Max(size.x, minWidth)), GUILayout.ExpandWidth(false));
            rect.y += 1;
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, color);
            GUI.Label(rect, content, PillStyle);
        }

        /// <summary>A button tinted with an accent colour, for the one action a section is about.</summary>
        public static bool AccentButton(GUIContent content, Color tint, params GUILayoutOption[] options)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = Color.Lerp(Color.white, tint, 0.65f);
            bool clicked = GUILayout.Button(content, options);
            GUI.backgroundColor = previous;
            return clicked;
        }

        /// <summary>An On/Off switch button whose colour shows the state.</summary>
        public static bool Switch(bool value, string tooltip, Color onColor)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = value ? Color.Lerp(Color.white, onColor, 0.7f) : previous;
            bool result = GUILayout.Toggle(value, new GUIContent(value ? "On" : "Off", tooltip), "Button", GUILayout.Width(56), GUILayout.Height(20));
            GUI.backgroundColor = previous;
            return result;
        }

        /// <summary>A field label with a tooltip; required fields get a " *".</summary>
        public static GUIContent Label(string text, string tooltip, bool required = false) =>
            new GUIContent(required ? text + " *" : text, required ? tooltip + " (Required.)" : tooltip);

        public static GUIContent Button(string text, string tooltip) => new GUIContent(text, tooltip);

        /// <summary>A thin horizontal rule between rows inside a card.</summary>
        public static void Separator()
        {
            GUILayout.Space(4);
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, Divider);
            GUILayout.Space(4);
        }

        /// <summary>Feedback from the last action, shown under the header so it's seen.</summary>
        public static void Feedback(string message, MessageType type)
        {
            if (string.IsNullOrEmpty(message)) return;
            EditorGUILayout.HelpBox(message, type);
            GUILayout.Space(8);
        }
    }
}
