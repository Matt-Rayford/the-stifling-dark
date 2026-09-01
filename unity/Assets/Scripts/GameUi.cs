using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;
using StiflingDark.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The in-game HUD: status bar, the roster and Adversary panels, the redacted event log,
    /// and the action bar.
    ///
    /// The server carries no list of legal moves — unlike the Lemonade Wars protocol, an
    /// <c>update</c> here is view + actingSeats + yourTurn, and the engine's
    /// <c>Game.ActionBlockers</c> is server-side only. So buttons are enabled from what the
    /// view does say (whose turn, MP, Charge, Final Action taken, Stamina, phase) and anything
    /// the view cannot know is offered anyway: pressing it posts the command and the engine's
    /// own refusal appears in the log. For a designer hunting rules bugs that is the useful
    /// behaviour — an over-eager button that explains itself beats a hidden one.
    /// </summary>
    public sealed class GameUi
    {
        private readonly IGameSession _session;
        private readonly BoardModel _board;
        private readonly BoardView _boardView;
        private readonly Describe _describe;
        private readonly Prompt _prompt;

        private readonly RectTransform _root;
        private readonly TMP_Text _status;
        private readonly TMP_Text _banner;
        private readonly TMP_Text _stats;
        private readonly RectTransform _rosterBody;
        private readonly RectTransform _actionsBody;
        private readonly RectTransform _logBody;
        private readonly RectTransform _actionBar;
        private readonly TokenActionMenu _tokenMenu;
        private readonly HandView _hand;
        private readonly TokenArt _art;
        private readonly CardPreview _eventPreview = new CardPreview();

        // The round-start Event reveal, and the resting card's hover-to-enlarge.
        private string _eventShownKey;
        private bool _eventBaselineSet;
        private float _eventHoverTime;
        private bool _eventPreviewShown;
        private readonly TurnBanner _turnBanner;

        private int _renderedRevision = -1;
        private int _renderedLogCount = -1;

        /// <summary>Mitchell just confirmed his own placement: open the Sweep aim as soon as
        /// the update carrying the new Flashlight arrives.</summary>
        private bool _offerSweepWhenPlaced;

        /// <summary>An outside click just closed the figure menu; the same click's release
        /// still fires SpaceClicked, which must not reopen it.</summary>
        private bool _menuClosedByThisClick;

        /// <summary>Dwell over the own figure opens the menu (see MaybeOpenMenuByHover).</summary>
        private const float FigureHoverOpenSeconds = 0.5f;
        private float _figureHoverTime;
        /// <summary>False from the moment the menu is open until the pointer leaves the
        /// figure, so closing it under the cursor doesn't pop it right back open.</summary>
        private bool _figureHoverArmed = true;

        // Hovered-move path preview + the auto-walk a click on it starts (see
        // UpdatePathPreview / ContinueWalk).
        private string _pathSignature;
        private List<string> _previewPath = new List<string>();
        private readonly List<string> _walkQueue = new List<string>();
        /// <summary>Where the figure stood when the pending step was sent; anywhere else
        /// than there or the step's target means something derailed the walk.</summary>
        private string _walkFrom;
        /// <summary>A step was sent and its confirming update hasn't landed yet.</summary>
        private bool _walkInFlight;
        /// <summary>Pace between steps, so a long walk reads as walking, not teleporting.</summary>
        private const float WalkStepSeconds = 0.25f;
        private float _walkNextStepTime;

        // A command that needs spaces picked off the board.
        private int _pickCount;
        private string _pickPrompt = "";
        private Action<List<string>> _pickDone;
        private readonly List<string> _picked = new List<string>();

        /// <summary>Leave the table and go back to the menu.</summary>
        public Action LeaveRequested;

        public GameUi(Transform canvas, IGameSession session, BoardModel board,
            BoardView boardView, TokenArt art, Describe describe, Prompt prompt)
        {
            _session = session;
            _board = board;
            _boardView = boardView;
            _describe = describe;
            _prompt = prompt;
            _art = art;

            _root = UiKit.CreateGroup(canvas, "GameUi");
            UiKit.Anchor(_root, Vector2.zero, Vector2.one);

            // ---- top bar
            var top = UiKit.CreatePanel(_root, "TopBar", UiKit.PanelColor);
            UiKit.Anchor(top, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -78), Vector2.zero);
            _status = UiKit.CreateText(top, "", 17, TextAnchor.UpperLeft);
            UiKit.Anchor((RectTransform)_status.transform, Vector2.zero, new Vector2(0.55f, 1),
                new Vector2(16, 4), new Vector2(0, -6));
            _stats = UiKit.CreateText(top, "", 16, TextAnchor.LowerLeft, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)_stats.transform, Vector2.zero, new Vector2(0.55f, 1),
                new Vector2(16, 6), new Vector2(0, -34));
            _banner = UiKit.CreateText(top, "", 20, TextAnchor.MiddleCenter, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)_banner.transform, new Vector2(0.55f, 0), new Vector2(0.86f, 1));

            var topButtons = UiKit.CreateRow(top, "TopButtons", 6f, 30f);
            UiKit.Anchor(topButtons, new Vector2(0.86f, 0), new Vector2(1, 1),
                new Vector2(4, 12), new Vector2(-12, -12));
            UiKit.CreateButton(topButtons, "Fit", 15, () => _boardView.ResetCamera());
            UiKit.CreateButton(topButtons, "Resync", 15, () => _session.Resync());
            UiKit.CreateButton(topButtons, "Leave", 15, () => LeaveRequested?.Invoke());

            // ---- left: roster + adversary
            var left = UiKit.CreatePanel(_root, "Left", UiKit.PanelColor);
            UiKit.Anchor(left, new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(0, 0), new Vector2(360, -78));
            _rosterBody = UiKit.CreateScrollList(left, 6f);

            // ---- right: actions above, log below
            var right = UiKit.CreatePanel(_root, "Right", UiKit.PanelColor);
            UiKit.Anchor(right, new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(-430, 0), new Vector2(0, -78));
            var actionsHost = UiKit.CreateGroup(right, "ActionsHost");
            UiKit.Anchor(actionsHost, new Vector2(0, 0.42f), new Vector2(1, 1),
                new Vector2(4, 4), new Vector2(-4, -4));
            _actionsBody = UiKit.CreateScrollList(actionsHost, 3f);

            var logLabel = UiKit.CreateText(right, "EVENT LOG", 16, TextAnchor.MiddleLeft,
                UiKit.TitleColor);
            logLabel.font = UiKit.MenuFont;
            UiKit.Anchor((RectTransform)logLabel.transform, new Vector2(0, 0.42f), new Vector2(1, 0.42f),
                new Vector2(10, -24), new Vector2(-10, 0));
            var logHost = UiKit.CreateGroup(right, "LogHost");
            UiKit.Anchor(logHost, Vector2.zero, new Vector2(1, 0.42f),
                new Vector2(4, 4), new Vector2(-4, -24));
            _logBody = UiKit.CreateScrollList(logHost, 1f);

            // ---- bottom: no band any more — the map runs to the screen's bottom edge. The
            // Adversary's action bar floats over it between the side columns (Investigators
            // act through the figure menu instead).
            _actionBar = UiKit.CreateGroup(_root, "Actions");
            UiKit.Anchor(_actionBar, Vector2.zero, new Vector2(1, 0),
                new Vector2(368, 8), new Vector2(-438, 62));

            _hand = new HandView(_root, art, describe);
            _tokenMenu = new TokenActionMenu(_root, boardView);
            // Above everything of the HUD's — only the Prompt canvas (order 200) outranks it.
            _turnBanner = new TurnBanner(_root);

            _boardView.SpaceClicked += OnSpaceClicked;
            // A refused step (surcharge, blocker, stale path) must stop an auto-walk cold.
            _session.ErrorReceived += OnSessionError;
        }

        private void OnSessionError(string message) => StopWalk();

        /// <summary>Tear the HUD down — used when a reconnect replaces the session.</summary>
        public void Destroy()
        {
            _boardView.SpaceClicked -= OnSpaceClicked;
            _session.ErrorReceived -= OnSessionError;
            UnityEngine.Object.Destroy(_root.gameObject);
        }

        public void SetActive(bool active)
        {
            _root.gameObject.SetActive(active);
            _boardView.SetActive(active);
            if (!active)
            {
                _prompt.Hide();
                _turnBanner.Hide();
                _tokenMenu.Close();
            }
        }

        // ------------------------------------------------------------ rendering

        public void Tick()
        {
            if (!_root.gameObject.activeSelf)
            {
                return;
            }
            // Any press outside the menu's buttons closes it — before the board sees the
            // click, so the release cannot both close and act on the space underneath.
            if (_tokenMenu.IsOpen && Input.GetMouseButtonDown(0) && !_tokenMenu.ContainsPointer())
            {
                _tokenMenu.Close();
                _menuClosedByThisClick = true;
            }
            _boardView.Tick();
            _tokenMenu.Tick();
            MaybeOpenMenuByHover();
            UpdateEventCardHover();
            ContinueWalk();
            UpdatePathPreview();
            if (_session.Revision != _renderedRevision || _session.Log.Count != _renderedLogCount)
            {
                _renderedRevision = _session.Revision;
                _renderedLogCount = _session.Log.Count;
                Render();
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_pickCount > 0)
                {
                    CancelPick();
                }
                else if (_tokenMenu.IsOpen)
                {
                    _tokenMenu.Close();
                }
                else if (_walkQueue.Count > 0)
                {
                    StopWalk();
                }
            }
            if (Input.GetMouseButtonUp(0))
            {
                // Runs after the board's Tick fired SpaceClicked for this release.
                _menuClosedByThisClick = false;
            }
        }

        private PlayerView View => _session.View;

        private string MyInvestigatorId
        {
            get
            {
                var seat = _session.Room.YourSeatInfo;
                if (seat != null && !string.IsNullOrEmpty(seat.InvestigatorId))
                {
                    return seat.InvestigatorId;
                }
                return View?.ViewerInvestigatorId ?? "";
            }
        }

        private bool AmAdversary =>
            _session.Room.YourSeatInfo?.Role == SeatRole.Adversary ||
            View?.Role == ViewRole.Adversary;

        private PlayerView.InvestigatorPanel Me => View?.Investigators
            .FirstOrDefault(i => i.DefId == MyInvestigatorId);

        private bool MyTurnActive => View != null && !AmAdversary &&
            View.Phase == GamePhase.InvestigatorTurns &&
            View.ActiveInvestigator == MyInvestigatorId &&
            !string.IsNullOrEmpty(MyInvestigatorId);

        private void Render()
        {
            var view = View;
            if (view == null)
            {
                _status.text = "Waiting for the first update…";
                return;
            }

            RenderStatus(view);
            RenderRoster(view);
            RenderLog();
            RenderActions(view);
            RenderMoveTargets(view);
            _boardView.Render(view, MyInvestigatorId);
            MaybeRevealEvent(view);
            FollowBotAction(view);
            // The hand covers the map's bottom edge, so it stands down whenever the map
            // underneath belongs to the mouse (aiming a beam, picking spaces).
            _hand.Render(AmAdversary ? null : Me, _boardView.Aiming || _pickCount > 0);
            RefreshTokenMenu(view);
            MaybeShowModal(view);
            MaybeShowTurnBanner(view);
            OfferSweepIfJustPlaced(view);
        }

        /// <summary>
        /// Between Investigator turns the old BEGIN TURN bar button is a full-screen banner
        /// instead. It appears only when pressing it would actually work: the phase is open,
        /// nobody is mid-turn, this seat's Investigator still has a turn, and the server says
        /// the table is waiting on this seat. Runs after <see cref="MaybeShowModal"/> so a
        /// between-turns Prompt (Spirit adoption, Escape selection) gets the screen first.
        /// </summary>
        private void MaybeShowTurnBanner(PlayerView view)
        {
            var me = AmAdversary ? null : Me;
            bool waiting = me != null
                && view.Phase == GamePhase.InvestigatorTurns
                && view.ActiveInvestigator == null
                && !me.TurnTakenThisRound
                && !me.Escaped
                && _session.YourTurn
                && !view.PendingWindowChoice;
            if (!waiting || _prompt.Open)
            {
                _turnBanner.Hide();
                return;
            }
            string investigatorId = me.DefId;
            _turnBanner.Show("turn:" + view.Round + ":" + investigatorId,
                () => Send(new BeginInvestigatorTurnCommand { InvestigatorId = investigatorId }));
        }

        /// <summary>
        /// Mitchell places, then places a 2nd time (designer note): the moment the update
        /// confirming his own placement arrives, drop straight back into aim mode for the
        /// Sweep's 2nd cone. Escape keeps the 1st position (the offer does not come back —
        /// the SWEEP bar button stays available all round instead).
        /// </summary>
        private void OfferSweepIfJustPlaced(PlayerView view)
        {
            if (!_offerSweepWhenPlaced)
            {
                return;
            }
            var placement = view.Flashlights.FirstOrDefault(f => f.InvestigatorId == "mitchell");
            if (placement == null)
            {
                return; // the placing command has not landed yet (or was refused); keep waiting
            }
            _offerSweepWhenPlaced = false;
            if (!view.RoundModifiers.ContainsKey(Game.SweepUsedPrefix + "mitchell"))
            {
                BeginSweepAim(placement.Space);
            }
        }

        private void RenderStatus(PlayerView view)
        {
            var seat = _session.Room.YourSeatInfo;
            string you = AmAdversary
                ? "seat " + _session.Room.YourSeat + " — Adversary"
                : "seat " + _session.Room.YourSeat + " — " + _describe.Investigator(MyInvestigatorId);
            _status.text =
                "Round " + view.Round + "/" + view.TotalRounds +
                "   ·   " + Describe.Scenario(view.ScenarioId) +
                "   vs   " + Describe.Adversary(view.Adversary.DefId) +
                "   ·   room " + _session.Room.Code +
                "\n" + you + (seat != null && !seat.Connected ? "  (disconnected)" : "");

            var me = Me;
            if (me != null)
            {
                _stats.text =
                    "MP " + me.MpRemaining + "   Stamina " + me.Stamina + "   Charge " + me.Charge +
                    "   Wounds " + me.Wounds.Count + (me.NonSlotWounds.Count > 0
                        ? "+" + me.NonSlotWounds.Count : "") +
                    "   Items " + me.ItemCount + "   Conditions " + me.ConditionCount +
                    "   Evidence " + me.EvidenceCarried.Count +
                    "   Final " + Describe.FinalAction(me.FinalAction) +
                    (me.MovementLocked ? "   [movement locked]" : "") +
                    (me.Dead ? "   [DEAD]" : "");
            }
            else
            {
                var adversary = view.Adversary;
                _stats.text = "Kills " + adversary.Kills + "/" + adversary.KillsToWin +
                    (adversary.MpRemaining.HasValue ? "   MP " + adversary.MpRemaining.Value : "") +
                    (adversary.SprintRolled.HasValue ? "   Sprint " + adversary.SprintRolled.Value : "") +
                    "   Evidence turned in " + view.Objective.EvidenceTurnedIn + "/" +
                    view.Objective.EvidenceRequired;
            }

            if (view.Phase == GamePhase.GameOver)
            {
                _banner.text = Describe.Result(view.Result);
            }
            else if (_pickCount > 0)
            {
                _banner.text = _pickPrompt;
            }
            else if (_boardView.Aiming)
            {
                _banner.text = "Aim the flashlight — click to place, Esc to cancel";
            }
            else if (_session.YourTurn)
            {
                _banner.text = view.Phase == GamePhase.InvestigatorTurns &&
                               view.ActiveInvestigator == null
                    ? "YOUR MOVE — begin your turn"
                    : "YOUR MOVE";
            }
            else
            {
                string waiting = string.Join(", ", _session.ActingSeats.Select(SeatName));
                _banner.text = Describe.Phase(view.Phase) +
                    (waiting.Length > 0 ? " — waiting on " + waiting : "");
            }
        }

        private string SeatName(int seat)
        {
            var info = _session.Room.Seats.FirstOrDefault(s => s.Seat == seat);
            if (info == null)
            {
                return "seat " + seat;
            }
            return info.Name + (info.Fill == SeatFill.Bot ? " (bot)" : "");
        }

        private void RenderRoster(PlayerView view)
        {
            UiKit.Clear(_rosterBody);
            foreach (var panel in view.Investigators)
            {
                bool isMe = panel.DefId == MyInvestigatorId;
                var card = UiKit.CreatePanel(_rosterBody, panel.DefId,
                    isMe ? new Color(0.16f, 0.15f, 0.10f, 0.95f) : UiKit.PanelSoft);
                var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(10, 10, 6, 6);
                layout.spacing = 1;
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;

                string flags = string.Join(" ", new[]
                {
                    panel.Dead ? "DEAD" : null,
                    panel.Escaped ? "ESCAPED" : null,
                    panel.TurnTakenThisRound ? "turn taken" : null,
                    panel.Rested ? "rested" : null,
                    panel.SprintedOrRested && !panel.Rested ? "sprinted" : null,
                    panel.MovementLocked ? "movement locked" : null,
                    panel.SpineChillRound > 0 ? "spine chill r" + panel.SpineChillRound : null,
                }.Where(f => f != null));

                Line(card, _describe.Investigator(panel.DefId) + (isMe ? "   ← you" : ""), 17,
                    isMe ? UiKit.AccentColor : UiKit.TextColor);
                Line(card, "space " + panel.Space + "  ·  " +
                    _board.ZoneName(_board.SpaceOrNull(panel.Space)?.Zone), 14, UiKit.MutedColor);
                Line(card, "St " + panel.Stamina + "   Ch " + panel.Charge +
                    "   MP " + panel.MpRemaining + "   Major " + panel.MajorAbilityTokens +
                    "   Final " + Describe.FinalAction(panel.FinalAction), 14);
                Line(card, "Wounds " + WoundSummary(panel) + "   Evidence " +
                    (panel.EvidenceCarried.Count == 0 ? "—" : string.Join("/", panel.EvidenceCarried)),
                    14);
                if (panel.Items != null && panel.Items.Count > 0)
                {
                    Line(card, "Items: " + string.Join(", ", panel.Items.Select(_describe.Card)), 14,
                        UiKit.MutedColor);
                }
                if (panel.Conditions != null && panel.Conditions.Count > 0)
                {
                    Line(card, "Conditions: " +
                        string.Join(", ", panel.Conditions.Select(_describe.Card)), 14, UiKit.DangerColor);
                }
                if (panel.ConditionCount > (panel.Conditions?.Count ?? 0))
                {
                    Line(card, "+" + (panel.ConditionCount - (panel.Conditions?.Count ?? 0)) +
                        " hidden condition(s)", 14, UiKit.MutedColor);
                }
                if (panel.MapTokens.Count > 0)
                {
                    Line(card, "Map tokens: " + string.Join(", ", panel.MapTokens), 14, UiKit.MutedColor);
                }
                if (panel.SpiritId != null)
                {
                    Line(card, "Spirit: " + _describe.Card(panel.SpiritId) +
                        "  (major " + panel.SpiritMajorTokens + ")", 14, UiKit.GoodColor);
                }
                if (flags.Length > 0)
                {
                    Line(card, flags, 13, UiKit.MutedColor);
                }
            }

            RenderAdversaryPanel(view);
            RenderObjectivePanel(view);
        }

        private string WoundSummary(PlayerView.InvestigatorPanel panel)
        {
            if (panel.Wounds.Count == 0 && panel.NonSlotWounds.Count == 0)
            {
                return "none";
            }
            var parts = panel.Wounds
                .Select(w => w.FaceUp ? _describe.Card(w.CardId) : "face-down")
                .ToList();
            parts.AddRange(panel.NonSlotWounds
                .Select(w => (w.FaceUp ? _describe.Card(w.CardId) : "face-down") + " (unslotted)"));
            return string.Join(", ", parts);
        }

        private void RenderAdversaryPanel(PlayerView view)
        {
            var adversary = view.Adversary;
            var card = UiKit.CreatePanel(_rosterBody, "Adversary",
                new Color(0.16f, 0.08f, 0.09f, 0.95f));
            var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.spacing = 1;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            Line(card, Describe.Adversary(adversary.DefId), 17, new Color(0.95f, 0.62f, 0.58f));
            Line(card, adversary.Space == null
                ? "position hidden" + (adversary.Revealed ? " (revealed?!)" : "")
                : "space " + adversary.Space + (adversary.Revealed ? "  ·  REVEALED" : ""), 14);
            Line(card, "Kills " + adversary.Kills + "/" + adversary.KillsToWin +
                (adversary.MpRemaining.HasValue ? "   MP " + adversary.MpRemaining.Value : "") +
                (adversary.TurnStarted ? "   turn started" : ""), 14);
            if (adversary.Counters.Count > 0)
            {
                Line(card, string.Join("   ",
                    adversary.Counters.OrderBy(c => c.Key).Select(c => c.Key + " " + c.Value)),
                    13, UiKit.MutedColor);
            }
            string attack = adversary.AttackCard != null ? _describe.Card(adversary.AttackCard) : "unknown";
            Line(card, "Attack: " + attack +
                (adversary.AttackUsedThisTurn ? " (used)" : "") +
                (adversary.AttackLockedThisTurn ? " (locked)" : ""), 14);
            Line(card, "Abilities known: " +
                (adversary.ActiveAbilities.Count == 0
                    ? "—"
                    : string.Join(", ", adversary.ActiveAbilities.Select(_describe.Card))) +
                "   (" + adversary.ActiveAbilityCount + " active, " +
                adversary.FaceDownAbilityCount + " face-down)", 14, UiKit.MutedColor);
            var cooldowns = adversary.Cooldown1.Concat(adversary.Cooldown2)
                .Select(c => c.CardId == null ? "?" : _describe.Card(c.CardId)).ToList();
            if (cooldowns.Count > 0)
            {
                Line(card, "Cooldown: " + string.Join(", ", cooldowns), 13, UiKit.MutedColor);
            }
            var living = adversary.Figures.Where(f => f.Alive).ToList();
            if (living.Count > 0)
            {
                Line(card, "Figures: " + string.Join(", ", living.Select(f =>
                    f.Id + (f.Space == null ? " (hidden)" : " @" + f.Space))), 13, UiKit.MutedColor);
            }
        }

        private void RenderObjectivePanel(PlayerView view)
        {
            var objective = view.Objective;
            var card = UiKit.CreatePanel(_rosterBody, "Objective", UiKit.PanelSoft);
            var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.spacing = 1;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            SectionHeader(card, "OBJECTIVE");
            Line(card, "Evidence turned in " + objective.EvidenceTurnedIn + "/" +
                objective.EvidenceRequired +
                (objective.SelectedEscapeCard != null
                    ? "   ·   " + _describe.Card(objective.SelectedEscapeCard)
                    : ""), 15);
            if (objective.Tokens.Count > 0)
            {
                Line(card, string.Join("   ", objective.Tokens.Select(t => t.Key + "@" + t.Value)),
                    13, UiKit.MutedColor);
            }
            if (objective.TokenCarriers.Count > 0)
            {
                Line(card, "carried: " + string.Join(", ", objective.TokenCarriers
                    .Select(t => t.Key + " by " + _describe.ShortInvestigator(t.Value))), 13,
                    UiKit.MutedColor);
            }
            Line(card, "Supplies " + objective.Supplies + "   Parts " + objective.PartsInstalled +
                (objective.EscapeOpen ? "   ESCAPE OPEN" : "") +
                (objective.EscapeReadyRound.HasValue
                    ? "   ready r" + objective.EscapeReadyRound.Value : ""), 13, UiKit.MutedColor);
            var decks = view.Decks;
            Line(card, "decks — event " + decks.Event + "  gen " + decks.GeneralItem +
                "  cursed " + decks.CursedItem + "  wound " + decks.Wound +
                " (+" + decks.WoundDiscard + " discard)", 13, UiKit.MutedColor);
            if (view.PersistentMajorEvents.Count > 0)
            {
                Line(card, "major events: " +
                    string.Join(", ", view.PersistentMajorEvents.Select(_describe.Card)), 13,
                    UiKit.DangerColor);
            }
            if (view.RoundModifiers.Count > 0)
            {
                Line(card, "round modifiers: " + string.Join("  ",
                    view.RoundModifiers.Select(m => m.Key + "=" + m.Value)), 12, UiKit.MutedColor);
            }
        }

        private static void Line(Transform parent, string content, int size, Color? color = null,
            float width = 330f)
        {
            var text = UiKit.CreateText(parent, content, size, TextAnchor.UpperLeft, color);
            var element = text.gameObject.AddComponent<LayoutElement>();
            element.minHeight = EstimateHeight(content, size, width);
            element.flexibleHeight = 0;
        }

        /// <summary>
        /// Rows are laid out by a VerticalLayoutGroup, which needs a height up front, and TMP
        /// truncates rather than growing. Estimating from the character count is crude but it
        /// keeps wrapped ability text readable instead of clipped.
        /// </summary>
        private static float EstimateHeight(string content, int size, float width)
        {
            if (string.IsNullOrEmpty(content))
            {
                return size + 5;
            }
            float charsPerLine = Mathf.Max(8f, width / (size * 0.52f));
            int lines = Mathf.Max(1, Mathf.CeilToInt(content.Length / charsPerLine));
            return lines * (size + 4) + 4;
        }

        private void RenderLog()
        {
            UiKit.Clear(_logBody);
            // Newest last, capped: the whole log can run to hundreds of lines after a resync.
            var entries = _session.Log.Skip(Math.Max(0, _session.Log.Count - 120)).ToList();
            foreach (var entry in entries)
            {
                var color = entry.Type == "error"
                    ? UiKit.DangerColor
                    : entry.Type == "gameover" || entry.Type == "escape"
                        ? UiKit.GoodColor
                        : entry.Type == "wound" || entry.Type == "death"
                            ? new Color(0.90f, 0.60f, 0.55f)
                            : UiKit.MutedColor;
                Line(_logBody, _describe.LogLine(entry), 13, color, 400f);
            }
        }

        // ------------------------------------------------------------- actions

        private void RenderActions(PlayerView view)
        {
            UiKit.Clear(_actionBar);
            UiKit.Clear(_actionsBody);

            if (view.Phase == GamePhase.GameOver)
            {
                UiKit.CreateText(_actionsBody, Describe.Result(view.Result), 18,
                    TextAnchor.MiddleLeft, UiKit.GoodColor)
                    .gameObject.AddComponent<LayoutElement>().minHeight = 30;
                return;
            }
            if (AmAdversary)
            {
                var bar = UiKit.CreateRow(_actionBar, "Bar", 8f, 40f);
                bar.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
                UiKit.Anchor(bar, Vector2.zero, Vector2.one);
                AdversaryUi.Render(this, bar, view);
                return;
            }
            RenderInvestigatorActions(view);
        }

        /// <summary>
        /// The side panel. The turn's own actions are NOT here any more — they live in the
        /// menu that opens on the Investigator's figure (<see cref="RenderTokenActions"/>).
        /// </summary>
        private void RenderInvestigatorActions(PlayerView view)
        {
            var me = Me;
            if (me == null)
            {
                UiKit.CreateText(_actionsBody, "This seat holds no Investigator.", 16,
                    TextAnchor.MiddleLeft, UiKit.MutedColor)
                    .gameObject.AddComponent<LayoutElement>().minHeight = 28;
                return;
            }

            bool between = view.Phase == GamePhase.InvestigatorTurns && view.ActiveInvestigator == null;
            bool active = MyTurnActive;

            if (between)
            {
                // Beginning your own turn is the YOUR TURN banner's job (MaybeShowTurnBanner).
                // Turn order inside a round is the team's to choose, and the engine tracks the
                // ACTIVE Investigator rather than which seat asked — so this genuinely drives
                // another seat's Investigator, bots included. Handy when a bot wedges; label it
                // plainly so it is never pressed by accident.
                foreach (var other in view.Investigators.Where(i =>
                    i.DefId != me.DefId && !i.TurnTakenThisRound && !i.Escaped))
                {
                    var captured = other;
                    UiKit.CreateButton(_actionsBody,
                        "Begin turn for " + _describe.Investigator(captured.DefId) +
                        "  — not your seat", 15,
                        () => Send(new BeginInvestigatorTurnCommand
                        {
                            InvestigatorId = captured.DefId,
                        }), true, null);
                }
            }

            // ---- context panel
            RenderInteracts(view, me, active);
            RenderItemsAndAbilities(view, me, active);
        }

        /// <summary>Close the figure menu when its Investigator is gone or moved; otherwise
        /// rebuild its rows so the enabled states track the view.</summary>
        private void RefreshTokenMenu(PlayerView view)
        {
            if (!_tokenMenu.IsOpen)
            {
                return;
            }
            var me = AmAdversary ? null : Me;
            if (me == null || me.Space != _tokenMenu.Space)
            {
                _tokenMenu.Close();
                return;
            }
            RenderTokenActions(view);
        }

        /// <summary>
        /// The figure menu's rows: the turn's direct actions plus the Involved-Action
        /// interacts that apply where the Investigator stands. Involved is a TYPE of action
        /// (turn in Evidence, flip a switch…), never a button of its own — so there is no
        /// generic INVOLVED here. Rest and Charge stay buttonless too: they happen on their
        /// own at end of turn.
        /// </summary>
        private void RenderTokenActions(PlayerView view)
        {
            var content = _tokenMenu.Content;
            UiKit.Clear(content);
            var me = Me;
            if (me == null)
            {
                _tokenMenu.Close();
                return;
            }
            bool active = MyTurnActive;
            bool blockedByWindow = view.PendingWindowChoice;

            void Row(string label, Action run, bool enabled, string tooltip = null)
            {
                UiKit.CreateButton(content, label, 16, () =>
                {
                    _tokenMenu.Close();
                    run();
                }, enabled, tooltip);
            }

            Row("Sprint", () => Send(new SprintCommand()),
                active && !me.SprintedOrRested && !blockedByWindow,
                me.SprintedOrRested ? "Already Sprinted this turn." : "Not your active turn.");
            Row("Place Flashlight", BeginFlashlightAim,
                active && me.FinalAction == FinalActionKind.None && me.Charge > 0 && !blockedByWindow,
                me.Charge <= 0
                    ? "No Charge left (a placement costs 1, more with a surcharge in force)."
                    : me.FinalAction != FinalActionKind.None
                        ? "Final Action already taken."
                        : "Not your active turn.");
            // Mitchell's Sweep is legal the whole round (his turn already ended when the 1st
            // cone went down), so it should read as the natural 2nd half of placing.
            if (me.DefId == "mitchell")
            {
                var sweepPlacement = view.Flashlights.FirstOrDefault(f => f.InvestigatorId == "mitchell");
                if (sweepPlacement != null)
                {
                    bool alreadySwept = view.RoundModifiers.ContainsKey(Game.SweepUsedPrefix + "mitchell");
                    Row("Sweep", () => BeginSweepAim(sweepPlacement.Space),
                        !alreadySwept && !blockedByWindow,
                        alreadySwept
                            ? "Sweep may only be used once per Flashlight."
                            : "Move the Flashlight to a 2nd position (replaces the 1st).");
                }
            }

            // ---- Involved-Action interacts, shown only where they apply (the engine rules)
            var mySpace = _board.SpaceOrNull(me.Space);
            if (mySpace != null && mySpace.Kind == SpaceKind.LightSwitch)
            {
                // A zone lights up once: after burning out it is Faltering for good, and a
                // dead button that says so beats a click whose refusal hides in the log.
                bool zoneSpent = mySpace.Zone != null &&
                    (view.FalteringZones.Contains(mySpace.Zone) ||
                     view.Overlay.BrightZones.Contains(mySpace.Zone));
                Row("Turn on light", () => Send(new ActivateLightSwitchCommand()),
                    active && !zoneSpent,
                    !zoneSpent
                        ? "Not your active turn."
                        : view.Overlay.BrightZones.Contains(mySpace.Zone ?? "")
                            ? "The lights here are already on."
                            : "These lights burned out (Faltering) — they cannot be turned on again.");
            }
            // Spirits acquire nothing new — no pickup rows (ruling 2026-08-31).
            bool canPickUp = me.SpiritId == null;
            if (canPickUp && view.Evidence.Any(e => e.Space == me.Space))
            {
                Row("Pickup evidence", () => Send(new PickUpEvidenceCommand()), active);
            }
            if (canPickUp && view.MedicalItemSpaces.Contains(me.Space))
            {
                Row("Pickup medical item", () => Send(new PickUpMedicalItemCommand()), active);
            }
            // A discovered POI token is an item stash: 2 General Items, or the Cursed Item on
            // the purple front.
            foreach (var poi in canPickUp
                ? view.PoiTokens.Where(p => p.TokenSpace == me.Space && !p.Collected)
                : Enumerable.Empty<PlayerView.PoiInfo>())
            {
                string label = poi.CursedFront == true
                    ? "Pickup Cursed Item"
                    : poi.CursedFront == false ? "Pickup 2 items" : "Pickup item stash";
                Row(label, () => Send(new PickUpPoiTokenCommand()), active);
            }
            var overlay = BoardModel.OverlayFrom(view);
            foreach (string doorSpace in _board.InteractRange(me.Space, overlay)
                .Where(s => _board.SpaceOrNull(s)?.Kind == SpaceKind.Door))
            {
                var state = view.Overlay.DoorStates.TryGetValue(doorSpace, out var known)
                    ? known : DoorState.Open;
                string door = doorSpace;
                if (state == DoorState.Open)
                {
                    Row("Close door " + door, () => Send(new LockDoorCommand { DoorSpace = door }),
                        active);
                }
                else if (state == DoorState.Locked)
                {
                    Row("Open door " + door, () => Send(new OpenDoorCommand { DoorSpace = door }),
                        active);
                }
            }
            // Turn-in only where the scenario's feature stands (Computer at the Sawmill,
            // Ticket Booth at the park) and never for a Spirit (ruling: a Spirit's Evidence
            // only leaves via a living Investigator Trading with it) — the engine refuses
            // both, so no button.
            var turnInKind = view.ScenarioId == "amusement-park"
                ? SpaceKind.TicketBooth
                : SpaceKind.Computer;
            if (me.EvidenceCarried.Count > 0 && me.SpiritId == null && mySpace != null &&
                mySpace.Kind == turnInKind)
            {
                Row("Turn " + me.EvidenceCarried.Count + " in evidence",
                    () => ShowEvidenceTurnIn(me), active);
            }
            RenderObjectiveRows(view, me, active, Row);

            Row("End Turn", () => Send(new EndTurnCommand()), active && !blockedByWindow,
                "Ends the turn. Rest (+1 Stamina if you did not Sprint) and Charge (+1 if you " +
                "did not place the Flashlight) happen on their own.");
        }

        /// <summary>The Escape card's flow per card id — mirrors the 'objective' field in
        /// game-data/cards/escape-cards.json, which the engine's CardDef does not carry.</summary>
        private static readonly Dictionary<string, string> EscapeFlow = new Dictionary<string, string>
        {
            ["north-gate"] = "gate", ["south-gate"] = "gate",
            ["garage"] = "truck", ["sawmill"] = "truck",
            ["tunnel-of-love"] = "tunnel", ["mirror-maze"] = "tunnel",
            ["the-zipper"] = "flare", ["ferris-wheel"] = "flare",
        };

        /// <summary>Landmark tokens that stay printed on the board; never offered as pickups.</summary>
        private static readonly HashSet<string> LandmarkTokens = new HashSet<string>
        {
            "saw", "locked-escape", "truck", "escape", "grave-actual", "grave-decoy", "altar",
        };

        /// <summary>
        /// Objective actions, shown only where their printed preconditions hold — the token
        /// stood on, the token carried, the selected Escape card's flow. Replaces the old
        /// always-visible OBJECTIVE ACTIONS wall; the engine still rules on legality.
        /// </summary>
        private void RenderObjectiveRows(PlayerView view, PlayerView.InvestigatorPanel me,
            bool active, Action<string, Action, bool, string> row)
        {
            var objective = view.Objective;
            bool OnToken(string name) =>
                objective.Tokens.TryGetValue(name, out var space) && space == me.Space;
            bool Carrying(string name) =>
                objective.TokenCarriers.TryGetValue(name, out var who) && who == me.DefId;
            void Add(string label, Action run) => row(label, run, active, null);

            string card = objective.SelectedEscapeCard ?? "";
            EscapeFlow.TryGetValue(card, out string flow);

            if (OnToken("saw") && Carrying("lockbox"))
            {
                Add("Open the Lockbox",
                    () => Send(new OpenLockboxCommand { PushYourLuck = false }));
                Add("Open the Lockbox — push your luck",
                    () => Send(new OpenLockboxCommand { PushYourLuck = true }));
            }
            if (OnToken("locked-escape"))
            {
                switch (flow)
                {
                    case "gate":
                        Add("Power the Gate", () => Send(new PowerTheGateCommand()));
                        Add("Escape through the Gate", () => Send(new EscapeThroughGateCommand()));
                        break;
                    case "tunnel":
                        Add("Open the Service Tunnel", () => Send(new OpenServiceTunnelCommand()));
                        Add("Escape through the Tunnel", () => Send(new EscapeThroughTunnelCommand()));
                        break;
                    case "flare":
                        Add("Fire the Flare Gun", () => Send(new FireFlareGunCommand()));
                        break;
                }
            }
            if (flow == "truck")
            {
                if (OnToken("truck"))
                {
                    Add("Start the Truck…", () => PickSpaces(1, "Click the Truck's escape space",
                        picked => Send(new StartTruckCommand { EscapeSpace = picked[0] })));
                    foreach (var carried in objective.TokenCarriers
                        .Where(c => c.Value == me.DefId && !LandmarkTokens.Contains(c.Key)))
                    {
                        string part = carried.Key;
                        Add("Install " + part,
                            () => Send(new InstallPartCommand { PartToken = part }));
                    }
                }
                if (OnToken("escape"))
                {
                    Add("Escape at the Truck exit", () => Send(new EscapeAtTruckExitCommand()));
                }
            }
            if (flow == "flare" && (objective.EscapeOpen || objective.EscapeReadyRound != null))
            {
                Add("Escape by Helicopter", () => Send(new EscapeByHelicopterCommand()));
            }
            switch (card)
            {
                case "the-grave":
                    Add("Dig up the Grave", () => Send(new DigUpGraveCommand()));
                    Add("Use the Hook…", () => PickSpaces(1, "Click the space the Hook targets",
                        picked => Send(new UseTheHookCommand { ChosenSpace = picked[0] })));
                    if (me.Items != null && me.Items.Contains("frayed-ropes"))
                    {
                        Add("Use the Frayed Ropes", () => Send(new UseFrayedRopesCommand()));
                    }
                    break;
                case "the-altar":
                    if (Carrying("ritual-knife"))
                    {
                        Add("Use the Ritual Knife",
                            () => Send(new UseRitualKnifeCommand { FlipFaceDownWound = false }));
                        Add("Use the Ritual Knife — flip a face-down Wound",
                            () => Send(new UseRitualKnifeCommand { FlipFaceDownWound = true }));
                    }
                    Add("Cut the Rope Circle", () => Send(new CutRopeCircleCommand()));
                    break;
                case "the-eggs":
                    Add("Destroy the Egg Sac", () => Send(new DestroyEggSacCommand()));
                    Add("Banish the Horror", () => Send(new BanishTheHorrorCommand()));
                    break;
            }

            // Carryable Objective tokens on this space, with the pickup verb their flow uses.
            foreach (var token in objective.Tokens
                .Where(t => t.Value == me.Space && !LandmarkTokens.Contains(t.Key)))
            {
                string name = token.Key;
                Add("Pick up " + name, () =>
                {
                    if (name == "ritual-knife" || name == "rope-circle")
                    {
                        Send(new PickUpBanishTokenCommand { TokenName = name });
                    }
                    else if (name.Contains("part") || name.Contains("ride"))
                    {
                        Send(new PickUpRidePartsCommand { Token = name });
                    }
                    else
                    {
                        Send(new PickUpObjectiveTokenCommand { TokenName = name });
                    }
                });
            }
            foreach (var carried in objective.TokenCarriers.Where(c => c.Value == me.DefId))
            {
                string name = carried.Key;
                Add("Drop " + name,
                    () => Send(new DropObjectiveTokenCommand { TokenName = name }));
            }
        }

        private void RenderInteracts(PlayerView view, PlayerView.InvestigatorPanel me, bool active)
        {
            // Pickups, doors, the light switch and Evidence turn-in moved to the figure menu
            // (RenderTokenActions); what remains here is trading and the placeable rewards,
            // header-free — the buttons say what they are.
            var overlay = BoardModel.OverlayFrom(view);
            var range = _board.InteractRange(me.Space, overlay);

            // Trading, with whoever is in range.
            foreach (var other in view.Investigators.Where(i =>
                i.DefId != me.DefId && !i.Dead && !i.Escaped && range.Contains(i.Space)))
            {
                var target = other;
                if (me.Items != null)
                {
                    foreach (string item in me.Items)
                    {
                        string cardId = item;
                        CommandButton("Give " + _describe.Card(cardId) + " to " +
                            _describe.ShortInvestigator(target.DefId),
                            new TradeItemCommand
                            {
                                ToInvestigatorId = target.DefId,
                                ItemCardId = cardId,
                            }, active);
                    }
                }
                foreach (string zone in me.EvidenceCarried)
                {
                    string carried = zone;
                    CommandButton("Give " + _board.ZoneName(carried) + " Evidence to " +
                        _describe.ShortInvestigator(target.DefId),
                        new TradeEvidenceCommand
                        {
                            ToInvestigatorId = target.DefId,
                            Zone = carried,
                        }, active);
                }
            }

            // Map tokens the Evidence rewards handed out.
            if (me.MapTokens.Contains("open-window"))
            {
                ActionButton("Place the Open Window token (pick 2 spaces)", () =>
                    PickSpaces(2, "Click the two spaces the Window joins",
                        picked => Send(new PlaceOpenWindowTokenCommand
                        {
                            A = picked[0],
                            B = picked[1],
                        })), active);
            }
            if (me.MapTokens.Contains("dim"))
            {
                foreach (var zone in _board.Map.Zones)
                {
                    var captured = zone;
                    ActionButton("Place the Dim token in " + captured.Value, () =>
                        Send(new PlaceDimTokenCommand { Zone = captured.Key }), active);
                }
            }
            if (me.MapTokens.Contains("secret-passage"))
            {
                ActionButton("Place the Secret Passage token (pick 2 spaces)", () =>
                    PickSpaces(2, "Click the two spaces the passage joins",
                        picked => Send(new PlaceSecretPassageCommand
                        {
                            A = picked[0],
                            B = picked[1],
                        })), active);
            }

            // Persistent major Events keep a text line here — the board-side card only
            // shows the round's own draw.
            foreach (string persistent in view.PersistentMajorEvents
                .Where(id => id != view.CurrentEvent))
            {
                Line(_actionsBody, _describe.Card(persistent) + "  (persists)", 15,
                    UiKit.AccentColor);
                Line(_actionsBody, _describe.CardText(persistent), 14, UiKit.MutedColor);
            }
        }

        private void RenderItemsAndAbilities(PlayerView view, PlayerView.InvestigatorPanel me,
            bool active)
        {
            Header("ITEMS");
            if (me.Items == null || me.Items.Count == 0)
            {
                Note("(none)");
            }
            else
            {
                foreach (string item in me.Items)
                {
                    string cardId = item;
                    ActionButton("Use " + _describe.Card(cardId) + _describe.SupplySuffix(cardId), () =>
                        AskArgs("Use " + _describe.Card(cardId), _describe.CardText(cardId),
                            args => Send(new UseItemCommand { CardId = cardId, Args = args })),
                        active);
                }
            }
            ActionButton("Resolve Painkillers…", () =>
                AskArgs("Painkillers", "Two arguments: the existing Wound card id to discard, " +
                    "then the drawn card id to keep.",
                    args => Send(new ResolvePainkillersCommand
                    {
                        ExistingWoundCardId = args != null && args.Count > 0 ? args[0] : null,
                        ChosenDrawnCardId = args != null && args.Count > 1 ? args[1] : null,
                    })), active);

            Header("ABILITIES");
            var def = _describe.InvestigatorOrNull(me.DefId);
            if (def != null)
            {
                Note(def.MinorAbility.Name + ": " + def.MinorAbility.Text);
                Note(def.MajorAbility.Name + ": " + def.MajorAbility.Text);
            }
            ActionButton("Use MINOR ability", () =>
                AskArgs("Minor ability", def?.MinorAbility.Text,
                    args => Send(new UseMinorAbilityCommand { Args = args })), active);
            ActionButton("Use MAJOR ability  (" + me.MajorAbilityTokens + " token)", () =>
                AskArgs("Major ability", def?.MajorAbility.Text,
                    args => Send(new UseMajorAbilityCommand { Args = args })),
                active && me.MajorAbilityTokens > 0);

            // (Mitchell's Sweep lives in the main action bar — see RenderInvestigatorActions —
            // where it reads as the 2nd half of placing rather than a buried ability.)

            // Another Investigator's ability, for the cards that let you lend one.
            foreach (var other in view.Investigators.Where(i => i.DefId != me.DefId))
            {
                var captured = other;
                ActionButton("Minor ability of " + _describe.ShortInvestigator(captured.DefId), () =>
                    AskArgs("Minor ability — " + _describe.Investigator(captured.DefId), null,
                        args => Send(new UseMinorAbilityCommand
                        {
                            InvestigatorId = captured.DefId,
                            Args = args,
                        })), active);
            }

            if (me.SpiritId != null)
            {
                Header("SPIRIT  ·  " + _describe.Card(me.SpiritId));
                Note(_describe.CardText(me.SpiritId));
                ActionButton("Use a Spirit ability…", () =>
                    AskArgs("Spirit ability",
                        "First argument is the ability name; the rest are its arguments.",
                        args => Send(new UseSpiritAbilityCommand
                        {
                            AbilityName = args != null && args.Count > 0 ? args[0] : "",
                            Args = args != null && args.Count > 1 ? args.Skip(1).ToList() : null,
                        })), active);
            }

            if (view.CurrentEvent != null)
            {
                Header("EVENT  ·  " + _describe.Card(view.CurrentEvent));
                Note(_describe.CardText(view.CurrentEvent));
            }
        }

        /// <summary>
        /// ONE Involved Action turns in EVERYTHING carried, all rewards collected (designer
        /// 2026-08-31 — the old picker sent one token and ended the turn). The wizard walks
        /// the carried tokens: each picks its reward (arguments included), and the single
        /// command with the whole list goes out at the end. Cancelling anywhere abandons
        /// the lot — nothing is sent, the turn is untouched.
        /// </summary>
        private void ShowEvidenceTurnIn(PlayerView.InvestigatorPanel me)
        {
            CollectTurnIns(me.EvidenceCarried.ToList(), new List<EvidenceTurnIn>());
        }

        private void CollectTurnIns(List<string> remaining, List<EvidenceTurnIn> chosen)
        {
            if (remaining.Count == 0)
            {
                Send(new TurnInEvidenceCommand { TurnIns = chosen });
                return;
            }
            string zone = remaining[0];
            var rest = remaining.Skip(1).ToList();
            var options = new List<PromptOption>();
            foreach (var reward in Describe.EvidenceRewards)
            {
                var captured = reward;
                options.Add(new PromptOption(captured.Label, () =>
                    ChooseRewardArg(captured, arg =>
                    {
                        chosen.Add(new EvidenceTurnIn
                        {
                            Zone = zone,
                            Reward = captured.Reward,
                            Arg = arg,
                        });
                        CollectTurnIns(rest, chosen);
                    })));
            }
            // The reward menu is identical no matter whose Zone the token came from, so the
            // steps are numbered plainly and the zones are consumed behind the scenes.
            int step = chosen.Count + 1;
            int total = chosen.Count + remaining.Count;
            _prompt.Show("turnin:" + zone + ":" + step,
                "Turn in Evidence — reward " + step + " of " + total,
                "One Involved Action turns in everything you carry; pick a reward for each " +
                "token. Turning in ends your turn.",
                options, () => { });
        }

        private void ChooseRewardArg(
            (string Reward, string Label, Describe.RewardArg Arg) reward, Action<string> done)
        {
            switch (reward.Arg)
            {
                case Describe.RewardArg.PoiSpace:
                {
                    var options = View.PoiTokens
                        .Select(p => new PromptOption("Point of Interest at " + p.PoiSpace,
                            () => done(p.PoiSpace)))
                        .ToList();
                    _prompt.Show("reward-poi", "Reveal which Point of Interest?", null, options,
                        () => { });
                    break;
                }
                case Describe.RewardArg.MirrorColor:
                {
                    var options = new[] { "red", "green", "blue" }
                        .Select(c => new PromptOption(c, () => done(c))).ToList();
                    _prompt.Show("reward-mirror", "Which Mirror Maze color is open?", null, options,
                        () => { });
                    break;
                }
                case Describe.RewardArg.Investigator:
                {
                    var options = View.Investigators
                        .Select(i => new PromptOption(_describe.Investigator(i.DefId),
                            () => done(i.DefId)))
                        .ToList();
                    _prompt.Show("reward-inv", "Give the Major Ability token to whom?", null, options,
                        () => { });
                    break;
                }
                default:
                    done(null);
                    break;
            }
        }

        // ------------------------------------------------------ pending modals

        private void MaybeShowModal(PlayerView view)
        {
            if (_pickCount > 0 || _boardView.Aiming)
            {
                return;
            }
            var me = Me;

            if (view.PendingWindowChoice && MyTurnActive)
            {
                _prompt.Show("window:" + view.Round, "You are halfway through a Window",
                    "Stop here and lose Stamina, or push on and take a Wound.",
                    new List<PromptOption>
                    {
                        new PromptOption("Stop — lose Stamina",
                            () => Send(new ResolveWindowCommand { StopAndLoseStamina = true })),
                        new PromptOption("Push on — take a Wound",
                            () => Send(new ResolveWindowCommand { StopAndLoseStamina = false })),
                    });
                return;
            }

            if (view.EscapeChoices != null && view.EscapeChoices.Count > 0 &&
                view.Objective.SelectedEscapeCard == null && !AmAdversary)
            {
                var options = view.EscapeChoices
                    .Select(id => new PromptOption(_describe.Card(id),
                        () => Send(new SelectEscapeCardCommand { CardId = id }),
                        _describe.CardText(id)))
                    .ToList();
                _prompt.Show("escape:" + string.Join(",", view.EscapeChoices),
                    "Choose the team's Escape card",
                    "Enough Evidence is in. This choice belongs to the whole team.", options);
                return;
            }

            if (me != null && me.Dead && me.SpiritId == null && view.AvailableSpiritIds.Count > 0)
            {
                var options = view.AvailableSpiritIds
                    .Select(id => new PromptOption(_describe.Card(id),
                        () => Send(new AdoptSpiritCommand
                        {
                            DeadInvestigatorId = me.DefId,
                            SpiritId = id,
                        }), _describe.CardText(id)))
                    .ToList();
                _prompt.Show("spirit:" + me.DefId, "Adopt a Spirit",
                    _describe.Investigator(me.DefId) + " is dead. Take a Spirit card to keep playing.",
                    options);
                return;
            }

            // These are the ADVERSARY's choices (Fallen Tree, Flare-Up, Roll Vortex, Fire
            // Tornado): only a human holding that seat is ever asked. A bot Adversary answers
            // them itself, and an Investigator seat must never see this modal.
            if (view.PendingEventChoices.Count > 0 && AmAdversary)
            {
                var options = view.PendingEventChoices
                    .Select(id => new PromptOption("Answer " + _describe.Card(id),
                        () => AskArgs("Answer " + _describe.Card(id), _describe.CardText(id),
                            args => Send(new ResolveEventChoiceCommand { EventId = id, Args = args })),
                        _describe.CardText(id)))
                    .ToList();
                _prompt.Show("event:" + string.Join(",", view.PendingEventChoices),
                    "An Event is waiting on a choice", null, options);
                return;
            }

            // Take down only the modals THIS method raises: a picker the player opened from a
            // button must survive the update that a bot's turn pushes in behind it.
            string signature = _prompt.Signature;
            if (signature.StartsWith("window:") || signature.StartsWith("escape:") ||
                signature.StartsWith("spirit:") || signature.StartsWith("event:"))
            {
                _prompt.Hide();
            }
        }

        private void AskArgs(string title, string body, Action<List<string>> submit)
        {
            var quickFill = new List<KeyValuePair<string, string>>();
            var view = View;
            var me = Me;
            if (view != null)
            {
                foreach (var panel in view.Investigators)
                {
                    quickFill.Add(new KeyValuePair<string, string>(panel.DefId,
                        "Investigator " + _describe.Investigator(panel.DefId)));
                }
                if (me != null)
                {
                    var overlay = BoardModel.OverlayFrom(view);
                    foreach (string space in _board.InteractRange(me.Space, overlay))
                    {
                        quickFill.Add(new KeyValuePair<string, string>(space, "space " + space));
                    }
                    if (me.Items != null)
                    {
                        foreach (string item in me.Items)
                        {
                            quickFill.Add(new KeyValuePair<string, string>(item,
                                "item " + _describe.Card(item)));
                        }
                    }
                }
                foreach (var zone in _board.Map.Zones)
                {
                    quickFill.Add(new KeyValuePair<string, string>(zone.Key, "zone " + zone.Value));
                }
            }
            _prompt.ShowArgs("args", title,
                (body ?? "") + "\n\nArguments are engine ids, comma separated. If the engine wants " +
                "something else it says so in the log.",
                quickFill, submit, () => { });
        }

        // -------------------------------------------------------- board clicks

        private void RenderMoveTargets(PlayerView view)
        {
            var costs = new Dictionary<string, int>();
            var overlay = BoardModel.OverlayFrom(view);
            if (_pickCount > 0)
            {
                _boardView.SetMoveTargets(costs);
                return;
            }
            if (AmAdversary && view.Adversary.Space != null &&
                (view.Phase == GamePhase.AdversaryTurn || view.Phase == GamePhase.AdversarySetup))
            {
                foreach (var step in _board.StepsFrom(view.Adversary.Space, FigureKind.Adversary, overlay))
                {
                    costs[step.Key] = step.Value.Cost;
                }
            }
            // Investigator turns get no adjacent-space rings any more: the hovered-path
            // preview (UpdatePathPreview) shows where a click will take you instead. The
            // human Adversary keeps them — no path preview on that side yet.
            _boardView.SetMoveTargets(costs);
        }

        // ------------------------------------------------------- hovered-move path

        /// <summary>
        /// Hovering a space while movement is available highlights the shortest walk there
        /// in blue — or, past the MP budget, the affordable prefix toward the mouse.
        /// Clicking then walks the highlighted path (see OnSpaceClicked/ContinueWalk).
        /// Recomputed only when the hover, position, MP or game state actually changed.
        /// </summary>
        private void UpdatePathPreview()
        {
            var me = AmAdversary ? null : Me;
            string hovered = _boardView.HoveredSpace;
            bool eligible = me != null && MyTurnActive && !me.MovementLocked &&
                me.MpRemaining > 0 && !_boardView.Aiming && _pickCount == 0 &&
                _walkQueue.Count == 0 && hovered != null && hovered != me.Space;
            string signature = eligible
                ? hovered + "|" + me.Space + "|" + me.MpRemaining + "|" + _session.Revision
                : "";
            if (signature == _pathSignature)
            {
                return;
            }
            _pathSignature = signature;
            _previewPath = eligible ? PathToward(hovered, me) : new List<string>();
            _boardView.SetPathPreview(_previewPath);
        }

        /// <summary>
        /// Preview-only surcharge on a Window step: the route prefers a slightly longer
        /// walk around, but a Window still previews (and walks) when it is genuinely
        /// shorter or the only way — the walk then pauses at the crossing for the
        /// Wound-or-Stamina prompt, which stays a manual, deliberate choice.
        /// </summary>
        private const int WindowPreviewSurcharge = 4;

        /// <summary>
        /// Dijkstra over legal steps (light-based costs included) from the figure to
        /// <paramref name="target"/>, cut down to what the remaining MP affords. Routes
        /// prefer to avoid Windows (see <see cref="WindowPreviewSurcharge"/>) but never
        /// blank out because of one: the blue path shows across the frame, and the
        /// auto-walk stops there for the crossing choice (ContinueWalk bails on
        /// PendingWindowChoice). Empty when no route exists at all.
        /// </summary>
        private List<string> PathToward(string target, PlayerView.InvestigatorPanel me)
        {
            var overlay = BoardModel.OverlayFrom(View);
            var kind = me.Dead && me.SpiritId != null
                ? FigureKind.Spirit
                : FigureKind.Investigator;
            // prio orders the search (with the Window surcharge); mp is the honest MP spend
            // used to trim the path to what this turn affords.
            var prio = new Dictionary<string, int> { [me.Space] = 0 };
            var mp = new Dictionary<string, int> { [me.Space] = 0 };
            var prev = new Dictionary<string, string>();
            var open = new List<string> { me.Space };
            var settled = new HashSet<string>();
            while (open.Count > 0)
            {
                // Linear min extraction: the board is ~350 spaces and this runs only when
                // the hover actually changes.
                string current = open[0];
                foreach (string candidate in open)
                {
                    if (prio[candidate] < prio[current])
                    {
                        current = candidate;
                    }
                }
                open.Remove(current);
                if (!settled.Add(current))
                {
                    continue;
                }
                if (current == target)
                {
                    break;
                }
                foreach (var step in _board.StepsFrom(current, kind, overlay))
                {
                    int surcharge = step.Value.CrossesWindow ? WindowPreviewSurcharge : 0;
                    int cost = prio[current] + step.Value.Cost + surcharge;
                    if (!prio.TryGetValue(step.Key, out int known) || cost < known)
                    {
                        prio[step.Key] = cost;
                        mp[step.Key] = mp[current] + step.Value.Cost;
                        prev[step.Key] = current;
                        if (!settled.Contains(step.Key))
                        {
                            open.Add(step.Key);
                        }
                    }
                }
            }
            if (!prev.ContainsKey(target))
            {
                return new List<string>();
            }
            var full = new List<string>();
            for (string space = target; space != me.Space; space = prev[space])
            {
                full.Add(space);
            }
            full.Reverse();
            var affordable = full.Where(space => mp[space] <= me.MpRemaining).ToList();
            return affordable;
        }

        /// <summary>
        /// A freshly drawn Event gets dealt to the table: full card over a darkened beat,
        /// then slid to its resting spot beside the board (see <see cref="EventReveal"/>).
        /// The first view after joining sets the baseline silently — replaying the reveal
        /// of a round already in progress would be noise.
        /// </summary>
        private void MaybeRevealEvent(PlayerView view)
        {
            string key = view.Round + ":" + view.CurrentEvent;
            if (!_eventBaselineSet)
            {
                _eventBaselineSet = true;
                _eventShownKey = key;
                return;
            }
            if (string.IsNullOrEmpty(view.CurrentEvent) || key == _eventShownKey)
            {
                return;
            }
            _eventShownKey = key;
            EventReveal.Play(_art.EventCard(view.CurrentEvent), _boardView.EventCardScreenSpot);
        }

        /// <summary>Hovering the resting Event card enlarges it like a hand card: a short
        /// dwell, or Alt/Cmd for instant.</summary>
        private void UpdateEventCardHover()
        {
            bool overUi = UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
            bool over = !overUi && !_boardView.Aiming && _pickCount == 0 &&
                _boardView.EventCardUnderMouse();
            if (!over)
            {
                _eventHoverTime = 0f;
                if (_eventPreviewShown)
                {
                    _eventPreview.Hide();
                    _eventPreviewShown = false;
                }
                return;
            }
            bool modifier = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            _eventHoverTime += Time.deltaTime;
            if (!_eventPreviewShown && (modifier || _eventHoverTime >= 0.75f))
            {
                _eventPreview.Show(_boardView.EventCardSprite);
                _eventPreviewShown = true;
            }
        }

        /// <summary>
        /// Keep the camera on whoever is acting when that someone is a bot, so their turn is
        /// watched rather than discovered from the log. Manual camera input overrides the
        /// glide (BoardView cancels it), and a hidden Adversary has no position to follow.
        /// </summary>
        private void FollowBotAction(PlayerView view)
        {
            string space = null;
            if (view.Phase == GamePhase.InvestigatorTurns && view.ActiveInvestigator != null &&
                view.ActiveInvestigator != MyInvestigatorId &&
                _session.Room.Seats.Any(s => s.Fill == SeatFill.Bot &&
                    s.InvestigatorId == view.ActiveInvestigator))
            {
                space = view.Investigators
                    .FirstOrDefault(i => i.DefId == view.ActiveInvestigator)?.Space;
            }
            else if (view.Phase == GamePhase.AdversaryTurn && !AmAdversary &&
                _session.Room.Seats.Any(s => s.Fill == SeatFill.Bot &&
                    s.Role == SeatRole.Adversary))
            {
                space = view.Adversary?.Space;
            }
            if (!string.IsNullOrEmpty(space))
            {
                _boardView.GlideCameraTo(space);
            }
        }

        /// <summary>Clicking a previewed path queues its steps; each confirmed arrival
        /// schedules the next one a beat later (<see cref="WalkStepSeconds"/>), so the walk
        /// reads as movement. Anything unexpected — a refusal, a window choice, a water
        /// float, the turn ending — abandons the rest.</summary>
        private void ContinueWalk()
        {
            if (_walkQueue.Count == 0)
            {
                return;
            }
            var me = AmAdversary ? null : Me;
            if (me == null || !MyTurnActive || View == null || View.PendingWindowChoice)
            {
                StopWalk();
                return;
            }
            if (_walkInFlight)
            {
                if (me.Space == _walkQueue[0])
                {
                    // Arrived: take a beat before the next step — or, at the destination,
                    // offer the actions right where you now stand.
                    _walkQueue.RemoveAt(0);
                    _walkFrom = me.Space;
                    _walkInFlight = false;
                    if (_walkQueue.Count == 0)
                    {
                        OpenMenuAtFigure(me);
                    }
                    else
                    {
                        _walkNextStepTime = Time.time + WalkStepSeconds;
                    }
                }
                else if (me.Space != _walkFrom)
                {
                    StopWalk(); // derailed (a float, a forced move)
                }
                return;
            }
            if (me.Space != _walkFrom)
            {
                StopWalk();
                return;
            }
            if (Time.time >= _walkNextStepTime)
            {
                _walkInFlight = true;
                Send(new MoveStepCommand { To = _walkQueue[0] });
            }
        }

        private void StopWalk()
        {
            _walkQueue.Clear();
            _walkInFlight = false;
        }

        /// <summary>The figure menu, opened for the player rather than by them — a finished
        /// walk offers the actions where they now stand. Stands down when something else
        /// already owns the screen or the mouse.</summary>
        private void OpenMenuAtFigure(PlayerView.InvestigatorPanel me)
        {
            if (_tokenMenu.IsOpen || _boardView.Aiming || _pickCount > 0 || _prompt.Open ||
                !MyTurnActive)
            {
                return;
            }
            _tokenMenu.OpenAt(me.Space);
            RenderTokenActions(View);
        }

        /// <summary>
        /// Resting the pointer on your own figure for half a second opens the action menu,
        /// clicking not required. Re-arms only after the pointer leaves the figure, so
        /// closing the menu under the cursor doesn't pop it straight back open.
        /// </summary>
        private void MaybeOpenMenuByHover()
        {
            var me = AmAdversary ? null : Me;
            bool overFigure = me != null && !_boardView.Aiming && _pickCount == 0 &&
                _boardView.HoveredSpace == me.Space;
            if (!overFigure)
            {
                _figureHoverArmed = true;
                _figureHoverTime = 0f;
                return;
            }
            if (_tokenMenu.IsOpen)
            {
                _figureHoverArmed = false;
                _figureHoverTime = 0f;
                return;
            }
            if (!_figureHoverArmed)
            {
                return;
            }
            _figureHoverTime += Time.deltaTime;
            if (_figureHoverTime >= FigureHoverOpenSeconds)
            {
                _tokenMenu.OpenAt(me.Space);
                RenderTokenActions(View);
            }
        }

        private void OnSpaceClicked(string spaceId)
        {
            if (_pickCount > 0)
            {
                _picked.Add(spaceId);
                if (_picked.Count >= _pickCount)
                {
                    var done = _pickDone;
                    var picked = new List<string>(_picked);
                    CancelPick();
                    done?.Invoke(picked);
                }
                else
                {
                    _pickPrompt = _pickPrompt + "  [" + string.Join(", ", _picked) + "]";
                    Render();
                }
                return;
            }
            if (AmAdversary && (View?.Phase == GamePhase.AdversaryTurn))
            {
                Send(new AdversaryMoveStepCommand { To = spaceId });
                return;
            }
            var me = Me;
            if (!AmAdversary && me != null && spaceId == me.Space)
            {
                // Your own token: no move to make here — this click is the action menu's.
                if (_tokenMenu.IsOpen)
                {
                    _tokenMenu.Close();
                }
                else if (!_menuClosedByThisClick)
                {
                    _tokenMenu.OpenAt(spaceId);
                    RenderTokenActions(View);
                }
                return;
            }
            _tokenMenu.Close();
            if (!MyTurnActive)
            {
                return;
            }
            if (_previewPath.Count > 0)
            {
                // Walk the highlighted path — as far as the MP budget's prefix reaches.
                // ContinueWalk sends the first step on the next tick and paces the rest.
                StopWalk();
                _walkQueue.AddRange(_previewPath);
                _previewPath = new List<string>();
                _pathSignature = null;
                _boardView.SetPathPreview(null);
                _walkFrom = me.Space;
                _walkNextStepTime = 0f;
                return;
            }
            Send(new MoveStepCommand { To = spaceId });
        }

        /// <summary>Collect n board clicks, then hand them to <paramref name="done"/>.</summary>
        public void PickSpaces(int count, string prompt, Action<List<string>> done)
        {
            _prompt.Hide();
            _tokenMenu.Close();
            _picked.Clear();
            _pickCount = count;
            _pickPrompt = prompt + " (Esc cancels)";
            _pickDone = done;
            Render();
        }

        private void CancelPick()
        {
            _pickCount = 0;
            _pickPrompt = "";
            _pickDone = null;
            _picked.Clear();
            Render();
        }

        private void BeginFlashlightAim()
        {
            var me = Me;
            if (me == null)
            {
                return;
            }
            _prompt.Hide();
            _boardView.FocusOn(me.Space);
            _boardView.BeginAim(me.Space,
                angle =>
                {
                    Send(new PlaceFlashlightCommand { AngleRadians = angle });
                    // Mitchell's Sweep is part of his placement flow (designer note): the
                    // moment the 1st cone lands, offer the 2nd immediately. The offer fires
                    // from Render() once the update confirming the placement arrives.
                    _offerSweepWhenPlaced = MyInvestigatorId == "mitchell";
                },
                () => Render());
            Render();
        }

        /// <summary>
        /// Re-enters beam-aiming for Mitchell's Sweep, anchored at the FLASHLIGHT'S space
        /// rather than his own (he may well have moved since placing it, and the printed text
        /// moves the beam, not him). Reuses the same aim mode as <see cref="BeginFlashlightAim"/>
        /// so the live ComputeBright preview is identical; on confirm it sends the Minor Ability
        /// wire format (UseMinorAbilityCommand, invId "mitchell", the angle as its one string
        /// arg) rather than PlaceFlashlightCommand, matching how MitchellSweep parses
        /// use.Args[0] server-side.
        /// </summary>
        private void BeginSweepAim(string flashlightSpace)
        {
            _prompt.Hide();
            _boardView.FocusOn(flashlightSpace);
            _boardView.BeginAim(flashlightSpace,
                angle => Send(new UseMinorAbilityCommand
                {
                    InvestigatorId = "mitchell",
                    Args = new List<string> { angle.ToString("R", CultureInfo.InvariantCulture) },
                }),
                () => Render());
            Render();
        }

        // ------------------------------------------------------------ plumbing

        /// <summary>Post a command. Used by <see cref="AdversaryUi"/> too.</summary>
        public void Send(GameCommand command) => _session.Submit(command);

        public Describe Describer => _describe;
        public BoardModel Board => _board;
        public PlayerView CurrentView => View;

        public void CommandButton(string label, GameCommand command, bool enabled,
            string tooltip = null)
        {
            if (command == null)
            {
                return;
            }
            UiKit.CreateButton(_actionsBody, label, 15, () => Send(command), enabled,
                tooltip ?? "Only available on your own active turn.");
        }

        public void ActionButton(string label, Action onClick, bool enabled, string tooltip = null)
        {
            UiKit.CreateButton(_actionsBody, label, 15, () => onClick(), enabled,
                tooltip ?? "Only available on your own active turn.");
        }

        public void Header(string title) => SectionHeader(_actionsBody, title);

        /// <summary>
        /// A section heading, in the main menu's own heading style (StiflingDarkApp.Menu's
        /// Head): sage green, the Cenotaph display font, one size up from body text. The amber
        /// accent stays with the things that demand attention — the banner, the hint line —
        /// rather than every label above a list.
        /// </summary>
        private static void SectionHeader(Transform parent, string title)
        {
            var text = UiKit.CreateText(parent, title, 16, TextAnchor.MiddleLeft, UiKit.TitleColor);
            text.font = UiKit.MenuFont;
            var element = text.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 26;
            element.flexibleHeight = 0;
        }

        public void Note(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return;
            }
            var text = UiKit.CreateText(_actionsBody, content, 13, TextAnchor.UpperLeft,
                UiKit.MutedColor);
            var element = text.gameObject.AddComponent<LayoutElement>();
            element.minHeight = EstimateHeight(content, 13, 400f);
            element.flexibleHeight = 0;
        }

        public void AskArgsPublic(string title, string body, Action<List<string>> submit) =>
            AskArgs(title, body, submit);

        public Prompt Modal => _prompt;
    }
}
