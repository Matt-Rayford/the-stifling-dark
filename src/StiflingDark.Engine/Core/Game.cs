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
            return game;
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
            State.Round = round;
            State.Phase = GamePhase.InvestigatorTurns;
            State.ActiveInvestigator = null;
            foreach (var inv in State.Investigators)
            {
                inv.TurnTakenThisRound = false;
                inv.CarriageRotationUsedThisRound = false;
            }
            State.Adversary.CarriageRotationUsedThisRound = false;
            if (State.EventDeck.Count > 0)
            {
                State.CurrentEvent = State.EventDeck[0];
                State.EventDeck.RemoveAt(0);
                Log("event", State.CurrentEvent!); // Effects are not auto-applied yet (card engine pending).
            }
        }

        public void BeginInvestigatorTurn(string invId)
        {
            RequirePhase(GamePhase.InvestigatorTurns);
            if (State.ActiveInvestigator != null)
            {
                throw new InvalidOperationException($"{State.ActiveInvestigator} has not finished their turn.");
            }
            var inv = Investigator(invId);
            if (inv.TurnTakenThisRound || inv.Dead || inv.Escaped)
            {
                throw new InvalidOperationException($"{invId} cannot take a turn.");
            }
            State.ActiveInvestigator = invId;
            inv.MpRemaining = Db.Investigator(invId).Mp;
            inv.SprintedOrRested = false;
            inv.Rested = false;
            inv.FinalAction = FinalActionKind.None;
            inv.MovementLocked = false;
            inv.WaterFloatUsedThisTurn = false;
            ApplyCarriageRotation(inv);
        }

        // ---------- Investigator actions ----------

        public void Sprint()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (inv.SprintedOrRested)
            {
                throw new InvalidOperationException("Sprint or Rest may be used once per turn.");
            }
            SpendStamina(inv, 1);
            int rolled = _rng.RollSprintDie(Db.Config.SprintDieFaces);
            SaveRng();
            inv.SprintedOrRested = true;
            inv.MpRemaining += rolled;
            Log("sprint", $"{inv.DefId} rolled {rolled} MP");
        }

        public void Rest()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (inv.SprintedOrRested)
            {
                throw new InvalidOperationException("Sprint or Rest may be used once per turn.");
            }
            inv.SprintedOrRested = true;
            inv.Rested = true;
        }

        public void MoveStep(string to)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (inv.MovementLocked)
            {
                throw new InvalidOperationException("Movement is over for this turn.");
            }
            var step = Graph.TryStep(FigureKind.Investigator, inv.Space, to, State.Overlay)
                ?? throw new InvalidOperationException($"Cannot move {inv.Space} -> {to}.");
            if (step.Cost > inv.MpRemaining)
            {
                throw new InvalidOperationException($"Move costs {step.Cost} MP, only {inv.MpRemaining} left.");
            }
            inv.MpRemaining -= step.Cost;
            inv.Space = to;
            if (step.CrossesWindow)
            {
                State.PendingWindowChoice = true;
            }
            ApplyCarriageRotation(inv);
            ApplyWaterFloat(inv);
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
                GainWound(inv, faceUp: false);
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
            RequireAdjacentForTrade(inv, target);
            if (!inv.EvidenceCarried.Remove(zone))
            {
                throw new InvalidOperationException($"{inv.DefId} does not carry {zone} evidence.");
            }
            target.EvidenceCarried.Add(zone);
        }

        private void RequireAdjacentForTrade(InvestigatorState a, InvestigatorState b)
        {
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

        public void ChargeFlashlight()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireNoFinalAction(inv);
            inv.FinalAction = FinalActionKind.Charge;
            inv.Charge = Math.Min(Db.Config.ChargeMax, inv.Charge + 1);
            EndTurn(inv);
        }

        public void PlaceFlashlight(double angleRadians)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireNoFinalAction(inv);
            if (inv.Charge < 1)
            {
                throw new InvalidOperationException("Placing the Flashlight costs 1 Charge.");
            }
            inv.Charge -= 1;
            inv.FinalAction = FinalActionKind.PlaceFlashlight;
            var bright = PreviewFlashlight(inv.DefId, angleRadians);
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
            EndTurn(inv);
        }

        /// <summary>The Bright set a flashlight would produce — call freely for the mouse preview.</summary>
        public HashSet<string> PreviewFlashlight(string invId, double angleRadians) =>
            _beam.ComputeBright(Graph, Investigator(invId).Space, angleRadians, _losBlocker);

        /// <summary>Generic Involved Action final: ends the turn with no Stamina gain. Specific
        /// Involved Actions (evidence turn-in, objectives) build on this as they are implemented.</summary>
        public void TakeInvolvedAction()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireNoFinalAction(inv);
            inv.FinalAction = FinalActionKind.InvolvedAction;
            EndTurn(inv);
        }

        public void EndTurnWithoutFinalAction()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            EndTurn(inv);
        }

        private void EndTurn(InvestigatorState inv)
        {
            // Moving through other Investigators is fine; ending the turn stacked is not.
            if (State.Investigators.Any(o => o != inv && !o.Dead && !o.Escaped && o.Space == inv.Space))
            {
                throw new InvalidOperationException("Cannot end the turn on another Investigator's space.");
            }
            if (inv.Rested && inv.FinalAction != FinalActionKind.InvolvedAction)
            {
                GainStamina(inv, 1);
            }
            inv.TurnTakenThisRound = true;
            State.ActiveInvestigator = null;
            if (State.Investigators.All(i => i.TurnTakenThisRound || i.Dead || i.Escaped))
            {
                State.Phase = GamePhase.AdversaryTurn;
                State.Adversary.NoiseTokens.Clear();
            }
        }

        // ---------- Adversary turn (core actions; per-adversary specials layer on later) ----------

        public void AdversaryMoveStep(string to)
        {
            EnsureAdversaryTurnStarted();
            string from = State.Adversary.Space;
            var step = Graph.TryStep(FigureKind.Adversary, from, to, State.Overlay)
                ?? throw new InvalidOperationException($"Adversary cannot move {from} -> {to}.");
            if (step.Cost > State.Adversary.MpRemaining)
            {
                throw new InvalidOperationException($"Move costs {step.Cost} MP, only {State.Adversary.MpRemaining} left.");
            }
            State.Adversary.MpRemaining -= step.Cost;
            State.Adversary.ActionsUsed.Add("move"); // Moving forecloses start-of-turn-only actions (e.g. Ambush).
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
        }

        public void AdversaryDisappear()
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            if (!adv.ActionsUsed.Add("disappear"))
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
            adv.Revealed = false;
            adv.ShadowTokens["main"] = adv.Space;
            Log("adversary", "disappeared");
        }

        public void AdversaryBreakDoor(string doorSpace)
        {
            EnsureAdversaryTurnStarted();
            if (!State.Adversary.ActionsUsed.Add("breakDoor"))
            {
                throw new InvalidOperationException("Break Door was already used this turn.");
            }
            bool adjacent = Graph.Edge(State.Adversary.Space, doorSpace) != null;
            if (State.Adversary.DefId == "insatiable-horror")
            {
                adjacent = Graph.DistancesFrom(State.Adversary.Space, 3, State.Overlay).ContainsKey(doorSpace);
            }
            if (!adjacent || Graph.Space(doorSpace).Kind != SpaceKind.Door)
            {
                throw new InvalidOperationException("No door in reach.");
            }
            var current = State.Overlay.DoorState(doorSpace);
            State.Overlay.DoorStates[doorSpace] = current switch
            {
                DoorState.Open => DoorState.Damaged,
                DoorState.Locked => DoorState.Damaged,
                DoorState.Damaged => DoorState.Destroyed,
                _ => throw new InvalidOperationException($"Door is {current}."),
            };
            Log("adversary", $"broke door {doorSpace} ({State.Overlay.DoorState(doorSpace)})");
        }

        public void AdversaryEndTurn()
        {
            EnsureAdversaryTurnStarted();
            AdvanceCooldowns();
            State.Adversary.TurnStarted = false;
            EndRound();
        }

        private void EndRound()
        {
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
                // Timeout: with the Grave/Eggs banish objectives the game is a draw; the
                // Altar (Cult) banish is an adversary win. Otherwise anyone still on the
                // board counts as being killed, handing the Adversary the win.
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

        public void GainWound(InvestigatorState inv, bool faceUp)
        {
            inv.Wounds.Add(new WoundInstance { CardId = Draw(State.WoundDeck, "wound"), FaceUp = faceUp });
            Log("wound", $"{inv.DefId} now has {inv.Wounds.Count} wound(s)");
            if (inv.Wounds.Count >= Db.Config.WoundsToDie)
            {
                inv.Dead = true;
                State.Adversary.Kills += 1;
                Log("death", inv.DefId);
                if (State.Adversary.Kills >= State.Adversary.KillsToWin)
                {
                    State.Phase = GamePhase.GameOver;
                    State.Result = GameResult.AdversaryWins;
                }
                // Otherwise the player may adopt a Spirit — Spirit play lands with abilities.
            }
        }

        private void SpendStamina(InvestigatorState inv, int amount)
        {
            if (inv.Stamina < amount)
            {
                throw new InvalidOperationException("Not enough Stamina.");
            }
            LoseStamina(inv, amount);
        }

        private void LoseStamina(InvestigatorState inv, int amount)
        {
            var track = Db.Investigator(inv.DefId).StaminaTrack;
            for (int i = 0; i < amount && inv.Stamina > 0; i++)
            {
                inv.Stamina -= 1;
                if (track.WoundIconSpaces.Contains(inv.Stamina))
                {
                    GainWound(inv, faceUp: false);
                }
            }
        }

        private void GainStamina(InvestigatorState inv, int amount)
        {
            int max = Db.Investigator(inv.DefId).StaminaTrack.Spaces - 1;
            inv.Stamina = Math.Min(max, inv.Stamina + amount);
        }

        private void ApplyWaterFloat(InvestigatorState inv)
        {
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
                    Log("reveal", $"{figure.Id} at {figure.Space} (caught in the light)");
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

        private void RevealAdversary(string reason)
        {
            if (!State.Adversary.Revealed)
            {
                State.Adversary.Revealed = true;
                Log("reveal", $"adversary at {State.Adversary.Space} ({reason})");
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
