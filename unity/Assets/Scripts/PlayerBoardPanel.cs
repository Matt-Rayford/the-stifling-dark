using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The human's own Investigator board, front side, laid over the bottom of the map: the
    /// printed abilities and turn structure as a live reference, with markers riding the
    /// printed Stamina and Charge tracks and the player's Wounds filling the printed slots.
    ///
    /// Everything is placed as a FRACTION of the board image (see the track tables below), so
    /// the panel is resolution-independent and the same geometry serves all ten boards — the
    /// bottom band of the art is one shared template. Unity UI anchors run from the BOTTOM
    /// left and the measurements run from the image's TOP left, hence the 1 - y throughout.
    /// </summary>
    public sealed class PlayerBoardPanel
    {
        /// <summary>Panel width as a share of the canvas' 1920 reference width.</summary>
        private const float WidthFraction = 0.385f;
        private const float ReferenceWidth = 1920f;
        /// <summary>Aspect of the rendered board art (1400x934); a loaded sprite overrides it.</summary>
        private const float FallbackAspect = 1400f / 934f;

        // ---- printed track geometry, as fractions of the board image (x, y from its top-left)
        private static readonly float[] StaminaCircleX =
            { 0.4909f, 0.5733f, 0.6581f, 0.7416f, 0.8246f, 0.9083f };
        private const float StaminaCircleY = 0.652f;
        private static readonly float[] ChargeCircleX = { 0.6161f, 0.6999f, 0.7834f, 0.8677f };
        private const float ChargeCircleY = 0.776f;
        /// <summary>Printed track circle radius, as a fraction of the board's WIDTH.</summary>
        private const float CircleRadius = 0.042f;
        /// <summary>Marker diameter over a track circle: just INSIDE the printed rim. The
        /// circles all but touch their neighbours, so a marker drawn around one would sit on
        /// the next value along.</summary>
        private const float MarkerDiameter = CircleRadius * 2f * 0.98f;

        // ---- printed Major Ability box (the gear ring the physical token covers)
        private const float MajorAbilityX = 0.1416f;
        private const float MajorAbilityY = 0.771f;
        /// <summary>Just inside the printed gear ring (0.095 W), sitting in it like the
        /// physical token does.</summary>
        private const float MajorAbilityDiameter = 0.09f;

        // ---- printed Wound slots along the bottom band
        private const int WoundSlots = 4;
        private const float WoundBandLeft = 0.032f;
        private const float WoundBandRight = 0.971f;
        private const float WoundBandGap = 0.014f;
        private const float WoundBandTop = 0.918f;
        private const float WoundBandBottom = 0.996f;

        /// <summary>How much of the board stays on screen when it is collapsed: enough for the
        /// printed name band along the top, which is what makes the tab identifiable.</summary>
        private const float CollapsedTabFraction = 0.12f;

        private readonly TokenArt _art;
        private readonly Describe _describe;
        private readonly RectTransform _root;
        private readonly Image _boardImage;
        private readonly RectTransform _markers;
        private readonly RectTransform _woundBand;
        private readonly RectTransform _fallbackBody;
        private readonly TMP_Text _toggleLabel;
        private readonly RectTransform _staminaMarker;
        private readonly RectTransform _chargeMarker;
        private readonly RectTransform _majorAbilityToken;

        private string _builtFor = "";
        private bool _collapsed;
        private float _height;

        public PlayerBoardPanel(Transform parent, TokenArt art, Describe describe)
        {
            _art = art;
            _describe = describe;

            float width = ReferenceWidth * WidthFraction;
            _height = width / FallbackAspect;

            _root = UiKit.CreatePanel(parent, "PlayerBoard", new Color(0, 0, 0, 0));
            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0f);
            _root.pivot = new Vector2(0.5f, 0f);
            _root.sizeDelta = new Vector2(width, _height);
            _boardImage = _root.GetComponent<Image>();
            _boardImage.preserveAspect = true;

            _fallbackBody = UiKit.CreateGroup(_root, "Fallback");
            UiKit.Anchor(_fallbackBody, Vector2.zero, Vector2.one, new Vector2(24, 24), new Vector2(-24, -24));

            _markers = UiKit.CreateGroup(_root, "Markers");
            UiKit.Anchor(_markers, Vector2.zero, Vector2.one);
            _staminaMarker = CreateTrackMarker("StaminaMarker", "Stamina.png");
            _chargeMarker = CreateTrackMarker("ChargeMarker", "Charge.png");
            _majorAbilityToken = CreateTrackMarker("MajorAbilityToken", "Major-Ability.png");

            _woundBand = UiKit.CreateGroup(_root, "Wounds");
            UiKit.Anchor(_woundBand, Vector2.zero, Vector2.one);

            // Hand-built rather than UiKit.CreateButton: this one is corner-pinned instead of
            // laid out in a row, and the label has to stay reachable to flip the caret.
            var toggle = UiKit.CreatePanel(_root, "Collapse", new Color(0.10f, 0.11f, 0.13f, 0.92f));
            toggle.anchorMin = toggle.anchorMax = new Vector2(1f, 1f);
            toggle.pivot = new Vector2(1f, 1f);
            toggle.sizeDelta = new Vector2(40f, 30f);
            toggle.anchoredPosition = new Vector2(-10f, -10f);
            var toggleImage = toggle.GetComponent<Image>();
            toggleImage.sprite = UiSprites.RoundedRect;
            toggleImage.type = Image.Type.Sliced;
            _toggleLabel = UiKit.CreateText(toggle, "▾", 18, TextAnchor.MiddleCenter, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)_toggleLabel.transform, Vector2.zero, Vector2.one);
            UiKit.AddClick(toggle.gameObject, ToggleCollapsed);
            UiKit.AddHover(toggle.gameObject,
                () => Tooltip.Show(_collapsed
                    ? "Show your player board"
                    : "Collapse your player board — it covers the map"),
                Tooltip.Hide);

            SetVisible(false);
        }

        /// <summary>
        /// Draw the panel for this seat's Investigator. <paramref name="me"/> null (an Adversary
        /// seat, or a seat holding nobody) hides it, as does <paramref name="boardNeedsClicks"/>
        /// — while the flashlight is being aimed or spaces are being picked, the map underneath
        /// belongs to the mouse.
        /// </summary>
        public void Render(PlayerView.InvestigatorPanel me, bool boardNeedsClicks)
        {
            if (me == null || boardNeedsClicks)
            {
                SetVisible(false);
                return;
            }
            SetVisible(true);
            BuildFor(me.DefId);
            PlaceMarker(_staminaMarker, StaminaCircleX, StaminaCircleY, me.Stamina);
            PlaceMarker(_chargeMarker, ChargeCircleX, ChargeCircleY, me.Charge);
            // The physical token sits on the printed gear while unspent and leaves when used.
            _majorAbilityToken.gameObject.SetActive(me.MajorAbilityTokens > 0);
            if (me.MajorAbilityTokens > 0)
            {
                _majorAbilityToken.anchorMin = _majorAbilityToken.anchorMax =
                    new Vector2(MajorAbilityX, 1f - MajorAbilityY);
                float size = MajorAbilityDiameter * _root.sizeDelta.x;
                _majorAbilityToken.sizeDelta = new Vector2(size, size);
                _majorAbilityToken.anchoredPosition = Vector2.zero;
            }
            RenderWounds(me);
            RenderFallback(me);
            ApplyCollapse();
        }

        // ------------------------------------------------------------- structure

        /// <summary>
        /// The board art for an Investigator, sized to the sprite's own aspect so the printed
        /// geometry below lines up with the panel's rect exactly (preserveAspect would otherwise
        /// letterbox the art inside a rect the markers are still measured against).
        /// </summary>
        private void BuildFor(string investigatorId)
        {
            if (_builtFor == investigatorId)
            {
                return;
            }
            _builtFor = investigatorId;
            var sprite = _art.PlayerBoardFront(investigatorId);
            _boardImage.sprite = sprite;
            _boardImage.color = sprite != null
                ? Color.white
                : new Color(0.055f, 0.06f, 0.075f, 0.96f);

            float width = ReferenceWidth * WidthFraction;
            float aspect = sprite != null && sprite.rect.height > 0f
                ? sprite.rect.width / sprite.rect.height
                : FallbackAspect;
            _height = width / aspect;
            _root.sizeDelta = new Vector2(width, _height);

            // Without the art there is no printed track to mark or slot to fill.
            _markers.gameObject.SetActive(sprite != null);
            _woundBand.gameObject.SetActive(sprite != null);
            _fallbackBody.gameObject.SetActive(sprite == null);
        }

        /// <summary>The physical marker token (tokens/investigator/player-board); when that
        /// art was never synced, a dark/white/dark ring band that stays legible over the pink
        /// Stamina circles, the yellow Charge circles and the parchment between them.</summary>
        private RectTransform CreateTrackMarker(string name, string tokenFile)
        {
            var marker = UiKit.CreateGroup(_markers, name);
            marker.pivot = new Vector2(0.5f, 0.5f);
            // The token art is a white square scan; the circle mask turns it back into the
            // physical punch-out disc.
            var art = _art.CircularToken("investigator/player-board/" + tokenFile);
            if (art != null)
            {
                var token = UiKit.CreatePanel(marker, "Token", Color.white);
                var image = token.GetComponent<Image>();
                image.sprite = art;
                image.preserveAspect = true;
                image.raycastTarget = false;
                UiKit.Anchor(token, Vector2.zero, Vector2.one);
                return marker;
            }
            Ring(marker, 1.00f, new Color(0.04f, 0.04f, 0.05f, 0.95f));
            Ring(marker, 0.90f, new Color(0.97f, 0.97f, 0.95f, 0.98f));
            Ring(marker, 0.80f, new Color(0.04f, 0.04f, 0.05f, 0.95f));
            return marker;
        }

        private static void Ring(RectTransform parent, float scale, Color color)
        {
            var ring = UiKit.CreatePanel(parent, "Ring", color);
            var image = ring.GetComponent<Image>();
            image.sprite = UiSprites.Ring;
            image.raycastTarget = false;
            float inset = (1f - scale) / 2f;
            ring.anchorMin = new Vector2(inset, inset);
            ring.anchorMax = new Vector2(1f - inset, 1f - inset);
            ring.offsetMin = ring.offsetMax = Vector2.zero;
        }

        // ---------------------------------------------------------------- markers

        /// <summary>
        /// Park a marker on the printed circle for <paramref name="value"/>. The circle's centre
        /// is a fraction of the image from its top-left, which becomes a point anchor at
        /// (x, 1 - y); the diameter is a fraction of the board's WIDTH, so the marker is sized in
        /// pixels rather than anchored (an anchored square would stretch with the board's aspect).
        /// </summary>
        private void PlaceMarker(RectTransform marker, float[] circleX, float circleY, int value)
        {
            int index = Mathf.Clamp(value, 0, circleX.Length - 1);
            marker.anchorMin = marker.anchorMax = new Vector2(circleX[index], 1f - circleY);
            float diameter = MarkerDiameter * _root.sizeDelta.x;
            marker.sizeDelta = new Vector2(diameter, diameter);
            marker.anchoredPosition = Vector2.zero;
        }

        // ----------------------------------------------------------------- wounds

        /// <summary>
        /// One chip per Wound across the printed slots: face-up Wounds name their card (hover
        /// for its text), face-down ones stay anonymous. Anything past the fourth slot — the
        /// unslotted Wounds included — collapses into the last slot as "+N more".
        /// </summary>
        private void RenderWounds(PlayerView.InvestigatorPanel me)
        {
            UiKit.Clear(_woundBand);
            var wounds = me.Wounds.Concat(me.NonSlotWounds).ToList();
            if (wounds.Count == 0)
            {
                return;
            }
            bool overflows = wounds.Count > WoundSlots;
            int shown = overflows ? WoundSlots - 1 : wounds.Count;
            for (int slot = 0; slot < shown; slot++)
            {
                var wound = wounds[slot];
                bool isNamed = IsNamed(wound);
                WoundChip(slot, WoundLabel(wound),
                    isNamed ? UiKit.DangerColor : UiKit.MutedColor,
                    isNamed ? _describe.CardText(wound.CardId) : null);
            }
            if (overflows)
            {
                WoundChip(WoundSlots - 1, "+" + (wounds.Count - shown) + " more", UiKit.DangerColor,
                    string.Join("\n", wounds.Skip(shown).Select(WoundLabel)));
            }
        }

        /// <summary>A face-up Wound whose card the viewer is allowed to see.</summary>
        private static bool IsNamed(PlayerView.WoundSlot wound) =>
            wound.FaceUp && !string.IsNullOrEmpty(wound.CardId);

        private string WoundLabel(PlayerView.WoundSlot wound) =>
            IsNamed(wound) ? _describe.Card(wound.CardId) : wound.FaceUp ? "Wound" : "Face-down";

        private void WoundChip(int slot, string label, Color color, string tooltip)
        {
            float slotWidth =
                (WoundBandRight - WoundBandLeft - WoundBandGap * (WoundSlots - 1)) / WoundSlots;
            float left = WoundBandLeft + slot * (slotWidth + WoundBandGap);

            var chip = UiKit.CreatePanel(_woundBand, "Wound" + slot, new Color(0.03f, 0.03f, 0.04f, 0.88f));
            var image = chip.GetComponent<Image>();
            image.sprite = UiSprites.RoundedRect;
            image.type = Image.Type.Sliced;
            UiKit.Anchor(chip,
                new Vector2(left, 1f - WoundBandBottom),
                new Vector2(left + slotWidth, 1f - WoundBandTop));

            var text = UiKit.CreateText(chip, label, 15, TextAnchor.MiddleCenter, color);
            UiKit.Anchor((RectTransform)text.transform, Vector2.zero, Vector2.one,
                new Vector2(6, 2), new Vector2(-6, -2));
            if (!string.IsNullOrEmpty(tooltip))
            {
                UiKit.AddHover(chip.gameObject, () => Tooltip.Show(tooltip), Tooltip.Hide);
            }
        }

        // --------------------------------------------------------------- fallback

        /// <summary>No synced art (the TokenArt contract: every lookup may miss) — name and the
        /// two numbers the printed tracks would have shown.</summary>
        private void RenderFallback(PlayerView.InvestigatorPanel me)
        {
            if (!_fallbackBody.gameObject.activeSelf)
            {
                return;
            }
            UiKit.Clear(_fallbackBody);
            var name = UiKit.CreateText(_fallbackBody, _describe.Investigator(me.DefId), 26,
                TextAnchor.UpperCenter, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)name.transform, new Vector2(0, 0.7f), new Vector2(1, 1));
            var stats = UiKit.CreateText(_fallbackBody,
                "Stamina " + me.Stamina + "     Charge " + me.Charge + "\nWounds " +
                (me.Wounds.Count + me.NonSlotWounds.Count), 20, TextAnchor.UpperCenter);
            UiKit.Anchor((RectTransform)stats.transform, new Vector2(0, 0.35f), new Vector2(1, 0.7f));
            var note = UiKit.CreateText(_fallbackBody,
                "No board art for this Investigator — run tools/sync_unity.sh.", 14,
                TextAnchor.LowerCenter, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)note.transform, Vector2.zero, new Vector2(1, 0.3f));
        }

        // --------------------------------------------------------------- collapse

        private void ToggleCollapsed()
        {
            _collapsed = !_collapsed;
            ApplyCollapse();
        }

        /// <summary>Collapsed, the board slides off the bottom edge until only its printed name
        /// band (and this control) is left. Session-only: nothing is persisted.</summary>
        private void ApplyCollapse()
        {
            _toggleLabel.text = _collapsed ? "▴" : "▾";
            _root.anchoredPosition = new Vector2(
                0f, _collapsed ? -(_height - _height * CollapsedTabFraction) : 0f);
        }

        private void SetVisible(bool visible)
        {
            if (_root.gameObject.activeSelf != visible)
            {
                _root.gameObject.SetActive(visible);
            }
        }
    }
}
