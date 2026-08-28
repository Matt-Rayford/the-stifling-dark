// Compile-only stubs for the slice of UnityEngine / UnityEngine.UI / TMPro that the client
// touches. There is no Unity editor on this machine, so this is how unity/Assets/Scripts gets a
// compiler run: it catches typos, wrong argument counts, missing usings, and bad member names
// across the ~3k lines of UI code.
//
// These are SHAPES, not behaviour — every method is a stub. A signature that drifts from the
// real Unity API would be missed here and caught the first time the project is opened, so
// treat a green UiCheck as "the code is well-formed", not "the code runs".
//
// Not part of TheStiflingDark.sln.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public float magnitude => 0f;
        public static Vector2 operator +(Vector2 a, Vector2 b) => a;
        public static Vector2 operator -(Vector2 a, Vector2 b) => a;
        public static Vector2 operator *(Vector2 a, float b) => a;
        public static Vector2 operator /(Vector2 a, float b) => a;
        public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public float magnitude => 0f;
        public static float Distance(Vector3 a, Vector3 b) => 0f;
        public static Vector3 operator +(Vector3 a, Vector3 b) => a;
        public static Vector3 operator -(Vector3 a, Vector3 b) => a;
        public static Vector3 operator *(Vector3 a, float b) => a;
        public static Vector3 operator /(Vector3 a, float b) => a;
        public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0f);
    }

    public struct Vector4
    {
        public Vector4(float x, float y, float z, float w) { }
    }

    public struct Vector2Int
    {
        public Vector2Int(int x, int y) { }
    }

    public struct Quaternion
    {
        public static Quaternion Euler(float x, float y, float z) => default;
        public static Vector3 operator *(Quaternion q, Vector3 v) => v;
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
        public static Color black => new Color(0, 0, 0);
        public static Color clear => new Color(0, 0, 0, 0);
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height) { }
        public float xMin => 0f;
        public float xMax => 0f;
        public float yMin => 0f;
        public float yMax => 0f;
        public float width => 0f;
        public float height => 0f;
        public Vector2 size => Vector2.zero;
        public bool Contains(Vector2 point) => false;
    }

    public struct Bounds
    {
        public Vector3 size => Vector3.one;
    }

    public sealed class RectOffset
    {
        public RectOffset(int left, int right, int top, int bottom) { }
    }

    public static class Mathf
    {
        public const float Rad2Deg = 57.29578f;
        public const float Deg2Rad = 0.0174533f;
        public static float Max(float a, float b) => a;
        public static int Max(int a, int b) => a;
        public static float Min(float a, float b) => a;
        public static int Min(int a, int b) => a;
        public static float Abs(float a) => a;
        public static int Abs(int a) => a;
        public static float Clamp01(float a) => a;
        public static float Clamp(float a, float min, float max) => a;
        public static int Clamp(int a, int min, int max) => a;
        public static float Lerp(float a, float b, float t) => a;
        public static float InverseLerp(float a, float b, float value) => a;
        public static float Pow(float a, float b) => a;
        public static float Sqrt(float a) => a;
        public static float Exp(float power) => power;
        public static float SmoothStep(float a, float b, float t) => a;
        public static float Cos(float a) => a;
        public static float Sin(float a) => a;
        public static float Atan2(float y, float x) => 0f;
        public static int CeilToInt(float a) => 0;
        public static int FloorToInt(float a) => 0;
        public static int RoundToInt(float a) => 0;
    }

    [Flags]
    public enum HideFlags { None = 0, DontSave = 1 }

    public enum TextureFormat { RGBA32 }
    public enum TextureWrapMode { Clamp }
    public enum FilterMode { Bilinear, Trilinear }
    public enum SpriteMeshType { FullRect }
    public enum SpriteDrawMode { Simple, Sliced }
    public enum CameraClearFlags { SolidColor }
    public enum RenderMode { ScreenSpaceOverlay }
    public enum TextAnchor
    {
        UpperLeft, UpperCenter, UpperRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        LowerLeft, LowerCenter, LowerRight,
    }
    public enum KeyCode
    {
        Escape,
        W, A, S, D,
        UpArrow, DownArrow, LeftArrow, RightArrow,
        LeftAlt, RightAlt, LeftCommand, RightCommand,
    }
    public enum RuntimeInitializeLoadType { AfterSceneLoad }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { }
    }

    public class Object
    {
        public string name { get; set; }
        public HideFlags hideFlags { get; set; }
        public static void Destroy(Object target) { }
        public static void DontDestroyOnLoad(Object target) { }
        public static T FindFirstObjectByType<T>() where T : Object => null;
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !ReferenceEquals(a, b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => 0;
    }

    public class Texture : Object { }

    public sealed class Texture2D : Texture
    {
        public Texture2D(int width, int height) { }
        public Texture2D(int width, int height, TextureFormat format, bool mipChain) { }
        public int width => 0;
        public int height => 0;
        public TextureWrapMode wrapMode { get; set; }
        public FilterMode filterMode { get; set; }
        public int anisoLevel { get; set; }
        public void SetPixels32(Color32[] colors) { }
        public Color32[] GetPixels32() => new Color32[width * height];
        public void SetPixels(Color[] colors, int miplevel) { }
        public Color[] GetPixels(int x, int y, int blockWidth, int blockHeight) => new Color[0];
        public void Apply() { }
        public void Apply(bool updateMipmaps) { }
        public bool LoadImage(byte[] data) => true;
    }

    public sealed class Sprite : Object
    {
        public Texture2D texture => null;
        public Bounds bounds => default;
        public Rect rect => default;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot) => null;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot,
            float pixelsPerUnit) => null;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot,
            float pixelsPerUnit, uint extrude, SpriteMeshType meshType) => null;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot,
            float pixelsPerUnit, uint extrude, SpriteMeshType meshType, Vector4 border) => null;
    }

    public sealed class Font : Object { }

    public sealed class AudioClip : Object { }

    public sealed class AudioSource : Behaviour
    {
        public AudioClip clip { get; set; }
        public bool loop { get; set; }
        public bool playOnAwake { get; set; }
        public bool isPlaying => false;
        public void Play() { }
        public void Stop() { }
    }

    public sealed class AudioListener : Behaviour
    {
        public static float volume { get; set; }
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
    }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { }
        public GameObject(string name, params Type[] components) { }
        public Transform transform => null;
        public string tag { get; set; }
        public bool activeSelf => true;
        public bool activeInHierarchy => true;
        public void SetActive(bool value) { }
        public T AddComponent<T>() where T : Component, new() => new T();
        public T GetComponent<T>() where T : Component, new() => new T();
    }

    public class Component : Object
    {
        public Transform transform => null;
        public GameObject gameObject => null;
        public T GetComponent<T>() where T : Component, new() => new T();
        public T GetComponentInParent<T>() where T : Component, new() => new T();
        public T AddComponent<T>() where T : Component, new() => new T();
    }

    public class Transform : Component
    {
        public Transform parent { get; set; }
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Vector3 localScale { get; set; }
        public Quaternion rotation { get; set; }
        public Vector3 localEulerAngles { get; set; }
        public int childCount => 0;
        public Transform GetChild(int index) => null;
        public void SetParent(Transform parent, bool worldPositionStays) { }
        public void SetAsLastSibling() { }
        public void SetSiblingIndex(int index) { }
    }

    public class RectTransform : Transform
    {
        public Vector2 anchorMin { get; set; }
        public Vector2 anchorMax { get; set; }
        public Vector2 offsetMin { get; set; }
        public Vector2 offsetMax { get; set; }
        public Vector2 pivot { get; set; }
        public Vector2 sizeDelta { get; set; }
        public Vector2 anchoredPosition { get; set; }
        public Rect rect => default;
    }

    public static class RectTransformUtility
    {
        public static bool RectangleContainsScreenPoint(RectTransform rect, Vector2 screenPoint) => true;

        public static bool ScreenPointToLocalPointInRectangle(RectTransform rect, Vector2 screenPoint,
            Camera camera, out Vector2 localPoint)
        {
            localPoint = Vector2.zero;
            return true;
        }
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; }
    }

    public class MonoBehaviour : Behaviour { }

    public class Renderer : Component
    {
        public int sortingOrder { get; set; }
    }

    public sealed class MeshRenderer : Renderer { }

    public sealed class SpriteRenderer : Renderer
    {
        public Sprite sprite { get; set; }
        public Color color { get; set; }
        public SpriteDrawMode drawMode { get; set; }
        public Vector2 size { get; set; }
    }

    public sealed class Camera : Behaviour
    {
        public static Camera main => null;
        public bool orthographic { get; set; }
        public float orthographicSize { get; set; }
        public CameraClearFlags clearFlags { get; set; }
        public Color backgroundColor { get; set; }
        public float aspect => 1.7f;
        public float nearClipPlane { get; set; }
        public float farClipPlane { get; set; }
        public Vector3 ScreenToWorldPoint(Vector3 position) => position;
        public Vector3 WorldToScreenPoint(Vector3 position) => position;
    }

    public static class Input
    {
        public static Vector3 mousePosition => Vector3.zero;
        public static Vector2 mouseScrollDelta => Vector2.zero;
        public static bool GetMouseButton(int button) => false;
        public static bool GetMouseButtonDown(int button) => false;
        public static bool GetMouseButtonUp(int button) => false;
        public static bool GetKeyDown(KeyCode key) => false;
        public static bool GetKey(KeyCode key) => false;
    }

    public static class Screen
    {
        public static int width => 1920;
        public static int height => 1080;
    }

    public static class Time
    {
        public static float time => 0f;
        public static float deltaTime => 0f;
        public static float unscaledDeltaTime => 0f;
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception) { }
    }

    public static class Application
    {
        public static string streamingAssetsPath => "";
        public static string dataPath => "";
        public static bool runInBackground { get; set; }
    }

    public static class PlayerPrefs
    {
        public static int GetInt(string key, int defaultValue = 0) => defaultValue;
        public static void SetInt(string key, int value) { }
        public static string GetString(string key, string defaultValue = "") => defaultValue;
        public static void SetString(string key, string value) { }
        public static void Save() { }
    }
}

namespace UnityEngine.Events
{
    public delegate void UnityAction();
    public delegate void UnityAction<T0>(T0 arg0);

    public class UnityEventBase { }

    public class UnityEvent<T0> : UnityEventBase
    {
        public void AddListener(UnityAction<T0> call) { }
        public void RemoveListener(UnityAction<T0> call) { }
        public void Invoke(T0 arg0) { }
    }

    public class UnityEvent : UnityEventBase
    {
        public void AddListener(UnityAction call) { }
    }
}

namespace UnityEngine.EventSystems
{
    public class UIBehaviour : MonoBehaviour { }

    public sealed class EventSystem : UIBehaviour
    {
        public static EventSystem current => null;
        public bool IsPointerOverGameObject() => false;
    }

    public sealed class StandaloneInputModule : UIBehaviour { }

    public class PointerEventData
    {
        public enum InputButton { Left, Right, Middle }
        public Vector2 position { get; set; }
        public InputButton button { get; set; }
    }

    public interface IPointerEnterHandler { void OnPointerEnter(PointerEventData eventData); }
    public interface IPointerExitHandler { void OnPointerExit(PointerEventData eventData); }
    public interface IPointerClickHandler { void OnPointerClick(PointerEventData eventData); }
    public interface IPointerMoveHandler { void OnPointerMove(PointerEventData eventData); }
}

namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.EventSystems.UIBehaviour
    {
        public Color color { get; set; }
        public bool raycastTarget { get; set; }
    }

    public class MaskableGraphic : Graphic { }

    public sealed class Image : MaskableGraphic
    {
        public enum Type { Simple, Sliced, Tiled, Filled }
        public Sprite sprite { get; set; }
        public Type type { get; set; }
        public float pixelsPerUnitMultiplier { get; set; }
        public bool preserveAspect { get; set; }
    }

    public sealed class RawImage : MaskableGraphic
    {
        public Texture texture { get; set; }
    }

    public class Selectable : UnityEngine.EventSystems.UIBehaviour
    {
        public enum Transition { None, ColorTint, SpriteSwap, Animation }
        public Transition transition { get; set; }
        public bool interactable { get; set; }
    }

    public class Slider : Selectable
    {
        public enum Direction { LeftToRight, RightToLeft, BottomToTop, TopToBottom }
        public sealed class SliderEvent : UnityEngine.Events.UnityEvent<float> { }
        public SliderEvent onValueChanged { get; } = new SliderEvent();
        public RectTransform fillRect { get; set; }
        public RectTransform handleRect { get; set; }
        public Graphic targetGraphic { get; set; }
        public Direction direction { get; set; }
        public bool wholeNumbers { get; set; }
        public float minValue { get; set; }
        public float maxValue { get; set; }
        public float value { get; set; }
    }

    public class Button : Selectable
    {
        public sealed class ButtonClickedEvent : UnityEngine.Events.UnityEvent { }
        public ButtonClickedEvent onClick { get; } = new ButtonClickedEvent();
    }

    public sealed class Canvas : Behaviour
    {
        public RenderMode renderMode { get; set; }
        public int sortingOrder { get; set; }
    }

    public sealed class CanvasScaler : UnityEngine.EventSystems.UIBehaviour
    {
        public enum ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }
        public ScaleMode uiScaleMode { get; set; }
        public Vector2 referenceResolution { get; set; }
        public float matchWidthOrHeight { get; set; }
    }

    public sealed class GraphicRaycaster : UnityEngine.EventSystems.UIBehaviour { }

    public sealed class LayoutElement : UnityEngine.EventSystems.UIBehaviour
    {
        public bool ignoreLayout { get; set; }
        public float minWidth { get; set; }
        public float minHeight { get; set; }
        public float preferredWidth { get; set; }
        public float preferredHeight { get; set; }
        public float flexibleWidth { get; set; }
        public float flexibleHeight { get; set; }
    }

    public abstract class LayoutGroup : UnityEngine.EventSystems.UIBehaviour
    {
        public RectOffset padding { get; set; }
        public TextAnchor childAlignment { get; set; }
    }

    public abstract class HorizontalOrVerticalLayoutGroup : LayoutGroup
    {
        public float spacing { get; set; }
        public bool childForceExpandWidth { get; set; }
        public bool childForceExpandHeight { get; set; }
        public bool childControlWidth { get; set; }
        public bool childControlHeight { get; set; }
    }

    public sealed class HorizontalLayoutGroup : HorizontalOrVerticalLayoutGroup { }
    public sealed class VerticalLayoutGroup : HorizontalOrVerticalLayoutGroup { }

    public sealed class ContentSizeFitter : UnityEngine.EventSystems.UIBehaviour
    {
        public enum FitMode { Unconstrained, MinSize, PreferredSize }
        public FitMode horizontalFit { get; set; }
        public FitMode verticalFit { get; set; }
    }

    public sealed class ScrollRect : UnityEngine.EventSystems.UIBehaviour
    {
        public enum MovementType { Unrestricted, Elastic, Clamped }
        public MovementType movementType { get; set; }
        public bool inertia { get; set; }
        public bool horizontal { get; set; }
        public bool vertical { get; set; }
        public float scrollSensitivity { get; set; }
        public RectTransform viewport { get; set; }
        public RectTransform content { get; set; }
        public float horizontalNormalizedPosition { get; set; }
        public UnityEngine.Vector2 velocity { get; set; }
    }

    public static class LayoutRebuilder
    {
        public static void ForceRebuildLayoutImmediate(UnityEngine.RectTransform layoutRoot) { }
    }

    public sealed class Mask : UnityEngine.EventSystems.UIBehaviour
    {
        public bool showMaskGraphic { get; set; }
    }

    public sealed class RectMask2D : UnityEngine.EventSystems.UIBehaviour
    {
        public UnityEngine.Vector2Int softness { get; set; }
    }
}

namespace TMPro
{
    using UnityEngine;

    public enum TextAlignmentOptions
    {
        TopLeft, Top, TopRight,
        Left, Center, Right,
        BottomLeft, Bottom, BottomRight,
        Midline,
    }

    [Flags]
    public enum FontStyles { Normal = 0, Bold = 1 }

    public enum TextWrappingModes { NoWrap, Normal, PreserveWhitespace }
    public enum TextOverflowModes { Overflow, Truncate, Ellipsis }

    public sealed class TMP_FontAsset : UnityEngine.Object
    {
        public List<TMP_FontAsset> fallbackFontAssetTable { get; set; }
        public static TMP_FontAsset CreateFontAsset(UnityEngine.Font font) => null;
    }

    public static class TMP_Settings
    {
        public static TMP_FontAsset defaultFontAsset => null;
    }

    public abstract class TMP_Text : UnityEngine.UI.MaskableGraphic
    {
        public TMP_FontAsset font { get; set; }
        public FontStyles fontStyle { get; set; }
        public float fontSize { get; set; }
        public TextAlignmentOptions alignment { get; set; }
        public string text { get; set; }
        public TextWrappingModes textWrappingMode { get; set; }
        public TextOverflowModes overflowMode { get; set; }
    }

    public class TextMeshProUGUI : TMP_Text { }

    public class TextMeshPro : TMP_Text { }

    public sealed class TMP_InputField : UnityEngine.UI.Selectable
    {
        public RectTransform textViewport { get; set; }
        public TMP_Text textComponent { get; set; }
        public UnityEngine.UI.Graphic placeholder { get; set; }
        public string text { get; set; }
    }
}
