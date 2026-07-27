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
