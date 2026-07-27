using System.Collections.Generic;
using StiflingDark.Engine.Core;
using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The light model, and the one piece of presentation the designer specified exactly: the
    /// map is DIMMED OVERALL, Bright spaces and zones punch through fully lit, Dim spaces sit
    /// in between. It is one sprite stretched over the board whose texture is a darkness mask —
    /// black with per-pixel alpha — composited on the CPU from the view's light state.
    ///
    /// Doing it as a mask rather than as additive glows matters: in a Bright space the darkness
    /// alpha drops to almost nothing, so the actual board art shows through at full strength,
    /// which is what makes it read as light rather than as a highlight. Pools are drawn a
    /// little wider than a space circle so neighbouring lit spaces knit into one pool.
    ///
    /// The live flashlight preview repaints only the spaces that entered or left the beam, so
    /// sweeping the mouse costs a few hundred pixel writes rather than a full rebuild.
    /// </summary>
    public sealed class LightOverlay
    {
        // 1024 across a ~7000px board puts a space circle at ~12px: coarse, but every shape
        // in the mask is a soft gradient, so bilinear filtering hides the resolution.
        private const int Resolution = 1024;

        // Darkness alpha per light level. Dark is heavy but not opaque — the designer still
        // needs to see the printed graph to plan a route through unlit rooms.
        private const float OffBoardAlpha = 0.90f;
        private const float DarkAlpha = 0.86f;
        private const float DimAlpha = 0.52f;
        private const float BrightAlpha = 0.05f;

        private static readonly Color32 Shade = new Color32(4, 5, 10, 255);
        // A warm amber wash for the beam being aimed: not the final light, an intention.
        private static readonly Color32 Preview = new Color32(255, 214, 148, 66);

        private readonly BoardModel _board;
        private readonly Texture2D _texture;
        private readonly SpriteRenderer _renderer;
        private readonly Color32[] _base;
        private readonly Color32[] _live;
        private readonly float _pixelsPerBoardUnit;
        private HashSet<string> _preview = new HashSet<string>();
        private bool _dirty;

        public LightOverlay(Transform parent, BoardModel board, int sortingOrder)
        {
            _board = board;
            // Both boards are square renders, so one factor covers both axes.
            _pixelsPerBoardUnit = Resolution / (float)board.SourceWidth;

            _texture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _base = new Color32[Resolution * Resolution];
            _live = new Color32[Resolution * Resolution];

            var go = new GameObject("LightOverlay", typeof(SpriteRenderer));
            go.transform.SetParent(parent, false);
            _renderer = go.GetComponent<SpriteRenderer>();
            _renderer.sprite = Sprite.Create(_texture, new Rect(0, 0, Resolution, Resolution),
                new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
            _renderer.sortingOrder = sortingOrder;
            // Board coordinates: the mask covers the same square the board texture does.
            go.transform.localScale = Vector3.one * (float)(board.SourceWidth / Resolution);

            FillBase(null);
            Commit();
        }

        /// <summary>Recompute the darkness mask from a fresh view.</summary>
        public void SetLight(PlayerView view)
        {
            FillBase(view);
            _dirty = true;
        }

        /// <summary>
        /// The spaces a flashlight placement being aimed right now would light. Costs only the
        /// difference from the last call.
        /// </summary>
        public void SetPreview(HashSet<string> spaces)
        {
            spaces = spaces ?? new HashSet<string>();
            bool changed = false;
            foreach (string id in _preview)
            {
                if (!spaces.Contains(id))
                {
                    RestoreSpace(id);
                    changed = true;
                }
            }
            foreach (string id in spaces)
            {
                if (!_preview.Contains(id))
                {
                    PaintSpace(_live, id, Preview, blendMinimum: true);
                    changed = true;
                }
            }
            _preview = new HashSet<string>(spaces);
            _dirty |= changed;
        }

        public void ClearPreview() => SetPreview(null);

        /// <summary>Upload at most once a frame, and only when something moved.</summary>
        public void Tick()
        {
            if (_dirty)
            {
                Commit();
            }
        }

        private void Commit()
        {
            _texture.SetPixels32(_live);
            _texture.Apply(false);
            _dirty = false;
        }

        private void FillBase(PlayerView view)
        {
            var off = Shade;
            off.a = (byte)(OffBoardAlpha * 255f);
            for (int i = 0; i < _base.Length; i++)
            {
                _base[i] = off;
            }

            var overlay = BoardModel.OverlayFrom(view);
            foreach (var space in _board.Map.Spaces)
            {
                var level = view == null
                    ? space.PrintedLight
                    : _board.Graph.EffectiveLight(space.Id, overlay);
                float alpha = level == LightLevel.Bright
                    ? BrightAlpha
                    : level == LightLevel.Dim ? DimAlpha : DarkAlpha;
                var color = Shade;
                color.a = (byte)(alpha * 255f);
                PaintSpace(_base, space.Id, color, blendMinimum: true);
            }

            System.Array.Copy(_base, _live, _base.Length);
            // A preview outlives a view update: the mouse has not moved, so keep the beam lit.
            foreach (string id in _preview)
            {
                PaintSpace(_live, id, Preview, blendMinimum: true);
            }
        }

        private void RestoreSpace(string spaceId)
        {
            var rect = SpaceRect(spaceId);
            if (rect.width <= 0)
            {
                return;
            }
            for (int y = rect.yMin; y < rect.yMax; y++)
            {
                int row = y * Resolution;
                for (int x = rect.xMin; x < rect.xMax; x++)
                {
                    _live[row + x] = _base[row + x];
                }
            }
        }

        /// <summary>
        /// Paint one space's pool. <paramref name="blendMinimum"/> keeps the LIGHTEST result,
        /// which is how the rulebook's precedence falls out for free: a Bright pool overlapping
        /// a Dim one stays Bright.
        /// </summary>
        private void PaintSpace(Color32[] target, string spaceId, Color32 color, bool blendMinimum)
        {
            var space = _board.SpaceOrNull(spaceId);
            if (space == null)
            {
                return;
            }
            // A pool reaches half a pitch past the space circle so adjacent lit spaces merge.
            float radius = (float)(_board.Map.SpaceRadius * 1.55) * _pixelsPerBoardUnit;
            float cx = (float)space.X * _pixelsPerBoardUnit;
            // Mask rows run bottom-up in a Texture2D; board y runs down.
            float cy = Resolution - (float)space.Y * _pixelsPerBoardUnit;
            int xMin = Mathf.Max(0, Mathf.FloorToInt(cx - radius));
            int xMax = Mathf.Min(Resolution, Mathf.CeilToInt(cx + radius) + 1);
            int yMin = Mathf.Max(0, Mathf.FloorToInt(cy - radius));
            int yMax = Mathf.Min(Resolution, Mathf.CeilToInt(cy + radius) + 1);
            // Hold full strength across the space itself, then ease out over the margin.
            float core = radius * 0.62f;

            for (int y = yMin; y < yMax; y++)
            {
                int row = y * Resolution;
                float dy = y + 0.5f - cy;
                for (int x = xMin; x < xMax; x++)
                {
                    float dx = x + 0.5f - cx;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    if (distance > radius)
                    {
                        continue;
                    }
                    float t = distance <= core
                        ? 1f
                        : 1f - Mathf.SmoothStep(0f, 1f, (distance - core) / (radius - core));
                    if (t <= 0f)
                    {
                        continue;
                    }
                    int index = row + x;
                    var existing = target[index];
                    var mixed = Lerp(existing, color, t);
                    target[index] = blendMinimum && mixed.a > existing.a ? existing : mixed;
                }
            }
        }

        private struct Rect4
        {
            public int xMin, xMax, yMin, yMax;
            public int width => xMax - xMin;
        }

        private Rect4 SpaceRect(string spaceId)
        {
            var space = _board.SpaceOrNull(spaceId);
            if (space == null)
            {
                return default;
            }
            float radius = (float)(_board.Map.SpaceRadius * 1.55) * _pixelsPerBoardUnit;
            float cx = (float)space.X * _pixelsPerBoardUnit;
            float cy = Resolution - (float)space.Y * _pixelsPerBoardUnit;
            return new Rect4
            {
                xMin = Mathf.Max(0, Mathf.FloorToInt(cx - radius)),
                xMax = Mathf.Min(Resolution, Mathf.CeilToInt(cx + radius) + 1),
                yMin = Mathf.Max(0, Mathf.FloorToInt(cy - radius)),
                yMax = Mathf.Min(Resolution, Mathf.CeilToInt(cy + radius) + 1),
            };
        }

        private static Color32 Lerp(Color32 from, Color32 to, float t) => new Color32(
            (byte)Mathf.Lerp(from.r, to.r, t),
            (byte)Mathf.Lerp(from.g, to.g, t),
            (byte)Mathf.Lerp(from.b, to.b, t),
            (byte)Mathf.Lerp(from.a, to.a, t));
    }
}
