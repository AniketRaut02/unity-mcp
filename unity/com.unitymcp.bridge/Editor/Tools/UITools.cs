using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityMCP;

namespace UnityMCP.Tools
{
    public enum MCPCanvasRenderMode
    {
        ScreenSpaceOverlay,
        ScreenSpaceCamera,
        WorldSpace
    }

    public enum MCPUIElementType
    {
        Panel,
        Button,
        Text,
        Image,
        InputField
    }

    public enum MCPLayoutType
    {
        Horizontal,
        Vertical,
        Grid
    }

    public static class UITools
    {
        [MCPTool(
            "create_canvas",
            "Creates a Canvas GameObject with CanvasScaler and GraphicRaycaster attached — the standard root for any " +
            "UGUI UI. Also creates an EventSystem in the scene if one doesn't already exist (required for UI input to " +
            "work at all). Requires the Unity UI (uGUI) package, included by default in virtually every Unity template.",
            group: "ui")]
        public static MCPResult CreateCanvas(
            MCPToolContext ctx,
            [MCPParam("GameObject name for the canvas.")] string name = "Canvas",
            [MCPParam("How the canvas renders: ScreenSpaceOverlay (default, on top of everything), ScreenSpaceCamera, or WorldSpace.")] MCPCanvasRenderMode renderMode = MCPCanvasRenderMode.ScreenSpaceOverlay)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Canvas");

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = MapRenderMode(renderMode);
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool(
            "create_ui_element",
            "Creates a common UGUI element under a parent hierarchy path (typically a Canvas or another UI element): " +
            "Panel (a semi-transparent Image), Button (Image + Button + child label), Text, Image, or InputField " +
            "(Image + InputField + placeholder/text children). This is a composite tool — several atomic operations " +
            "(create GameObject, add components, create children) bundled into one call, the same 'composition over " +
            "code' pattern the whole platform is built on. Uses legacy UnityEngine.UI.Text, not TextMeshPro — swap " +
            "manually if your project uses TMP.",
            group: "ui")]
        public static MCPResult CreateUIElement(
            MCPToolContext ctx,
            [MCPParam("Which UGUI element to create.")] MCPUIElementType type,
            [MCPParam("Hierarchy path of the parent (typically a Canvas or another UI element).")] string parentPath,
            [MCPParam("GameObject name for the new element. Omit to use the element type's name.")] string name = null)
        {
            var parent = MCPSceneUtil.ResolvePath(parentPath);
            if (parent == null) return MCPResult.Fail($"Parent path '{parentPath}' not found.");

            var go = new GameObject(string.IsNullOrEmpty(name) ? type.ToString() : name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create UI Element");
            go.transform.SetParent(parent.transform, false);

            switch (type)
            {
                case MCPUIElementType.Panel:
                    go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.4f);
                    break;

                case MCPUIElementType.Button:
                {
                    go.AddComponent<Image>();
                    go.AddComponent<Button>();

                    var label = new GameObject("Text", typeof(RectTransform));
                    Undo.RegisterCreatedObjectUndo(label, "MCP: Create UI Element");
                    label.transform.SetParent(go.transform, false);
                    var text = label.AddComponent<Text>();
                    text.text = "Button";
                    text.alignment = TextAnchor.MiddleCenter;
                    text.color = Color.black;
                    text.font = GetDefaultFont();
                    StretchToParent(label.GetComponent<RectTransform>());
                    break;
                }

                case MCPUIElementType.Text:
                {
                    var text = go.AddComponent<Text>();
                    text.text = "Text";
                    text.color = Color.black;
                    text.font = GetDefaultFont();
                    break;
                }

                case MCPUIElementType.Image:
                    go.AddComponent<Image>();
                    break;

                case MCPUIElementType.InputField:
                {
                    go.AddComponent<Image>();
                    var field = go.AddComponent<InputField>();

                    var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
                    Undo.RegisterCreatedObjectUndo(placeholderGo, "MCP: Create UI Element");
                    placeholderGo.transform.SetParent(go.transform, false);
                    var placeholder = placeholderGo.AddComponent<Text>();
                    placeholder.text = "Enter text...";
                    placeholder.fontStyle = FontStyle.Italic;
                    placeholder.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);
                    placeholder.font = GetDefaultFont();
                    StretchToParent(placeholderGo.GetComponent<RectTransform>());

                    var textGo = new GameObject("Text", typeof(RectTransform));
                    Undo.RegisterCreatedObjectUndo(textGo, "MCP: Create UI Element");
                    textGo.transform.SetParent(go.transform, false);
                    var text = textGo.AddComponent<Text>();
                    text.color = Color.black;
                    text.font = GetDefaultFont();
                    StretchToParent(textGo.GetComponent<RectTransform>());

                    field.textComponent = text;
                    field.placeholder = placeholder;
                    break;
                }
            }

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }

        [MCPTool(
            "set_rect_transform",
            "Sets anchorMin/anchorMax/pivot/anchoredPosition/sizeDelta on a UI element's RectTransform by path. All " +
            "values are 2D (x,y). Omitted values are left unchanged.",
            group: "ui")]
        public static MCPResult SetRectTransform(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target UI element.")] string path,
            [MCPParam("Anchor min X (0-1, fraction of parent width). Omit to leave unchanged.")] float? anchorMinX = null,
            [MCPParam("Anchor min Y (0-1, fraction of parent height). Omit to leave unchanged.")] float? anchorMinY = null,
            [MCPParam("Anchor max X (0-1, fraction of parent width). Omit to leave unchanged.")] float? anchorMaxX = null,
            [MCPParam("Anchor max Y (0-1, fraction of parent height). Omit to leave unchanged.")] float? anchorMaxY = null,
            [MCPParam("Pivot X (0-1). Omit to leave unchanged.")] float? pivotX = null,
            [MCPParam("Pivot Y (0-1). Omit to leave unchanged.")] float? pivotY = null,
            [MCPParam("Anchored position X, in pixels from the anchor. Omit to leave unchanged.")] float? posX = null,
            [MCPParam("Anchored position Y, in pixels from the anchor. Omit to leave unchanged.")] float? posY = null,
            [MCPParam("Width in pixels. Omit to leave unchanged.")] float? sizeX = null,
            [MCPParam("Height in pixels. Omit to leave unchanged.")] float? sizeY = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return MCPResult.Fail($"GameObject at '{path}' has no RectTransform (is it a UI element?).");

            Undo.RecordObject(rt, "MCP: Set RectTransform");

            var anchorMin = rt.anchorMin;
            ApplyVector2Overrides(ref anchorMin, anchorMinX, anchorMinY);
            rt.anchorMin = anchorMin;

            var anchorMax = rt.anchorMax;
            ApplyVector2Overrides(ref anchorMax, anchorMaxX, anchorMaxY);
            rt.anchorMax = anchorMax;

            var pivot = rt.pivot;
            ApplyVector2Overrides(ref pivot, pivotX, pivotY);
            rt.pivot = pivot;

            var pos = rt.anchoredPosition;
            ApplyVector2Overrides(ref pos, posX, posY);
            rt.anchoredPosition = pos;

            var size = rt.sizeDelta;
            ApplyVector2Overrides(ref size, sizeX, sizeY);
            rt.sizeDelta = size;

            return MCPResult.Success(RectTransformStateAnon(rt));
        }

        [MCPTool("get_rect_transform", "Reads back anchorMin/anchorMax/pivot/anchoredPosition/sizeDelta for a UI element by path.", group: "ui")]
        public static MCPResult GetRectTransform(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target UI element.")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return MCPResult.Fail($"GameObject at '{path}' has no RectTransform.");

            return MCPResult.Success(RectTransformStateAnon(rt));
        }

        [MCPTool(
            "set_layout",
            "Adds a layout group to a GameObject by path if it doesn't have one of the requested type yet, otherwise " +
            "reconfigures the existing one (layout groups are effectively singleton-per-GameObject, unlike colliders). " +
            "Horizontal/Vertical/Grid — automatically arranges children.",
            group: "ui")]
        public static MCPResult SetLayout(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Layout group type: Horizontal, Vertical, or Grid.")] MCPLayoutType type,
            [MCPParam("Spacing between children on the X axis (Horizontal), or cell spacing X (Grid). Ignored for Vertical.")] float spacingX = 0f,
            [MCPParam("Spacing between children on the Y axis (Vertical), or cell spacing Y (Grid). Ignored for Horizontal.")] float spacingY = 0f,
            [MCPParam("Left padding in pixels.")] int paddingLeft = 0,
            [MCPParam("Right padding in pixels.")] int paddingRight = 0,
            [MCPParam("Top padding in pixels.")] int paddingTop = 0,
            [MCPParam("Bottom padding in pixels.")] int paddingBottom = 0,
            [MCPParam("Whether children are force-expanded to fill available width. Horizontal/Vertical only.")] bool childForceExpandWidth = true,
            [MCPParam("Whether children are force-expanded to fill available height. Horizontal/Vertical only.")] bool childForceExpandHeight = true)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var padding = new RectOffset(paddingLeft, paddingRight, paddingTop, paddingBottom);

            switch (type)
            {
                case MCPLayoutType.Horizontal:
                {
                    var layout = go.GetComponent<HorizontalLayoutGroup>();
                    if (layout == null) layout = go.AddComponent<HorizontalLayoutGroup>();
                    layout.spacing = spacingX;
                    layout.padding = padding;
                    layout.childForceExpandWidth = childForceExpandWidth;
                    layout.childForceExpandHeight = childForceExpandHeight;
                    break;
                }
                case MCPLayoutType.Vertical:
                {
                    var layout = go.GetComponent<VerticalLayoutGroup>();
                    if (layout == null) layout = go.AddComponent<VerticalLayoutGroup>();
                    layout.spacing = spacingY;
                    layout.padding = padding;
                    layout.childForceExpandWidth = childForceExpandWidth;
                    layout.childForceExpandHeight = childForceExpandHeight;
                    break;
                }
                case MCPLayoutType.Grid:
                {
                    var layout = go.GetComponent<GridLayoutGroup>();
                    if (layout == null) layout = go.AddComponent<GridLayoutGroup>();
                    layout.spacing = new Vector2(spacingX, spacingY);
                    layout.padding = padding;
                    break;
                }
            }

            return MCPResult.Success(new { path, type = type.ToString() });
        }

        [MCPTool(
            "set_ui_color",
            "Sets the color (including alpha) on a UI Graphic component by path — covers both Image and Text (and TMP " +
            "text) in one tool since they share the common Graphic base class.",
            group: "ui")]
        public static MCPResult SetUIColor(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target UI element (must have an Image or Text component).")] string path,
            [MCPParam("Red component (0-1). Omit to leave unchanged.")] float? r = null,
            [MCPParam("Green component (0-1). Omit to leave unchanged.")] float? g = null,
            [MCPParam("Blue component (0-1). Omit to leave unchanged.")] float? b = null,
            [MCPParam("Alpha component (0-1). Omit to leave unchanged.")] float? a = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var graphic = go.GetComponent<Graphic>();
            if (graphic == null) return MCPResult.Fail($"GameObject at '{path}' has no Image/Text (Graphic) component.");

            Undo.RecordObject(graphic, "MCP: Set UI Color");

            var color = graphic.color;
            if (r.HasValue) color.r = r.Value;
            if (g.HasValue) color.g = g.Value;
            if (b.HasValue) color.b = b.Value;
            if (a.HasValue) color.a = a.Value;
            graphic.color = color;

            return MCPResult.Success(new { path, color = new { r = color.r, g = color.g, b = color.b, a = color.a } });
        }

        private static void EnsureEventSystem()
        {
            // FindObjectOfType is the obsolete-but-functional name on newer Unity versions
            // (FindFirstObjectByType is the current recommendation) — same caveat as
            // Rigidbody.drag elsewhere in this codebase; swap if your Editor warns about it.
            if (Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() != null) return;

            var go = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create EventSystem");
            go.AddComponent<UnityEngine.EventSystems.EventSystem>();
            // StandaloneInputModule assumes the legacy Input Manager. If this project uses
            // the newer Input System package, swap this for InputSystemUIInputModule.
            go.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        private static Font GetDefaultFont()
        {
            // Built-in resource name changed across Unity versions (font licensing) —
            // try both rather than guessing which your Editor version uses.
            var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font; // may still be null on some versions; Text falls back to Unity's own default rendering
        }

        private static void StretchToParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        internal static void ApplyVector2Overrides(ref Vector2 vec, float? x, float? y)
        {
            if (x.HasValue) vec.x = x.Value;
            if (y.HasValue) vec.y = y.Value;
        }

        private static object Vec2ToAnon(Vector2 v) => new { x = v.x, y = v.y };

        private static object RectTransformStateAnon(RectTransform rt) => new
        {
            anchorMin = Vec2ToAnon(rt.anchorMin),
            anchorMax = Vec2ToAnon(rt.anchorMax),
            pivot = Vec2ToAnon(rt.pivot),
            anchoredPosition = Vec2ToAnon(rt.anchoredPosition),
            sizeDelta = Vec2ToAnon(rt.sizeDelta)
        };

        private static RenderMode MapRenderMode(MCPCanvasRenderMode mode)
        {
            switch (mode)
            {
                case MCPCanvasRenderMode.ScreenSpaceCamera: return RenderMode.ScreenSpaceCamera;
                case MCPCanvasRenderMode.WorldSpace: return RenderMode.WorldSpace;
                default: return RenderMode.ScreenSpaceOverlay;
            }
        }
    }
}
