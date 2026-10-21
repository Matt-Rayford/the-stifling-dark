using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Protocol;
using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Board-side input: the hovered-move path preview and the paced auto-walk a click on it
    /// starts, opening the figure menu by click or dwell, collecting space picks for a command,
    /// and beam aiming for Flashlight placement and Mitchell's Sweep.
    /// </summary>
    public sealed partial class GameUi
    {
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

        /// <summary>Mitchell just confirmed his own placement: open the Sweep aim as soon as
        /// the update carrying the new Flashlight arrives.</summary>
        private bool _offerSweepWhenPlaced;

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

        // ------------------------------------------ walking the previewed path

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

        // ----------------------------------------- space picks and beam aiming

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
    }
}
