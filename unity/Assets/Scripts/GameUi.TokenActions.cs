using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Protocol;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The Investigator's actions: the menu that opens on their own figure (the turn's direct
    /// actions plus the Involved-Action interacts and Objective steps that apply where they
    /// stand), the side panel's trading / Items / Abilities rows, and the Evidence turn-in wizard.
    /// </summary>
    public sealed partial class GameUi
    {
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
            RenderAbilityRows(view, me, active, Row);

            Row("End Turn", () => Send(new EndTurnCommand()), active && !blockedByWindow,
                "Ends the turn. Rest (+1 Stamina if you did not Sprint) and Charge (+1 if you " +
                "did not place the Flashlight) happen on their own.");
        }

        /// <summary>
        /// Minor and Major Abilities, Spirit abilities, and a pending Painkillers draw —
        /// everything the retired ABILITIES panel section used to offer, now beside the
        /// figure with the other actions. The Abilities' own choices still go through the
        /// argument sheet; a Spirit's abilities are named there too.
        /// </summary>
        private void RenderAbilityRows(PlayerView view, PlayerView.InvestigatorPanel me, bool active,
            Action<string, Action, bool, string> row)
        {
            var def = _describe.InvestigatorOrNull(me.DefId);
            if (me.SpiritId == null && def != null)
            {
                // The data carries no ability names (only their text), so the rows say what
                // kind of ability they are and the tooltip carries the card text.
                string minor = string.IsNullOrEmpty(def.MinorAbility.Name) ? "Minor ability" : def.MinorAbility.Name;
                string major = string.IsNullOrEmpty(def.MajorAbility.Name) ? "Major ability" : def.MajorAbility.Name;
                row(minor, () =>
                    AskArgs(minor, def.MinorAbility.Text,
                        args => Send(new UseMinorAbilityCommand { Args = args })),
                    active, def.MinorAbility.Text);
                row(major + "  (" + me.MajorAbilityTokens + " token)", () =>
                    AskArgs(major, def.MajorAbility.Text,
                        args => Send(new UseMajorAbilityCommand { Args = args })),
                    active && me.MajorAbilityTokens > 0,
                    me.MajorAbilityTokens > 0 ? def.MajorAbility.Text : "No Major Ability token left.");
            }
            if (me.SpiritId != null)
            {
                row("Spirit ability…", () =>
                    AskArgs(_describe.Card(me.SpiritId),
                        "First argument is the ability name; the rest are its arguments.",
                        args => Send(new UseSpiritAbilityCommand
                        {
                            AbilityName = args != null && args.Count > 0 ? args[0] : "",
                            Args = args != null && args.Count > 1 ? args.Skip(1).ToList() : null,
                        })), active, _describe.CardText(me.SpiritId));
            }
            string painkillers = (me.Items ?? new List<string>())
                .FirstOrDefault(i => i.StartsWith("marker:painkillers:", StringComparison.Ordinal));
            if (painkillers != null)
            {
                row("Resolve Painkillers", () => ResolvePainkillers(me, painkillers), active,
                    "Two Wound cards were drawn: swap one for a face-up Wound, or keep none.");
            }
        }

        /// <summary>Painkillers drew two Wound cards: swap one for a face-up Wound, or keep
        /// none. The engine parks the draw as "marker:painkillers:&lt;a&gt;:&lt;b&gt;" in Items.</summary>
        private void ResolvePainkillers(PlayerView.InvestigatorPanel me, string marker)
        {
            var parts = marker.Split(':');
            var drawn = parts.Skip(2).Where(id => id.Length > 0).ToList();
            var wounds = me.Wounds.Where(w => w.FaceUp && w.CardId != null).ToList();
            var options = new List<PromptOption>();
            foreach (var wound in wounds)
            {
                foreach (string card in drawn)
                {
                    options.Add(new PromptOption(
                        "Swap " + _describe.Card(wound.CardId) + " for " + _describe.Card(card),
                        () => Send(new ResolvePainkillersCommand
                        {
                            ExistingWoundCardId = wound.CardId,
                            ChosenDrawnCardId = card,
                        }), _describe.CardText(card)));
                }
            }
            options.Add(new PromptOption("Keep my Wounds as they are", () =>
                Send(new ResolvePainkillersCommand())));
            _prompt.Show("painkillers:" + marker, "Painkillers",
                wounds.Count == 0
                    ? "No face-up Wound to swap — the drawn cards are discarded."
                    : "Replace one face-up Wound with a drawn card, or decline.",
                options, () => { });
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

        // ----------------------------------------------------- side panel rows

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
    }
}
