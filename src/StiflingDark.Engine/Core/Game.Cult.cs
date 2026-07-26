using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The Cult of Hunlow: the multi-figure Adversary. The main figure on
    /// <see cref="AdversaryState.Space"/> is Mor'gonnod; the Cultists are
    /// <see cref="AdversaryState.Figures"/> ("c1".."c4"), each with their own MP budget and
    /// their own numbered Shadow token. Bloodletting fills the Blood track, and at 5 The
    /// Final Sacrifice consumes the Cultists and turns Mor'gonnod Corporeal. Also implements
    /// The Altar banish Objective (Ritual Knife / Rope Circle).
    ///
    /// All per-turn bookkeeping lives in serializable state: once-per-turn flags in
    /// <see cref="AdversaryState.ActionsUsed"/> (cleared by the shared framework each turn)
    /// and integer budgets/latches in <see cref="AdversaryState.Counters"/> (reset in
    /// <see cref="BeginCultTurn"/>). No private instance fields are used, so a save/load in
    /// the middle of an Adversary turn keeps every Cultist's remaining MP and acting order.
    ///
    /// Counters keys used here: "blood", "corporeal", "cmp:cN" (per-Cultist MP), "cfin:cN"
    /// (Cultist has finished acting), "cult-actor" (index of the Cultist currently acting),
    /// "bloodlet-count", "burning-heart-round", "dried-tongue-round", "shriveled-hand-round",
    /// "unblinking-eye-round", "altar-revealed", "knife-used-round", "banish-supplies".
    /// Corporeal Mor'gonnod spends the shared AdversaryState.MpRemaining like every other main
    /// figure; the turn framework simply hands him a flat CorporealMoveMp with no Sprint die.
    /// </summary>
    public sealed partial class Game
    {
        private const string CultDefId = "cult-of-hunlow";
        private const int BloodTrackMax = 5;
        private const int BanishSupplySlots = 3;
        private const int CorporealMoveMp = 10;

        // ---------- Adversary setup ----------

        /// <summary>
        /// Place the Cultists and the Altar (Adversary setup, after <see cref="PlaceAdversary"/>
        /// has put Mor'gonnod down). Cultist count matches the Investigator count; every
        /// Cultist must be adjacent to at least 1 other, forming a single group, and
        /// Mor'gonnod must be adjacent to one of them. The Altar goes on a General space of
        /// any Zone.
        /// </summary>
        public void SetupCultists(List<string> spaces, string altarSpace)
        {
            RequirePhase(GamePhase.AdversarySetup);
            RequireCultAdversary();
            var adv = State.Adversary;
            if (spaces == null)
            {
                throw new InvalidOperationException("Cultist spaces are required.");
            }
            int expected = CultistCount(State.Investigators.Count);
            if (spaces.Count != expected)
            {
                throw new InvalidOperationException(
                    $"The Cult takes {expected} Cultist(s) with {State.Investigators.Count} Investigators, got {spaces.Count}.");
            }
            if (spaces.Distinct().Count() != spaces.Count)
            {
                throw new InvalidOperationException("Each Cultist needs their own space.");
            }
            foreach (string space in spaces)
            {
                RequireSpace(space);
            }
            if (adv.Space.Length == 0)
            {
                throw new InvalidOperationException("Place Mor'gonnod (PlaceAdversary) before the Cultists.");
            }
            RequireSingleGroup(spaces, "Cultists");
            if (!spaces.Any(space => CultAdjacent(adv.Space, space)))
            {
                throw new InvalidOperationException($"Mor'gonnod ({adv.Space}) must start adjacent to one of the Cultists.");
            }
            RequireSpace(altarSpace);
            var altar = Graph.Space(altarSpace);
            if (altar.Kind != SpaceKind.Normal || altar.Zone == null)
            {
                throw new InvalidOperationException("The Altar goes on a General space inside a Zone.");
            }

            adv.Figures.Clear();
            for (int i = 0; i < spaces.Count; i++)
            {
                adv.Figures.Add(new AdversaryFigure { Id = "c" + (i + 1), Space = spaces[i] });
            }
            adv.Counters["blood"] = 0;
            adv.Counters["corporeal"] = 0;
            State.Objective.Tokens["altar"] = altarSpace;
            Log("setup", $"{expected} Cultists at {string.Join(", ", spaces)}; Altar on {altarSpace}");
        }

        /// <summary>Cultists on the board at each Investigator count (adversaries.json setup).</summary>
        private static int CultistCount(int investigators) => investigators switch
        {
            2 => 2,
            3 => 3,
            4 => 4,
            _ => throw new InvalidOperationException($"Unsupported Investigator count {investigators}."),
        };

        // ---------- Turn start ----------

        /// <summary>
        /// Locations first, then act: refresh every figure's Shadow token, then hand out the
        /// per-figure MP budgets (each Cultist gets 3 + the shared Sprint roll the framework
        /// already made; Corporeal Mor'gonnod gets his own 10 MP budget).
        /// </summary>
        partial void BeginCultTurn()
        {
            var adv = State.Adversary;
            bool corporeal = IsCorporeal();
            if (!corporeal)
            {
                adv.ShadowTokens["main"] = adv.Space;
                foreach (var cultist in adv.Figures.Where(f => f.Alive))
                {
                    adv.ShadowTokens[cultist.Id] = cultist.Space;
                }
            }

            int bonus = BurningHeartBonus();
            // Mor'gonnod: 3 + Sprint while Ethereal, a flat CorporealMoveMp once Corporeal
            // (handed out by EnsureAdversaryTurnStarted), plus Burning Heart either way.
            adv.MpRemaining += bonus;
            if (corporeal)
            {
                adv.Revealed = true;              // Corporeal is Revealed for the rest of the game
                adv.AttackLockedThisTurn = false; // ...and ignores the Revealed card
            }
            foreach (var cultist in adv.Figures.Where(f => f.Alive))
            {
                adv.Counters["cmp:" + cultist.Id] = AdversaryBaseMp[CultDefId] + adv.SprintRolled + bonus;
                adv.Counters.Remove("cfin:" + cultist.Id);
            }
            adv.Counters["cult-actor"] = 0;
            adv.Counters["bloodlet-count"] = 0;
            NoteAltarRevealed(); // this round's lights are still on during the Adversary turn
        }

        /// <summary>Burning Heart: +1 MP per Flashlight on the board, from the turn after it was played.</summary>
        private int BurningHeartBonus()
        {
            int from = CultCounter("burning-heart-round");
            return from != 0 && State.Round >= from ? State.Flashlights.Count : 0;
        }

        // ---------- Cultist Actions ----------

        /// <summary>
        /// One step of a Cultist's Move (3 MP + the shared Sprint roll). A Cultist who steps
        /// onto a Bright space is Revealed.
        /// </summary>
        public void CultistMoveStep(string cultistId, string to)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            var cultist = LivingCultist(cultistId);
            RequireSpace(to);
            var step = Graph.TryStep(FigureKind.Adversary, cultist.Space, to, State.Overlay)
                ?? throw new InvalidOperationException($"Cultist {cultist.Id} cannot move {cultist.Space} -> {to}.");
            int budget = CultCounter("cmp:" + cultist.Id);
            if (step.Cost > budget)
            {
                throw new InvalidOperationException($"Move costs {step.Cost} MP, Cultist {cultist.Id} has {budget} left.");
            }
            BeginCultistAction(cultist);

            string from = cultist.Space;
            adv.Counters["cmp:" + cultist.Id] = budget - step.Cost;
            adv.ActionsUsed.Add("cmove:" + cultist.Id);
            cultist.Space = to;
            if (step.CrossesWindow)
            {
                string key = BoardOverlay.EdgeKey(from, to);
                if (!adv.NoiseTokens.Contains(key))
                {
                    adv.NoiseTokens.Add(key);
                }
            }
            if (!cultist.Revealed && IsBright(to))
            {
                cultist.Revealed = true;
                Log("reveal", $"Cultist {cultist.Id} at {to} (moved onto a Bright space)");
            }
            // A Cultist already standing on a space that a Flashlight or Light Switch later
            // makes Bright is Revealed by the shared RevealOnBright (Game.cs), which walks
            // AdversaryState.Figures alongside the main figure.
            // todo: carriage rotation (ApplyAdversaryCarriageRotation) only rotates the main
            // figure; a Cultist in a carriage is not rotated. Per-figure rotation needs a
            // per-figure "already rotated this round" flag, and AdversaryFigure has none.
        }

        /// <summary>A Revealed Cultist in a Dim or Dark space goes Hidden; they may not Bloodlet this turn.</summary>
        public void CultistDisappear(string cultistId)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            var cultist = LivingCultist(cultistId);
            if (adv.ActionsUsed.Contains("cdisappear:" + cultist.Id))
            {
                throw new InvalidOperationException($"Cultist {cultist.Id} already used Disappear this turn.");
            }
            if (!cultist.Revealed)
            {
                throw new InvalidOperationException($"Cultist {cultist.Id} is already Hidden.");
            }
            if (Graph.EffectiveLight(cultist.Space, State.Overlay) == LightLevel.Bright)
            {
                throw new InvalidOperationException("Disappear requires a Dim or Dark space.");
            }
            BeginCultistAction(cultist);
            cultist.Revealed = false;
            adv.ActionsUsed.Add("cdisappear:" + cultist.Id);
            adv.ShadowTokens[cultist.Id] = cultist.Space;
            Log("adversary", $"Cultist {cultist.Id} disappeared on {cultist.Space}");
        }

        /// <summary>Break Door, once per round for the whole Cult (shared with the main figure's Break Door).</summary>
        public void CultistBreakDoor(string cultistId, string doorSpace)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            var cultist = LivingCultist(cultistId);
            if (adv.ActionsUsed.Contains("breakDoor"))
            {
                throw new InvalidOperationException("Break Door may only be used by 1 Cultist per round.");
            }
            RequireSpace(doorSpace);
            if (Graph.Edge(cultist.Space, doorSpace) == null || Graph.Space(doorSpace).Kind != SpaceKind.Door)
            {
                throw new InvalidOperationException("No door in reach.");
            }
            var current = State.Overlay.DoorState(doorSpace);
            var next = current switch
            {
                DoorState.Open => DoorState.Damaged,
                DoorState.Locked => DoorState.Damaged,
                DoorState.Damaged => DoorState.Destroyed,
                _ => throw new InvalidOperationException($"Door is {current}."),
            };
            BeginCultistAction(cultist);
            State.Overlay.DoorStates[doorSpace] = next;
            adv.ActionsUsed.Add("breakDoor");
            Log("adversary", $"Cultist {cultist.Id} broke door {doorSpace} ({next})");
        }

        /// <summary>
        /// Bloodletting: 1 Cultist per Adversary turn (2 different Cultists the round after
        /// Shriveled Hand), never in round 1, adjacent to an Investigator. Gives a face-down
        /// Wound (face-up the round after Dried Tongue), advances the Blood track, and flips
        /// 1 of Mor'gonnod's face-down Abilities face-up.
        /// </summary>
        public void Bloodletting(string cultistId, string investigatorId)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            if (State.Round <= 1)
            {
                throw new InvalidOperationException("Bloodletting may not be used during round 1.");
            }
            var cultist = LivingCultist(cultistId);
            int allowed = ShriveledHandActive() ? 2 : 1;
            int used = CultCounter("bloodlet-count");
            if (used >= allowed)
            {
                throw new InvalidOperationException(
                    $"Bloodletting is limited to {allowed} Cultist(s) per Adversary turn.");
            }
            if (adv.ActionsUsed.Contains("bloodlet:" + cultist.Id))
            {
                throw new InvalidOperationException($"Cultist {cultist.Id} already Bloodlet this turn.");
            }
            if (adv.ActionsUsed.Contains("cdisappear:" + cultist.Id))
            {
                throw new InvalidOperationException($"Cultist {cultist.Id} Disappeared this turn and may not Bloodlet.");
            }
            if (cultist.Revealed)
            {
                // Revealed card: a Cultist whose standee was on the main board this turn may not Bloodlet.
                throw new InvalidOperationException($"Cultist {cultist.Id} is Revealed and may not Bloodlet this turn.");
            }
            var inv = InvestigatorOnBoard(investigatorId);
            if (!AdversaryAdjacentTo(cultist.Space, inv.Space))
            {
                throw new InvalidOperationException(
                    $"Cultist {cultist.Id} ({cultist.Space}) is not adjacent to {inv.DefId} ({inv.Space}).");
            }
            BeginCultistAction(cultist);

            adv.ShadowTokens[cultist.Id] = cultist.Space;
            adv.Counters["bloodlet-count"] = used + 1;
            adv.ActionsUsed.Add("bloodlet:" + cultist.Id);
            int blood = Math.Min(BloodTrackMax, CultCounter("blood") + 1);
            adv.Counters["blood"] = blood;
            bool faceUp = DriedTongueActive();
            GainWound(inv, faceUp);
            FlipOneFaceDownAbility();
            Log("adversary", $"Cultist {cultist.Id} Bloodlet {inv.DefId}; Blood track {blood}/{BloodTrackMax}");
        }

        /// <summary>Each Bloodletting makes 1 more of Mor'gonnod's Abilities usable.</summary>
        private void FlipOneFaceDownAbility()
        {
            var adv = State.Adversary;
            if (adv.FaceDownAbilities.Count == 0)
            {
                return;
            }
            string card = adv.FaceDownAbilities[0];
            adv.FaceDownAbilities.RemoveAt(0);
            adv.ActiveAbilities.Add(card);
            Log("adversary", $"{card} is face-up");
        }

        /// <summary>
        /// "Each Cultist fully completes their Actions before the next Cultist acts": acting
        /// with a different Cultist retires whoever was acting before them.
        /// </summary>
        private void BeginCultistAction(AdversaryFigure cultist)
        {
            var counters = State.Adversary.Counters;
            if (CultCounter("cfin:" + cultist.Id) == 1)
            {
                throw new InvalidOperationException(
                    $"Cultist {cultist.Id} already finished acting this turn (another Cultist has acted since).");
            }
            int index = CultistIndex(cultist.Id);
            int actor = CultCounter("cult-actor");
            if (actor != 0 && actor != index)
            {
                counters["cfin:c" + actor] = 1;
            }
            counters["cult-actor"] = index;
        }

        private static int CultistIndex(string cultistId) =>
            int.TryParse(cultistId.Substring(1), out int index)
                ? index
                : throw new InvalidOperationException($"'{cultistId}' is not a Cultist id.");

        // ---------- The Final Sacrifice / Corporeal Mor'gonnod ----------

        /// <summary>
        /// With the Blood track at 5, the Cultists in a single group and Mor'gonnod adjacent
        /// to one of them: the Cultists are consumed, Mor'gonnod steps onto the main board
        /// Corporeal with his Attack card face-up, and the Adversary turn ends.
        /// </summary>
        public void TheFinalSacrifice()
        {
            EnsureAdversaryTurnStarted();
            RequireCultAdversary();
            var adv = State.Adversary;
            if (IsCorporeal())
            {
                throw new InvalidOperationException("Mor'gonnod is already Corporeal.");
            }
            int blood = CultCounter("blood");
            if (blood < BloodTrackMax)
            {
                throw new InvalidOperationException(
                    $"The Final Sacrifice needs the Blood track at {BloodTrackMax} (currently {blood}).");
            }
            var living = adv.Figures.Where(f => f.Alive).ToList();
            if (living.Count == 0)
            {
                throw new InvalidOperationException("There are no Cultists to sacrifice.");
            }
            var spaces = living.Select(f => f.Space).Distinct().ToList();
            RequireSingleGroup(spaces, "Cultists");
            if (!spaces.Any(space => CultAdjacent(adv.Space, space)))
            {
                throw new InvalidOperationException($"Mor'gonnod ({adv.Space}) must be adjacent to at least 1 Cultist.");
            }

            foreach (var cultist in living)
            {
                cultist.Revealed = true; // revealed, then consumed
                cultist.Alive = false;
                adv.Counters.Remove("cmp:" + cultist.Id);
                adv.Counters.Remove("cfin:" + cultist.Id);
            }
            adv.ShadowTokens.Clear(); // every remaining figure is on the main board
            adv.Revealed = true;
            adv.Counters["corporeal"] = 1;
            adv.MpRemaining = 0; // his own budget is handed out at the start of his next turn
            adv.AttackLockedThisTurn = false; // the Attack card is face-up from now on
            Log("adversary", $"The Final Sacrifice at {adv.Space}: Mor'gonnod is Corporeal");
            AdversaryEndTurn();
        }

        /// <summary>
        /// Corporeal movement: a flat CorporealMoveMp per turn with 2 MP to enter a Bright space.
        /// Both of those now live in the shared framework — EnsureAdversaryTurnStarted hands out
        /// the flat budget, <see cref="AdversaryMoveStep"/> charges the Bright premium — so this
        /// is just the named entry point for the Cult UI, and there is no second movement path to
        /// keep in step: calling AdversaryMoveStep directly while Corporeal is equally correct.
        /// </summary>
        public void MorgonnodCorporealMoveStep(string to)
        {
            RequireCultAdversary();
            if (!IsCorporeal())
            {
                throw new InvalidOperationException("Mor'gonnod is Ethereal; use AdversaryMoveStep (3 MP + Sprint).");
            }
            AdversaryMoveStep(to);
        }

        private bool IsCorporeal() => CultCounter("corporeal") == 1;

        // ---------- Attack and Ability cards ----------

        /// <summary>
        /// Resolves the Cult's 3 Attacks and 9 Abilities. Target conventions:
        /// ravage [inv]; immolate [inv] or [inv, otherInv]; flagellate [invToWound (may be
        /// "" to skip), invToPull, destinationSpace]; cleft-hoof [inv, hellfireSpace x0..3];
        /// razor-like-talons / severed-ear [inv]; twisted-horn [space x0..3]; the remaining
        /// Abilities take no targets.
        /// </summary>
        partial void ApplyCultCard(string cardId, List<string> targets)
        {
            var adv = State.Adversary;
            var card = Db.Deck("adversary").First(c => c.Id == cardId);
            bool corporeal = IsCorporeal();
            if (card.AdversaryCardType == "attack")
            {
                if (!corporeal)
                {
                    throw new InvalidOperationException("Mor'gonnod may never use the Attack card while Ethereal.");
                }
            }
            else if (!corporeal && cardId != "spiked-vertebrae")
            {
                // Spiked Vertebrae explicitly works even after being Revealed.
                if (adv.Revealed)
                {
                    throw new InvalidOperationException("Mor'gonnod must be Hidden to use an Ability while Ethereal.");
                }
                if (adv.ActionsUsed.Contains("disappear"))
                {
                    throw new InvalidOperationException("After Disappearing, Mor'gonnod may not use Abilities for the rest of the turn.");
                }
            }

            switch (cardId)
            {
                // ----- Attacks (Corporeal only) -----
                case "ravage":
                {
                    // "Give an adjacent Investigator 3 face-up Wounds."
                    var inv = AdjacentCardTarget(targets, 0, cardId);
                    DealAttackWounds(inv.DefId, 3, faceUp: true);
                    break;
                }
                case "immolate":
                {
                    // "Give an adjacent Investigator 2 face-up Wounds; may repeat once on a different Investigator."
                    if (targets.Count < 1 || targets.Count > 2)
                    {
                        throw new InvalidOperationException("Immolate takes 1 or 2 Investigator targets.");
                    }
                    if (targets.Distinct().Count() != targets.Count)
                    {
                        throw new InvalidOperationException("Immolate may only repeat on a different Investigator.");
                    }
                    for (int i = 0; i < targets.Count; i++)
                    {
                        var inv = AdjacentCardTarget(targets, i, cardId);
                        DealAttackWounds(inv.DefId, 2, faceUp: true);
                    }
                    break;
                }
                case "flagellate":
                {
                    // "Move an Investigator 2 spaces away adjacent to yourself; additionally
                    //  give an adjacent Investigator 1 face-up Wound and the Bleeding Condition."
                    if (targets.Count >= 3 && targets[1].Length > 0)
                    {
                        var pulled = InvestigatorOnBoard(targets[1]);
                        string destination = targets[2];
                        RequireSpace(destination);
                        var reach = Graph.DistancesFrom(adv.Space, 2, State.Overlay);
                        if (!reach.TryGetValue(pulled.Space, out int distance) || distance != 2)
                        {
                            throw new InvalidOperationException(
                                $"Flagellate only pulls an Investigator exactly 2 spaces away; {pulled.DefId} is on {pulled.Space}.");
                        }
                        if (!AdversaryAdjacentTo(adv.Space, destination))
                        {
                            throw new InvalidOperationException($"'{destination}' is not adjacent to Mor'gonnod.");
                        }
                        if (State.Investigators.Any(o => o != pulled && !o.Dead && !o.Escaped && o.Space == destination))
                        {
                            throw new InvalidOperationException($"'{destination}' is occupied by another Investigator.");
                        }
                        pulled.Space = destination;
                        RemoveFlashlightIfForcedMove(pulled.DefId);
                        Log("adversary", $"Flagellate dragged {pulled.DefId} to {destination}");
                    }
                    if (targets.Count > 0 && targets[0].Length > 0)
                    {
                        var inv = AdjacentCardTarget(targets, 0, cardId);
                        DealAttackWounds(inv.DefId, 1, faceUp: true);
                        GrantConditionWithSubstitution(inv, "bleeding");
                    }
                    break;
                }

                // ----- Abilities (Hidden Mor'gonnod, or Corporeal) -----
                case "burning-heart":
                {
                    // "+1 MP for each Flashlight on the board, from your next turn onwards."
                    adv.Counters["burning-heart-round"] = State.Round + 1;
                    Log("adversary", $"Burning Heart: from round {State.Round + 1}, +1 MP per Flashlight");
                    break;
                }
                case "cleft-hoof":
                {
                    // "Flip 1 face-up Wound face-down on any Investigator to place 3 Hellfire tokens within 5 spaces."
                    var inv = CardTarget(targets, 0, cardId);
                    var wound = inv.Wounds.FirstOrDefault(w => w.FaceUp)
                        ?? throw new InvalidOperationException($"{inv.DefId} has no face-up Wound to flip.");
                    var hellfire = targets.Skip(1).ToList();
                    ValidateTokenSpaces(hellfire, 3, "hellfire");
                    FlipWoundFaceDown(inv, wound); // honors Hemorrhage's flip-down restriction
                    PlaceCardTokens("hellfire", hellfire);
                    UpdateMorgonnodShadow();
                    break;
                }
                case "dried-tongue":
                {
                    // "Next round, Bloodletting gives a face-up Wound."
                    adv.Counters["dried-tongue-round"] = State.Round + 1;
                    Log("adversary", $"Dried Tongue: Bloodletting is face-up in round {State.Round + 1}");
                    break;
                }
                case "razor-like-talons":
                {
                    // "When adjacent to an Investigator, flip 1 of their face-down Wounds face-up."
                    var inv = AdjacentCardTarget(targets, 0, cardId);
                    var wound = inv.Wounds.FirstOrDefault(w => !w.FaceUp)
                        ?? throw new InvalidOperationException($"{inv.DefId} has no face-down Wound to flip.");
                    UpdateMorgonnodShadow();
                    Log("adversary", $"Razor-like Talons flipped a Wound face-up on {inv.DefId}");
                    FlipWoundFaceUp(inv, wound); // resolves the Wound card's own text
                    break;
                }
                case "severed-ear":
                {
                    // "Give an adjacent Investigator the Possessed Condition (not on the first round)."
                    if (State.Round <= 1)
                    {
                        throw new InvalidOperationException("Severed Ear may not be used on the first round.");
                    }
                    var inv = AdjacentCardTarget(targets, 0, cardId);
                    UpdateMorgonnodShadow();
                    // There is only 1 Possessed card, so granting it to a new Investigator
                    // moves it off whoever held it.
                    foreach (var other in State.Investigators.Where(i => i != inv && HasCondition(i, "possessed")).ToList())
                    {
                        DiscardCondition(other, "possessed");
                    }
                    GrantConditionWithSubstitution(inv, "possessed");
                    break;
                }
                case "shriveled-hand":
                {
                    // "Next round you may Bloodlet with 2 different Cultists."
                    adv.Counters["shriveled-hand-round"] = State.Round + 1;
                    Log("adversary", $"Shriveled Hand: 2 Cultists may Bloodlet in round {State.Round + 1}");
                    break;
                }
                case "spiked-vertebrae":
                {
                    // Freeform table deal; nothing to enforce mechanically.
                    Log("todo", "spiked-vertebrae: a negotiated deal between the Adversary and 1 Investigator — apply the agreed changes through the individual actions");
                    break;
                }
                case "twisted-horn":
                {
                    // "Place up to 3 Desecrated Ground tokens within 5 spaces on General spaces."
                    ValidateTokenSpaces(targets, 3, "desecrated-ground");
                    PlaceCardTokens("desecrated-ground", targets);
                    UpdateMorgonnodShadow();
                    break;
                }
                case "unblinking-eye":
                {
                    // "Investigators who do not place a Flashlight next round gain the Paranoid
                    // Condition." Judged at the end of the Adversary turn of that round, while
                    // State.Flashlights still holds every placement made during it.
                    adv.Counters["unblinking-eye-round"] = State.Round + 1;
                    Log("adversary", $"Unblinking Eye: anyone without a Flashlight in round {State.Round + 1} gains Paranoid");
                    break;
                }
                default:
                    throw new InvalidOperationException($"'{cardId}' is not a Cult of Hunlow card.");
            }
        }

        // ---------- Token triggers and end-of-turn follow-ups (Game.EffectDispatch.cs) ----------

        /// <summary>Round-modifier prefix + Investigator def id: they Moved onto an unlit
        /// Desecrated Ground token this turn and owe its end-of-turn D6.</summary>
        private const string DesecratedGroundPrefix = "desecrated-ground-stepped:";

        partial void CultOnInvestigatorMoveStep(InvestigatorState inv, string from, string to)
        {
            // inv.Space rather than `to`: a carriage rotation or water float may have carried them
            // onward after the step, and a token only triggers where they actually end up.
            if (HasBoardTokenAt("hellfire-", inv.Space))
            {
                // "Investigators must take a face-up Wound if they Move onto a Hellfire token."
                Log("adversary", $"{inv.DefId} Moved onto Hellfire at {inv.Space}");
                GainWound(inv, faceUp: true, WoundFromAdversary);
            }
            // "If an Investigator Moves onto one or more Desecrated Ground tokens, they must roll
            // a D6 at the end of their turn. On a 1-3, gain a face-down Wound. If the Desecrated
            // Ground token is Bright when the Investigator Moves onto it, they may ignore it."
            if (HasBoardTokenAt("desecrated-ground-", inv.Space) && !IsBright(inv.Space))
            {
                SetRoundModifier(DesecratedGroundPrefix + inv.DefId, 1);
            }
        }

        partial void CultOnInvestigatorTurnEnd(InvestigatorState inv)
        {
            if (!ClearRoundModifier(DesecratedGroundPrefix + inv.DefId))
            {
                return;
            }
            int roll = _rng.Roll(6);
            SaveRng();
            Log("adversary", $"{inv.DefId} rolls {roll} for the Desecrated Ground they walked on");
            if (roll <= 3)
            {
                GainWound(inv, faceUp: false, WoundFromAdversary);
            }
        }

        /// <summary>
        /// The Cult's three "at the end of your turn" follow-ups. Each of them reads state that is
        /// only complete at this moment: Unblinking Eye needs the round's full set of Flashlight
        /// placements (still on the board until EndRound clears them), Shriveled Hand needs the
        /// Bloodletting count of the turn that is finishing, and Burning Heart needs to know
        /// whether anybody gained Charge or Stamina across the whole round.
        /// </summary>
        partial void CultOnAdversaryTurnEnd()
        {
            var adv = State.Adversary;

            if (CultCounter("unblinking-eye-round") == State.Round)
            {
                adv.Counters.Remove("unblinking-eye-round");
                foreach (var inv in State.Investigators.Where(i => !i.Dead && !i.Escaped))
                {
                    if (!State.Flashlights.Any(f => f.InvestigatorId == inv.DefId))
                    {
                        Log("adversary", $"Unblinking Eye: {inv.DefId} placed no Flashlight this round");
                        GrantConditionWithSubstitution(inv, "paranoid");
                    }
                }
            }

            if (CultCounter("shriveled-hand-round") == State.Round)
            {
                adv.Counters.Remove("shriveled-hand-round");
                adv.ActiveAbilities.Remove("shriveled-hand");
                if (CultCounter("bloodlet-count") >= 2)
                {
                    Log("adversary", "Shriveled Hand did its work and is removed from the game");
                }
                else
                {
                    adv.Cooldown1.Add(new CooldownCard { CardId = "shriveled-hand", FaceUp = false });
                    Log("adversary", "fewer than 2 Cultists Bloodlet: Shriveled Hand goes face-down into Cooldown 1");
                }
            }

            int burningFrom = CultCounter("burning-heart-round");
            if (burningFrom != 0 && State.Round >= burningFrom && !HasRoundModifier(ResourceGainedKey))
            {
                adv.Counters.Remove("burning-heart-round");
                adv.ActiveAbilities.Remove("burning-heart");
                adv.Cooldown2.Add(new CooldownCard { CardId = "burning-heart", FaceUp = false });
                Log("adversary", "no Investigator gained Charge or Stamina this round: " +
                                 "Burning Heart goes face-down into Cooldown 2");
            }
        }

        /// <summary>The demon-head cost row on the position-dependent Abilities: update Mor'gonnod's Shadow token.</summary>
        private void UpdateMorgonnodShadow()
        {
            if (!IsCorporeal())
            {
                State.Adversary.ShadowTokens["main"] = State.Adversary.Space;
            }
        }

        /// <summary>Hellfire / Desecrated Ground placements: General spaces within 5 of Mor'gonnod.</summary>
        private void ValidateTokenSpaces(List<string> spaces, int max, string tokenName)
        {
            if (spaces.Count == 0)
            {
                return;
            }
            if (spaces.Count > max)
            {
                throw new InvalidOperationException($"At most {max} {tokenName} token(s) may be placed.");
            }
            if (spaces.Distinct().Count() != spaces.Count)
            {
                throw new InvalidOperationException($"{tokenName} tokens go on different spaces.");
            }
            var reach = Graph.DistancesFrom(State.Adversary.Space, 5, State.Overlay);
            foreach (string space in spaces)
            {
                RequireSpace(space);
                if (Graph.Space(space).Kind != SpaceKind.Normal || !reach.ContainsKey(space))
                {
                    throw new InvalidOperationException(
                        $"{tokenName} tokens go on General spaces within 5 spaces of Mor'gonnod ('{space}' does not qualify).");
                }
            }
            Log("adversary", $"{tokenName} tokens: {string.Join(", ", spaces)}");
        }

        /// <summary>Put a fresh batch of Hellfire / Desecrated Ground tokens on the board,
        /// numbering the instance ids from 1 after clearing any earlier batch of that kind.</summary>
        private void PlaceCardTokens(string tokenKind, List<string> spaces)
        {
            if (spaces.Count == 0)
            {
                return; // "up to N": placing none leaves any earlier batch where it is
            }
            RemoveBoardTokens(tokenKind + "-");
            for (int i = 0; i < spaces.Count; i++)
            {
                PlaceBoardToken($"{tokenKind}-{i + 1}", spaces[i]);
            }
        }

        private bool DriedTongueActive() => CultCounter("dried-tongue-round") == State.Round;

        private bool ShriveledHandActive() => CultCounter("shriveled-hand-round") == State.Round;

        // ---------- The Altar (banish Objective) ----------

        /// <summary>The Altar banish setup: the Altar is already on the board from Adversary setup.</summary>
        partial void SetupAltarBanish()
        {
            RequireCultAdversary();
            if (!State.Objective.Tokens.ContainsKey("altar"))
            {
                throw new InvalidOperationException("The Altar token is not on the board (SetupCultists places it).");
            }
            _banishSetupDone = true;
            Log("objective", "The Altar: the Adversary places the Ritual Knife and Rope Circle within 10 spaces of the Altar");
        }

        /// <summary>Adversary places the Ritual Knife and Rope Circle on General spaces within 10 of the Altar.</summary>
        public void PlaceRitualTokens(string knifeSpace, string ropeSpace)
        {
            if (State.Phase == GamePhase.GameOver)
            {
                throw new InvalidOperationException("The game is over.");
            }
            RequireAltarObjective();
            if (knifeSpace == ropeSpace)
            {
                throw new InvalidOperationException("The Ritual Knife and Rope Circle go on different spaces.");
            }
            string altar = TokenSpace("altar");
            var within = Graph.DistancesFrom(altar, 10, State.Overlay);
            var placements = new List<(string token, string space)>
            {
                ("ritual-knife", knifeSpace),
                ("rope-circle", ropeSpace),
            };
            foreach (var (token, space) in placements)
            {
                RequireSpace(space);
                if (Graph.Space(space).Kind != SpaceKind.Normal || !within.ContainsKey(space))
                {
                    throw new InvalidOperationException(
                        $"The {token} goes on a General space within 10 spaces of the Altar ('{space}' does not qualify).");
                }
            }
            foreach (var (token, space) in placements)
            {
                State.Objective.Tokens[token] = space;
                Log("objective", $"{token} token on {space}");
            }
        }

        /// <summary>Interact Action: pick up the Ritual Knife or Rope Circle on your space.</summary>
        public void PickUpBanishToken(string tokenName)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (tokenName != "ritual-knife" && tokenName != "rope-circle")
            {
                throw new InvalidOperationException($"'{tokenName}' is not a Banish The Cult token.");
            }
            RequireOnToken(inv, tokenName);
            State.Objective.Tokens.Remove(tokenName);
            State.Objective.TokenCarriers[tokenName] = inv.DefId;
            Log("objective", $"{inv.DefId} picked up {tokenName}");
        }

        /// <summary>
        /// Involved Action on the Altar with the Ritual Knife, once per round for the whole
        /// team: flip one of your face-down Wounds face-up, or give yourself a face-down
        /// Wound, to place 1 Supply token on the Banish The Cult aid.
        /// </summary>
        public void UseRitualKnife(bool flipFaceDownWound)
        {
            var inv = BeginInvolvedAction();
            RequireAltarObjective();
            RequireCarrying(inv, "ritual-knife");
            RequireOnToken(inv, "altar");
            RequireAltarRevealed();
            if (CultCounter("knife-used-round") == State.Round)
            {
                throw new InvalidOperationException("The Ritual Knife may only be used once per round (not once per Investigator).");
            }
            var toFlip = flipFaceDownWound
                ? inv.Wounds.FirstOrDefault(w => !w.FaceUp)
                    ?? throw new InvalidOperationException($"{inv.DefId} has no face-down Wound to flip.")
                : null;

            var adv = State.Adversary;
            adv.Counters["knife-used-round"] = State.Round;
            int supplies = Math.Min(BanishSupplySlots, CultCounter("banish-supplies") + 1);
            adv.Counters["banish-supplies"] = supplies;
            Log("objective", $"{inv.DefId} used the Ritual Knife: supply {supplies}/{BanishSupplySlots}");
            if (toFlip != null)
            {
                FlipWoundFaceUp(inv, toFlip); // resolves the Wound card's own text
            }
            else
            {
                GainWound(inv, faceUp: false);
            }
            FinishInvolvedAction(inv);
        }

        /// <summary>
        /// Free Interact with the Rope Circle: with 3 Supply tokens and every surviving
        /// Investigator in a single group, at least 1 of them on or adjacent to the Altar,
        /// the Cult is Banished and the Investigators win.
        /// </summary>
        public void CutRopeCircle()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireAltarObjective();
            RequireCarrying(inv, "rope-circle");
            RequireAltarRevealed();
            int supplies = CultCounter("banish-supplies");
            if (supplies < BanishSupplySlots)
            {
                throw new InvalidOperationException(
                    $"Cutting the Rope Circle needs {BanishSupplySlots} Supply tokens ({supplies} so far).");
            }
            string altar = TokenSpace("altar");
            var survivors = State.Investigators.Where(i => !i.Dead && !i.Escaped).ToList();
            RequireSingleGroup(survivors.Select(i => i.Space).Distinct().ToList(), "Investigators");
            if (!survivors.Any(i => i.Space == altar || CultAdjacent(i.Space, altar)))
            {
                throw new InvalidOperationException("At least 1 Investigator must be on or adjacent to the Altar.");
            }
            State.Phase = GamePhase.GameOver;
            State.Result = GameResult.InvestigatorsWin;
            Log("gameover", $"{inv.DefId} cut the Rope Circle: the Cult of Hunlow is Banished");
        }

        /// <summary>Latch the Altar's reveal the first time its space is seen Bright.</summary>
        private void NoteAltarRevealed()
        {
            if (CultCounter("altar-revealed") == 1 ||
                !State.Objective.Tokens.TryGetValue("altar", out string altar) ||
                !IsBright(altar))
            {
                return;
            }
            State.Adversary.Counters["altar-revealed"] = 1;
            Log("reveal", $"the Altar at {altar} moves to the main board");
        }

        private void RequireAltarRevealed()
        {
            NoteAltarRevealed();
            if (CultCounter("altar-revealed") != 1)
            {
                throw new InvalidOperationException("The Altar must be Revealed (its space made Bright) first.");
            }
        }

        private void RequireAltarObjective()
        {
            RequireCultAdversary();
            if (State.Objective.SelectedEscapeCard != "the-altar")
            {
                throw new InvalidOperationException("The Altar Objective has not been selected.");
            }
        }

        // ---------- Shared Cult plumbing ----------

        private int CultCounter(string key) =>
            State.Adversary.Counters.TryGetValue(key, out int value) ? value : 0;

        private void RequireCultAdversary()
        {
            if (State.Adversary.DefId != CultDefId)
            {
                throw new InvalidOperationException($"This Action belongs to the Cult of Hunlow, not '{State.Adversary.DefId}'.");
            }
        }

        private AdversaryFigure LivingCultist(string cultistId)
        {
            RequireCultAdversary();
            var cultist = State.Adversary.Figures.FirstOrDefault(f => f.Id == cultistId)
                ?? throw new InvalidOperationException($"No Cultist '{cultistId}' in this game.");
            if (!cultist.Alive)
            {
                throw new InvalidOperationException($"Cultist {cultistId} was consumed by The Final Sacrifice.");
            }
            return cultist;
        }

        private InvestigatorState InvestigatorOnBoard(string invId)
        {
            var inv = Investigator(invId);
            if (inv.Dead || inv.Escaped)
            {
                throw new InvalidOperationException($"{invId} is not on the board.");
            }
            return inv;
        }

        private InvestigatorState CardTarget(List<string> targets, int index, string cardId)
        {
            if (targets.Count <= index || targets[index].Length == 0)
            {
                throw new InvalidOperationException($"'{cardId}' needs an Investigator target (position {index}).");
            }
            return InvestigatorOnBoard(targets[index]);
        }

        private InvestigatorState AdjacentCardTarget(List<string> targets, int index, string cardId)
        {
            var inv = CardTarget(targets, index, cardId);
            if (!AdversaryAdjacentTo(State.Adversary.Space, inv.Space))
            {
                throw new InvalidOperationException(
                    $"'{cardId}' needs a target adjacent to Mor'gonnod ({State.Adversary.Space}); {inv.DefId} is on {inv.Space}.");
            }
            return inv;
        }

        /// <summary>Adjacency for grouping: Movement-line adjacency (Windows count, blocked Doors do not).</summary>
        private bool CultAdjacent(string a, string b) =>
            a != b && Graph.DistancesFrom(a, 1, State.Overlay).ContainsKey(b);

        /// <summary>Every space must be reachable from the first through the set: one connected group.</summary>
        private void RequireSingleGroup(List<string> spaces, string what)
        {
            if (spaces.Count <= 1)
            {
                return; // a lone figure is trivially its own group
            }
            var remaining = new HashSet<string>(spaces);
            remaining.Remove(spaces[0]);
            var frontier = new Queue<string>();
            frontier.Enqueue(spaces[0]);
            while (frontier.Count > 0)
            {
                string current = frontier.Dequeue();
                foreach (string next in remaining.Where(space => CultAdjacent(current, space)).ToList())
                {
                    remaining.Remove(next);
                    frontier.Enqueue(next);
                }
            }
            if (remaining.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The {what} must form a single group, each adjacent to at least 1 other ({string.Join(", ", remaining)} stand apart).");
            }
        }

        private void RequireSpace(string spaceId)
        {
            if (!Graph.HasSpace(spaceId))
            {
                throw new InvalidOperationException($"No space '{spaceId}' on map '{Graph.Def.Id}'.");
            }
        }
    }
}
