using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Small helpers for building the whole HUD from code — same approach as the
    /// Lemonade Wars client, so the two projects read alike. Nothing here is authored in
    /// a scene: <see cref="StiflingDarkApp"/> spawns itself and builds every panel.
    /// </summary>
    public static class UiKit
    {
        // A horror game played in the dark: near-black panels, bone text, a cold amber accent.
        public static readonly Color PanelColor = new Color(0.055f, 0.06f, 0.075f, 0.94f);
        public static readonly Color PanelSoft = new Color(0.09f, 0.10f, 0.12f, 0.88f);
        public static readonly Color TextColor = new Color(0.88f, 0.87f, 0.84f);
        public static readonly Color MutedColor = new Color(0.55f, 0.56f, 0.60f);
        public static readonly Color AccentColor = new Color(0.98f, 0.76f, 0.36f);
        public static readonly Color AccentTextColor = new Color(0.08f, 0.07f, 0.05f);
        /// <summary>Sampled from the rulebook cover's title lettering — the game's sage green.</summary>
        public static readonly Color TitleColor = new Color(0.44f, 0.69f, 0.53f);
        public static readonly Color DangerColor = new Color(0.86f, 0.28f, 0.26f);
        public static readonly Color GoodColor = new Color(0.45f, 0.80f, 0.52f);

        private static TMP_FontAsset _font;
        private static TMP_FontAsset _titleFont;
        private static TMP_FontAsset _menuFont;

        /// <summary>
        /// The body font: TMP's bundled Liberation Sans SDF (Assets/TextMesh Pro, committed) —
        /// legible at HUD sizes, every glyph present, nothing to import.
        /// </summary>
        public static TMP_FontAsset Font
        {
            get
            {
                if (_font == null)
                {
                    _font = TMP_Settings.defaultFontAsset;
                }
                return _font;
            }
        }

        /// <summary>The game-title display font ("The Macabre").</summary>
        public static TMP_FontAsset TitleFont
        {
            get
            {
                if (_titleFont == null)
                {
                    _titleFont = LoadDisplayFont("Fonts/The Macabre");
                }
                return _titleFont;
            }
        }

        /// <summary>Menu buttons and screen headings ("Cenotaph Titling").</summary>
        public static TMP_FontAsset MenuFont
        {
            get
            {
                if (_menuFont == null)
                {
                    _menuFont = LoadDisplayFont("Fonts/Cenotaph-Titling");
                }
                return _menuFont;
            }
        }

        /// <summary>
        /// Display fonts come from game-assets/fonts, which tools/sync_unity.sh copies into
        /// Assets/Resources/Fonts so Unity imports them. Built as dynamic TMP assets at runtime
        /// (glyphs rasterize on demand), with the body font as glyph fallback; when the file
        /// was never synced this IS the body font.
        /// </summary>
        private static TMP_FontAsset LoadDisplayFont(string resourcePath)
        {
            var source = Resources.Load<Font>(resourcePath);
            if (source == null)
            {
                return Font;
            }
            var asset = TMP_FontAsset.CreateFontAsset(source);
            asset.fallbackFontAssetTable =
                new System.Collections.Generic.List<TMP_FontAsset> { Font };
            return asset;
        }

        private static TextAlignmentOptions Align(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
                case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                default: return TextAlignmentOptions.BottomRight;
            }
        }

        public static Canvas CreateCanvas(string name, int sortOrder)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            }
            return canvas;
        }

        public static RectTransform CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return (RectTransform)go.transform;
        }

        /// <summary>Transparent container: groups children without painting anything.</summary>
        public static RectTransform CreateGroup(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform Anchor(RectTransform rt, Vector2 min, Vector2 max,
            Vector2 offsetMin = default, Vector2 offsetMax = default)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return rt;
        }

        public static TextMeshProUGUI CreateText(Transform parent, string content, int size,
            TextAnchor align = TextAnchor.UpperLeft, Color? color = null)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<TextMeshProUGUI>();
            text.font = Font;
            text.fontSize = size;
            text.alignment = Align(align);
            text.color = color ?? TextColor;
            text.text = content;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Truncate;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>
        /// A HUD button. Disabled buttons stay visible and greyed rather than vanishing —
        /// the designer is hunting rules bugs, and "why is this greyed out?" is a better
        /// question than "where did that button go?". <paramref name="tooltip"/> shows the
        /// reason on hover.
        ///
        /// <paramref name="fixedWidth"/> pins the width instead of deriving it from the label:
        /// a horizontal row that cannot fit every child's preferred width shrinks them all
        /// toward their minWidth, so a small icon button beside a wide one needs a minWidth
        /// too or it collapses into a sliver. Fixed-width buttons centre their label.
        /// </summary>
        public static Button CreateButton(Transform parent, string label, int fontSize,
            UnityEngine.Events.UnityAction onClick, bool enabled = true, string tooltip = null,
            bool danger = false, float fixedWidth = 0f, TMP_FontAsset labelFont = null)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button),
                typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var element = go.GetComponent<LayoutElement>();
            element.minHeight = 32;
            // Horizontal rows hand each child its PREFERRED width; without one a button
            // collapses to nothing. Vertical lists force-expand width and ignore this.
            element.preferredWidth = fixedWidth > 0f
                ? fixedWidth
                : label.Length * fontSize * 0.56f + 26f;
            if (fixedWidth > 0f)
            {
                element.minWidth = fixedWidth;
                element.flexibleWidth = 0;
            }
            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(onClick);
            button.interactable = enabled;

            var idleBackground = !enabled
                ? new Color(0.12f, 0.13f, 0.15f, 0.75f)
                : danger
                    ? new Color(0.32f, 0.10f, 0.10f, 0.92f)
                    : new Color(0.20f, 0.22f, 0.27f, 0.92f);
            var idleText = !enabled
                ? new Color(0.42f, 0.43f, 0.46f)
                : danger ? DangerColor : TextColor;
            var image = go.GetComponent<Image>();
            image.color = idleBackground;

            var text = CreateText(go.transform, label, fontSize,
                fixedWidth > 0f ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, idleText);
            if (labelFont != null)
            {
                text.font = labelFont;
            }
            Anchor((RectTransform)text.transform, Vector2.zero, Vector2.one,
                fixedWidth > 0f ? new Vector2(2, 2) : new Vector2(10, 2),
                fixedWidth > 0f ? new Vector2(-2, -2) : new Vector2(-8, -2));

            if (enabled)
            {
                var hoverBackground = danger ? DangerColor : TitleColor;
                var hoverText = danger ? new Color(0.98f, 0.92f, 0.90f) : AccentTextColor;
                AddHover(go,
                    () =>
                    {
                        image.color = hoverBackground;
                        text.color = hoverText;
                    },
                    () =>
                    {
                        image.color = idleBackground;
                        text.color = idleText;
                    });
            }
            else if (!string.IsNullOrEmpty(tooltip))
            {
                AddHover(go, () => Tooltip.Show(tooltip), Tooltip.Hide);
            }
            return button;
        }

        /// <summary>Vertical scroll list; returns the content container to fill.</summary>
        public static RectTransform CreateScrollList(RectTransform host, float spacing = 4f)
        {
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect), typeof(Image));
            scrollGo.transform.SetParent(host, false);
            Anchor((RectTransform)scrollGo.transform, Vector2.zero, Vector2.one,
                new Vector2(2, 2), new Vector2(-2, -2));
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.22f);
            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 24f;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            Anchor((RectTransform)viewportGo.transform, Vector2.zero, Vector2.one);
            viewportGo.GetComponent<Mask>().showMaskGraphic = false;

            var contentGo = new GameObject("Content", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewportGo.transform, false);
            var content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            // A fresh RectTransform defaults to sizeDelta (100,100), which with stretch
            // anchors pokes rows 50px past both edges.
            content.sizeDelta = Vector2.zero;
            var layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = (RectTransform)viewportGo.transform;
            scroll.content = content;
            return content;
        }

        /// <summary>A row that lays its children out left to right.</summary>
        public static RectTransform CreateRow(Transform parent, string name, float spacing = 6f,
            float minHeight = 34f)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup),
                typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().minHeight = minHeight;
            var layout = go.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            return (RectTransform)go.transform;
        }

        public static void Clear(Transform container)
        {
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                // Detach before the deferred Destroy: a dying child left in place still
                // takes part in this frame's layout pass, so every rebuild flashed one
                // frame of doubled content (old rows AND new stacked together).
                child.SetParent(null, false);
                Object.Destroy(child.gameObject);
            }
        }

        /// <summary>Attach pointer-enter/exit hover callbacks to any UI object.</summary>
        public static void AddHover(GameObject go,
            UnityEngine.Events.UnityAction onEnter, UnityEngine.Events.UnityAction onExit)
        {
            var relay = go.GetComponent<PointerRelay>() ?? go.AddComponent<PointerRelay>();
            relay.Entered += () => onEnter();
            relay.Exited += () => onExit();
        }

        /// <summary>Make any UI object clickable.</summary>
        public static void AddClick(GameObject go, UnityEngine.Events.UnityAction onClick)
        {
            var relay = go.GetComponent<PointerRelay>() ?? go.AddComponent<PointerRelay>();
            relay.Clicked += () => onClick();
        }

        /// <summary>Single-line text input with placeholder.</summary>
        public static TMP_InputField CreateInput(Transform parent, string placeholder,
            string initial = "", float minWidth = 220f)
        {
            var go = new GameObject("Input", typeof(RectTransform), typeof(Image),
                typeof(TMP_InputField), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var background = go.GetComponent<Image>();
            background.color = new Color(0.03f, 0.04f, 0.05f, 0.95f);
            var element = go.GetComponent<LayoutElement>();
            element.minHeight = 38;
            element.minWidth = minWidth;

            // TMP inputs render inside an explicit masked viewport.
            var areaGo = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            areaGo.transform.SetParent(go.transform, false);
            var area = (RectTransform)areaGo.transform;
            Anchor(area, Vector2.zero, Vector2.one, new Vector2(10, 3), new Vector2(-10, -3));

            var textGo = CreateText(area, "", 17, TextAnchor.MiddleLeft);
            Anchor((RectTransform)textGo.transform, Vector2.zero, Vector2.one);
            var placeholderGo = CreateText(area, placeholder, 17, TextAnchor.MiddleLeft, MutedColor);
            Anchor((RectTransform)placeholderGo.transform, Vector2.zero, Vector2.one);

            var input = go.GetComponent<TMP_InputField>();
            input.textViewport = area;
            input.textComponent = textGo;
            input.placeholder = placeholderGo;
            input.text = initial;
            return input;
        }

        /// <summary>Small pill caption — stats, seat roles, counters.</summary>
        public static TextMeshProUGUI CreateBadge(Transform parent, string content, int size,
            Color background, Color? textColor = null)
        {
            var go = new GameObject("Badge", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = UiSprites.RoundedRect;
            image.type = Image.Type.Sliced;
            image.color = background;
            go.GetComponent<LayoutElement>().minHeight = size + 10;
            var text = CreateText(go.transform, content, size, TextAnchor.MiddleCenter, textColor);
            Anchor((RectTransform)text.transform, Vector2.zero, Vector2.one,
                new Vector2(8, 2), new Vector2(-8, -2));
            return text;
        }

        /// <summary>A titled section inside a side panel. Returns the body container.</summary>
        public static RectTransform CreateSection(Transform parent, string title, float bodyHeight)
        {
            var section = new GameObject(title, typeof(RectTransform), typeof(VerticalLayoutGroup),
                typeof(LayoutElement));
            section.transform.SetParent(parent, false);
            var layout = section.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 2;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            var header = CreateText(section.transform, title.ToUpperInvariant(), 13,
                TextAnchor.MiddleLeft, MutedColor);
            header.gameObject.AddComponent<LayoutElement>().minHeight = 20;

            var body = new GameObject("Body", typeof(RectTransform), typeof(LayoutElement));
            body.transform.SetParent(section.transform, false);
            body.GetComponent<LayoutElement>().preferredHeight = bodyHeight;
            return (RectTransform)body.transform;
        }
    }
}
