using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Core
{
    public sealed class GameSetup
    {
        public string ScenarioId { get; set; } = "";
        public ulong Seed { get; set; }
        public string AdversaryId { get; set; } = "";
        /// <summary>Investigator def id -> chosen Start space.</summary>
        public Dictionary<string, string> InvestigatorStartSpaces { get; set; } = new Dictionary<string, string>();
        /// <summary>Where the Medical Item tokens begin (count validated against config).</summary>
        public List<string> MedicalItemSpaces { get; set; } = new List<string>();
        public bool UseMiniExpansionCards { get; set; }
        /// <summary>
        /// Who holds the short-handed team's starting Items (see GrantStartingItems).
        /// Null: the first Investigator in setup order.
        /// </summary>
        public string? StartingItemsInvestigatorId { get; set; }
    }

    /// <summary>
    /// The rules state machine. Wraps one GameState; every public method is a player
    /// action that validates, mutates, and logs. Illegal actions throw
    /// InvalidOperationException and leave the state untouched.
    /// </summary>
    public sealed partial class Game
    {
        public GameDatabase Db { get; }
        public GameState State { get; }
        public MapGraph Graph { get; }

        private readonly FlashlightBeam _beam;
        private ILineOfSightBlocker _losBlocker = NoLineOfSightBlocker.None;
        private readonly DeterministicRng _rng;

        /// <summary>Swap in the obstacle-mask blocker once wall geometry is extracted.</summary>
        public void SetLineOfSightBlocker(ILineOfSightBlocker blocker) => _losBlocker = blocker;

        private Game(GameDatabase db, GameState state)
        {
            Db = db;
            State = state;
            Graph = new MapGraph(db.Map(state.ScenarioId));
            _beam = new FlashlightBeam(db.Flashlight);
            _rng = new DeterministicRng(state.RngState);
            var mask = db.LosMask(state.ScenarioId);
            if (mask != null)
            {
                _losBlocker = mask;
            }
        }

        /// <summary>Resume from a saved state.</summary>
        public static Game FromState(GameDatabase db, GameState state) => new Game(db, state);

        public static Game NewGame(GameDatabase db, GameSetup setup)
        {
            var map = db.Map(setup.ScenarioId);
            var graph = new MapGraph(map);
            int count = setup.InvestigatorStartSpaces.Count;
            if (!db.Config.ByInvestigatorCount.TryGetValue(count, out var countRules))
            {
                throw new InvalidOperationException($"Unsupported investigator count {count}.");
            }
            if (setup.MedicalItemSpaces.Count != countRules.MedicalItemsOnBoard)
            {
                throw new InvalidOperationException(
                    $"{count} investigators start with {countRules.MedicalItemsOnBoard} Medical Item token(s), got {setup.MedicalItemSpaces.Count}.");
            }
            var startSpaces = map.Spaces.Where(s => s.Kind == SpaceKind.Start).Select(s => s.Id).ToHashSet();
            foreach (var (invId, space) in setup.InvestigatorStartSpaces.Select(kv => (kv.Key, kv.Value)))
            {
                var def = db.Investigator(invId);
                if (def.Set != "base")
                {
                    throw new InvalidOperationException($"'{invId}' is not part of the v1 roster.");
                }
                if (!startSpaces.Contains(space))
                {
                    throw new InvalidOperationException($"'{space}' is not a Start space.");
                }
            }
            foreach (string space in setup.MedicalItemSpaces)
            {
                if (graph.Space(space).Kind != SpaceKind.MedicalItem)
                {
                    throw new InvalidOperationException($"'{space}' is not a Medical Item space.");
                }
            }
            if (setup.MedicalItemSpaces.Distinct().Count() != setup.MedicalItemSpaces.Count)
            {
                throw new InvalidOperationException("Medical Item tokens must go on different spaces.");
            }

            var state = new GameState
            {
                ScenarioId = setup.ScenarioId,
                RngState = setup.Seed,
                Phase = GamePhase.AdversarySetup,
                Round = 0,
                MedicalItemSpaces = setup.MedicalItemSpaces.ToList(),
            };
            foreach (var (invId, space) in setup.InvestigatorStartSpaces.Select(kv => (kv.Key, kv.Value)))
            {
                var def = db.Investigator(invId);
                state.Investigators.Add(new InvestigatorState
                {
                    DefId = invId,
                    Space = space,
                    Stamina = def.StaminaTrack.Start,
                    Charge = def.ChargeTrack.Start,
                });
            }
            state.Adversary = new AdversaryState
            {
                DefId = setup.AdversaryId,
                KillsToWin = setup.AdversaryId switch
                {
                    "butcher" => 1,
                    "insatiable-horror" => 2,
                    "cult-of-hunlow" => count,
                    _ => throw new InvalidOperationException($"Unknown adversary '{setup.AdversaryId}'."),
                },
            };

            var game = new Game(db, state);
            game.BuildDecks(setup.UseMiniExpansionCards);
            game.Log("setup", $"{count} investigators vs {setup.AdversaryId} at {setup.ScenarioId}");
            game.GrantStartingItems(setup);
            return game;
        }

        /// <summary>
        /// Short-handed compensation (designer ruling 2026-08): a 3-Investigator team starts
        /// with 2 General Items, a 2-Investigator team with 4, a full team with none. All of
        /// them go to ONE Investigator — with bots at the table that is the human's seat,
        /// wired through <see cref="GameSetup.StartingItemsInvestigatorId"/>. Distribution
        /// may become a team choice later.
        /// </summary>
        private void GrantStartingItems(GameSetup setup)
        {
            int count = State.Investigators.Count switch { 2 => 4, 3 => 2, _ => 0 };
            if (count == 0)
            {
                return;
            }
            var holder = setup.StartingItemsInvestigatorId != null
                ? Investigator(setup.StartingItemsInvestigatorId)
                : State.Investigators[0];
            for (int i = 0; i < count; i++)
            {
                holder.Items.Add(Draw(State.GeneralItemDeck, "general item"));
            }
            Log("setup", $"{holder.DefId} starts with {count} Items (short-handed team)");
        }

        private void BuildDecks(bool useMiniExpansion)
        {
            List<string> Shuffled(IEnumerable<CardDef> cards)
            {
                var ids = cards.SelectMany(c => Enumerable.Repeat(c.Id, c.Count)).ToList();
                _rng.Shuffle(ids);
                return ids;
            }

            var general = Db.Deck("general-item").Where(c => c.Set == "base").ToList();
            if (useMiniExpansion)
            {
                var replaced = Db.Deck("general-item").Where(c => c.Set == "MI").Select(c => c.Replaces!).ToHashSet();
                general = general.Where(c => !replaced.Contains(c.Id))
                    .Concat(Db.Deck("general-item").Where(c => c.Set == "MI")).ToList();
            }
            State.GeneralItemDeck = Shuffled(general);
            State.CursedItemDeck = Shuffled(Db.Deck("cursed-item"));
            State.WoundDeck = Shuffled(Db.Deck("wound"));

            var events = Db.Deck("event").Where(c => c.Owner == State.ScenarioId).ToList();
            var majors = events.Where(c => c.Severity == "major")
                .SelectMany(c => Enumerable.Repeat(c.Id, c.Count)).ToList();
            var deck = new List<string> { majors[_rng.Next(majors.Count)] };
            var moderates = Shuffled(events.Where(c => c.Severity == "moderate"));
            var minors = Shuffled(events.Where(c => c.Severity == "minor"));
            deck.InsertRange(0, moderates);
            deck.InsertRange(0, minors);
            State.EventDeck = deck; // index 0 = top
            SaveRng();
        }

        // ---------- Adversary setup ----------

        public void PlaceHiddenEvidence(string zone, string spaceId)
        {
            RequirePhase(GamePhase.AdversarySetup);
            var space = Graph.Space(spaceId);
            if (space.Zone != zone || space.Kind != SpaceKind.Normal)
            {
                throw new InvalidOperationException($"Evidence for zone {zone} must go on a General space in that zone.");
            }
            State.Evidence[zone] = new HiddenTokenState { Space = spaceId };
        }

        public void PlacePoiToken(string poiSpace, string tokenSpace, bool cursedFront)
        {
            RequirePhase(GamePhase.AdversarySetup);
            if (Graph.Space(poiSpace).Kind != SpaceKind.PointOfInterest)
            {
                throw new InvalidOperationException($"'{poiSpace}' is not a Point of Interest.");
            }
            var target = Graph.Space(tokenSpace);
            var distances = Graph.DistancesFrom(poiSpace, 2, State.Overlay);
            if (target.Kind != SpaceKind.Normal || !distances.ContainsKey(tokenSpace))
            {
                throw new InvalidOperationException("POI tokens go on a General space within 2 spaces of their POI.");
            }
            State.PoiTokens.RemoveAll(p => p.PoiSpace == poiSpace);
            State.PoiTokens.Add(new PoiTokenState { PoiSpace = poiSpace, TokenSpace = tokenSpace, CursedFront = cursedFront });
        }

        public void PlaceAdversary(string spaceId)
        {
            RequirePhase(GamePhase.AdversarySetup);
            Graph.Space(spaceId);
            State.Adversary.Space = spaceId;
        }

        public void FinishAdversarySetup()
        {
            RequirePhase(GamePhase.AdversarySetup);
            var zones = Graph.Def.Zones.Keys.ToHashSet();
            if (!zones.SetEquals(State.Evidence.Keys))
            {
                throw new InvalidOperationException("Every zone needs exactly 1 hidden Evidence token.");
            }
            var poiSpaces = Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest).Select(s => s.Id).ToHashSet();
            if (State.PoiTokens.Count != poiSpaces.Count)
            {
                throw new InvalidOperationException("Every Point of Interest needs a token.");
            }
            if (State.PoiTokens.Count(p => p.CursedFront) != 1)
            {
                throw new InvalidOperationException("Exactly one POI token has the Cursed Item front.");
            }
            if (State.Adversary.Space.Length == 0)
            {
                throw new InvalidOperationException("The Adversary standee has not been placed.");
            }
            BeginRound(1);
        }

        // ---------- Round structure ----------

        private void BeginRound(int round)
        {
            if (State.Phase == GamePhase.GameOver)
            {
                // A hook that ran while ending the previous round already decided the game;
                // a decided game never opens a new round.
                return;
            }
            State.Round = round;
            State.Phase = GamePhase.InvestigatorTurns;
            State.ActiveInvestigator = null;
            foreach (var inv in State.Investigators)
            {
                inv.TurnTakenThisRound = false;
                inv.CarriageRotationUsedThisRound = false;
            }
            State.Adversary.CarriageRotationUsedThisRound = false;
            // Round modifiers expire here, before the new Event card gets a chance to set its own.
            State.RoundModifiers.Clear();
            if (State.EventDeck.Count > 0)
            {
                State.CurrentEvent = State.EventDeck[0];
                State.EventDeck.RemoveAt(0);
                Log("event", State.CurrentEvent!);
                EventsOnDrawn();
            }
            // Last: cards armed on an earlier round add their own round-long modifiers on top
            // of whatever this round's Event just wrote (the Butcher's Decay stacking with Foggy).
            OnRoundStart();
        }

        public void BeginInvestigatorTurn(string invId)
        {
            RequirePhase(GamePhase.InvestigatorTurns);
            if (State.ActiveInvestigator != null)
            {
                throw new InvalidOperationException($"{State.ActiveInvestigator} has not finished their turn.");
            }
            var inv = Investigator(invId);
            // A dead Investigator whose player took a Spirit card keeps taking turns (4 MP plus
            // a free Sprint die); anyone else who is Dead or Escaped is off the board.
            if (inv.TurnTakenThisRound || inv.Escaped || (inv.Dead && !IsSpirit(inv)))
            {
                throw new InvalidOperationException($"{invId} cannot take a turn.");
            }
            State.ActiveInvestigator = invId;
            inv.MpRemaining = IsSpirit(inv) ? SpiritMp : Db.Investigator(invId).Mp;
            inv.SprintedOrRested = false;
            inv.Rested = false;
            inv.FinalAction = FinalActionKind.None;
            inv.MovementLocked = false;
            inv.WaterFloatUsedThisTurn = false;
            inv.SpiritAbilitiesUsedThisTurn = 0;
            ApplyCarriageRotation(inv);
            NoteTurnStartResources(inv);
            OnInvestigatorTurnStart(inv);
        }

        // ---------- Per-action card gating ----------

        /// <summary>
        /// Refuse an action a card currently forbids. <paramref name="actionKey"/> is one of
        /// the Action* consts in Game.Effects.cs; every blocking clause any held card
        /// contributes is collected first, so the thrown message names all of them at once and
        /// no partial state change has happened yet.
        /// </summary>
        private void RequireActionAllowed(InvestigatorState inv, string actionKey)
        {
            var blockers = new List<string>();
            CollectActionBlockers(inv, actionKey, blockers);
            if (blockers.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{inv.DefId} may not take the '{actionKey}' action: {string.Join("; ", blockers)}");
            }
        }

        /// <summary>The blocking clauses that would stop <paramref name="invId"/> taking
        /// <paramref name="actionKey"/> right now — for UIs that want to grey the button out
        /// instead of catching the exception.</summary>
        public List<string> ActionBlockers(string invId, string actionKey)
        {
            var blockers = new List<string>();
            CollectActionBlockers(Investigator(invId), actionKey, blockers);
            return blockers;
        }

        // ---------- Investigator actions ----------

        public void Sprint()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireActionAllowed(inv, ActionSprint);
            if (inv.SprintedOrRested)
            {
                throw new InvalidOperationException("Sprint or Rest may be used once per turn.");
            }
            if (IsSpirit(inv))
            {
                // "Spirits can Sprint every round" and pay no Stamina for it.
                SprintAsSpirit(inv);
                return;
            }
            // Squall / Severe Heat make every Sprint cost extra Stamina. The whole cost is
            // spent as one Sprint-origin loss, so Punctured Lung and Cold Front see it too.
            int staminaCost = 1 + Math.Max(0, EventRoundModifier(SprintStaminaSurchargeKey));
            SpendStamina(inv, staminaCost, WoundFromSprint);
            int rolled = _rng.RollSprintDie(Db.Config.SprintDieFaces);
            SaveRng();
            var rollBox = new List<int> { rolled };
            ModifySprintRoll(inv, rollBox);
            int finalRoll = Math.Max(1, rollBox[0]);
            inv.SprintedOrRested = true;
            inv.MpRemaining += finalRoll;
            Log("sprint", finalRoll == rolled
                ? $"{inv.DefId} rolled {finalRoll} MP"
                : $"{inv.DefId} rolled {rolled} MP, adjusted to {finalRoll}");
            // Pyrocumulus: "an Investigator who Sprints must roll a D6 and take a face-down
            // Wound on a 4+".
            int threshold = EventRoundModifier(SprintD6WoundThresholdKey);
            if (threshold > 0)
            {
                int d6 = _rng.Roll(6);
                SaveRng();
                Log("event", $"pyrocumulus: {inv.DefId} rolled {d6} for Sprinting");
                if (d6 >= threshold)
                {
                    GainWound(inv, faceUp: false, origin: WoundFromSprint);
                }
            }
        }

        public void MoveStep(string to)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireActionAllowed(inv, ActionMove);
            if (inv.MovementLocked)
            {
                throw new InvalidOperationException("Movement is over for this turn.");
            }
            string from = inv.Space;
            var step = Graph.TryStep(FigureKindOf(inv), inv.Space, to, State.Overlay)
                ?? throw new InvalidOperationException($"Cannot move {inv.Space} -> {to}.");
            // MapGraph charges by printed light level only; an Ability may still discount this
            // one step (Dylan's "treat up to 3 Dark spaces as Dim"). The hook is asked before
            // the MP check and is required to be side-effect free, so a step nobody can afford
            // costs no allowance — see AdjustMoveCost in Game.Effects.cs.
            var costBox = new List<int> { step.Cost };
            AdjustMoveCost(inv, inv.Space, to, costBox);
            int cost = Math.Max(1, costBox[0]);
            if (cost > inv.MpRemaining)
            {
                throw new InvalidOperationException($"Move costs {cost} MP, only {inv.MpRemaining} left.");
            }
            inv.MpRemaining -= cost;
            inv.Space = to;
            if (step.CrossesWindow)
            {
                State.PendingWindowChoice = true;
            }
            ApplyCarriageRotation(inv);
            ApplyWaterFloat(inv);
            OnInvestigatorMoveStep(inv, from, to);
        }

        /// <summary>Resolve a Window crossing: keep moving (face-down Wound) or stop for the turn (lose 1 Stamina if able).</summary>
        public void ResolveWindow(bool stopAndLoseStamina)
        {
            var inv = ActiveInv();
            if (!State.PendingWindowChoice)
            {
                throw new InvalidOperationException("No window crossing to resolve.");
            }
            State.PendingWindowChoice = false;
            if (stopAndLoseStamina && inv.Stamina > 0)
            {
                LoseStamina(inv, 1);
                inv.MovementLocked = true;
            }
            else
            {
                GainWound(inv, faceUp: false, origin: WoundFromWindow);
            }
        }

        public void PickUpEvidence()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            string? zone = State.Evidence
                .Where(kv => kv.Value.Space == inv.Space && kv.Value.Revealed)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (zone == null)
            {
                throw new InvalidOperationException("No revealed Evidence token here.");
            }
            inv.EvidenceCarried.Add(zone);
            State.Evidence.Remove(zone);
            Log("evidence", $"{inv.DefId} picked up {zone} evidence");
        }

        public void ActivateLightSwitch()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            var space = Graph.Space(inv.Space);
            if (space.Kind != SpaceKind.LightSwitch)
            {
                throw new InvalidOperationException("Not on a Light Switch space.");
            }
            string zone = space.Zone ?? throw new InvalidOperationException("Light Switch has no zone.");
            if (State.FalteringZones.Contains(zone) || State.Overlay.BrightZones.Contains(zone))
            {
                throw new InvalidOperationException($"Zone {zone} lights cannot be turned on.");
            }
            State.Overlay.BrightZones.Add(zone);
            Log("lights", $"zone {zone} is Bright");
            RevealOnBright(Graph.ZoneSpaces(zone).Select(s => s.Id));
        }

        public void LockDoor(string doorSpace)
        {
            InteractWithDoor(doorSpace, locking: true);
        }

        public void OpenDoor(string doorSpace)
        {
            InteractWithDoor(doorSpace, locking: false);
        }

        private void InteractWithDoor(string doorSpace, bool locking)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireActionAllowed(inv, locking ? ActionLockDoor : ActionOpenDoor);
            if (!Graph.DistancesFrom(inv.Space, 1, State.Overlay).ContainsKey(doorSpace) &&
                Graph.Edge(inv.Space, doorSpace) == null)
            {
                throw new InvalidOperationException("Door is not adjacent.");
            }
            if (Graph.Space(doorSpace).Kind != SpaceKind.Door)
            {
                throw new InvalidOperationException($"'{doorSpace}' is not a Door space.");
            }
            var current = State.Overlay.DoorState(doorSpace);
            if (locking)
            {
                if (current != DoorState.Open)
                {
                    throw new InvalidOperationException("Only an empty Open Door can be Locked.");
                }
                if (State.Adversary.Space == doorSpace)
                {
                    // Locking the Adversary's space reveals them and forces them to step aside.
                    // The forced adjacent move is an Adversary choice — surfaced via reveal;
                    // full resolution comes with adversary decision plumbing.
                    RevealAdversary("locked door on their space");
                }
                State.Overlay.DoorStates[doorSpace] = DoorState.Locked;
            }
            else
            {
                switch (current)
                {
                    case DoorState.Locked:
                        State.Overlay.DoorStates.Remove(doorSpace);
                        break;
                    case DoorState.Damaged:
                        State.Overlay.DoorStates[doorSpace] = DoorState.Destroyed;
                        break;
                    default:
                        throw new InvalidOperationException("Nothing to Open here.");
                }
            }
        }

        public void PickUpMedicalItem()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireNotSpirit(inv, "pick up Medical Items");
            if (!State.MedicalItemSpaces.Remove(inv.Space))
            {
                throw new InvalidOperationException("No Medical Item token here.");
            }
            inv.Items.Add(DrawMedicalItem());
        }

        public void PickUpPoiToken()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireActionAllowed(inv, ActionPickUpPoi);
            var token = State.PoiTokens.FirstOrDefault(p => p.TokenSpace == inv.Space && p.Revealed && !p.Collected)
                ?? throw new InvalidOperationException("No revealed POI token here.");
            token.Collected = true;
            if (token.CursedFront)
            {
                inv.Items.Add(Draw(State.CursedItemDeck, "cursed item"));
            }
            else
            {
                inv.Items.Add(Draw(State.GeneralItemDeck, "general item"));
                inv.Items.Add(Draw(State.GeneralItemDeck, "general item"));
            }
        }

        public void TradeItem(string toInvId, string itemCardId)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            var target = Investigator(toInvId);
            // Mistrust reads "you may not Trade or be Traded with", so both sides are gated.
            RequireActionAllowed(inv, ActionTrade);
            RequireActionAllowed(target, ActionTrade);
            RequireCanReceiveTrade(target);
            RequireAdjacentForTrade(inv, target);
            if (!inv.Items.Remove(itemCardId))
            {
                throw new InvalidOperationException($"{inv.DefId} does not carry '{itemCardId}'.");
            }
            target.Items.Add(itemCardId);
        }

        public void TradeEvidence(string toInvId, string zone)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            var target = Investigator(toInvId);
            RequireActionAllowed(inv, ActionTrade);
            RequireActionAllowed(target, ActionTrade);
            RequireCanReceiveTrade(target);
            RequireAdjacentForTrade(inv, target);
            if (!inv.EvidenceCarried.Remove(zone))
            {
                throw new InvalidOperationException($"{inv.DefId} does not carry {zone} evidence.");
            }
            target.EvidenceCarried.Add(zone);
        }

        private void RequireAdjacentForTrade(InvestigatorState a, InvestigatorState b)
        {
            // Ibraheem's Major Ability stretches his Trade reach to 5 spaces for the round; it
            // applies whichever side of the Trade he is on (Game.InvestigatorAbilities.cs).
            int range = ExtendedTradeRange(a, b);
            if (range > 1 && Graph.DistancesFrom(a.Space, range, State.Overlay).ContainsKey(b.Space))
            {
                return;
            }
            var edge = Graph.Edge(a.Space, b.Space);
            bool ok = edge != null && edge.Type != EdgeType.AdversaryLink &&
                      !State.Overlay.FalseWindows.Contains(BoardOverlay.EdgeKey(a.Space, b.Space)) &&
                      (edge.Type != EdgeType.MirrorDoor || edge.Color == State.Overlay.OpenMirrorColor);
            if (!ok)
            {
                throw new InvalidOperationException("Trading requires adjacency (windows OK, closed mirror doors and carriage links are not).");
            }
        }

        // ---------- Final actions & end of turn ----------

        public void PlaceFlashlight(double angleRadians)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireActionAllowed(inv, ActionPlaceFlashlight);
            RequireNoFinalAction(inv);
            PayFlashlightCharge(inv);
            inv.FinalAction = FinalActionKind.PlaceFlashlight;
            var bright = PreviewFlashlight(inv.DefId, angleRadians);
            // Cards that shrink the beam (Misty's range, Hazy/Downpour/Tunnel Vision's center
            // lines) trim it here, before anything is lit or Revealed, so a space outside the
            // reduced beam never reveals what stands on it.
            TrimFlashlightBright(inv, angleRadians, bright);
            State.Flashlights.Add(new FlashlightPlacement
            {
                InvestigatorId = inv.DefId,
                Space = inv.Space,
                AngleRadians = angleRadians,
                BrightSpaces = bright.OrderBy(s => s).ToList(),
            });
            State.Overlay.BrightSpaces.UnionWith(bright);
            Log("flashlight", $"{inv.DefId} lit {bright.Count} spaces");
            RevealOnBright(bright);
            OnFlashlightPlaced(inv);
            // Designer ruling: Mitchell's Sweep is the second half of placing, so HIS turn
            // stays open — the table (bots included) waits until he Sweeps and/or explicitly
            // ends the turn. Movement is still over: the placement was the Final Action.
            if (inv.DefId == "mitchell")
            {
                inv.MovementLocked = true;
                Log("turn", $"{inv.DefId} may Sweep the Flashlight to a 2nd position, or end their turn");
                return;
            }
            EndTurn(inv);
        }

        /// <summary>
        /// Validate and spend a Flashlight placement's Charge up front: the printed 1 plus any
        /// surcharge in force this round (Foggy, the Butcher's Decay), less 1 if a card is
        /// paying for it (Spare Batteries' Supply token). Refusing the placement before
        /// anything is lit is the whole point of doing it here rather than in a card hook.
        /// </summary>
        private void PayFlashlightCharge(InvestigatorState inv)
        {
            string waiverKey = FlashlightChargeWaiverPrefix + inv.DefId;
            bool waived = HasRoundModifier(waiverKey);
            int cost = 1 + Math.Max(0, EventRoundModifier(FlashlightChargeSurchargeKey)) - (waived ? 1 : 0);
            // Validate before spending anything, the waiver included: a refused placement must
            // leave the Investigator exactly as they were.
            if (inv.Charge < cost)
            {
                throw new InvalidOperationException(
                    $"Placing the Flashlight costs {cost} Charge, {inv.DefId} has {inv.Charge}.");
            }
            if (waived)
            {
                ClearRoundModifier(waiverKey);
                Log("flashlight", $"{inv.DefId} pays 1 less Charge for this placement");
            }
            inv.Charge -= cost;
        }

        /// <summary>The Bright set a flashlight would produce — call freely for the mouse preview.</summary>
        /// <summary><paramref name="sightLineLimit"/> restricts LOS to the template's first
        /// N printed lines (ordered centre vertical, side verticals, then the angled fans) —
        /// how Hazy and the tunnel-vision Wounds narrow the beam.</summary>
        public HashSet<string> PreviewFlashlight(string invId, double angleRadians,
            int? sightLineLimit = null) =>
            _beam.ComputeBright(Graph, Investigator(invId).Space, angleRadians, _losBlocker,
                sightLineLimit);

        /// <summary>Generic Involved Action final: ends the turn with no Stamina gain. Specific
        /// Involved Actions (evidence turn-in, objectives) build on this as they are implemented.</summary>
        public void TakeInvolvedAction()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireActionAllowed(inv, ActionInvolved);
            RequireNoFinalAction(inv);
            inv.FinalAction = FinalActionKind.InvolvedAction;
            EndTurn(inv);
        }

        public void EndTurnWithoutFinalAction()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            // The Fear Wound's "you must use your Major Ability on your next turn" — the Final
            // Actions are refused by the per-action gate, and this is the remaining way out of
            // a turn (Game.InvestigatorAbilities.cs).
            RequireForcedAbilityUsed(inv);
            EndTurn(inv);
        }

        private void EndTurn(InvestigatorState inv)
        {
            // Spare Tools: the Involved Action just resolved counts as an Interact Action
            // instead, so the turn does not end and a different Final Action is still open.
            // Every Involved Action funnels through here, so one check covers them all.
            if (inv.FinalAction == FinalActionKind.InvolvedAction &&
                ClearRoundModifier(InvolvedAsInteractPrefix + inv.DefId))
            {
                inv.FinalAction = FinalActionKind.None;
                SetRoundModifier(InvolvedActionUsedPrefix + inv.DefId, 1);
                Log("item", $"{inv.DefId}'s Spare Tools turned that Involved Action into an Interact; their turn continues");
                return;
            }
            // Moving through other Investigators is fine; ending the turn stacked is not. A
            // Spirit is not an Investigator and has no player board, so the rule cuts neither
            // way for it: living Investigators already ignore Spirit-occupied spaces (the
            // o.Dead filter), and a Spirit may end its turn wherever it likes.
            if (!IsSpirit(inv) &&
                State.Investigators.Any(o => o != inv && !o.Dead && !o.Escaped && o.Space == inv.Space))
            {
                throw new InvalidOperationException("Cannot end the turn on another Investigator's space.");
            }
            // Designer ruling: Rest and Charge are not chosen actions. Any turn that did not
            // Sprint ends by Resting, and any turn that did not Place the Flashlight ends by
            // Charging — unless an Involved Action consumed the turn's effort. Rest is decided
            // here but its Stamina is granted after the turn-end hooks (below), so Panic and
            // the no-Stamina weather events still veto it; the Charge point lands now, before
            // the hooks. Designer-confirmed corollary: on a quiet turn Breathless's and Dying
            // Battery's end-of-turn drains net out against the automatic recovery ("they
            // should stay the same") — those Wounds only bite on turns whose Sprint,
            // Flashlight, or Involved Action already forfeited the matching recovery.
            if (!IsSpirit(inv) && !inv.Dead && !inv.Escaped &&
                inv.FinalAction != FinalActionKind.InvolvedAction)
            {
                if (!inv.SprintedOrRested)
                {
                    inv.SprintedOrRested = true;
                    inv.Rested = true;
                }
                if (inv.FinalAction != FinalActionKind.PlaceFlashlight)
                {
                    AutoCharge(inv);
                }
            }
            OnInvestigatorTurnEnd(inv);
            if (State.Phase == GamePhase.GameOver)
            {
                // A Wound or Condition resolved above (e.g. Bleeding) just decided the game;
                // nothing below may touch TurnTakenThisRound or hand the phase to the Adversary.
                return;
            }
            if (inv.Rested && inv.FinalAction != FinalActionKind.InvolvedAction)
            {
                // Heavy Winds / Heavy Smoke / Tornado: no Stamina may be gained as part of a
                // Final Action this round, Rest included.
                if (HasEventRoundModifier(NoRestStaminaKey))
                {
                    Log("event", $"{State.CurrentEvent}: {inv.DefId} gains no Stamina from Resting this round");
                }
                else
                {
                    GainStamina(inv, 1);
                }
            }
            NoteTurnResourceGains(inv);
            inv.TurnTakenThisRound = true;
            State.ActiveInvestigator = null;
            // Spirits still take a turn every round, so a dead Investigator only stops holding
            // the phase open once their player has declined (or lost) a Spirit card.
            if (State.Investigators.All(i => i.TurnTakenThisRound || i.Escaped || (i.Dead && !IsSpirit(i))))
            {
                State.Phase = GamePhase.AdversaryTurn;
                State.Adversary.NoiseTokens.Clear();
            }
        }

        /// <summary>
        /// The automatic end-of-turn Charge. Everything that used to gate or tax the Charge
        /// Final Action still applies — Drain's ban, Interference's round ban, Gear Jam's
        /// Stamina toll (paid through the same OnChargeDeclared hook), Gear Jam at 0 Stamina —
        /// it now vetoes or taxes the automatic gain instead of a button.
        /// </summary>
        private void AutoCharge(InvestigatorState inv)
        {
            var blockers = ActionBlockers(inv.DefId, ActionCharge);
            if (blockers.Count > 0)
            {
                // Only worth a log line when it actually cost them something.
                if (inv.Charge < Db.Config.ChargeMax)
                {
                    Log("turn", $"{inv.DefId} does not Charge: {blockers[0]}");
                }
                return;
            }
            OnChargeDeclared(inv);
            inv.Charge = Math.Min(Db.Config.ChargeMax, inv.Charge + 1);
        }

        // ---------- Adversary turn (core actions; per-adversary specials layer on later) ----------

        public void AdversaryMoveStep(string to)
        {
            EnsureAdversaryTurnStarted();
            string from = State.Adversary.Space;
            var step = Graph.TryStep(FigureKind.Adversary, from, to, State.Overlay)
                ?? throw new InvalidOperationException($"Adversary cannot move {from} -> {to}.");
            // The Enraged Horror and the Corporeal Mor'gonnod both pay 2 MP to enter a Bright
            // space; every other figure pays the printed 1 everywhere. MapGraph knows nothing
            // about light level, so the extra MP is added on here rather than in TryStep.
            int cost = step.Cost + (AdversaryPaysDoubleForBright() && IsBright(to) ? 1 : 0);
            if (cost > State.Adversary.MpRemaining)
            {
                throw new InvalidOperationException($"Move costs {cost} MP, only {State.Adversary.MpRemaining} left.");
            }
            State.Adversary.MpRemaining -= cost;
            State.Adversary.ActionsUsed.Add("move"); // Moving forecloses start-of-turn-only actions (e.g. Ambush).
            if (IsBright(to))
            {
                // Devour: "as long as you do not Move onto any Bright spaces before doing so."
                State.Adversary.ActionsUsed.Add("moved-onto-bright");
            }
            State.Adversary.Space = to;
            if (step.CrossesWindow)
            {
                string key = BoardOverlay.EdgeKey(from, to);
                if (!State.Adversary.NoiseTokens.Contains(key))
                {
                    State.Adversary.NoiseTokens.Add(key);
                }
            }
            if (!State.Adversary.Revealed && IsBright(to))
            {
                if (State.Adversary.DefId == "insatiable-horror")
                {
                    // The Horror cannot be Revealed while Moving; it drops a Shadow token
                    // on each Bright space it moves through instead.
                    State.Adversary.ShadowTokens[to] = to;
                }
                else
                {
                    RevealAdversary("moved onto a Bright space");
                }
            }
            ApplyAdversaryCarriageRotation();
            // Only Investigator-hidden while the figure itself is still Hidden after this step:
            // once Revealed, the physical token is standing in plain sight, so naming the space
            // it moved to/from is no different from what is already on the table.
            Log(State.Adversary.Revealed ? "adversary" : AdversaryHiddenPositionLogType,
                $"moved {from} -> {to} ({cost} MP, {State.Adversary.MpRemaining} left)");
            OnAdversaryMoveStep(from, to);
        }

        /// <summary>
        /// True while the main Adversary figure spends 2 MP to enter a Bright space instead of
        /// 1: the Enraged Horror's replacement movement rules, and the Corporeal Mor'gonnod's.
        /// Both also get a flat MP budget with no Sprint die (see EnsureAdversaryTurnStarted).
        /// </summary>
        private bool AdversaryPaysDoubleForBright() =>
            AdversaryCounter("enraged") == 1 || AdversaryCounter("corporeal") == 1;

        private int AdversaryCounter(string key) =>
            State.Adversary.Counters.TryGetValue(key, out int value) ? value : 0;

        public void AdversaryDisappear()
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            if (AdversaryCounter("enraged") == 1)
            {
                // The Enraged Horror is on the main board for good.
                throw new InvalidOperationException("The Horror is Enraged and can no longer Disappear.");
            }
            if (AdversaryCounter("corporeal") == 1)
            {
                throw new InvalidOperationException("Corporeal Mor'gonnod can no longer Disappear.");
            }
            if (adv.ActionsUsed.Contains("disappear"))
            {
                throw new InvalidOperationException("Disappear was already used this turn.");
            }
            if (!adv.Revealed)
            {
                throw new InvalidOperationException("Already Hidden.");
            }
            if (Graph.EffectiveLight(adv.Space, State.Overlay) == LightLevel.Bright && adv.DefId != "insatiable-horror")
            {
                throw new InvalidOperationException("Disappear requires a Dim or Dark space.");
            }
            // Every check above must pass before "disappear" is marked used: a refused call
            // must leave ActionsUsed untouched, or it would wrongly block the Attack card too
            // (see ApplyAttack's ActionsUsed.Contains("disappear") gate).
            adv.ActionsUsed.Add("disappear");
            adv.Revealed = false;
            adv.ShadowTokens["main"] = adv.Space;
            Log("adversary", "disappeared");
        }

        public void AdversaryBreakDoor(string doorSpace)
        {
            EnsureAdversaryTurnStarted();
            if (State.Adversary.ActionsUsed.Contains("breakDoor"))
            {
                throw new InvalidOperationException("Break Door was already used this turn.");
            }
            bool adjacent = Graph.Edge(State.Adversary.Space, doorSpace) != null;
            if (State.Adversary.DefId == "insatiable-horror")
            {
                adjacent = Graph.DistancesFrom(State.Adversary.Space, 3, State.Overlay).ContainsKey(doorSpace);
            }
            // Lucy Belle's Barricade tokens "work like Doors for the Adversary, except when they
            // are Destroyed the Barricade token is removed": Break Door is the only way through
            // one, and it burns the same once-per-turn slot (Game.InvestigatorAbilities.cs).
            if (adjacent && BreakBarricadeAt(doorSpace))
            {
                State.Adversary.ActionsUsed.Add("breakDoor");
                return;
            }
            if (!adjacent || Graph.Space(doorSpace).Kind != SpaceKind.Door)
            {
                throw new InvalidOperationException("No door in reach.");
            }
            var current = State.Overlay.DoorState(doorSpace);
            DoorState next = current switch
            {
                DoorState.Open => DoorState.Damaged,
                DoorState.Locked => DoorState.Damaged,
                DoorState.Damaged => DoorState.Destroyed,
                _ => throw new InvalidOperationException($"Door is {current}."),
            };
            // Every check above (including the door-state switch) must pass before
            // "breakDoor" is marked used, or a refused call would burn the once-per-turn slot.
            State.Adversary.ActionsUsed.Add("breakDoor");
            State.Overlay.DoorStates[doorSpace] = next;
            Log("adversary", $"broke door {doorSpace} ({State.Overlay.DoorState(doorSpace)})");
        }

        public void AdversaryEndTurn()
        {
            if (State.Phase == GamePhase.GameOver)
            {
                return;
            }
            EnsureAdversaryTurnStarted();
            // Cards that resolve "at the end of your turn" run while this round's Flashlights
            // are still on the board and before the cooldown tracks move.
            OnAdversaryTurnEnd();
            if (State.Phase == GamePhase.GameOver)
            {
                // The end-of-turn hooks just decided the game; cooldowns and the round no
                // longer advance for a game that is already over.
                return;
            }
            AdvanceCooldowns();
            State.Adversary.TurnStarted = false;
            EndRound();
        }

        private void EndRound()
        {
            if (State.Phase == GamePhase.GameOver)
            {
                return;
            }
            // Cards that expire or trigger "at the end of the round" run first, while this
            // round's lights are still on and before the round counter moves.
            OnRoundEnd();
            if (State.Phase == GamePhase.GameOver)
            {
                // OnRoundEnd just decided the game (a Wound or escape effect); the round-limit
                // branch below must never overwrite that Result, and no new round may begin.
                return;
            }
            State.Overlay.BrightSpaces.Clear();
            State.Flashlights.Clear();
            // Zone lights burn out to Faltering after their round.
            foreach (string zone in State.Overlay.BrightZones)
            {
                State.FalteringZones.Add(zone);
            }
            State.Overlay.BrightZones.Clear();

            if (State.Round >= Db.Config.Rounds)
            {
                // Designer ruling: if the timer runs out, the Investigators lose. The only
                // exceptions are the Grave/Eggs banish cards, whose printed text makes a
                // timeout a draw (Golden Rule: the card beats the rulebook).
                State.Phase = GamePhase.GameOver;
                string? selected = State.Objective.SelectedEscapeCard;
                State.Result = selected == "the-grave" || selected == "the-eggs"
                    ? GameResult.Draw
                    : GameResult.AdversaryWins;
                Log("gameover", "round limit reached");
                return;
            }
            BeginRound(State.Round + 1);
        }

        // ---------- Shared mechanics ----------

        /// <summary>
        /// Draw a Wound card into an Investigator's Wound slots. <paramref name="origin"/> is
        /// one of the WoundFrom* consts (Game.Effects.cs) and tells the cards that care where
        /// the Wound came from — Punctured Lung turns Sprint Wounds face-up, Mauled adds an
        /// extra face-down Wound to Adversary ones. It defaults to "" (an untagged Wound: a
        /// card effect, an objective cost) so callers with nothing to say need not say it.
        /// </summary>
        public void GainWound(InvestigatorState inv, bool faceUp, string origin = "")
        {
            if (IsSpirit(inv))
            {
                // A Spirit has no player board and therefore no Wound slots: it cannot die again.
                Log("spirit", $"{inv.DefId}'s Spirit has no Wound slots (Wound ignored)");
                return;
            }
            var wound = new WoundInstance { CardId = DrawWound(), FaceUp = faceUp };
            // Cards may still flip this Wound face-up, or inflict further Wounds, before it
            // lands (see the OnWoundGained hook).
            OnWoundGained(inv, wound, origin);
            inv.Wounds.Add(wound);
            Log("wound", $"{inv.DefId} now has {inv.Wounds.Count} wound(s)");
            if (wound.FaceUp)
            {
                ResolveWoundFaceUp(inv, wound);
            }
            if (inv.Wounds.Count >= Db.Config.WoundsToDie && !inv.Dead)
            {
                inv.Dead = true;
                State.Adversary.Kills += 1;
                Log("death", inv.DefId);
                OnInvestigatorDeath(inv);
                if (State.Adversary.Kills >= State.Adversary.KillsToWin)
                {
                    State.Phase = GamePhase.GameOver;
                    State.Result = GameResult.AdversaryWins;
                }
                else if (!State.Investigators.Any(i => !i.Dead && !i.Escaped))
                {
                    // The Adversary has not won outright, but nobody is left on the board to
                    // act. Revised designer ruling: deaths no longer downgrade an escape to a
                    // Draw — dead Investigators play on as Spirits, so if some Investigators did
                    // escape, the team wins outright the moment the last living, non-escaped
                    // Investigator dies. If instead nobody escaped either, every Investigator is
                    // dead and the Adversary's kill count has simply not caught up yet (e.g. a
                    // Rabbit's Foot revival lowered it); nothing to decide here — the game either
                    // already ended above or is not yet over.
                    if (State.Investigators.Any(i => i.Escaped))
                    {
                        State.Phase = GamePhase.GameOver;
                        State.Result = GameResult.InvestigatorsWin;
                        Log("gameover", "the last living Investigator died with the rest already escaped: an Investigators win");
                    }
                }
                // Otherwise the player may adopt a Spirit — Spirit play lands with abilities.
            }
        }

        private void SpendStamina(InvestigatorState inv, int amount, string origin = WoundFromStaminaTrack)
        {
            if (inv.Stamina < amount)
            {
                throw new InvalidOperationException("Not enough Stamina.");
            }
            LoseStamina(inv, amount, origin);
        }

        private void LoseStamina(InvestigatorState inv, int amount, string origin = WoundFromStaminaTrack)
        {
            var track = Db.Investigator(inv.DefId).StaminaTrack;
            // Cold Front: "Sprinting must trip the Stamina track's Wound icons 1 space early."
            int shift = origin == WoundFromSprint ? Math.Max(0, EventRoundModifier(SprintWoundIconShiftKey)) : 0;
            for (int i = 0; i < amount && inv.Stamina > 0; i++)
            {
                inv.Stamina -= 1;
                if (track.WoundIconSpaces.Contains(inv.Stamina) ||
                    (shift > 0 && track.WoundIconSpaces.Contains(inv.Stamina - shift)))
                {
                    GainWound(inv, faceUp: false, origin: origin);
                }
            }
        }

        private void GainStamina(InvestigatorState inv, int amount)
        {
            int max = Db.Investigator(inv.DefId).StaminaTrack.Spaces - 1;
            inv.Stamina = Math.Min(max, inv.Stamina + amount);
        }

        // ---------- Per-turn resource-gain watch (the Cult's Burning Heart end condition) ----------

        private const string TurnStartStaminaPrefix = "turn-start-stamina:";
        private const string TurnStartChargePrefix = "turn-start-charge:";

        private void NoteTurnStartResources(InvestigatorState inv)
        {
            SetRoundModifier(TurnStartStaminaPrefix + inv.DefId, inv.Stamina);
            SetRoundModifier(TurnStartChargePrefix + inv.DefId, inv.Charge);
        }

        /// <summary>
        /// Burning Heart stays in effect "until no Investigators gain Charge or Lungs from any
        /// source during their turns", so what has to be watched is each turn as a whole rather
        /// than each individual gain: an Investigator who ends their turn holding more of
        /// either than they started it with gained some. That reading also costs nothing to
        /// enforce — no card-specific call has to be threaded through every mutator — at the
        /// price of missing a gain that was spent again inside the same turn.
        /// </summary>
        private void NoteTurnResourceGains(InvestigatorState inv)
        {
            if (inv.Stamina > RoundModifier(TurnStartStaminaPrefix + inv.DefId) ||
                inv.Charge > RoundModifier(TurnStartChargePrefix + inv.DefId))
            {
                SetRoundModifier(ResourceGainedKey, 1);
            }
        }

        private void ApplyWaterFloat(InvestigatorState inv)
        {
            // "Spirits are not affected by Dark spaces, Map Hazards, or anything that affects
            // movement": the Tunnel of Love current does not carry them.
            if (IsSpirit(inv))
            {
                return;
            }
            if (!Graph.HasSpace(inv.Space) || !Graph.Space(inv.Space).Water ||
                inv.WaterFloatUsedThisTurn || Graph.Def.WaterFlowLoop.Count == 0)
            {
                return;
            }
            inv.WaterFloatUsedThisTurn = true;
            string target = inv.Space;
            for (int i = 0; i < 2; i++)
            {
                string next = Graph.WaterNext(target, 1);
                if (State.Investigators.Any(o => o != inv && !o.Dead && !o.Escaped && o.Space == next))
                {
                    break; // float as far as possible without ending on another Investigator
                }
                target = next;
            }
            if (target != inv.Space)
            {
                inv.Space = target;
                Log("water", $"{inv.DefId} floated to {target}");
                RemoveFlashlightIfForcedMove(inv.DefId);
            }
        }

        private void ApplyCarriageRotation(InvestigatorState inv)
        {
            if (IsSpirit(inv))
            {
                return; // a forced ride rotation is something that affects movement; Spirits float
            }
            if (inv.CarriageRotationUsedThisRound)
            {
                return;
            }
            string? next = Graph.RideNext(inv.Space);
            if (next == null)
            {
                return;
            }
            bool occupied = State.Investigators.Any(o => o != inv && !o.Dead && !o.Escaped && o.Space == next) ||
                            (State.Adversary.Revealed && State.Adversary.Space == next);
            inv.CarriageRotationUsedThisRound = true;
            if (!occupied)
            {
                inv.Space = next;
                Log("ride", $"{inv.DefId} rotated to {next}");
                RemoveFlashlightIfForcedMove(inv.DefId);
            }
        }

        private void ApplyAdversaryCarriageRotation()
        {
            var adv = State.Adversary;
            if (adv.CarriageRotationUsedThisRound)
            {
                return;
            }
            string? next = Graph.RideNext(adv.Space);
            if (next == null)
            {
                return;
            }
            adv.CarriageRotationUsedThisRound = true;
            if (!State.Investigators.Any(o => !o.Dead && !o.Escaped && o.Space == next))
            {
                adv.Space = next;
            }
        }

        private void RemoveFlashlightIfForcedMove(string invId)
        {
            var placed = State.Flashlights.FirstOrDefault(f => f.InvestigatorId == invId);
            if (placed != null)
            {
                State.Flashlights.Remove(placed);
                RecomputeBrightSpaces();
            }
        }

        private void RecomputeBrightSpaces()
        {
            State.Overlay.BrightSpaces.Clear();
            foreach (var f in State.Flashlights)
            {
                State.Overlay.BrightSpaces.UnionWith(f.BrightSpaces);
            }
        }

        private bool IsBright(string spaceId) => Graph.EffectiveLight(spaceId, State.Overlay) == LightLevel.Bright;

        private void RevealOnBright(IEnumerable<string> newlyBright)
        {
            var brightSet = newlyBright.ToHashSet();
            if (!State.Adversary.Revealed && brightSet.Contains(State.Adversary.Space))
            {
                RevealAdversary("caught in the light");
            }
            foreach (var figure in State.Adversary.Figures)
            {
                if (figure.Alive && !figure.Revealed && brightSet.Contains(figure.Space))
                {
                    figure.Revealed = true;
                    DropShadowToken(figure.Id);
                    Log("reveal", $"{figure.Id} at {figure.Space} (caught in the light)");
                    OnAdversaryRevealed(figure.Id);
                }
            }
            foreach (var (zone, token) in State.Evidence.Select(kv => (kv.Key, kv.Value)))
            {
                if (!token.Revealed && brightSet.Contains(token.Space))
                {
                    token.Revealed = true;
                    Log("reveal", $"evidence {zone} at {token.Space}");
                }
            }
            foreach (var poi in State.PoiTokens)
            {
                if (!poi.Revealed && brightSet.Contains(poi.TokenSpace))
                {
                    poi.Revealed = true;
                    Log("reveal", $"POI token at {poi.TokenSpace}");
                }
            }
        }

        /// <summary>
        /// A figure standing in plain sight has no Shadow token — on the table the standee
        /// replaces it — so every reveal path drops the revealed figure's token. Without this
        /// the board shows one marker per figure PLUS a stale face-down Shadow token for the
        /// same figure. ("frayed", the Butcher's Frayed Ropes decoy, belongs to no figure and
        /// is deliberately left alone.)
        /// </summary>
        private void DropShadowToken(string figureKey) => State.Adversary.ShadowTokens.Remove(figureKey);

        /// <summary>Turn-start Shadow token: on the figure's space while it is Hidden, gone once
        /// its standee is on the board.</summary>
        private void RefreshShadowToken(string figureKey, string space, bool hidden)
        {
            if (hidden)
            {
                State.Adversary.ShadowTokens[figureKey] = space;
            }
            else
            {
                DropShadowToken(figureKey);
            }
        }

        private void RevealAdversary(string reason)
        {
            if (!State.Adversary.Revealed)
            {
                State.Adversary.Revealed = true;
                DropShadowToken("main");
                // Designer-confirmed: being Revealed during their own turn locks the Attack
                // for the rest of the round, same as beginning the turn Revealed. This is
                // the tactical point of flashlights: a beam across an approach lane both
                // exposes the Adversary and disarms them.
                if (State.Phase == GamePhase.AdversaryTurn)
                {
                    State.Adversary.AttackLockedThisTurn = true;
                }
                Log("reveal", $"adversary at {State.Adversary.Space} ({reason})");
                OnAdversaryRevealed("main");
            }
        }

        private string Draw(List<string> deck, string kind)
        {
            if (deck.Count == 0)
            {
                throw new InvalidOperationException($"The {kind} deck is empty.");
            }
            string id = deck[0];
            deck.RemoveAt(0);
            return id;
        }

        /// <summary>
        /// Draw the top card of the Wound deck. Designer ruling: when the Wound deck runs
        /// out, reshuffle <see cref="GameState.WoundDiscard"/> into a fresh Wound deck and
        /// keep drawing, rather than treating the deck as a hard limit. Every Wound draw in
        /// the engine funnels through here (GainWound, Rend, Painkillers, Neurotoxin) so the
        /// reshuffle applies uniformly. If both the deck and the discard pile are empty, every
        /// one of the 26 Wound cards is currently sitting in a Wound slot somewhere on the
        /// board — a state this throws for rather than silently fabricating a card.
        /// </summary>
        private string DrawWound()
        {
            if (State.WoundDeck.Count == 0)
            {
                if (State.WoundDiscard.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No Wound cards remain to draw: all 26 are on player boards.");
                }
                State.WoundDeck.AddRange(State.WoundDiscard);
                State.WoundDiscard.Clear();
                _rng.Shuffle(State.WoundDeck);
                SaveRng();
                Log("deck", "wound discards reshuffled");
            }
            return Draw(State.WoundDeck, "wound");
        }

        private string DrawMedicalItem()
        {
            var medkits = Db.Deck("medical-item").ToList();
            return medkits[0].Id; // single card type; no shuffle needed per the rulebook
        }

        private InvestigatorState ActiveInv()
        {
            RequirePhase(GamePhase.InvestigatorTurns);
            return Investigator(State.ActiveInvestigator
                ?? throw new InvalidOperationException("No investigator turn in progress."));
        }

        private InvestigatorState Investigator(string id) =>
            State.Investigators.FirstOrDefault(i => i.DefId == id)
            ?? throw new InvalidOperationException($"No investigator '{id}' in this game.");

        private void RequirePhase(GamePhase phase)
        {
            if (State.Phase != phase)
            {
                throw new InvalidOperationException($"Action requires phase {phase}, current is {State.Phase}.");
            }
        }

        private void RequireNoPendingWindow()
        {
            if (State.PendingWindowChoice)
            {
                throw new InvalidOperationException("Resolve the Window crossing first.");
            }
        }

        private static void RequireNoFinalAction(InvestigatorState inv)
        {
            if (inv.FinalAction != FinalActionKind.None)
            {
                throw new InvalidOperationException("A Final Action was already taken.");
            }
        }

        private void SaveRng() => State.RngState = _rng.State;

        private void Log(string type, string detail) =>
            State.Log.Add(new GameEvent { Round = State.Round, Type = type, Detail = detail });
    }
}
