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
        private readonly ServerSession _session;
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
        private readonly TMP_Text _hint;

        private int _renderedRevision = -1;
        private int _renderedLogCount = -1;

        // A command that needs spaces picked off the board.
        private int _pickCount;
        private string _pickPrompt = "";
        private Action<List<string>> _pickDone;
        private readonly List<string> _picked = new List<string>();

        /// <summary>Leave the table and go back to the menu.</summary>
        public Action LeaveRequested;

        public GameUi(Transform canvas, ServerSession session, BoardModel board,
            BoardView boardView, Describe describe, Prompt prompt)
        {
            _session = session;
            _board = board;
            _boardView = boardView;
            _describe = describe;
            _prompt = prompt;

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
                new Vector2(0, 150), new Vector2(360, -78));
            _rosterBody = UiKit.CreateScrollList(left, 6f);

            // ---- right: actions above, log below
            var right = UiKit.CreatePanel(_root, "Right", UiKit.PanelColor);
            UiKit.Anchor(right, new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(-430, 150), new Vector2(0, -78));
            var actionsHost = UiKit.CreateGroup(right, "ActionsHost");
            UiKit.Anchor(actionsHost, new Vector2(0, 0.42f), new Vector2(1, 1),
                new Vector2(4, 4), new Vector2(-4, -4));
            _actionsBody = UiKit.CreateScrollList(actionsHost, 3f);

            var logLabel = UiKit.CreateText(right, "EVENT LOG", 13, TextAnchor.MiddleLeft, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)logLabel.transform, new Vector2(0, 0.42f), new Vector2(1, 0.42f),
                new Vector2(10, -22), new Vector2(-10, 0));
            var logHost = UiKit.CreateGroup(right, "LogHost");
            UiKit.Anchor(logHost, Vector2.zero, new Vector2(1, 0.42f),
                new Vector2(4, 4), new Vector2(-4, -24));
            _logBody = UiKit.CreateScrollList(logHost, 1f);

            // ---- bottom action bar
            var bottom = UiKit.CreatePanel(_root, "Bottom", UiKit.PanelColor);
            UiKit.Anchor(bottom, Vector2.zero, new Vector2(1, 0), Vector2.zero, new Vector2(0, 150));
            _hint = UiKit.CreateText(bottom, "", 16, TextAnchor.MiddleLeft, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)_hint.transform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(16, -30), new Vector2(-16, -4));
            _actionBar = UiKit.CreateGroup(bottom, "Actions");
            UiKit.Anchor(_actionBar, Vector2.zero, new Vector2(1, 1),
                new Vector2(10, 8), new Vector2(-10, -32));

            _boardView.SpaceClicked += OnSpaceClicked;
        }

        /// <summary>Tear the HUD down — used when a reconnect replaces the session.</summary>
        public void Destroy()
        {
            _boardView.SpaceClicked -= OnSpaceClicked;
            UnityEngine.Object.Destroy(_root.gameObject);
        }

        public void SetActive(bool active)
        {
            _root.gameObject.SetActive(active);
            _boardView.SetActive(active);
            if (!active)
            {
                _prompt.Hide();
            }
        }

        // ------------------------------------------------------------ rendering

        public void Tick()
        {
            if (!_root.gameObject.activeSelf)
            {
                return;
            }
            _boardView.Tick();
            if (_session.Revision != _renderedRevision || _session.Log.Count != _renderedLogCount)
            {
                _renderedRevision = _session.Revision;
                _renderedLogCount = _session.Log.Count;
                Render();
            }
            if (_pickCount > 0 && Input.GetKeyDown(KeyCode.Escape))
            {
                CancelPick();
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
            MaybeShowModal(view);
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

            _hint.text = _pickCount > 0
                ? _pickPrompt
                : _boardView.Aiming
                    ? "Move the mouse around your figure to aim — the lit spaces are the real " +
                      "Bright set, computed here with the engine's own beam solver."
                    : MyTurnActive
                        ? "Click a highlighted space to Move.  Scroll to zoom, right-drag to pan."
                        : "Scroll to zoom, right-drag to pan, hover a space to inspect it.";

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

            Line(card, "OBJECTIVE", 14, UiKit.MutedColor);
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
            var bar = UiKit.CreateRow(_actionBar, "Bar", 8f, 40f);
            UiKit.Anchor(bar, Vector2.zero, Vector2.one);

            if (view.Phase == GamePhase.GameOver)
            {
                UiKit.CreateText(_actionsBody, Describe.Result(view.Result), 18,
                    TextAnchor.MiddleLeft, UiKit.GoodColor)
                    .gameObject.AddComponent<LayoutElement>().minHeight = 30;
                return;
            }
            if (AmAdversary)
            {
                AdversaryUi.Render(this, bar, view);
                return;
            }
            RenderInvestigatorActions(view, bar);
        }

        private void RenderInvestigatorActions(PlayerView view, RectTransform bar)
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
            bool blockedByWindow = view.PendingWindowChoice;

            if (between)
            {
                UiKit.CreateButton(bar, "BEGIN TURN — " + _describe.ShortInvestigator(me.DefId), 18,
                    () => Send(new BeginInvestigatorTurnCommand { InvestigatorId = me.DefId }),
                    !me.TurnTakenThisRound && _session.YourTurn,
                    me.TurnTakenThisRound
                        ? "This Investigator has already taken a turn this round."
                        : "The game is not waiting on your seat.");
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

            // ---- core actions
            UiKit.CreateButton(bar, "SPRINT", 17,
                () => Send(new SprintCommand()),
                active && !me.SprintedOrRested && !blockedByWindow,
                me.SprintedOrRested ? "Already Sprinted or Rested this turn." : "Not your active turn.");
            UiKit.CreateButton(bar, "REST", 17,
                () => Send(new RestCommand()),
                active && !me.SprintedOrRested && !blockedByWindow,
                me.SprintedOrRested ? "Already Sprinted or Rested this turn." : "Not your active turn.");

            // ---- final actions
            UiKit.CreateButton(bar, "CHARGE", 17,
                () => Send(new ChargeFlashlightCommand()),
                active && me.FinalAction == FinalActionKind.None && !blockedByWindow,
                me.FinalAction != FinalActionKind.None
                    ? "Final Action already taken: " + Describe.FinalAction(me.FinalAction)
                    : "Not your active turn.");
            UiKit.CreateButton(bar, "PLACE FLASHLIGHT", 17,
                BeginFlashlightAim,
                active && me.FinalAction == FinalActionKind.None && me.Charge > 0 && !blockedByWindow,
                me.Charge <= 0
                    ? "No Charge left (a placement costs 1, more with a surcharge in force)."
                    : me.FinalAction != FinalActionKind.None
                        ? "Final Action already taken."
                        : "Not your active turn.");
            UiKit.CreateButton(bar, "INVOLVED", 17,
                () => Send(new TakeInvolvedActionCommand()),
                active && me.FinalAction == FinalActionKind.None && !blockedByWindow,
                "Generic Involved Action — ends the turn with no Stamina gain.");
            UiKit.CreateButton(bar, "END TURN", 17,
                () => Send(new EndTurnCommand()), active && !blockedByWindow, "Not your active turn.");

            // ---- context panel
            RenderInteracts(view, me, active);
            RenderItemsAndAbilities(view, me, active);
        }

        private void RenderInteracts(PlayerView view, PlayerView.InvestigatorPanel me, bool active)
        {
            var overlay = BoardModel.OverlayFrom(view);
            var range = _board.InteractRange(me.Space, overlay);
            Header("INTERACT  ·  " + me.Space + " and adjacent");

            var space = _board.SpaceOrNull(me.Space);
            if (space != null && space.Kind == SpaceKind.LightSwitch)
            {
                CommandButton("Flip the Light Switch here", new ActivateLightSwitchCommand(), active);
            }
            foreach (var evidence in view.Evidence.Where(e => e.Space == me.Space))
            {
                CommandButton("Pick up " + _board.ZoneName(evidence.Zone) + " Evidence",
                    new PickUpEvidenceCommand(), active);
            }
            if (view.MedicalItemSpaces.Contains(me.Space))
            {
                CommandButton("Pick up the Medical Item", new PickUpMedicalItemCommand(), active);
            }
            foreach (var poi in view.PoiTokens.Where(p => p.TokenSpace == me.Space && !p.Collected))
            {
                CommandButton("Pick up the Point of Interest token", new PickUpPoiTokenCommand(), active);
            }
            foreach (string doorSpace in range.Where(s =>
                _board.SpaceOrNull(s)?.Kind == SpaceKind.Door))
            {
                var state = view.Overlay.DoorStates.TryGetValue(doorSpace, out var s)
                    ? s : DoorState.Open;
                string label = " (" + Describe.Door(state) + ")";
                CommandButton("Lock door " + doorSpace + label,
                    new LockDoorCommand { DoorSpace = doorSpace }, active);
                CommandButton("Open door " + doorSpace + label,
                    new OpenDoorCommand { DoorSpace = doorSpace }, active);
            }

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

            // Evidence turn-in is an Involved Action, so it lives here but ends the turn.
            if (me.EvidenceCarried.Count > 0)
            {
                ActionButton("TURN IN EVIDENCE…", () => ShowEvidenceTurnIn(me), active);
            }

            RenderObjectiveActions(view, me, active);
        }

        /// <summary>
        /// Every objective interact the protocol has, offered whenever the Investigator is
        /// active. Which are legal depends on the scenario, the Escape card, tokens carried and
        /// the space stood on — all of it engine-side — so these are deliberately unfiltered
        /// and the engine's refusal is the answer.
        /// </summary>
        private void RenderObjectiveActions(PlayerView view, PlayerView.InvestigatorPanel me,
            bool active)
        {
            Header("OBJECTIVE ACTIONS  (the engine rules on these)");
            CommandButton("Open the Lockbox", new OpenLockboxCommand { PushYourLuck = false }, active);
            CommandButton("Open the Lockbox — push your luck",
                new OpenLockboxCommand { PushYourLuck = true }, active);
            CommandButton("Power the Gate", new PowerTheGateCommand(), active);
            CommandButton("Escape through the Gate", new EscapeThroughGateCommand(), active);
            ActionButton("Start the Truck…", () => PickSpaces(1, "Click the Truck's escape space",
                picked => Send(new StartTruckCommand { EscapeSpace = picked[0] })), active);
            CommandButton("Escape at the Truck exit", new EscapeAtTruckExitCommand(), active);
            CommandButton("Fire the Flare Gun", new FireFlareGunCommand(), active);
            CommandButton("Escape by Helicopter", new EscapeByHelicopterCommand(), active);
            CommandButton("Open the Service Tunnel", new OpenServiceTunnelCommand(), active);
            CommandButton("Escape through the Tunnel", new EscapeThroughTunnelCommand(), active);
            CommandButton("Dig up the Grave", new DigUpGraveCommand(), active);
            ActionButton("Use the Hook…", () => PickSpaces(1, "Click the space the Hook targets",
                picked => Send(new UseTheHookCommand { ChosenSpace = picked[0] })), active);
            CommandButton("Use the Frayed Ropes", new UseFrayedRopesCommand(), active);
            CommandButton("Destroy the Egg Sac", new DestroyEggSacCommand(), active);
            CommandButton("Banish the Horror", new BanishTheHorrorCommand(), active);
            CommandButton("Use the Ritual Knife", new UseRitualKnifeCommand { FlipFaceDownWound = false },
                active);
            CommandButton("Use the Ritual Knife — flip a face-down Wound",
                new UseRitualKnifeCommand { FlipFaceDownWound = true }, active);
            CommandButton("Cut the Rope Circle", new CutRopeCircleCommand(), active);

            foreach (var token in view.Objective.Tokens)
            {
                var captured = token;
                CommandButton("Pick up " + captured.Key,
                    new PickUpObjectiveTokenCommand { TokenName = captured.Key }, active);
                CommandButton("Install " + captured.Key,
                    new InstallPartCommand { PartToken = captured.Key }, active);
                CommandButton("Pick up ride parts: " + captured.Key,
                    new PickUpRidePartsCommand { Token = captured.Key }, active);
                CommandButton("Pick up Banish token " + captured.Key,
                    new PickUpBanishTokenCommand { TokenName = captured.Key }, active);
            }
            foreach (var carried in view.Objective.TokenCarriers.Where(c => c.Value == me.DefId))
            {
                var captured = carried;
                CommandButton("Drop " + captured.Key,
                    new DropObjectiveTokenCommand { TokenName = captured.Key }, active);
                CommandButton("Install " + captured.Key,
                    new InstallPartCommand { PartToken = captured.Key }, active);
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

            // Mitchell's Minor ("Sweep") fires AFTER his own turn already ended — the engine
            // allows it off-turn (UseMinorAbility resolves invId "mitchell" directly rather
            // than the active Investigator) — so it needs its own always-visible button rather
            // than living behind the generic "Use MINOR ability" (which is greyed out except on
            // his own active turn). Offered whenever his Flashlight is still on the board; if
            // the view happens not to carry the "already Swept" round modifier for some reason,
            // this stays enabled per this file's own rule (see the class doc comment): an
            // over-eager button that explains itself beats a hidden one.
            if (me.DefId == "mitchell")
            {
                var placement = view.Flashlights.FirstOrDefault(f => f.InvestigatorId == "mitchell");
                if (placement != null)
                {
                    bool alreadySwept = view.RoundModifiers.ContainsKey(Game.SweepUsedPrefix + "mitchell");
                    ActionButton("Sweep flashlight", () => BeginSweepAim(placement.Space),
                        !alreadySwept,
                        alreadySwept
                            ? "Sweep may only be used once per Flashlight."
                            : "Aim the Flashlight's 2nd position.");
                }
            }

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

        private void ShowEvidenceTurnIn(PlayerView.InvestigatorPanel me)
        {
            // One token at a time keeps the picker honest: the command takes a list, but a
            // reward with an argument needs its own question.
            var options = new List<PromptOption>();
            foreach (string zone in me.EvidenceCarried)
            {
                string carried = zone;
                foreach (var reward in Describe.EvidenceRewards)
                {
                    var captured = reward;
                    options.Add(new PromptOption(
                        _board.ZoneName(carried) + " Evidence  →  " + captured.Label,
                        () => TurnIn(carried, captured)));
                }
            }
            _prompt.Show("turnin:" + me.EvidenceCarried.Count, "Turn in Evidence",
                "This is an Involved Action: it ends your turn. Standing on the scenario's " +
                "turn-in feature is required (Computer at the Sawmill, Ticket Booth at the park).",
                options, () => { });
        }

        private void TurnIn(string zone, (string Reward, string Label, Describe.RewardArg Arg) reward)
        {
            void Post(string arg) => Send(new TurnInEvidenceCommand
            {
                TurnIns = new List<EvidenceTurnIn>
                {
                    new EvidenceTurnIn { Zone = zone, Reward = reward.Reward, Arg = arg },
                },
            });

            switch (reward.Arg)
            {
                case Describe.RewardArg.PoiSpace:
                {
                    var options = View.PoiTokens
                        .Select(p => new PromptOption("Point of Interest at " + p.PoiSpace,
                            () => Post(p.PoiSpace)))
                        .ToList();
                    _prompt.Show("reward-poi", "Reveal which Point of Interest?", null, options,
                        () => { });
                    break;
                }
                case Describe.RewardArg.MirrorColor:
                {
                    var options = new[] { "red", "green", "blue" }
                        .Select(c => new PromptOption(c, () => Post(c))).ToList();
                    _prompt.Show("reward-mirror", "Which Mirror Maze color is open?", null, options,
                        () => { });
                    break;
                }
                case Describe.RewardArg.Investigator:
                {
                    var options = View.Investigators
                        .Select(i => new PromptOption(_describe.Investigator(i.DefId),
                            () => Post(i.DefId)))
                        .ToList();
                    _prompt.Show("reward-inv", "Give the Major Ability token to whom?", null, options,
                        () => { });
                    break;
                }
                default:
                    Post(null);
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

            if (view.PendingEventChoices.Count > 0 && !AmAdversary)
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
            else if (MyTurnActive)
            {
                var me = Me;
                if (me != null && !me.MovementLocked && me.MpRemaining > 0)
                {
                    var kind = me.Dead && me.SpiritId != null
                        ? FigureKind.Spirit
                        : FigureKind.Investigator;
                    foreach (var step in _board.StepsFrom(me.Space, kind, overlay))
                    {
                        if (step.Value.Cost <= me.MpRemaining)
                        {
                            costs[step.Key] = step.Value.Cost;
                        }
                    }
                }
            }
            _boardView.SetMoveTargets(costs);
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
            if (MyTurnActive)
            {
                Send(new MoveStepCommand { To = spaceId });
            }
        }

        /// <summary>Collect n board clicks, then hand them to <paramref name="done"/>.</summary>
        public void PickSpaces(int count, string prompt, Action<List<string>> done)
        {
            _prompt.Hide();
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
                angle => Send(new PlaceFlashlightCommand { AngleRadians = angle }),
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

        public void Header(string title)
        {
            var text = UiKit.CreateText(_actionsBody, title, 13, TextAnchor.MiddleLeft,
                UiKit.AccentColor);
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
