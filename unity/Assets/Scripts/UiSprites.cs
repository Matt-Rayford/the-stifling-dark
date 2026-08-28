using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Procedurally generated shared sprites — no asset files, no import settings, nothing
    /// for a missing Unity editor to have configured: a rounded rect, a disc, and a ring.
    /// </summary>
    public static class UiSprites
    {
        private static Sprite _roundedRect;
        private static Sprite _circle;
        private static Sprite _ring;
        private static Sprite _rectGlow;
        private static string _rectGlowKey = "";

        /// <summary>Anti-aliased rounded rectangle, 9-sliced so corners keep their radius.</summary>
        public static Sprite RoundedRect
        {
            get
            {
                if (_roundedRect == null)
                {
                    _roundedRect = Rounded(128, 26f, border: 40f);
                }
                return _roundedRect;
            }
        }

        /// <summary>Hard-edged disc: figures, space hit highlights, token backdrops.</summary>
        public static Sprite Circle
        {
            get
            {
                if (_circle == null)
                {
                    _circle = Rounded(128, 62f, border: 0f);
                }
                return _circle;
            }
        }

        /// <summary>Thin ring: selection and adjacency markers that keep the art readable.</summary>
        public static Sprite Ring
        {
            get
            {
                if (_ring == null)
                {
                    _ring = Rounded(128, 60f, border: 0f, outline: 5f);
                }
                return _ring;
            }
        }

        /// <summary>
        /// A card-sized halo, generated UNSLICED at the exact pixel size it will be drawn:
        /// no 9-slice border scaling anywhere in the chain, so the rim and fade widths on
        /// screen are precisely the numbers passed in (canvas reference pixels). Full alpha
        /// under the core and through the <paramref name="rimPx"/> ring, then a cubic
        /// (gamma-encoded — alpha composites in linear color space, where mid alphas display
        /// far brighter than their number) fade to true zero over <paramref name="fadePx"/>.
        /// Cached for the one size in use; a new size regenerates.
        /// </summary>
        public static Sprite CardGlow(int coreWidth, int coreHeight, float rimPx, float fadePx)
        {
            string key = coreWidth + "x" + coreHeight + ":" + rimPx + ":" + fadePx;
            if (_rectGlow != null && key == _rectGlowKey)
            {
                return _rectGlow;
            }
            int margin = Mathf.CeilToInt(rimPx + fadePx);
            int width = coreWidth + margin * 2;
            int height = coreHeight + margin * 2;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Max(margin - x, x - (width - 1 - margin)));
                    float dy = Mathf.Max(0f, Mathf.Max(margin - y, y - (height - 1 - margin)));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float eased = d <= rimPx
                        ? 1f
                        : Mathf.Pow(Mathf.Clamp01(1f - (d - rimPx) / fadePx), 3f);
                    byte alpha = (byte)(255f * Mathf.Pow(eased, 2.2f));
                    pixels[y * width + x] = new Color32(255, 255, 255, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            _rectGlowKey = key;
            _rectGlow = Sprite.Create(texture, new Rect(0, 0, width, height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            return _rectGlow;
        }

        /// <summary>
        /// Rounded rect of the given corner radius with a 1px anti-aliased edge, optionally
        /// reduced to an <paramref name="outline"/>-wide rim.
        /// </summary>
        private static Sprite Rounded(int size, float radius, float border, float outline = 0f)
        {
            var texture = NewTexture(size);
            float half = size / 2f;
            var coreHalf = new Vector2(half - 2f, half - 2f);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f - half, y + 0.5f - half);
                    var q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) -
                            (coreHalf - new Vector2(radius, radius));
                    float outside = new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude;
                    float inside = Mathf.Min(Mathf.Max(q.x, q.y), 0);
                    float dist = outside + inside - radius;
                    float alpha = Mathf.Clamp01(0.5f - dist);
                    if (outline > 0f)
                    {
                        alpha *= Mathf.Clamp01(dist + outline + 0.5f);
                    }
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }

        private static Texture2D NewTexture(int size) =>
            new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
    }
}
