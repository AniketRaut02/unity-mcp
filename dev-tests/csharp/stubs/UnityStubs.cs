// Minimal stand-ins for Unity API surface used by the Phase 1 code, purely so we can
// run a real C# compiler over the actual source files and catch syntax/type errors
// that a text read would miss. Not shipped; Unity provides the real implementations.
using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Collider : Component
    {
        public bool isTrigger;
    }

    public class BoxCollider : Collider
    {
        public Vector3 size = new Vector3(1f, 1f, 1f);
        public Vector3 center = new Vector3(0f, 0f, 0f);
    }

    public class SphereCollider : Collider
    {
        public float radius = 0.5f;
        public Vector3 center = new Vector3(0f, 0f, 0f);
    }

    public class CapsuleCollider : Collider
    {
        public float radius = 0.5f;
        public float height = 2f;
        public Vector3 center = new Vector3(0f, 0f, 0f);
    }

    public enum ForceMode { Force, Impulse, VelocityChange, Acceleration }

    public class Rigidbody : Component
    {
        public float mass = 1f;
        public float drag = 0f;
        public float angularDrag = 0.05f;
        public bool useGravity = true;
        public bool isKinematic = false;
        public Vector3 velocity;
        public Vector3 angularVelocity;
        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force) {}
    }

    public struct RaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
        public Collider collider;
    }

    public static class Physics
    {
        public const int DefaultRaycastLayers = ~0; // stub value: "all layers", close enough for compile/logic checks
        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hitInfo, float maxDistance, int layerMask)
        {
            // Always misses in the stub environment — there's no real physics world to hit.
            // Tests use this to verify the input-validation and miss-result-shape paths;
            // the hit-result-shape path is exercised by inspection/code review instead.
            hitInfo = default(RaycastHit);
            return false;
        }
    }

    public class Object
    {
        public static void DestroyImmediate(Object obj) {}
        public static T[] FindObjectsOfType<T>() where T : Object => new T[0];
        public static T FindObjectOfType<T>() where T : Object => null;
        public static T Instantiate<T>(T original, Transform parent) where T : Object => original;
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color black => new Color(0f, 0f, 0f, 1f);
        public static Color white => new Color(1f, 1f, 1f, 1f);
    }

    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }
    public enum FontStyle { Normal, Bold, Italic, BoldAndItalic }
    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace }

    public class Font : Object {}

    public static class Resources
    {
        public static T GetBuiltinResource<T>(string path) where T : Object => null;
    }

    public class RectOffset
    {
        public int left, right, top, bottom;
        public RectOffset(int left, int right, int top, int bottom)
        {
            this.left = left; this.right = right; this.top = top; this.bottom = bottom;
        }
    }

    public class Shader : Object
    {
        public static Shader Find(string name) => new Shader();
    }

    public class Material : Object
    {
        public Material(Shader shader) {}
        public Color color;
    }

    public class ScriptableObject : Object
    {
        public static ScriptableObject CreateInstance(Type type) => (ScriptableObject)Activator.CreateInstance(type);
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
        public T GetComponent<T>() => default(T);
        public Component GetComponent(Type t) => null;
        public T[] GetComponents<T>() => new T[0];
        public T AddComponent<T>() where T : Component, new() => new T();

        // Real Unity's Component.name proxies to gameObject.name -- this is what lets
        // a MonoBehaviour subclass reference bare `name` and get its own GameObject's
        // name, a very common idiom (see BTNodeComponent subclasses in the BT framework).
        public string name
        {
            get => gameObject != null ? gameObject.name : null;
            set { if (gameObject != null) gameObject.name = value; }
        }
    }

    /// <summary>
    /// Real MonoBehaviour has Start/Update/etc. called automatically by the Unity
    /// runtime loop; this stub doesn't simulate that scheduling (nothing in this
    /// codebase drives it), it exists purely so BT framework scripts that declare
    /// `class Foo : MonoBehaviour` have a real base type to compile against.
    /// </summary>
    public class MonoBehaviour : Component {}

    public class Transform : Component, IEnumerable
    {
        public Transform parent;
        public string name;
        public Vector3 position;
        public Vector3 localPosition;
        public Vector3 localEulerAngles;
        public Vector3 localScale;
        public Transform Find(string n) => null;
        public void SetParent(Transform p, bool worldPositionStays = true) { parent = p; }
        public IEnumerator GetEnumerator() => new List<Transform>().GetEnumerator();
    }

    public class RectTransform : Transform
    {
        public Vector2 anchorMin = new Vector2(0f, 0f);
        public Vector2 anchorMax = new Vector2(1f, 1f);
        public Vector2 pivot = new Vector2(0.5f, 0.5f);
        public Vector2 anchoredPosition;
        public Vector2 sizeDelta;
        public Vector2 offsetMin;
        public Vector2 offsetMax;
    }

    public class Canvas : Component
    {
        public RenderMode renderMode;
    }

    public class GameObject : Object
    {
        public string name;
        public Transform transform;
        public bool activeSelf;
        public GameObject() { transform = new Transform(); }
        public GameObject(string name) { this.name = name; transform = new Transform { name = name }; }
        public GameObject(string name, params Type[] components) { this.name = name; transform = new Transform { name = name }; }
        public T GetComponent<T>() => default(T);
        public Component GetComponent(Type t) => null;
        public T[] GetComponents<T>() => new T[0];
        public T AddComponent<T>() where T : Component, new() => new T();
        public static GameObject[] FindGameObjectsWithTag(string tag) => new GameObject[0];
    }

    public enum LogType { Log, Warning, Error, Assert, Exception }

    public enum RuntimePlatform { WindowsEditor, OSXEditor, LinuxEditor }

    public static class Application
    {
        public static string dataPath = "/fake/Assets";
        public static string unityVersion = "2022.3.0f1";
        public static RuntimePlatform platform = RuntimePlatform.OSXEditor;
        public static event Action<string, string, LogType> logMessageReceivedThreaded;
    }

    public static class Time
    {
        public static float deltaTime = 0.016f;
    }

    public static class Debug
    {
        public static void Log(object msg) {}
        public static void LogError(object msg) {}
        public static void LogWarning(object msg) {}
    }

    namespace SceneManagement
    {
        public class Scene
        {
            public string name;
            public string path;
            public GameObject[] GetRootGameObjects() => new GameObject[0];
        }

        public static class SceneManager
        {
            public static Scene GetActiveScene() => new Scene();
        }
    }

    namespace UI
    {
        public class Graphic : Component
        {
            public Color color = Color.white;
        }

        public class Text : Graphic
        {
            public string text;
            public TextAnchor alignment;
            public FontStyle fontStyle;
            public Font font;
        }

        public class Image : Graphic {}

        public class Selectable : Component {}

        public class Button : Selectable {}

        public class InputField : Selectable
        {
            public Text textComponent;
            public Graphic placeholder;
        }

        public class CanvasScaler : Component {}
        public class GraphicRaycaster : Component {}

        public class LayoutGroup : Component
        {
            public RectOffset padding = new RectOffset(0, 0, 0, 0);
        }

        public class HorizontalOrVerticalLayoutGroup : LayoutGroup
        {
            public float spacing;
            public bool childForceExpandWidth = true;
            public bool childForceExpandHeight = true;
        }

        public class HorizontalLayoutGroup : HorizontalOrVerticalLayoutGroup {}
        public class VerticalLayoutGroup : HorizontalOrVerticalLayoutGroup {}

        public class GridLayoutGroup : LayoutGroup
        {
            public Vector2 spacing;
        }
    }

    namespace EventSystems
    {
        public class EventSystem : Component {}
        public class StandaloneInputModule : Component {}
    }
}

namespace UnityEditor
{
    using UnityEngine;

    [AttributeUsage(AttributeTargets.Class)]
    public class InitializeOnLoadAttribute : Attribute {}

    public static class EditorApplication
    {
        public static event Action update;
        public static event Action quitting;
        public static event Action hierarchyChanged;
        public static bool isCompiling = false;
        public static double timeSinceStartup = 0.0;

        // Test-only hook -- real Unity fires hierarchyChanged internally whenever the
        // Editor detects a structural scene change; nothing in this stub simulates that
        // automatically, so tests trigger it explicitly to verify invalidation logic.
        public static void RaiseHierarchyChangedForTest() => hierarchyChanged?.Invoke();
    }

    // Real Unity API (2021.1+) — see MCPHierarchyCache.cs for why this is used alongside
    // hierarchyChanged rather than instead of it.
    public struct ObjectChangeEventStream
    {
        public int length;
    }

    public delegate void ObjectChangeEventsDelegate(ref ObjectChangeEventStream stream);

    public static class ObjectChangeEvents
    {
        public static event ObjectChangeEventsDelegate changesPublished;

        // Test-only hook, mirroring RaiseHierarchyChangedForTest's pattern above.
        public static void RaisePublishedForTest(int length)
        {
            var stream = new ObjectChangeEventStream { length = length };
            changesPublished?.Invoke(ref stream);
        }
    }

    public static class AssetDatabase
    {
        public static void Refresh() {}

        public static bool DeleteAsset(string assetPath)
        {
            try
            {
                var projectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
                var full = System.IO.Path.Combine(projectRoot, assetPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!System.IO.File.Exists(full)) return false;
                System.IO.File.Delete(full);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void CreateAsset(Object asset, string path) {}
        public static T LoadAssetAtPath<T>(string path) where T : Object => null;
        public static void SaveAssets() {}
    }

    public static class PrefabUtility
    {
        public static GameObject SaveAsPrefabAsset(GameObject instanceRoot, string assetPath, out bool success)
        {
            success = true;
            return instanceRoot;
        }

        public static Object InstantiatePrefab(Object assetComponentOrGameObject) => assetComponentOrGameObject;
    }

    public static class EditorUtility
    {
        public static void SetDirty(Object obj) {}
        public static string OpenFolderPanel(string title, string folder, string defaultName) => "";
    }

    namespace Compilation
    {
        public enum CompilerMessageType { Error, Warning }

        public class CompilerMessage
        {
            public string message;
            public string file;
            public int line;
            public int column;
            public CompilerMessageType type;
        }

        public static class CompilationPipeline
        {
            public static event Action<object> compilationStarted;
            public static event Action<string, CompilerMessage[]> assemblyCompilationFinished;
        }
    }

    public static class AssemblyReloadEvents
    {
        public static event Action beforeAssemblyReload;
    }

    public static class Undo
    {
        public static void RegisterCreatedObjectUndo(Object obj, string name) {}
        public static void DestroyObjectImmediate(Object obj) {}
        public static void RecordObject(Object obj, string name) {}
        public static void SetTransformParent(Transform t, Transform parent, string name) {}
        public static void AddComponent(GameObject go, Type t) {}
        public static T AddComponent<T>(GameObject go) where T : Component, new() => new T();
    }

    public static class Selection
    {
        public static GameObject activeGameObject;
    }

    public enum BuildTarget { StandaloneWindows64 }

    public static class EditorUserBuildSettings
    {
        public static BuildTarget activeBuildTarget = BuildTarget.StandaloneWindows64;
    }

    // --- Minimal IMGUI surface, just enough for MCPSetupWindow.cs to compile. Not
    // meaningfully testable without a real Editor (nothing here renders), so this stub
    // intentionally goes no deeper than "does it compile" — see MCPClientDetector /
    // MCPClientConfigurator for the parts of the Setup window that ARE unit-tested.

    [AttributeUsage(AttributeTargets.Method)]
    public class MenuItem : Attribute
    {
        public MenuItem(string path) {}
    }

    public class GUIContent
    {
        public GUIContent(string text) {}
    }

    public enum MessageType { None, Info, Warning, Error }

    public class GUIStyle {}

    public class GUISkin
    {
        public GUIStyle box = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin = new GUISkin();
    }

    public class GUILayoutOption {}

    public static class EditorStyles
    {
        public static GUIStyle boldLabel = new GUIStyle();
        public static GUIStyle miniLabel = new GUIStyle();
        public static GUIStyle wordWrappedLabel = new GUIStyle();
    }

    public static class GUILayout
    {
        public static void Label(string text) {}
        public static void Label(string text, GUIStyle style) {}
        public static void Label(string text, params GUILayoutOption[] options) {}
        public static bool Button(string text, params GUILayoutOption[] options) => false;
        public static void BeginHorizontal(params GUILayoutOption[] options) {}
        public static void EndHorizontal() {}
        public static void BeginVertical(params GUILayoutOption[] options) {}
        public static void BeginVertical(GUIStyle style, params GUILayoutOption[] options) {}
        public static void EndVertical() {}
        public static void Space(float pixels) {}
        public static GUILayoutOption Width(float width) => new GUILayoutOption();
        public static GUILayoutOption Height(float height) => new GUILayoutOption();
    }

    public static class EditorGUILayout
    {
        public static void HelpBox(string message, MessageType type) {}
        public static string TextField(string value) => value;
        public static string TextField(string value, GUIStyle style) => value;
        public static string TextField(string value, params GUILayoutOption[] options) => value;
        public static string TextField(string label, string value) => value;
        public static string TextField(string label, string value, params GUILayoutOption[] options) => value;
        public static Enum EnumPopup(Enum value, params GUILayoutOption[] options) => value;
    }

    public static class EditorPrefs
    {
        private static readonly System.Collections.Generic.Dictionary<string, string> _values =
            new System.Collections.Generic.Dictionary<string, string>();

        public static string GetString(string key, string defaultValue) =>
            _values.TryGetValue(key, out var v) ? v : defaultValue;

        public static void SetString(string key, string value) => _values[key] = value;
    }

    public class EditorWindow
    {
        public GUIContent titleContent;
        public UnityEngine.Vector2 minSize;

        public static T GetWindow<T>() where T : EditorWindow, new() => new T();

        // Real Unity does NOT declare OnGUI as a virtual method to override — like
        // MonoBehaviour's Start()/Update(), it's a "magic method" the Editor discovers
        // and calls via its own message-dispatch convention, not C# virtual dispatch.
        // A subclass just declares `void OnGUI()` with no `override` keyword, exactly
        // as MCPSetupWindow.cs does — so this stub deliberately has no OnGUI at all.
        public void Repaint() {}
    }
}

namespace Newtonsoft.Json
{
    public enum Formatting { None, Indented }

    public static class JsonConvert
    {
        public static string SerializeObject(object o) => "{}";
        public static string SerializeObject(object o, Formatting f) => "{}";
        public static T DeserializeObject<T>(string json) => default(T);
    }
}
