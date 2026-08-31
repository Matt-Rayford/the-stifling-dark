using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The offline-setup screen: scenario, which side the human takes, the bot seats, and two
    /// board-back strips — the Adversary in play and, when the human is an Investigator, which
    /// one they are. Everything here is local: nothing is sent anywhere until START builds a
    /// <see cref="LocalGameSession"/>. The strips themselves live in
    /// StiflingDarkApp.BoardStrips.cs.
    ///
    /// The screen is built as SECTIONS, each in its own host container that is created once and
    /// outlives every interaction. A click re-renders only the sections its state reaches —
    /// often none of them, because a board pick is a redress of cards that already exist. The
    /// screen used to rebuild whole and jumped on every click; a section that nobody rebuilt
    /// cannot jump.
    ///
    /// The bot dropdown floats on its own layer above every menu screen, because the setup
    /// panel it is raised from clips its children.
    /// </summary>
    public sealed partial class StiflingDarkApp
    {
        /// <summary>A bot seat left on Random: the session draws its Investigator at START.</summary>
        private const string RandomPick = "";

        // ---- offline setup, as the solo screen has it dialled in
        private string _soloScenario = "sawmill";
        private string _soloAdversary = "butcher";
        private SeatRole _soloRole = SeatRole.Investigator;
        private string _soloInvestigator = "";
        /// <summary>One entry per bot Investigator seat, in row order. The bot Adversary the
        /// human plays against is implicit and never listed here. Defaults to a FULL table
        /// (3 bots beside the human Investigator = 4 total); remove rows to play short.</summary>
        private readonly List<string> _soloBotInvestigators =
            new List<string> { RandomPick, RandomPick, RandomPick };

        private RectTransform _scenarioSection;
        private RectTransform _adversarySection;
        private RectTransform _roleSection;
        private RectTransform _investigatorSection;
        private RectTransform _botSection;
        private RectTransform _startSection;
        /// <summary>False until the sections have content; game-data has to load first.</summary>
        private bool _soloSectionsFilled;

        private RectTransform _popupLayer;
        /// <summary>Bot seat whose dropdown is open, or -1. Only ever one at a time.</summary>
        private int _openDropdownSeat = -1;

        // --------------------------------------------------------------- build

        /// <summary>
        /// The empty section hosts and the floating layers, built once with the menu. Order here
        /// is the order on screen; nothing below ever re-parents or re-orders them.
        /// </summary>
        private void BuildSoloSetup()
        {
            // The setup list springs back elastically whenever its content changes height —
            // which a section rebuild can legitimately do — and that rubber band reads as the
            // list lurching on a click. Clamped, it simply stays where the player left it.
            var listScroll = _soloBody.GetComponentInParent<ScrollRect>();
            if (listScroll != null)
            {
                listScroll.movementType = ScrollRect.MovementType.Clamped;
            }

            _scenarioSection = CreateSoloSection("ScenarioSection");
            _adversarySection = CreateSoloSection("AdversarySection");
            _roleSection = CreateSoloSection("RoleSection");
            _investigatorSection = CreateSoloSection("InvestigatorSection");
            _botSection = CreateSoloSection("BotSection");
            _startSection = CreateSoloSection("StartSection");

            _popupLayer = UiKit.CreateGroup(_menu, "SoloPopups");
            UiKit.Anchor(_popupLayer, Vector2.zero, Vector2.one);
            _popupLayer.gameObject.SetActive(false);

            BuildBoardZoomLayer();
        }

        /// <summary>
        /// One section of the setup list. It is a layout group in its own right, so the list
        /// reads its height straight off it and a section can be emptied and refilled without
        /// the sections around it being touched.
        /// </summary>
        private RectTransform CreateSoloSection(string name)
        {
            var section = UiKit.CreateGroup(_soloBody, name);
            var layout = section.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            return section;
        }

        // -------------------------------------------------------------- render

        /// <summary>
        /// Fills every section — which happens once, on the first frame the solo screen has
        /// game-data. After that each interaction renders its own sections and this only
        /// re-dresses the strips, so an unrelated menu revision cannot rebuild the screen
        /// under the player.
        /// </summary>
        private void RenderSoloScreen()
        {
            if (_describe == null)
            {
                return;
            }
            if (_soloSectionsFilled)
            {
                RefreshStrip(InvestigatorStripKey);
                RefreshStrip(AdversaryStripKey);
                return;
            }
            _soloSectionsFilled = true;
            RenderScenarioSection();
            RenderAdversarySection();
            RenderRoleSection();
            RenderInvestigatorSection();
            RenderBotSection();
            RenderStartSection();
        }

        private void RenderScenarioSection()
        {
            UiKit.Clear(_scenarioSection);
            Head(_scenarioSection, "SCENARIO");
            foreach (string scenario in new[] { "sawmill", "amusement-park" })
            {
                string captured = scenario;
                Choice(_scenarioSection, Describe.Scenario(captured), _soloScenario == captured,
                    () =>
                    {
                        if (_soloScenario == captured)
                        {
                            return; // already the pick; rebuilding it would only be churn
                        }
                        _soloScenario = captured;
                        RenderScenarioSection();
                    });
            }
            SettleSection(_scenarioSection);
        }

        private void RenderAdversarySection()
        {
            RetireStrip(AdversaryStripKey);
            UiKit.Clear(_adversarySection);
            RenderBoardStrip(_adversarySection, AdversaryStripModel(), "ADVERSARY",
                new List<string> { "butcher", "cult-of-hunlow", "insatiable-horror" });
            SettleSection(_adversarySection);
        }

        private void RenderRoleSection()
        {
            UiKit.Clear(_roleSection);
            Head(_roleSection, "YOU PLAY");
            Choice(_roleSection, "An Investigator", _soloRole == SeatRole.Investigator,
                () => SetSoloRole(SeatRole.Investigator));
            Choice(_roleSection, "The Adversary", _soloRole == SeatRole.Adversary,
                () => SetSoloRole(SeatRole.Adversary));
            SettleSection(_roleSection);
        }

        /// <summary>The human's own Investigator, and only when they play one — an Adversary
        /// picks no board, so the section empties and stops taking up any height.</summary>
        private void RenderInvestigatorSection()
        {
            RetireStrip(InvestigatorStripKey);
            UiKit.Clear(_investigatorSection);
            bool playing = _soloRole == SeatRole.Investigator;
            _investigatorSection.gameObject.SetActive(playing);
            if (playing)
            {
                RenderBoardStrip(_investigatorSection, InvestigatorStripModel(),
                    "YOUR INVESTIGATOR",
                    _describe.BaseInvestigators.Select(def => def.Id).ToList());
            }
            SettleSection(_investigatorSection);
        }

        private void RenderStartSection()
        {
            UiKit.Clear(_startSection);
            UiKit.CreateButton(_startSection, "START", 20, StartSoloGame)
                .GetComponent<LayoutElement>().minHeight = 48;
            SettleSection(_startSection);
        }

        /// <summary>
        /// Layout normally runs at the end of the frame, and in that gap freshly built content
        /// is unmeasured: zero height, strips uncentred and unfaded — one visible frame of jump.
        /// The section pass sizes what was just built, the body pass slides the sections below
        /// it into their new places, and the strips re-settle against real widths, all before
        /// this frame draws.
        /// </summary>
        private void SettleSection(RectTransform section)
        {
            if (section.gameObject.activeInHierarchy)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(section);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(_soloBody);
            FitBoardStrips();
        }

        /// <summary>One radio row of the solo setup; picking re-renders whatever it affects.</summary>
        private void Choice(RectTransform section, string label, bool current, Action pick)
        {
            UiKit.CreateButton(section, (current ? "●  " : "○  ") + label, 16, () => pick());
        }

        // ------------------------------------------------------------ selection

        /// <summary>
        /// Taking a board: the strip re-dresses itself in place, and the bot rows follow only
        /// when this actually knocked a bot off the Investigator it was holding.
        /// </summary>
        private void PickInvestigator(string investigatorId)
        {
            _soloInvestigator = investigatorId;
            bool botsChanged = ReleaseHumanInvestigatorFromBots();
            RefreshStrip(InvestigatorStripKey);
            if (botsChanged)
            {
                RenderBotSection();
            }
        }

        private void PickAdversary(string adversaryId)
        {
            _soloAdversary = adversaryId;
            RefreshStrip(AdversaryStripKey);
        }

        /// <summary>
        /// Switching sides re-seats the table: the role row redraws, the Investigator strip
        /// appears or goes away, and the bot rows clamp to the new minimum and maximum.
        /// </summary>
        private void SetSoloRole(SeatRole role)
        {
            if (_soloRole == role)
            {
                return;
            }
            _soloRole = role;
            // Re-seating defaults back to a full table for the new side; rows can still be
            // removed afterwards to play short-handed.
            while (_soloBotInvestigators.Count < MaxBotSeats)
            {
                _soloBotInvestigators.Add(RandomPick);
            }
            while (_soloBotInvestigators.Count > MaxBotSeats)
            {
                _soloBotInvestigators.RemoveAt(_soloBotInvestigators.Count - 1);
            }
            ReleaseHumanInvestigatorFromBots();
            RenderRoleSection();
            RenderInvestigatorSection();
            RenderBotSection();
        }

        /// <summary>The human's claim wins: a bot already holding that Investigator falls back
        /// to Random rather than the two of them starting the same character. True when that
        /// actually happened, so the caller knows whether the bot rows need redrawing.</summary>
        private bool ReleaseHumanInvestigatorFromBots()
        {
            if (_soloRole != SeatRole.Investigator)
            {
                return false;
            }
            bool released = false;
            for (int i = 0; i < _soloBotInvestigators.Count; i++)
            {
                if (_soloBotInvestigators[i] == _soloInvestigator)
                {
                    _soloBotInvestigators[i] = RandomPick;
                    released = true;
                }
            }
            return released;
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

        // ------------------------------------------------------------ bot seats

        /// <summary>2 to 4 Investigators play. The human holds one of those seats unless they
        /// took the Adversary, in which case the bots are the whole team.</summary>
        private int MinBotSeats => _soloRole == SeatRole.Adversary ? 2 : 1;

        private int MaxBotSeats => _soloRole == SeatRole.Adversary ? 4 : 3;

        private void RenderBotSection()
        {
            UiKit.Clear(_botSection);
            Head(_botSection, "BOT INVESTIGATORS");
            var note = UiKit.CreateText(_botSection,
                "Pick each bot's Investigator from its dropdown — Random is drawn at start.",
                13, TextAnchor.MiddleLeft, UiKit.MutedColor);
            note.gameObject.AddComponent<LayoutElement>().minHeight = 22;

            for (int i = 0; i < _soloBotInvestigators.Count; i++)
            {
                CreateBotSeatRow(i);
            }
            UiKit.CreateButton(_botSection, "+  Add a bot", 16, () =>
            {
                _soloBotInvestigators.Add(RandomPick);
                RenderBotSection();
                RefreshStrip(InvestigatorStripKey);
            }, _soloBotInvestigators.Count < MaxBotSeats,
                "Four Investigators is the most that play.");
            SettleSection(_botSection);
        }

        private void CreateBotSeatRow(int seatIndex)
        {
            var row = UiKit.CreateRow(_botSection, "BotSeat" + seatIndex);
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
                RenderBotSection();
                RefreshStrip(InvestigatorStripKey);
            }, _soloBotInvestigators.Count > MinBotSeats,
                _soloRole == SeatRole.Adversary
                    ? "An all-bot team still needs two Investigators."
                    : "The game needs at least two Investigators.",
                danger: true, fixedWidth: 34f);
        }

        private string PickLabel(string investigatorId) =>
            investigatorId == RandomPick ? "Random" : _describe.Investigator(investigatorId);

        /// <summary>"Bot 2's pick" when a bot seat holds that Investigator, else null. Only the
        /// Investigator strip has claims; nothing can claim an Adversary board.</summary>
        private string BotClaimLabel(string investigatorId)
        {
            int seat = _soloBotInvestigators.IndexOf(investigatorId);
            return seat < 0 ? null : "Bot " + (seat + 1) + "'s pick";
        }

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
                        CloseBotDropdown();
                        RenderBotSection();
                        RefreshStrip(InvestigatorStripKey);
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

        // ---------------------------------------------------------------- tick

        /// <summary>
        /// Runs every frame from Update: the strips' edge glide and fit, the inspect-key zoom,
        /// and Escape for an open dropdown.
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
            TickBoardZoom();
        }
    }
}
