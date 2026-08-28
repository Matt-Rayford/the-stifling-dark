using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The offline-setup screen: scenario, which side the human takes, the bot seats, and two
    /// board-back strips — the Adversary in play and, when the human is an Investigator, which
    /// one they are. Everything here is local: nothing is sent anywhere until START builds a
    /// <see cref="LocalGameSession"/>.
    ///
    /// The two floating layers (the bot dropdown, the zoomed board) live above every menu
    /// screen rather than inside the scrolling setup panel, which clips its children.
    /// </summary>
    public sealed partial class StiflingDarkApp
    {
        /// <summary>A bot seat left on Random: the session draws its Investigator at START.</summary>
        private const string RandomPick = "";

        /// <summary>The board backs render 1100x734, so a card this wide is 140 tall.</summary>
        private const float BoardCardWidth = 252f;
        private const float BoardCardHeight = 175f;
        /// <summary>Width of the soft rim on a strip that overflows. Zero when it fits.</summary>
        private const int StripFadePixels = 70;
        /// <summary>Selected-card glow: full brightness this far past the card edge...</summary>
        private const float GlowRimPx = 0.5f;
        /// <summary>...then a fade to true black across this much more. On-screen pixels
        /// (canvas reference), drawn 1:1 — tune these two and nothing else.</summary>
        private const float GlowFadePx = 12f;

        // ---- offline setup, as the solo screen has it dialled in
        private string _soloScenario = "sawmill";
        private string _soloAdversary = "butcher";
        private SeatRole _soloRole = SeatRole.Investigator;
        private string _soloInvestigator = "";
        /// <summary>One entry per bot Investigator seat, in row order. The bot Adversary the
        /// human plays against is implicit and never listed here.</summary>
        private readonly List<string> _soloBotInvestigators =
            new List<string> { RandomPick, RandomPick };

        private RectTransform _popupLayer;
        private RectTransform _zoomLayer;
        private Image _zoomBoard;
        private TMP_Text _zoomHint;
        /// <summary>Bot seat whose dropdown is open, or -1. Only ever one at a time.</summary>
        private int _openDropdownSeat = -1;
        /// <summary>The strips currently on screen, rebuilt with the body they live in.</summary>
        private readonly List<BoardStrip> _boardStrips = new List<BoardStrip>();
        /// <summary>Scroll positions carried across body rebuilds, by strip key.</summary>
        private readonly Dictionary<string, float> _stripScrollMemory =
            new Dictionary<string, float>();
        private BoardStrip _hoveredStrip;
        private string _hoveredBoard = "";
        private bool _zoomShown;

        // --------------------------------------------------------------- build

        /// <summary>
        /// The floating layers, built once with the menu. Both sit above the four screens so a
        /// dropdown or a zoomed board is never clipped by the panel that raised it.
        /// </summary>
        private void BuildSoloOverlays()
        {
            _popupLayer = UiKit.CreateGroup(_menu, "SoloPopups");
            UiKit.Anchor(_popupLayer, Vector2.zero, Vector2.one);
            _popupLayer.gameObject.SetActive(false);

            _zoomLayer = UiKit.CreatePanel(_menu, "BoardZoom", new Color(0.02f, 0.02f, 0.03f, 0.82f));
            UiKit.Anchor(_zoomLayer, Vector2.zero, Vector2.one);
            _zoomLayer.GetComponent<Image>().raycastTarget = false;

            var boardGo = new GameObject("Board", typeof(RectTransform), typeof(Image));
            boardGo.transform.SetParent(_zoomLayer, false);
            var boardRect = (RectTransform)boardGo.transform;
            boardRect.anchorMin = boardRect.anchorMax = new Vector2(0.5f, 0.5f);
            boardRect.sizeDelta = new Vector2(1120f, 760f);
            _zoomBoard = boardGo.GetComponent<Image>();
            _zoomBoard.preserveAspect = true;
            _zoomBoard.raycastTarget = false;

            _zoomHint = UiKit.CreateText(_zoomLayer, "", 16,
                TextAnchor.MiddleCenter, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)_zoomHint.transform,
                new Vector2(0, 0.04f), new Vector2(1, 0.09f));

            _zoomLayer.gameObject.SetActive(false);
        }

        // -------------------------------------------------------------- render

        private void RenderSoloScreen()
        {
            // A rebuild orphans the row that raised the dropdown and the card being hovered —
            // and a destroyed card never sends its pointer-exit — so both close with the body.
            CloseBotDropdown();
            EndBoardHover();
            foreach (var strip in _boardStrips)
            {
                if (strip.Scroll != null && strip.Fits == false)
                {
                    _stripScrollMemory[strip.Key] = strip.Scroll.content.anchoredPosition.x;
                }
            }
            _boardStrips.Clear();
            UiKit.Clear(_soloBody);
            if (_describe == null)
            {
                return;
            }

            Head(_soloBody, "SCENARIO");
            foreach (string scenario in new[] { "sawmill", "amusement-park" })
            {
                string captured = scenario;
                Choice(Describe.Scenario(captured), _soloScenario == captured,
                    () => _soloScenario = captured);
            }

            RenderAdversaryBoards();

            Head(_soloBody, "YOU PLAY");
            Choice("An Investigator", _soloRole == SeatRole.Investigator,
                () => SetSoloRole(SeatRole.Investigator));
            Choice("The Adversary", _soloRole == SeatRole.Adversary,
                () => SetSoloRole(SeatRole.Adversary));

            if (_soloRole == SeatRole.Investigator)
            {
                RenderInvestigatorBoards();
            }
            RenderBotSeats();

            UiKit.CreateButton(_soloBody, "START", 20, StartSoloGame)
                .GetComponent<LayoutElement>().minHeight = 48;
        }

        /// <summary>One radio row of the solo setup; picking re-renders so the dot moves.</summary>
        private void Choice(string label, bool current, Action pick)
        {
            UiKit.CreateButton(_soloBody, (current ? "●  " : "○  ") + label, 16, () =>
            {
                pick();
                RenderMenu();
            });
        }

        // ------------------------------------------------------- board picker

        private void RenderInvestigatorBoards() => RenderBoardStrip(
            new BoardStrip
            {
                Key = "Investigator",
                Art = _art.PlayerBoard,
                Name = _describe.Investigator,
                Selected = () => _soloInvestigator,
                Select = SetSoloInvestigator,
                ClaimedBy = BotClaimLabel,
                ZoomHint = "Click the board to play them",
            },
            "YOUR INVESTIGATOR",
            _describe.BaseInvestigators.Select(def => def.Id).ToList());

        private void RenderAdversaryBoards() => RenderBoardStrip(
            new BoardStrip
            {
                Key = "Adversary",
                Art = _art.AdversaryBoard,
                Name = Describe.Adversary,
                Selected = () => _soloAdversary,
                Select = id => _soloAdversary = id,
                ZoomHint = "Click the board to face them",
            },
            "ADVERSARY",
            new List<string> { "butcher", "cult-of-hunlow", "insatiable-horror" });

        private void RenderBoardStrip(BoardStrip strip, string heading, 
            IReadOnlyList<string> ids)
        {
            Head(_soloBody, heading);
            var host = UiKit.CreateGroup(_soloBody, strip.Key + "StripHost");
            host.gameObject.AddComponent<LayoutElement>().minHeight = BoardCardHeight + 20f;
            var row = CreateScrollRow(host, strip);
            foreach (string id in ids)
            {
                CreateBoardCard(row, strip, id);
            }
            if (_stripScrollMemory.TryGetValue(strip.Key, out float remembered))
            {
                // Size the content NOW: until a layout pass runs, the freshly built content
                // is zero-sized, and the ScrollRect's elastic spring treats any restored
                // position as out-of-bounds and quietly animates it back to the start —
                // which is exactly the reset being fixed. With real bounds in place, the
                // pixel-space restore below just sticks.
                LayoutRebuilder.ForceRebuildLayoutImmediate(row);
                row.anchoredPosition = new Vector2(remembered, 0f);
                strip.Scroll.velocity = Vector2.zero;
            }
            _boardStrips.Add(strip);
        }

        /// <summary>"Bot 2's pick" when a bot seat holds that Investigator, else null. Only the
        /// Investigator strip has claims; nothing can claim an Adversary board.</summary>
        private string BotClaimLabel(string investigatorId)
        {
            int seat = _soloBotInvestigators.IndexOf(investigatorId);
            return seat < 0 ? null : "Bot " + (seat + 1) + "'s pick";
        }

        /// <summary>
        /// One board back in the strip: white glow when it is the pick, dimmed with a badge when
        /// something else claims it (clicking still takes it — the human's claim wins), and a
        /// plain captioned card when the art was never synced.
        /// </summary>
        private void CreateBoardCard(RectTransform row, BoardStrip strip, string boardId)
        {
            bool selected = strip.Selected() == boardId;
            string claim = strip.ClaimedBy == null ? null : strip.ClaimedBy(boardId);

            var card = UiKit.CreatePanel(row, "Board " + boardId, UiKit.PanelSoft);
            var cardLayout = card.gameObject.AddComponent<LayoutElement>();
            cardLayout.preferredWidth = BoardCardWidth;
            cardLayout.minWidth = BoardCardWidth;
            cardLayout.preferredHeight = BoardCardHeight;
            cardLayout.minHeight = BoardCardHeight;

            if (selected)
            {
                CreateGlow(card);
            }

            var art = strip.Art(boardId);
            if (art != null)
            {
                var boardGo = new GameObject("Art", typeof(RectTransform), typeof(Image));
                boardGo.transform.SetParent(card, false);
                UiKit.Anchor((RectTransform)boardGo.transform, Vector2.zero, Vector2.one,
                    new Vector2(3, 3), new Vector2(-3, -3));
                var image = boardGo.GetComponent<Image>();
                image.sprite = art;
                image.preserveAspect = true;
                image.raycastTarget = false;
                image.color = claim == null ? Color.white : new Color(0.62f, 0.62f, 0.64f);
            }
            else
            {
                var fallback = UiKit.CreateText(card, strip.Name(boardId), 16,
                    TextAnchor.MiddleCenter, selected ? UiKit.AccentColor : UiKit.TextColor);
                UiKit.Anchor((RectTransform)fallback.transform, Vector2.zero, Vector2.one,
                    new Vector2(8, 8), new Vector2(-8, -8));
            }

            if (claim != null)
            {
                var badge = UiKit.CreatePanel(card, "Claimed", new Color(0.02f, 0.02f, 0.03f, 0.85f));
                UiKit.Anchor(badge, Vector2.zero, new Vector2(1, 0),
                    new Vector2(6, 6), new Vector2(-6, 26));
                badge.GetComponent<Image>().raycastTarget = false;
                var claimed = UiKit.CreateText(badge, claim, 12,
                    TextAnchor.MiddleCenter, UiKit.MutedColor);
                UiKit.Anchor((RectTransform)claimed.transform, Vector2.zero, Vector2.one);
            }

            UiKit.AddHover(card.gameObject, () => BeginBoardHover(strip, boardId), EndBoardHover);
            UiKit.AddClick(card.gameObject, () =>
            {
                strip.Select(boardId);
                EndBoardHover();
                RenderMenu();
            });
        }

        /// <summary>A halo behind the selected card: bright right at the card's edge, black by
        /// <see cref="GlowRimPx"/> + <see cref="GlowFadePx"/> out. Drawn 1:1 from an unsliced
        /// card-sized texture, so those two constants ARE the on-screen widths.</summary>
        private static void CreateGlow(RectTransform card)
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
            foreach (var strip in _boardStrips)
            {
                if (strip.Scroll == null || !strip.Scroll.gameObject.activeInHierarchy)
                {
                    continue;
                }
                FitStrip(strip);
                GlideStrip(strip.Scroll);
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
        /// Runs every frame from Update. Hovering a board raises it after a short dwell — held
        /// Alt or Cmd skips the wait, the same inspect gesture Lemonade Wars uses for cards —
        /// and Escape closes an open bot dropdown.
        /// </summary>
        private void TickSoloSetup()
        {
            if (_menuScreen != MenuScreen.Solo || _stage != Stage.Menu)
            {
                CloseBotDropdown();
                EndBoardHover();
                return;
            }
            TickBoardStrips();
            if (_openDropdownSeat >= 0 && Input.GetKeyDown(KeyCode.Escape))
            {
                CloseBotDropdown();
            }
            // Zoom is shortcut-only (designer note — the dwell felt intrusive): the overlay
            // lives exactly as long as the inspect key is down over a board.
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

        // ------------------------------------------------------------ bot seats

        /// <summary>2 to 4 Investigators play. The human holds one of those seats unless they
        /// took the Adversary, in which case the bots are the whole team.</summary>
        private int MinBotSeats => _soloRole == SeatRole.Adversary ? 2 : 1;

        private int MaxBotSeats => _soloRole == SeatRole.Adversary ? 4 : 3;

        private void RenderBotSeats()
        {
            Head(_soloBody, "BOT INVESTIGATORS");
            var note = UiKit.CreateText(_soloBody,
                "Pick each bot's Investigator from its dropdown — Random is drawn at start.",
                13, TextAnchor.MiddleLeft, UiKit.MutedColor);
            note.gameObject.AddComponent<LayoutElement>().minHeight = 22;

            for (int i = 0; i < _soloBotInvestigators.Count; i++)
            {
                CreateBotSeatRow(i);
            }
            UiKit.CreateButton(_soloBody, "+  Add a bot", 16, () =>
            {
                _soloBotInvestigators.Add(RandomPick);
                RenderMenu();
            }, _soloBotInvestigators.Count < MaxBotSeats,
                "Four Investigators is the most that play.");
        }

        private void CreateBotSeatRow(int seatIndex)
        {
            var row = UiKit.CreateRow(_soloBody, "BotSeat" + seatIndex);
            var label = UiKit.CreateText(row, "Bot " + (seatIndex + 1), 15,
                TextAnchor.MiddleLeft, UiKit.MutedColor);
            var labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.minWidth = 62f;
            labelLayout.preferredWidth = 62f;

            var selector = UiKit.CreateButton(row, PickLabel(_soloBotInvestigators[seatIndex]) +
                "   ▾", 15, () => OpenBotDropdown(seatIndex));
            var selectorLayout = selector.GetComponent<LayoutElement>();
            selectorLayout.preferredWidth = 200f;
            selectorLayout.flexibleWidth = 1;

            UiKit.CreateButton(row, "×", 15, () =>
            {
                _soloBotInvestigators.RemoveAt(seatIndex);
                RenderMenu();
            }, _soloBotInvestigators.Count > MinBotSeats,
                _soloRole == SeatRole.Adversary
                    ? "An all-bot team still needs two Investigators."
                    : "The game needs at least two Investigators.",
                danger: true, fixedWidth: 34f);
        }

        private string PickLabel(string investigatorId) =>
            investigatorId == RandomPick ? "Random" : _describe.Investigator(investigatorId);

        /// <summary>
        /// The option list, floated on the popup layer just under the cursor: a backdrop that
        /// swallows the next click outside it, and the Investigators no other seat holds.
        /// </summary>
        private void OpenBotDropdown(int seatIndex)
        {
            CloseBotDropdown();
            _openDropdownSeat = seatIndex;
            _popupLayer.gameObject.SetActive(true);

            var catcher = UiKit.CreatePanel(_popupLayer, "Catcher", new Color(0f, 0f, 0f, 0f));
            UiKit.Anchor(catcher, Vector2.zero, Vector2.one);
            UiKit.AddClick(catcher.gameObject, CloseBotDropdown);

            var options = BotInvestigatorOptions(seatIndex);
            float height = Mathf.Min(options.Count * 36f + 12f, 330f);
            var panel = UiKit.CreatePanel(_popupLayer, "Options", UiKit.PanelColor);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0f, 1f);
            panel.sizeDelta = new Vector2(240f, height);
            panel.anchoredPosition = DropdownPosition(240f, height);

            var list = UiKit.CreateScrollList(panel, 2f);
            foreach (string option in options)
            {
                string captured = option;
                UiKit.CreateButton(list,
                    (captured == _soloBotInvestigators[seatIndex] ? "●  " : "○  ") +
                    PickLabel(captured), 15, () =>
                    {
                        _soloBotInvestigators[seatIndex] = captured;
                        RenderMenu();
                    });
            }
        }

        /// <summary>Under the cursor, nudged back onto the screen when it would hang off.</summary>
        private Vector2 DropdownPosition(float width, float height)
        {
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _popupLayer, Input.mousePosition, null, out local);
            var bounds = _popupLayer.rect;
            return new Vector2(
                Mathf.Min(local.x - 10f, bounds.xMax - width - 8f),
                Mathf.Max(local.y - 8f, bounds.yMin + height + 8f));
        }

        private void CloseBotDropdown()
        {
            if (_openDropdownSeat < 0)
            {
                return;
            }
            _openDropdownSeat = -1;
            UiKit.Clear(_popupLayer);
            _popupLayer.gameObject.SetActive(false);
        }

        /// <summary>The dropdown only offers Investigators no other seat holds, so two bots can
        /// never collide and START never has a duplicate to resolve.</summary>
        private List<string> BotInvestigatorOptions(int seatIndex)
        {
            var options = new List<string> { RandomPick };
            options.AddRange(_describe.BaseInvestigators.Select(def => def.Id).Where(id =>
                (_soloRole != SeatRole.Investigator || id != _soloInvestigator) &&
                !_soloBotInvestigators.Where((held, i) => i != seatIndex && held == id).Any()));
            return options;
        }

        // ----------------------------------------------------------- selection

        private void SetSoloRole(SeatRole role)
        {
            _soloRole = role;
            while (_soloBotInvestigators.Count < MinBotSeats)
            {
                _soloBotInvestigators.Add(RandomPick);
            }
            while (_soloBotInvestigators.Count > MaxBotSeats)
            {
                _soloBotInvestigators.RemoveAt(_soloBotInvestigators.Count - 1);
            }
            ReleaseHumanInvestigatorFromBots();
        }

        private void SetSoloInvestigator(string investigatorId)
        {
            _soloInvestigator = investigatorId;
            ReleaseHumanInvestigatorFromBots();
        }

        /// <summary>The human's claim wins: a bot already holding that Investigator falls back
        /// to Random rather than the two of them starting the same character.</summary>
        private void ReleaseHumanInvestigatorFromBots()
        {
            if (_soloRole != SeatRole.Investigator)
            {
                return;
            }
            for (int i = 0; i < _soloBotInvestigators.Count; i++)
            {
                if (_soloBotInvestigators[i] == _soloInvestigator)
                {
                    _soloBotInvestigators[i] = RandomPick;
                }
            }
        }

        /// <summary>What START hands the session: the human's Investigator first when they play
        /// one, then a seat per bot, each entry either a chosen Investigator or Random.</summary>
        private List<string> SoloInvestigatorPicks()
        {
            var picks = new List<string>();
            if (_soloRole == SeatRole.Investigator)
            {
                picks.Add(_soloInvestigator);
            }
            picks.AddRange(_soloBotInvestigators);
            return picks;
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

        }
    }
}
