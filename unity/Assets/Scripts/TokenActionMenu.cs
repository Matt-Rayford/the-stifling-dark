using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The Investigator's action menu, pinned beside their own figure on the board: opened by
    /// clicking the token, closed by clicking anywhere else (or Esc). GameUi owns the content;
    /// this class owns the shape — an equal-width column sized to its widest one-line label —
    /// and the placement, following the figure through every pan and zoom.
    /// </summary>
    public sealed class TokenActionMenu
    {
        /// <summary>Gap between the figure's edge and the menu, in space radii from centre.</summary>
        private const float FigureGapRadii = 1.35f;

        // The HUD chrome the menu must not sit under — mirrors GameUi's layout constants.
        private const float TopBarHeight = 78f;
        private const float BottomMargin = 12f;
        private const float RightColumnWidth = 430f;

        private readonly BoardView _boardView;
        private readonly RectTransform _root;

        /// <summary>The space the menu is pinned to; null when closed.</summary>
        public string Space { get; private set; }
        public bool IsOpen => Space != null;
        /// <summary>Fill with UiKit.CreateButton rows; the column stretches them all to the
        /// widest label's preferred width, so nothing wraps.</summary>
        public RectTransform Content => _root;

        public TokenActionMenu(RectTransform parent, BoardView boardView)
        {
            _boardView = boardView;
            // No backdrop: the buttons carry their own backgrounds, the board shows between.
            _root = UiKit.CreateGroup(parent, "TokenActions");
            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
            var fitter = _root.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var layout = _root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 6, 6);
            layout.spacing = 4f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            _root.gameObject.SetActive(false);
        }

        public void OpenAt(string spaceId)
        {
            Space = spaceId;
            _root.gameObject.SetActive(true);
            Reposition();
        }

        public void Close()
        {
            Space = null;
            UiKit.Clear(_root);
            _root.gameObject.SetActive(false);
        }

        /// <summary>Is the mouse over the menu's column right now?</summary>
        public bool ContainsPointer()
            => RectTransformUtility.RectangleContainsScreenPoint(_root, Input.mousePosition);

        /// <summary>Track the figure while open — the camera moves between renders.</summary>
        public void Tick()
        {
            if (IsOpen)
            {
                Reposition();
            }
        }

        private void Reposition()
        {
            var parent = (RectTransform)_root.parent;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                _boardView.ScreenPointBeside(Space, FigureGapRadii), null, out var local);
            float width = _root.rect.width;
            float height = _root.rect.height;

            // Flip to the figure's left side when the column would run under the right panel.
            // Skip on the unmeasured first frame (rect still 0); Tick re-runs every frame.
            if (width > 0f && local.x + width > parent.rect.xMax - RightColumnWidth)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                    _boardView.ScreenPointBeside(Space, -FigureGapRadii), null, out local);
                _root.pivot = new Vector2(1f, 0.5f);
            }
            else
            {
                _root.pivot = new Vector2(0f, 0.5f);
            }
            if (height > 0f)
            {
                local.y = Mathf.Clamp(local.y,
                    parent.rect.yMin + BottomMargin + height / 2f,
                    parent.rect.yMax - TopBarHeight - height / 2f);
            }
            _root.anchoredPosition = local;
        }
    }
}
