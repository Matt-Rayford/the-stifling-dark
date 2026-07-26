using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Event card effects (game-data/cards/events.json: 13 unique cards per scenario, one
    /// drawn at the start of every round by <see cref="BeginRound"/>). Implements the
    /// Events sub-hooks declared in Game.EffectDispatch.cs.
    ///
    /// Lifetime, per config.json roundStructure: "Non-Major Event effects last only for the
    /// current round." That maps exactly onto <see cref="GameState.RoundModifiers"/>, which
    /// BeginRound clears before the new Event is drawn, so every non-Major card simply
    /// writes its modifier(s) in <see cref="EventsOnDrawn"/> and forgets about them.
    ///
    /// The 5 Majors carrying the {infinity} icon (Downpour, Tornado, Firestorm, Fire
    /// Tornado, Toxic Gasses) last for the rest of the game and therefore cannot live in
    /// RoundModifiers. Drawing one writes a persistent marker into
    /// <see cref="GameState.BoardTokens"/> under the key "major-event:&lt;card id&gt;" with the
    /// value <see cref="MajorEventFlagSpace"/> ("-"). "-" is deliberately not a space id on
    /// any board: BoardTokens is an id -> space map, and these entries are flags rather than
    /// figures, so they are written directly instead of through
    /// <see cref="PlaceBoardToken"/> (which validates the space against the map).
    /// <see cref="ApplyPersistentMajorEvents"/> then re-applies every marked Major once per
    /// round. Hail Storm is the one Major that is not persistent ("Discard this card after
    /// applying its effects"), so it resolves once, on draw.
    ///
    /// Re-application timing: the only per-round hook this file owns is
    /// <see cref="EventsOnDrawn"/>, and BeginRound skips it once the 12-card deck runs out
    /// (round 13 of 17 onward, when CurrentEvent simply stops changing). Persistent Majors
    /// are therefore re-applied from <see cref="EventsOnTurnStart"/> instead, on the first
    /// Investigator turn of the round — which is still "the start of the round" as far as
    /// every one of their effects is concerned, and needs no extra latch state because
    /// "no Investigator has taken their turn yet" already identifies that moment. (A
    /// mid-round <see cref="FromState"/> resume skips that round's re-application.)
    ///
    /// Enforcement: an Event whose text has to be enforced somewhere the card-effect hooks
    /// do not reach (mostly inside Game.cs's own action methods) still records its modifier,
    /// and additionally logs a "todo" naming the method that has to read it. Everything the
    /// hooks *can* enforce is enforced for real:
    ///   EventsOnTurnStart        MP penalty (Rainy, Updraft).
    ///   EventsOnMoveStep         Move Stamina cost (Firestorm).
    ///   EventsOnFlashlightPlaced Charge surcharge (Foggy) and the line-of-sight limits
    ///                            (Misty, Hazy, Downpour).
    ///   EventsOnRoundEnd         expiry of an unresolved Adversary choice.
    ///
    /// Player/Adversary choices (Fallen Tree, Flare-Up, Roll Vortex, and Fire Tornado's 4-6
    /// branch) latch <see cref="EventChoicePending"/> and are answered by
    /// <see cref="ResolveEventChoice(string, List{string})"/>; see that method for the argument grammar.
    /// </summary>
    public sealed partial class Game
    {
        // ---------- RoundModifiers key glossary ----------

        /// <summary>MP removed from every Investigator's budget at the start of their turn.
        /// Enforced in <see cref="EventsOnTurnStart"/>. (Rainy, Updraft)</summary>
        public const string MpPenaltyKey = "mp-penalty";

        /// <summary>Extra Stamina every Sprint costs. Read site owed by Game.Sprint. (Squall, Severe Heat)</summary>
        public const string SprintStaminaSurchargeKey = "sprint-stamina-surcharge";

        /// <summary>Sprinting trips the Stamina track's Wound icons this many spaces early.
        /// Read site owed by Game.Sprint/Game.LoseStamina. (Cold Front)</summary>
        public const string SprintWoundIconShiftKey = "sprint-wound-icon-shift";

        /// <summary>D6 result at or above which a Sprint costs a face-down Wound; 0 = no roll.
        /// Read site owed by Game.Sprint. (Pyrocumulus)</summary>
        public const string SprintD6WoundThresholdKey = "sprint-d6-wound-threshold";

        /// <summary>Extra Charge a Flashlight placement costs. Deducted in
        /// <see cref="EventsOnFlashlightPlaced"/>; the up-front affordability check is owed by
        /// Game.PlaceFlashlight. (Foggy; the Butcher's Decay wants the same key.)</summary>
        public const string FlashlightChargeSurchargeKey = "flashlight-charge-surcharge";

        /// <summary>Maximum distance, in spaces, at which a Flashlight grants line of sight;
        /// 0 = unlimited. Enforced in <see cref="EventsOnFlashlightPlaced"/>. (Misty)</summary>
        public const string FlashlightLosRangeKey = "flashlight-los-range";

        /// <summary>Set: only the Flashlight's single center line grants line of sight.
        /// Enforced in <see cref="EventsOnFlashlightPlaced"/>. (Hazy, Downpour)</summary>
        public const string FlashlightCenterLineOnlyKey = "flashlight-center-line-only";

        /// <summary>Set: Point of Interest tokens may not be picked up. Read site owed by
        /// Game.PickUpPoiToken. (Muddy)</summary>
        public const string PoiPickupForbiddenKey = "poi-pickup-forbidden";

        /// <summary>Set: the Charge Final Action is unavailable. Read site owed by
        /// Game.ChargeFlashlight. (Interference)</summary>
        public const string ChargeActionForbiddenKey = "charge-action-forbidden";

        /// <summary>Set: no Stamina is gained as part of a Final Action, Rest included. Read
        /// site owed by Game.EndTurn. (Heavy Winds, Heavy Smoke, Tornado)</summary>
        public const string NoRestStaminaKey = "no-rest-stamina";

        /// <summary>Stamina the first Move step of a turn costs. Enforced in
        /// <see cref="EventsOnMoveStep"/>. (Firestorm)</summary>
        public const string MoveStaminaCostKey = "move-stamina-cost";

        /// <summary>Prefix + Investigator def id: that Investigator already paid
        /// <see cref="MoveStaminaCostKey"/> this round. One turn per Investigator per round
        /// makes the round scope the turn scope. (Firestorm)</summary>
        public const string MoveStaminaPaidPrefix = "move-stamina-paid:";

        /// <summary>Prefix + card id: that Event card is waiting for
        /// <see cref="ResolveEventChoice(List{string})"/>. The owning card is part of the key
        /// because a persistent Major (Fire Tornado) can be waiting on a choice in a round
        /// whose <see cref="GameState.CurrentEvent"/> is some later card entirely.</summary>
        public const string EventChoicePendingPrefix = "event-choice-pending:";

        /// <summary>The D6 Fire Tornado rolled this round (1-3 drains, 4-6 offers the Zone choice).</summary>
        public const string FireTornadoRollKey = "fire-tornado-roll";

        // ---------- Persistent Major markers ----------

        /// <summary>BoardTokens key prefix for a persistent ({infinity}) Major Event marker.</summary>
        public const string MajorEventTokenPrefix = "major-event:";

        /// <summary>The BoardTokens value used for Major Event markers. Not a real space id
        /// on any board — these entries are flags, not figures.</summary>
        public const string MajorEventFlagSpace = "-";

        /// <summary>Card ids of the persistent Major Events currently in force, in id order.</summary>
        public List<string> PersistentMajorEvents() =>
            BoardTokenIds(MajorEventTokenPrefix)
                .Select(id => id.Substring(MajorEventTokenPrefix.Length))
                .ToList();

        /// <summary>Card ids of the Event choices awaiting an answer this round, in id order.</summary>
        public List<string> PendingEventChoices() =>
            State.RoundModifiers.Keys
                .Where(k => k.StartsWith(EventChoicePendingPrefix, StringComparison.Ordinal))
                .Select(k => k.Substring(EventChoicePendingPrefix.Length))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

        /// <summary>True while some Event card is waiting for
        /// <see cref="ResolveEventChoice(List{string})"/>.</summary>
        public bool EventChoicePending => PendingEventChoices().Count > 0;

        // ---------- Hooks ----------

        partial void EventsOnDrawn()
        {
            string? id = State.CurrentEvent;
            if (id == null)
            {
                return;
            }
            ResolveEventCard(id);
        }

        partial void EventsOnTurnStart(InvestigatorState inv)
        {
            // First Investigator turn of the round == "the start of the round" for every
            // persistent Major's recurring text (see the class comment).
            if (State.Investigators.All(other => !other.TurnTakenThisRound))
            {
                ApplyPersistentMajorEvents();
            }

            int penalty = RoundModifier(MpPenaltyKey);
            if (penalty > 0 && inv.MpRemaining > 0)
            {
                int lost = Math.Min(penalty, inv.MpRemaining);
                inv.MpRemaining -= lost;
                Log("event", $"{State.CurrentEvent}: {inv.DefId} has {lost} less MP this turn");
            }
        }

        partial void EventsOnMoveStep(InvestigatorState inv, string from, string to)
        {
            int cost = RoundModifier(MoveStaminaCostKey);
            if (cost <= 0)
            {
                return;
            }
            string paidKey = MoveStaminaPaidPrefix + inv.DefId;
            if (HasRoundModifier(paidKey))
            {
                return;
            }
            SetRoundModifier(paidKey, 1);
            LoseStamina(inv, cost);
            Log("event", $"firestorm: {inv.DefId} pays {cost} Stamina to Move this turn");
        }

        partial void EventsOnFlashlightPlaced(InvestigatorState inv)
        {
            int surcharge = RoundModifier(FlashlightChargeSurchargeKey);
            if (surcharge > 0)
            {
                int paid = Math.Min(surcharge, inv.Charge);
                inv.Charge -= paid;
                Log("event", $"{inv.DefId} pays {paid} extra Charge for the Flashlight");
                Log("todo", "the extra Charge is taken after the fact; refusing a placement the Investigator " +
                            "cannot afford needs the surcharge read in Game.PlaceFlashlight (Game.cs), which has " +
                            $"no card hook before it spends Charge (RoundModifiers[\"{FlashlightChargeSurchargeKey}\"]).");
            }
            TrimFlashlightLineOfSight(inv);
        }

        partial void EventsOnRoundEnd()
        {
            foreach (string id in PendingEventChoices())
            {
                ClearRoundModifier(EventChoicePendingPrefix + id);
                Log("event", $"{id}: the Adversary never made their choice; it expires unused");
            }
        }

        // ---------- Card resolution ----------

        /// <summary>
        /// Resolve the Event card that just became <see cref="GameState.CurrentEvent"/>.
        /// Persistent Majors only place their marker here; their text is applied (this round
        /// included) by <see cref="ApplyPersistentMajorEvents"/> at the first turn start.
        /// </summary>
        private void ResolveEventCard(string id)
        {
            switch (id)
            {
                // ----- Amusement Park, minor -----
                case "cold-front":
                    SetRoundModifier(SprintWoundIconShiftKey, 1);
                    LogUnenforced(id, "Game.Sprint/Game.LoseStamina", SprintWoundIconShiftKey,
                        "Sprinting must trip the Stamina track's Wound icons 1 space early");
                    break;
                case "foggy":
                    SetRoundModifier(FlashlightChargeSurchargeKey, 1);
                    Log("event", $"{id}: Flashlight placements cost 1 extra Charge this round");
                    break;
                case "muddy":
                    SetRoundModifier(PoiPickupForbiddenKey, 1);
                    LogUnenforced(id, "Game.PickUpPoiToken", PoiPickupForbiddenKey,
                        "Point of Interest tokens may not be picked up this round");
                    break;
                case "rainy":
                case "updraft":
                    AddRoundModifier(MpPenaltyKey, 1);
                    Log("event", $"{id}: every Investigator has 1 less MP this round");
                    break;
                case "squall":
                case "severe-heat":
                    SetRoundModifier(SprintStaminaSurchargeKey, 1);
                    LogUnenforced(id, "Game.Sprint", SprintStaminaSurchargeKey,
                        "Sprinting costs 1 extra Stamina this round");
                    break;

                // ----- Amusement Park, moderate -----
                case "heavy-winds":
                case "heavy-smoke":
                    SetRoundModifier(NoRestStaminaKey, 1);
                    LogUnenforced(id, "Game.EndTurn", NoRestStaminaKey,
                        "no Stamina may be gained as part of a Final Action this round, Rest included");
                    break;
                case "interference":
                    SetRoundModifier(ChargeActionForbiddenKey, 1);
                    LogUnenforced(id, "Game.ChargeFlashlight", ChargeActionForbiddenKey,
                        "the Charge Final Action is unavailable this round");
                    break;
                case "misty":
                    SetRoundModifier(FlashlightLosRangeKey, 3);
                    Log("event", $"{id}: Flashlights only reach 3 spaces this round");
                    break;

                // ----- Amusement Park, major -----
                case "downpour":
                case "tornado":
                    MarkPersistentMajor(id);
                    break;
                case "hail-storm":
                    ApplyHailStorm();
                    break;

                // ----- Sawmill, minor -----
                case "fallen-tree":
                    BeginEventChoice(id, "1 False Door on an empty Door space, or 1 False Window on a Window");
                    break;
                case "flare-up":
                    BeginEventChoice(id, "lower the Charge of up to 2 Investigators by 1");
                    break;
                case "hazy":
                    SetRoundModifier(FlashlightCenterLineOnlyKey, 1);
                    Log("event", $"{id}: only the Flashlight's center line has line of sight this round");
                    break;

                // ----- Sawmill, moderate -----
                case "pyrocumulus":
                    SetRoundModifier(SprintD6WoundThresholdKey, 4);
                    LogUnenforced(id, "Game.Sprint", SprintD6WoundThresholdKey,
                        "an Investigator who Sprints must roll a D6 and take a face-down Wound on a 4+");
                    break;
                case "roll-vortex":
                    BeginEventChoice(id, "a Zone, up to 2 Destroyed Doors and 1 Open Window in it");
                    break;

                // ----- Sawmill, major -----
                case "firestorm":
                case "fire-tornado":
                case "toxic-gasses":
                    MarkPersistentMajor(id);
                    break;

                // ----- Flavour-only cards (printed "This card has no effect") -----
                case "the-storm-grows":
                case "eerie-calm":
                case "creeping-fire":
                case "firebreak":
                    Log("event", $"{id}: no effect");
                    break;

                default:
                    Log("todo", $"Event card '{id}' has no implementation in Game.EventEffects.cs.");
                    break;
            }
        }

        /// <summary>Record an Event whose text can only be enforced inside a Game.cs action.</summary>
        private void LogUnenforced(string id, string site, string modifierKey, string what)
        {
            Log("event", $"{id}: {what}");
            Log("todo", $"{id}: {what} — {site} has to read RoundModifiers[\"{modifierKey}\"]; " +
                        "it lives in Game.cs and exposes no card hook at that point.");
        }

        // ---------- Persistent Majors ----------

        private void MarkPersistentMajor(string id)
        {
            if (State.BoardTokens.ContainsKey(MajorEventTokenPrefix + id))
            {
                return;
            }
            State.BoardTokens[MajorEventTokenPrefix + id] = MajorEventFlagSpace;
            Log("event", $"{id}: a Major Event, in force for the rest of the game");
        }

        /// <summary>Re-apply every persistent Major once per round (see the class comment).</summary>
        private void ApplyPersistentMajorEvents()
        {
            foreach (string id in PersistentMajorEvents())
            {
                switch (id)
                {
                    case "downpour":
                        SetRoundModifier(FlashlightCenterLineOnlyKey, 1);
                        break;
                    case "tornado":
                        SetRoundModifier(NoRestStaminaKey, 1);
                        LogUnenforced(id, "Game.EndTurn", NoRestStaminaKey,
                            "no Stamina may be gained as part of a Final Action, Rest included");
                        break;
                    case "firestorm":
                        SetRoundModifier(MoveStaminaCostKey, 1);
                        break;
                    case "fire-tornado":
                        RollFireTornado();
                        break;
                    case "toxic-gasses":
                        RollToxicGasses();
                        break;
                }
            }
        }

        /// <summary>Fire Tornado: "Roll a D6 now and at the start of each round". 1-3 drains
        /// everyone; 4-6 hands the Adversary a Zone choice (<see cref="ResolveEventChoice(string, List{string})"/>).</summary>
        private void RollFireTornado()
        {
            int roll = _rng.Roll(6);
            SaveRng();
            SetRoundModifier(FireTornadoRollKey, roll);
            if (roll <= 3)
            {
                foreach (var inv in OnBoardInvestigators())
                {
                    // "Losing Lungs in this way does not incur face-down Wounds."
                    inv.Stamina = Math.Max(0, inv.Stamina - 1);
                    inv.Charge = Math.Max(0, inv.Charge - 1);
                }
                Log("event", $"fire-tornado rolled {roll}: every Investigator loses 1 Stamina and 1 Charge");
            }
            else
            {
                BeginEventChoice("fire-tornado", $"rolled {roll}: a Zone whose Doors are all Destroyed");
            }
        }

        /// <summary>Toxic Gasses: "each Investigator must roll their Sprint die. On a 3, they
        /// must flip a random face-down Wound face-up or gain a face-down Wound if they have
        /// none." Not a Sprint Action, so no MP or Stamina changes hands.</summary>
        private void RollToxicGasses()
        {
            foreach (var inv in OnBoardInvestigators())
            {
                int rolled = _rng.RollSprintDie(Db.Config.SprintDieFaces);
                SaveRng();
                if (rolled != 3)
                {
                    continue;
                }
                Log("event", $"toxic-gasses: {inv.DefId} rolled a 3");
                FlipRandomFaceDownWound(inv, gainWhenNone: true);
            }
        }

        /// <summary>
        /// Hail Storm (Major, one-shot): "All Investigators that do not have a Wound
        /// immediately gain 1 face-up Wound. Additionally, all existing face-down Wounds get
        /// flipped face-up." The blood-drop icon is the Wound token, so "do not have a
        /// {blood}" reads as holding no Wound card at all, face-up or face-down.
        ///
        /// The card's rider — "Cards and Abilities that ignore Event cards delay the effects
        /// of this Event by 1 round instead of ignoring it" — has nothing to act on: no
        /// implemented card ignores Event cards.
        /// </summary>
        private void ApplyHailStorm()
        {
            foreach (var inv in OnBoardInvestigators())
            {
                if (inv.Wounds.Count == 0)
                {
                    Log("event", $"hail-storm: {inv.DefId} was unwounded and gains 1 face-up Wound");
                    GainWound(inv, faceUp: true);
                }
            }
            foreach (var inv in OnBoardInvestigators())
            {
                foreach (var wound in inv.Wounds.Where(w => !w.FaceUp).ToList())
                {
                    FlipWoundFaceUp(inv, wound);
                }
            }
        }

        // ---------- Choice resolution ----------

        private void BeginEventChoice(string id, string what)
        {
            SetRoundModifier(EventChoicePendingPrefix + id, 1);
            Log("event", $"{id}: the Adversary may choose {what} (ResolveEventChoice)");
        }

        /// <summary>
        /// Answer the single Event choice awaiting the Adversary. Throws when none is pending,
        /// or when two are (a persistent Fire Tornado alongside a choice-bearing Event card) —
        /// name the card with <see cref="ResolveEventChoice(string, List{string})"/> then.
        /// </summary>
        public void ResolveEventChoice(List<string>? args = null)
        {
            var pending = PendingEventChoices();
            if (pending.Count == 0)
            {
                throw new InvalidOperationException("No Event card is waiting on a choice.");
            }
            if (pending.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{pending.Count} Event choices are pending ({string.Join(", ", pending)}); name the one to resolve.");
            }
            ResolveEventChoice(pending[0], args);
        }

        /// <summary>
        /// Answer the Adversary choice a specific Event card is waiting on. An empty (or null)
        /// argument list always means "decline". Arguments are validated in full before
        /// anything is applied, so a rejected call leaves the state untouched and the choice
        /// still pending. An unanswered choice expires at the end of the round.
        ///
        /// Per card:
        ///   fallen-tree   ["door:&lt;doorSpace&gt;"] places a False Door on an empty Open Door
        ///                 space, or ["window:&lt;a&gt;|&lt;b&gt;"] places a False Window on that
        ///                 Window edge.
        ///   flare-up      up to 2 different Investigator def ids, each losing 1 Charge.
        ///   roll-vortex   ["&lt;zone&gt;", ...] where the tail holds up to 2 "door:&lt;space&gt;"
        ///                 (Destroyed Door) and up to 1 "window:&lt;a&gt;|&lt;b&gt;" (Open Window),
        ///                 all inside that Zone. Every Investigator in the Zone then loses
        ///                 1 Stamina (no Wound icon) and 1 Charge.
        ///   fire-tornado  ["&lt;zone&gt;"] Destroys every Door in the Zone and flips 1 random
        ///                 face-down Wound face-up on every Investigator in it; an optional
        ///                 "false:&lt;space&gt;" additionally flips one Destroyed Door token on an
        ///                 otherwise empty space to its False Door side. Only offered on a 4-6.
        /// </summary>
        public void ResolveEventChoice(string eventId, List<string>? args)
        {
            if (!HasRoundModifier(EventChoicePendingPrefix + eventId))
            {
                throw new InvalidOperationException($"Event '{eventId}' is not waiting on a choice.");
            }
            var list = args ?? new List<string>();
            switch (eventId)
            {
                case "fallen-tree": ResolveFallenTree(list); break;
                case "flare-up": ResolveFlareUp(list); break;
                case "roll-vortex": ResolveRollVortex(list); break;
                case "fire-tornado": ResolveFireTornadoChoice(list); break;
                default:
                    throw new InvalidOperationException($"Event '{eventId}' has no choice to resolve.");
            }
            ClearRoundModifier(EventChoicePendingPrefix + eventId);
        }

        /// <summary>"The Adversary may immediately place a False Door token on any empty Door
        /// space or a False Window token on any Window on the main board."</summary>
        private void ResolveFallenTree(List<string> args)
        {
            if (args.Count == 0)
            {
                Log("event", "fallen-tree: the Adversary places nothing");
                return;
            }
            if (args.Count != 1)
            {
                throw new InvalidOperationException("Fallen Tree places exactly 1 token (or none).");
            }
            string arg = args[0];
            if (arg.StartsWith("door:", StringComparison.Ordinal))
            {
                string space = RequireEmptyOpenDoor(arg.Substring("door:".Length));
                State.Overlay.DoorStates[space] = DoorState.False;
                Log("event", $"fallen-tree: False Door token on {space}");
            }
            else if (arg.StartsWith("window:", StringComparison.Ordinal))
            {
                string key = RequireWindowEdge(arg.Substring("window:".Length), null);
                State.Overlay.FalseWindows.Add(key);
                Log("event", $"fallen-tree: False Window token on {key}");
            }
            else
            {
                throw new InvalidOperationException(
                    "Fallen Tree takes \"door:<space>\" or \"window:<a>|<b>\".");
            }
        }

        /// <summary>"The Adversary may immediately lower the Charge of up to two Investigators by 1."</summary>
        private void ResolveFlareUp(List<string> args)
        {
            if (args.Count > 2)
            {
                throw new InvalidOperationException("Flare-Up affects at most 2 Investigators.");
            }
            if (args.Distinct().Count() != args.Count)
            {
                throw new InvalidOperationException("Flare-Up affects 2 different Investigators.");
            }
            var chosen = args.Select(Investigator).ToList();
            foreach (var inv in chosen)
            {
                if (inv.Dead || inv.Escaped)
                {
                    throw new InvalidOperationException($"{inv.DefId} is no longer on the board.");
                }
            }
            foreach (var inv in chosen)
            {
                inv.Charge = Math.Max(0, inv.Charge - 1);
                Log("event", $"flare-up: {inv.DefId} loses 1 Charge");
            }
            if (chosen.Count == 0)
            {
                Log("event", "flare-up: the Adversary lowers nobody's Charge");
            }
        }

        /// <summary>"The Adversary may immediately choose a Zone and place Destroyed Door
        /// tokens on up to 2 Doors and an Open Window token on 1 Window in that Zone ... All
        /// Investigators currently in that Zone lose 1 Stamina and 1 Charge. Losing Stamina
        /// in this way does not incur face-down Wounds."</summary>
        private void ResolveRollVortex(List<string> args)
        {
            if (args.Count == 0)
            {
                Log("event", "roll-vortex: the Adversary chooses no Zone");
                return;
            }
            string zone = RequireZone(args[0]);
            var doors = new List<string>();
            var windows = new List<string>();
            foreach (string arg in args.Skip(1))
            {
                if (arg.StartsWith("door:", StringComparison.Ordinal))
                {
                    doors.Add(RequireZoneDoor(arg.Substring("door:".Length), zone));
                }
                else if (arg.StartsWith("window:", StringComparison.Ordinal))
                {
                    windows.Add(RequireWindowEdge(arg.Substring("window:".Length), zone));
                }
                else
                {
                    throw new InvalidOperationException(
                        "Roll Vortex takes a Zone letter then \"door:<space>\" / \"window:<a>|<b>\" entries.");
                }
            }
            if (doors.Count > 2 || doors.Distinct().Count() != doors.Count)
            {
                throw new InvalidOperationException("Roll Vortex Destroys at most 2 different Doors.");
            }
            if (windows.Count > 1)
            {
                throw new InvalidOperationException("Roll Vortex opens at most 1 Window.");
            }

            foreach (string space in doors)
            {
                State.Overlay.DoorStates[space] = DoorState.Destroyed;
                Log("event", $"roll-vortex: Destroyed Door token on {space}");
            }
            foreach (string key in windows)
            {
                State.Overlay.OpenWindows.Add(key);
                Log("event", $"roll-vortex: Open Window token on {key}");
            }
            foreach (var inv in InvestigatorsInZone(zone))
            {
                inv.Stamina = Math.Max(0, inv.Stamina - 1);
                inv.Charge = Math.Max(0, inv.Charge - 1);
                Log("event", $"roll-vortex: {inv.DefId} loses 1 Stamina and 1 Charge in zone {zone}");
            }
        }

        /// <summary>Fire Tornado's 4-6 branch: "The Adversary may place Destroyed Door tokens
        /// on all Doors in a Zone ... They may also flip 1 Destroyed Door token on an
        /// otherwise empty space to the False Door side. All Investigators currently in that
        /// Zone must flip 1 random face-down Wound face-up."</summary>
        private void ResolveFireTornadoChoice(List<string> args)
        {
            if (args.Count == 0)
            {
                Log("event", "fire-tornado: the Adversary chooses no Zone");
                return;
            }
            string zone = RequireZone(args[0]);
            string? flipToFalse = null;
            foreach (string arg in args.Skip(1))
            {
                if (!arg.StartsWith("false:", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Fire Tornado takes a Zone letter and an optional \"false:<space>\".");
                }
                if (flipToFalse != null)
                {
                    throw new InvalidOperationException("Fire Tornado flips at most 1 token to its False Door side.");
                }
                flipToFalse = arg.Substring("false:".Length);
            }

            var zoneDoors = Graph.ZoneSpaces(zone).Where(s => s.Kind == SpaceKind.Door).Select(s => s.Id).ToList();
            if (flipToFalse != null)
            {
                RequireSpaceOnBoard(flipToFalse);
                bool willBeDestroyed = zoneDoors.Contains(flipToFalse) ||
                                       State.Overlay.DoorState(flipToFalse) == DoorState.Destroyed;
                if (!willBeDestroyed)
                {
                    throw new InvalidOperationException(
                        $"'{flipToFalse}' carries no Destroyed Door token to flip to its False Door side.");
                }
                if (OccupiedByAnyFigure(flipToFalse))
                {
                    throw new InvalidOperationException($"'{flipToFalse}' is not an otherwise empty space.");
                }
            }

            foreach (string space in zoneDoors)
            {
                State.Overlay.DoorStates[space] = DoorState.Destroyed;
            }
            Log("event", $"fire-tornado: every Door in zone {zone} is Destroyed ({zoneDoors.Count})");
            if (flipToFalse != null)
            {
                State.Overlay.DoorStates[flipToFalse] = DoorState.False;
                Log("event", $"fire-tornado: {flipToFalse} is flipped to its False Door side");
            }
            foreach (var inv in InvestigatorsInZone(zone))
            {
                FlipRandomFaceDownWound(inv, gainWhenNone: false);
            }
        }

        // ---------- Flashlight line-of-sight limits ----------

        /// <summary>
        /// Apply Misty's 3-space range and Hazy/Downpour's center-line-only restriction to
        /// the placement that was just made, trimming both the placement's own Bright list
        /// and the board overlay.
        ///
        /// "Center line" is the digital reading of the printed template's middle sight line:
        /// the ray from the Investigator along the placement angle, keeping the spaces whose
        /// circle that ray passes through. The Investigator's own space always stays lit.
        /// </summary>
        private void TrimFlashlightLineOfSight(InvestigatorState inv)
        {
            int range = RoundModifier(FlashlightLosRangeKey);
            bool centerOnly = HasRoundModifier(FlashlightCenterLineOnlyKey);
            if (range <= 0 && !centerOnly)
            {
                return;
            }
            var placement = State.Flashlights.LastOrDefault(f => f.InvestigatorId == inv.DefId);
            if (placement == null)
            {
                return;
            }
            var within = range > 0
                ? Graph.DistancesFrom(placement.Space, range, State.Overlay)
                : null;
            var origin = Graph.Space(placement.Space);
            double fx = Math.Cos(placement.AngleRadians);
            double fy = Math.Sin(placement.AngleRadians);
            double radius = Graph.Def.SpaceRadius;

            var kept = new List<string>();
            foreach (string spaceId in placement.BrightSpaces)
            {
                if (spaceId == placement.Space)
                {
                    kept.Add(spaceId);
                    continue;
                }
                if (within != null && !within.ContainsKey(spaceId))
                {
                    continue;
                }
                if (centerOnly)
                {
                    var space = Graph.Space(spaceId);
                    double dx = space.X - origin.X;
                    double dy = space.Y - origin.Y;
                    if (dx * fx + dy * fy < 0 || Math.Abs(dx * -fy + dy * fx) > radius)
                    {
                        continue;
                    }
                }
                kept.Add(spaceId);
            }
            if (kept.Count == placement.BrightSpaces.Count)
            {
                return;
            }
            Log("event", $"{placement.BrightSpaces.Count - kept.Count} space(s) fall outside " +
                         $"{inv.DefId}'s reduced Flashlight line of sight");
            placement.BrightSpaces = kept;
            RecomputeBrightSpaces();
            Log("todo", "the line-of-sight limit is applied after the placement, so anything the full beam " +
                        "already Revealed stays Revealed; the limit belongs inside FlashlightBeam.ComputeBright / " +
                        "Game.PreviewFlashlight (Game.cs), which take no card modifiers.");
        }

        // ---------- Shared helpers ----------

        private List<InvestigatorState> OnBoardInvestigators() =>
            State.Investigators.Where(i => !i.Dead && !i.Escaped).ToList();

        private List<InvestigatorState> InvestigatorsInZone(string zone) =>
            OnBoardInvestigators().Where(i => Graph.Space(i.Space).Zone == zone).ToList();

        /// <summary>Flip 1 random face-down Wound face-up, resolving its text. With no
        /// face-down Wound, either gain one face-down or do nothing, per the calling card.</summary>
        private void FlipRandomFaceDownWound(InvestigatorState inv, bool gainWhenNone)
        {
            var faceDown = inv.Wounds.Where(w => !w.FaceUp).ToList();
            if (faceDown.Count == 0)
            {
                if (gainWhenNone)
                {
                    GainWound(inv, faceUp: false);
                }
                return;
            }
            int index = _rng.Next(faceDown.Count);
            SaveRng();
            FlipWoundFaceUp(inv, faceDown[index]);
        }

        private bool OccupiedByAnyFigure(string spaceId) =>
            State.Investigators.Any(i => !i.Dead && !i.Escaped && i.Space == spaceId) ||
            State.Adversary.Space == spaceId ||
            State.Adversary.Figures.Any(f => f.Alive && f.Space == spaceId);

        private void RequireSpaceOnBoard(string spaceId)
        {
            if (!Graph.HasSpace(spaceId))
            {
                throw new InvalidOperationException($"No space '{spaceId}' on this board.");
            }
        }

        private string RequireZone(string zone)
        {
            if (!Graph.Def.Zones.ContainsKey(zone))
            {
                throw new InvalidOperationException($"'{zone}' is not a Zone on this board.");
            }
            return zone;
        }

        private string RequireEmptyOpenDoor(string spaceId)
        {
            RequireSpaceOnBoard(spaceId);
            if (Graph.Space(spaceId).Kind != SpaceKind.Door)
            {
                throw new InvalidOperationException($"'{spaceId}' is not a Door space.");
            }
            if (State.Overlay.DoorState(spaceId) != DoorState.Open)
            {
                throw new InvalidOperationException($"Door '{spaceId}' already carries a token.");
            }
            if (OccupiedByAnyFigure(spaceId))
            {
                throw new InvalidOperationException($"Door '{spaceId}' is not empty.");
            }
            return spaceId;
        }

        private string RequireZoneDoor(string spaceId, string zone)
        {
            RequireSpaceOnBoard(spaceId);
            var space = Graph.Space(spaceId);
            if (space.Kind != SpaceKind.Door)
            {
                throw new InvalidOperationException($"'{spaceId}' is not a Door space.");
            }
            if (space.Zone != zone)
            {
                throw new InvalidOperationException($"Door '{spaceId}' is not in zone {zone}.");
            }
            return spaceId;
        }

        /// <summary>Validate a "&lt;a&gt;|&lt;b&gt;" Window edge, optionally requiring it to touch a Zone.</summary>
        private string RequireWindowEdge(string pair, string? zone)
        {
            int sep = pair.IndexOf('|');
            if (sep <= 0 || sep == pair.Length - 1)
            {
                throw new InvalidOperationException($"'{pair}' is not a \"<a>|<b>\" Window edge.");
            }
            string a = pair.Substring(0, sep);
            string b = pair.Substring(sep + 1);
            RequireSpaceOnBoard(a);
            RequireSpaceOnBoard(b);
            var edge = Graph.Edge(a, b);
            if (edge == null || edge.Type != EdgeType.Window)
            {
                throw new InvalidOperationException($"There is no Window between '{a}' and '{b}'.");
            }
            if (zone != null && Graph.Space(a).Zone != zone && Graph.Space(b).Zone != zone)
            {
                throw new InvalidOperationException($"The Window {a}|{b} is not in zone {zone}.");
            }
            return BoardOverlay.EdgeKey(a, b);
        }
    }
}
