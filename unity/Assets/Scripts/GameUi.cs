using System;
using System.Collections.Generic;
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
    public sealed partial class GameUi
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
        private readonly TurnBanner _turnBanner;

        private int _renderedRevision = -1;
        private int _renderedLogCount = -1;

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
            _hand.UseRequested = UseItemFromHand;
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
            View.TurnOpenFor(MyInvestigatorId);

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
                _banner.text = view.Phase == GamePhase.InvestigatorTurns && !MyTurnActive
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

            // "Between turns" for this seat: no turn of ours is open. At a sequential table
            // that also means nobody's is; at a simultaneous one other humans may be mid-turn.
            bool between = view.Phase == GamePhase.InvestigatorTurns && !MyTurnActive &&
                           (view.SimultaneousTurns || !view.OpenTurnIds.Any());
            bool active = MyTurnActive;

            if (between)
            {
                // Beginning your own turn is the YOUR TURN banner's job (MaybeShowTurnBanner).
                // Turn order inside a round is the team's to choose, and the engine tracks the
                // ACTIVE Investigator rather than which seat asked — so this genuinely drives
                // another seat's Investigator, bots included. Handy when a bot wedges; label it
                // plainly so it is never pressed by accident.
                foreach (var other in view.Investigators.Where(i =>
                    i.DefId != me.DefId && !i.TurnTakenThisRound && !i.Escaped &&
                    !view.TurnOpenFor(i.DefId)))
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
            RenderEventSection(view);
            RenderInteracts(view, me, active);
        }

        /// <summary>
        /// Keep the camera on whoever is acting when that someone is a bot, so their turn is
        /// watched rather than discovered from the log. Manual camera input overrides the
        /// glide (BoardView cancels it), and a hidden Adversary has no position to follow.
        /// </summary>
        private void FollowBotAction(PlayerView view)
        {
            string space = null;
            string actingBot = view.Phase == GamePhase.InvestigatorTurns
                ? view.OpenTurnIds.FirstOrDefault(id => id != MyInvestigatorId &&
                    _session.Room.Seats.Any(s => s.Fill == SeatFill.Bot && s.InvestigatorId == id))
                : null;
            if (actingBot != null)
            {
                space = view.Investigators.FirstOrDefault(i => i.DefId == actingBot)?.Space;
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
