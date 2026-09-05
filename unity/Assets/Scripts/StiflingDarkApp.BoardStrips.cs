using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The board-back strips on the solo screen — the Investigator roster and the three
    /// Adversaries — and the zoom overlay they raise.
    ///
    /// A strip is built once and then updated IN PLACE: picking a board moves the glow and
    /// re-badges its neighbours without destroying a single card. Nothing is re-created, so
    /// nothing can be re-measured, so the row cannot move under the cursor — which is the whole
    /// point, and also why the hovered card keeps its hover (a destroyed card never sends its
    /// pointer-exit, and its replacement never sends an enter until the mouse moves again).
    /// </summary>
    public sealed partial class StiflingDarkApp
    {
        private const string InvestigatorStripKey = "Investigator";
        private const string AdversaryStripKey = "Adversary";

        /// <summary>The board backs render 1100x734; the height keeps that exact ratio so
        /// the art fills the card edge-to-edge and the selection glow hugs it evenly —
        /// a taller card letterboxes the art and fattens the glow gap above and below.</summary>
        private const float BoardCardWidth = 252f;
        private const float BoardCardHeight = 168f;
        /// <summary>Width of the soft rim on a strip that overflows. Zero when it fits.</summary>
        private const int StripFadePixels = 70;
        /// <summary>Selected-card glow: full brightness this far past the card edge...</summary>
        private const float GlowRimPx = 2f;
        /// <summary>...then a fade to true black across this much more. On-screen pixels
        /// (canvas reference), drawn 1:1 — tune these two and nothing else.</summary>
        private const float GlowFadePx = 12f;

        private static readonly Color ClaimedBoardColor = new Color(0.62f, 0.62f, 0.64f);

        /// <summary>The strips on screen, by key. A strip is replaced only when its own section
        /// is rebuilt, which for the Investigator strip means a role change and nothing else.</summary>
        private readonly Dictionary<string, BoardStrip> _boardStrips =
            new Dictionary<string, BoardStrip>();
        /// <summary>Scroll positions carried across the rare strip rebuild, by strip key.</summary>
        private readonly Dictionary<string, float> _stripScrollMemory =
            new Dictionary<string, float>();

        private RectTransform _zoomLayer;
        private Image _zoomBoard;
        private TMP_Text _zoomHint;
        private BoardStrip _hoveredStrip;
        private string _hoveredBoard = "";
        private bool _zoomShown;

        // --------------------------------------------------------------- build

        private void BuildBoardZoomLayer()
        {
            _zoomLayer = UiKit.CreatePanel(_menu, "BoardZoom", new Color(0.02f, 0.02f, 0.03f, 0.82f));
            UiKit.Anchor(_zoomLayer, Vector2.zero, Vector2.one);
            _zoomLayer.GetComponent<Image>().raycastTarget = false;

            var boardGo = new GameObject("Board", typeof(RectTransform), typeof(Image));
            boardGo.transform.SetParent(_zoomLayer, false);
            var boardRect = (RectTransform)boardGo.transform;
            boardRect.anchorMin = boardRect.anchorMax = new Vector2(0.5f, 0.5f);
            boardRect.sizeDelta = new Vector2(1120f, 747f);
            _zoomBoard = boardGo.GetComponent<Image>();
            _zoomBoard.preserveAspect = true;
            _zoomBoard.raycastTarget = false;

            _zoomHint = UiKit.CreateText(_zoomLayer, "", 16,
                TextAnchor.MiddleCenter, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)_zoomHint.transform,
                new Vector2(0, 0.04f), new Vector2(1, 0.09f));

            _zoomLayer.gameObject.SetActive(false);
        }

        // -------------------------------------------------------------- strips

        private BoardStrip InvestigatorStripModel() => new BoardStrip
        {
            Key = InvestigatorStripKey,
            Art = _art.PlayerBoard,
            Name = _describe.Investigator,
            Selected = () => _soloInvestigator,
            Select = PickInvestigator,
            ClaimedBy = BotClaimLabel,
            ZoomHint = "Click the board to play them",
        };

        private BoardStrip AdversaryStripModel() => new BoardStrip
        {
            Key = AdversaryStripKey,
            Art = _art.AdversaryBoard,
            Name = Describe.Adversary,
            Selected = () => _soloAdversary,
            Select = PickAdversary,
            ZoomHint = "Click the board to face them",
        };

        /// <summary>
        /// Fill a section with one strip. The only callers are the two section renders, so this
        /// runs on a role change and on first build — never on a plain pick.
        /// </summary>
        private void RenderBoardStrip(RectTransform section, BoardStrip strip, string heading,
            IReadOnlyList<string> ids)
        {
            Head(section, heading);
            var host = UiKit.CreateGroup(section, strip.Key + "StripHost");
            host.gameObject.AddComponent<LayoutElement>().minHeight = BoardCardHeight + 20f;
            var row = CreateScrollRow(host, strip);
            foreach (string id in ids)
            {
                strip.Cards.Add(CreateBoardCard(row, strip, id));
            }
            if (_stripScrollMemory.TryGetValue(strip.Key, out float remembered))
            {
                // Size the content NOW: until a layout pass runs, the freshly built content
                // is zero-sized, and the ScrollRect treats any restored position as
                // out-of-bounds and clamps it straight back to the start.
                LayoutRebuilder.ForceRebuildLayoutImmediate(row);
                row.anchoredPosition = new Vector2(remembered, 0f);
                strip.Scroll.velocity = Vector2.zero;
            }
            _boardStrips[strip.Key] = strip;
            RefreshStripCards(strip);
        }

        /// <summary>
        /// Let go of a strip whose cards are about to be destroyed: hold its scroll position for
        /// the rebuild (only an overflowing strip has one), and drop any hover it owns — a
        /// destroyed card never sends its pointer-exit, and a zoom keyed to a dead strip would
        /// outlive the strip.
        /// </summary>
        private void RetireStrip(string key)
        {
            if (!_boardStrips.TryGetValue(key, out var strip))
            {
                return;
            }
            if (strip.Scroll != null && strip.Fits == false)
            {
                _stripScrollMemory[key] = strip.Scroll.content.anchoredPosition.x;
            }
            if (_hoveredStrip == strip)
            {
                EndBoardHover();
            }
            _boardStrips.Remove(key);
        }

        /// <summary>
        /// Re-dress the cards a strip already has: the glow onto the pick, the dim and badge
        /// onto whatever a bot has claimed. Every one of these objects exists from the start
        /// and is only shown or hidden, and none of them is a layout child of the row, so the
        /// strip's measurements come out identical and nothing shifts.
        /// </summary>
        private void RefreshStripCards(BoardStrip strip)
        {
            foreach (var card in strip.Cards)
            {
                bool selected = strip.Selected() == card.Id;
                string claim = strip.ClaimedBy == null ? null : strip.ClaimedBy(card.Id);
                card.Glow.SetActive(selected);
                card.Badge.gameObject.SetActive(claim != null);
                if (claim != null)
                {
                    card.BadgeText.text = claim;
                }
                if (card.Art != null)
                {
                    card.Art.color = claim == null ? Color.white : ClaimedBoardColor;
                }
                if (card.Fallback != null)
                {
                    card.Fallback.color = selected ? UiKit.AccentColor : UiKit.TextColor;
                }
            }
        }

        /// <summary>Refresh a strip that is on screen; does nothing when it is not.</summary>
        private void RefreshStrip(string key)
        {
            if (_boardStrips.TryGetValue(key, out var strip))
            {
                RefreshStripCards(strip);
            }
        }

        /// <summary>
        /// One board back. Glow and badge are built with it and toggled thereafter — building
        /// them on demand would re-dirty the row's layout on every pick, which is the jump this
        /// whole structure exists to avoid.
        /// </summary>
        private BoardCard CreateBoardCard(RectTransform row, BoardStrip strip, string boardId)
        {
            var card = new BoardCard { Id = boardId };
            var root = UiKit.CreatePanel(row, "Board " + boardId, UiKit.PanelSoft);
            var cardLayout = root.gameObject.AddComponent<LayoutElement>();
            cardLayout.preferredWidth = BoardCardWidth;
            cardLayout.minWidth = BoardCardWidth;
            cardLayout.preferredHeight = BoardCardHeight;
            cardLayout.minHeight = BoardCardHeight;
            card.Glow = CreateGlow(root);

            var art = strip.Art(boardId);
            if (art != null)
            {
                var boardGo = new GameObject("Art", typeof(RectTransform), typeof(Image));
                boardGo.transform.SetParent(root, false);
                UiKit.Anchor((RectTransform)boardGo.transform, Vector2.zero, Vector2.one);
                card.Art = boardGo.GetComponent<Image>();
                card.Art.sprite = art;
                card.Art.preserveAspect = true;
                card.Art.raycastTarget = false;
            }
            else
            {
                card.Fallback = UiKit.CreateText(root, strip.Name(boardId), 16,
                    TextAnchor.MiddleCenter);
                UiKit.Anchor((RectTransform)card.Fallback.transform, Vector2.zero, Vector2.one,
                    new Vector2(8, 8), new Vector2(-8, -8));
            }

            card.Badge = UiKit.CreatePanel(root, "Claimed", new Color(0.02f, 0.02f, 0.03f, 0.85f));
            UiKit.Anchor(card.Badge, Vector2.zero, new Vector2(1, 0),
                new Vector2(6, 6), new Vector2(-6, 26));
            card.Badge.GetComponent<Image>().raycastTarget = false;
            card.BadgeText = UiKit.CreateText(card.Badge, "", 12,
                TextAnchor.MiddleCenter, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)card.BadgeText.transform, Vector2.zero, Vector2.one);

            UiKit.AddHover(root.gameObject, () => BeginBoardHover(strip, boardId), EndBoardHover);
            // The card survives the pick, so the hover it is already holding survives with it:
            // no pointer-exit, no re-entry, and an open zoom stays open on the board just taken.
            UiKit.AddClick(root.gameObject, () => strip.Select(boardId));
            return card;
        }

        /// <summary>A halo behind the selected card: bright right at the card's edge, black by
        /// <see cref="GlowRimPx"/> + <see cref="GlowFadePx"/> out. Drawn 1:1 from an unsliced
        /// card-sized texture, so those two constants ARE the on-screen widths.</summary>
        private static GameObject CreateGlow(RectTransform card)
        {
            float bleed = GlowRimPx + GlowFadePx;
            var go = new GameObject("Glow", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(card, false);
            UiKit.Anchor((RectTransform)go.transform, Vector2.zero, Vector2.one,
                new Vector2(-bleed, -bleed), new Vector2(bleed, bleed));
            var image = go.GetComponent<Image>();
            image.sprite = UiSprites.CardGlow(
                (int)BoardCardWidth, (int)BoardCardHeight, GlowRimPx, GlowFadePx);
            // The box art's flashlight halo: a bright pale green (sampled from the cover).
            image.color = new Color(0.70f, 0.94f, 0.79f, 1f);
            image.raycastTarget = false;
            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// Horizontal strip of boards — the same shape as the Lemonade Wars hand. Whether it
        /// scrolls at all is settled per frame in <see cref="FitStrip"/>, once the viewport has
        /// a measured width.
        /// </summary>
        private RectTransform CreateScrollRow(RectTransform host, BoardStrip strip)
        {
            var scrollGo = new GameObject("ScrollRow", typeof(RectTransform), typeof(ScrollRect),
                typeof(Image));
            scrollGo.transform.SetParent(host, false);
            UiKit.Anchor((RectTransform)scrollGo.transform, Vector2.zero, Vector2.one);
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.18f);
            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.vertical = false;
            scroll.horizontal = true;
            // No wheel: an overflowing strip glides on edge hover (GlideStrip), Lemonade Wars
            // style. Dragging still works through the ScrollRect itself.
            scroll.scrollSensitivity = 0f;
            // Clamped, not the default Elastic: the glide writes the position outright, and an
            // elastic spring answers every such write — and every out-of-bounds frame while the
            // content is still being measured — with a visible rubber-band settle.
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            strip.Scroll = scroll;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform),
                typeof(RectMask2D));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            UiKit.Anchor((RectTransform)viewportGo.transform, Vector2.zero, Vector2.one);
            // Hard edges until FitStrip finds an overflow; the soft rim IS the "this scrolls" cue.
            strip.Mask = viewportGo.GetComponent<RectMask2D>();
            strip.Mask.softness = new Vector2Int(0, 0);

            var contentGo = new GameObject("Content", typeof(RectTransform),
                typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewportGo.transform, false);
            var content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0, 0);
            content.anchorMax = new Vector2(0, 1);
            content.pivot = new Vector2(0, 0.5f);
            content.sizeDelta = Vector2.zero;
            var layout = contentGo.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 10;
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            contentGo.GetComponent<ContentSizeFitter>().horizontalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = (RectTransform)viewportGo.transform;
            scroll.content = content;
            return content;
        }

        private void TickBoardStrips()
        {
            foreach (var strip in _boardStrips.Values)
            {
                if (strip.Scroll == null || !strip.Scroll.gameObject.activeInHierarchy)
                {
                    continue;
                }
                FitStrip(strip);
                GlideStrip(strip.Scroll);
            }
        }

        /// <summary>Settle every strip that is on screen, right now rather than next frame.</summary>
        private void FitBoardStrips()
        {
            foreach (var strip in _boardStrips.Values)
            {
                if (strip.Scroll != null && strip.Scroll.gameObject.activeInHierarchy)
                {
                    FitStrip(strip);
                }
            }
        }

        /// <summary>
        /// Lemonade Wars' rule for a row of cards: one that overflows scrolls and wears the soft
        /// fade at its rim, one that fits sits centred with hard edges and no scrolling at all.
        /// Only the viewport's measured width can tell the two apart, so it is settled here
        /// rather than at build time — and it re-settles when the window changes size.
        /// </summary>
        private static void FitStrip(BoardStrip strip)
        {
            var content = strip.Scroll.content;
            if (strip.Scroll.viewport.rect.width <= 0f)
            {
                return; // no layout pass yet — a zero-width viewport would misread as "fits"
            }
            float slack = strip.Scroll.viewport.rect.width - content.rect.width;
            bool fits = slack >= 0f;
            if (strip.Fits != fits)
            {
                strip.Fits = fits;
                strip.Mask.softness = new Vector2Int(fits ? 0 : StripFadePixels, 0);
                // A disabled ScrollRect stops dragging and stops clamping the content back to
                // the left edge, which is what leaves the centring below alone.
                strip.Scroll.enabled = !fits;
            }
            if (fits)
            {
                content.anchoredPosition = new Vector2(slack / 2f, 0f);
            }
        }

        /// <summary>
        /// Lemonade Wars' edge-hover glide: hovering the outer 15% of the strip scrolls it,
        /// speed ramping toward the edge (700 px/s at the very rim). Replaces the wheel.
        /// </summary>
        private static void GlideStrip(ScrollRect scroll)
        {
            var viewport = scroll.viewport;
            float overflow = scroll.content.rect.width - viewport.rect.width;
            if (overflow <= 0f)
            {
                return;
            }
            Vector2 screen = Input.mousePosition;
            if (!RectTransformUtility.RectangleContainsScreenPoint(viewport, screen))
            {
                return;
            }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                viewport, screen, null, out var local);
            var rect = viewport.rect;
            float fraction = (local.x - rect.xMin) / Mathf.Max(1f, rect.width);
            const float edgeZone = 0.15f;
            float velocity = 0f;
            if (fraction < edgeZone)
            {
                velocity = -Mathf.InverseLerp(edgeZone, 0f, fraction);
            }
            else if (fraction > 1f - edgeZone)
            {
                velocity = Mathf.InverseLerp(1f - edgeZone, 1f, fraction);
            }
            if (velocity == 0f)
            {
                return;
            }
            float scrolledPx = scroll.horizontalNormalizedPosition * overflow +
                velocity * 700f * Time.deltaTime;
            scroll.horizontalNormalizedPosition = Mathf.Clamp01(scrolledPx / overflow);
        }

        // ---------------------------------------------------------- board zoom

        private void BeginBoardHover(BoardStrip strip, string boardId)
        {
            _hoveredStrip = strip;
            _hoveredBoard = boardId;
        }

        private void EndBoardHover()
        {
            _hoveredStrip = null;
            _hoveredBoard = "";
            HideBoardZoom();
        }

        private void HideBoardZoom()
        {
            if (!_zoomShown)
            {
                return;
            }
            _zoomShown = false;
            _zoomLayer.gameObject.SetActive(false);
        }

        /// <summary>
        /// Zoom is shortcut-only (designer note — the dwell felt intrusive): the overlay lives
        /// exactly as long as the inspect key is down over a board. Held across a click, it
        /// simply re-points at whatever is now selected.
        /// </summary>
        private void TickBoardZoom()
        {
            bool inspectHeld =
                Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            if (_zoomShown && !inspectHeld)
            {
                HideBoardZoom();
                return;
            }
            if (!_zoomShown && inspectHeld && _hoveredStrip != null)
            {
                ShowBoardZoom(_hoveredStrip, _hoveredBoard);
            }
        }

        private void ShowBoardZoom(BoardStrip strip, string boardId)
        {
            var art = strip.Art(boardId);
            if (art == null)
            {
                return; // nothing to enlarge; the strip already shows the fallback caption
            }
            _zoomBoard.sprite = art;
            _zoomHint.text = strip.ZoomHint;
            _zoomLayer.gameObject.SetActive(true);
            _zoomShown = true;
        }

        /// <summary>
        /// One board-back strip: where its art and names come from, which card is lit, and what
        /// a click sets. The Investigator roster and the three Adversaries each fill one in, and
        /// everything the strips do — glide, fade, glow, zoom — works off this rather than off
        /// either one of them.
        /// </summary>
        private sealed class BoardStrip
        {
            public string Key;
            public Func<string, Sprite> Art;
            public Func<string, string> Name;
            public Func<string> Selected;
            public Action<string> Select;
            /// <summary>Dims and badges a card. Null on a strip nothing can claim.</summary>
            public Func<string, string> ClaimedBy;
            public string ZoomHint;
            public ScrollRect Scroll;
            public RectMask2D Mask;
            /// <summary>Null until a layout pass has given the viewport a width to measure.</summary>
            public bool? Fits;
            public readonly List<BoardCard> Cards = new List<BoardCard>();
        }

        /// <summary>The handles a card needs to be re-dressed without being rebuilt.</summary>
        private sealed class BoardCard
        {
            public string Id;
            /// <summary>Null when the art was never synced; <see cref="Fallback"/> is then set.</summary>
            public Image Art;
            public TMP_Text Fallback;
            public GameObject Glow;
            public RectTransform Badge;
            public TMP_Text BadgeText;
        }
    }
}
