using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Shared card-effect infrastructure: the Condition cards an Investigator holds, the
    /// board tokens cards drop on spaces, round-scoped modifiers for Event cards, and the
    /// trigger hooks the deck implementations hang their timed effects on.
    ///
    /// The hooks below are declared here and called from Game.cs at the single canonical
    /// moment each one fires. They are C# partial methods: with no implementation the call
    /// compiles away to nothing, so a deck partial (Game.Conditions / Game.Wounds /
    /// Game.Events / ...) can implement exactly the ones it needs and ignore the rest.
    /// </summary>
    public sealed partial class Game
    {
        // ---------- Trigger hooks (implemented by the card-deck partials) ----------

        /// <summary>Start of an Investigator's turn, after MP/Final Action bookkeeping is
        /// reset and the forced carriage rotation is applied (Neurotoxin, Paranoid).</summary>
        partial void OnInvestigatorTurnStart(InvestigatorState inv);

        /// <summary>End of an Investigator's turn, before the Rest Stamina gain, so a card
        /// may still cancel or precede it (Bleeding, Breathless, Dying Battery, Gear Jam).</summary>
        partial void OnInvestigatorTurnEnd(InvestigatorState inv);

        /// <summary>One completed Investigator movement step. <paramref name="from"/> is the
        /// space left and <paramref name="to"/> the space stepped onto; note that a forced
        /// carriage rotation or water float may have moved <paramref name="inv"/> onward
        /// already, so read inv.Space for where they actually stand (Hellfire, Mucus,
        /// Desecrated Ground, Evil Eye).</summary>
        partial void OnInvestigatorMoveStep(InvestigatorState inv, string from, string to);

        /// <summary>A Flashlight has just been placed and its Bright spaces applied, before
        /// the turn ends (Decay's Charge surcharge, Unblinking Eye, Tunnel Vision).</summary>
        partial void OnFlashlightPlaced(InvestigatorState inv);

        /// <summary>The Charge Final Action has just been declared legal and committed to
        /// (<see cref="FinalActionKind.Charge"/> is already set), before the Charge point is
        /// granted or the turn ends. Any cost a card attaches to *taking* Charge (Gear Jam's
        /// Stamina) must be paid here rather than in <see cref="OnInvestigatorTurnEnd"/>: that
        /// fanout also runs Wound/Condition effects that can drain the same resource first
        /// (Breathless's own end-of-turn Stamina loss), and paying late can throw a card's
        /// own cost out of EndTurn instead of refusing the Action up front.</summary>
        partial void OnChargeDeclared(InvestigatorState inv);

        /// <summary>End of a round, before the round counter advances and while the round's
        /// Bright spaces are still lit (token expiry, Neurotoxin's 2-Wound discard).</summary>
        partial void OnRoundEnd();

        /// <summary>A Wound card has just become face-up, either freshly gained face-up or
        /// flipped up later; the deck partial resolves the card's immediate text here
        /// (Discharge, Fear, Fumble, Spasm).</summary>
        partial void ResolveWoundFaceUp(InvestigatorState inv, WoundInstance wound);

        /// <summary>
        /// Collect every reason this Investigator may not take <paramref name="actionKey"/>
        /// (one of the Action* consts below) right now. Implementors append one
        /// human-readable clause per blocking card; an empty list means the action is legal.
        /// Called by <see cref="RequireActionAllowed"/> at the very top of each gated action,
        /// before anything has been validated or mutated, so implementations must be free of
        /// side effects — a blocked action must leave the state untouched.
        /// </summary>
        partial void CollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers);

        /// <summary>A Sprint die has just been rolled and the result may still be changed.
        /// <paramref name="rollBox"/> is a single-element mutable cell holding the roll (Torn
        /// Ligament's -1, Paranoid's halving, Lucky Dice's reroll).</summary>
        partial void ModifySprintRoll(InvestigatorState inv, List<int> rollBox);

        /// <summary>The Bright set a Flashlight placement is about to produce, still mutable
        /// and not yet recorded: trim it here and nothing outside the reduced beam is ever
        /// lit or Revealed (Misty's range limit, Hazy/Downpour/Tunnel Vision/Bufotoxin's
        /// center-line limits).</summary>
        partial void TrimFlashlightBright(InvestigatorState inv, double angleRadians, HashSet<string> bright);

        /// <summary>A Wound card has been drawn for <paramref name="inv"/> and is about to
        /// enter their Wound slots; <paramref name="origin"/> is one of the WoundFrom* consts.
        /// Implementors may still flip <paramref name="wound"/> face-up (Punctured Lung) or
        /// inflict further Wounds (Mauled).</summary>
        partial void OnWoundGained(InvestigatorState inv, WoundInstance wound, string origin);

        /// <summary>An Investigator has just died, before the Adversary's win condition is
        /// checked. Implemented by Game.Spirits.cs, which offers the Spirit card when the kill
        /// does not end the game (see <see cref="AdoptSpirit"/>).</summary>
        partial void OnInvestigatorDeath(InvestigatorState inv);

        /// <summary>Start of the Adversary turn, before the per-adversary Begin hooks run
        /// (Possessed, Bufotoxin's flip window).</summary>
        partial void OnAdversaryTurnStart();

        /// <summary>End of the Adversary turn, before the cooldowns advance and before the
        /// round ends — this round's Flashlights are still on the board here (Unblinking Eye,
        /// Burning Heart's end condition, Shriveled Hand's follow-up).</summary>
        partial void OnAdversaryTurnEnd();

        /// <summary>Start of a round, after <see cref="GameState.RoundModifiers"/> has been
        /// cleared and the round's Event card resolved, so a card armed on an earlier round
        /// can still write a modifier that covers this whole round (the Butcher's Decay).</summary>
        partial void OnRoundStart();

        /// <summary>One completed Adversary movement step, after the figure has arrived and the
        /// cost has been paid, so <see cref="AdversaryState.Space"/> is already the new space
        /// (Brielle's Can tokens flipping to their Noise side).</summary>
        partial void OnAdversaryMoveStep(string from, string to);

        /// <summary>An Adversary figure has just been Revealed. <paramref name="figureId"/> is
        /// "main" for the Adversary's own standee, or the extra figure's id for a Cultist
        /// (Mada's Coin token).</summary>
        partial void OnAdversaryRevealed(string figureId);

        /// <summary>The MP one Investigator Move step is about to cost, still mutable and not
        /// yet charged. <paramref name="costBox"/> is a single-element cell holding MapGraph's
        /// printed cost; Game.MoveStep floors the result at 1. Implementations must be free of
        /// side effects — the step may still be refused for want of MP — so an allowance an
        /// adjustment draws on is spent from <see cref="OnInvestigatorMoveStep"/> instead, once
        /// the step has actually happened (Dylan's Dark-as-Dim discount).</summary>
        partial void AdjustMoveCost(InvestigatorState inv, string from, string to, List<int> costBox);

        // ---------- Action keys (RequireActionAllowed / CollectActionBlockers) ----------

        /// <summary>The Sprint core action.</summary>
        public const string ActionSprint = "sprint";
        /// <summary>The Rest core action.</summary>
        public const string ActionRest = "rest";
        /// <summary>The Charge Final Action.</summary>
        public const string ActionCharge = "charge";
        /// <summary>The Place Flashlight Final Action.</summary>
        public const string ActionPlaceFlashlight = "place-flashlight";
        /// <summary>Locking a Door.</summary>
        public const string ActionLockDoor = "lock-door";
        /// <summary>Opening (or un-Locking) a Door.</summary>
        public const string ActionOpenDoor = "open-door";
        /// <summary>Trading an Item or an Evidence token, in either direction.</summary>
        public const string ActionTrade = "trade";
        /// <summary>Picking up a Point of Interest token.</summary>
        public const string ActionPickUpPoi = "pickup-poi";
        /// <summary>Any Involved Action Final Action (generic, Evidence turn-in, objectives).</summary>
        public const string ActionInvolved = "involved";
        /// <summary>Using an Item / Medical Item / Cursed Item card.</summary>
        public const string ActionUseItem = "use-item";
        /// <summary>One Move step.</summary>
        public const string ActionMove = "move";
        /// <summary>Using your Investigator's printed Minor or Major Ability (Disoriented).</summary>
        public const string ActionUseAbility = "use-ability";

        // ---------- Wound origin tags (GainWound / OnWoundGained) ----------

        /// <summary>The Wound was incurred while resolving a Sprint (its Stamina cost, Cold
        /// Front's shifted icons, Pyrocumulus' D6). Punctured Lung flips these face-up.</summary>
        public const string WoundFromSprint = "sprint";
        /// <summary>The Wound came from the Adversary (an Attack, Bloodletting, an Ability).
        /// Mauled adds 1 extra face-down Wound to these.</summary>
        public const string WoundFromAdversary = "adversary";
        /// <summary>The Wound came from choosing to keep moving through a Window.</summary>
        public const string WoundFromWindow = "window";
        /// <summary>The Wound came from crossing a Wound icon on the Stamina track, outside
        /// a Sprint.</summary>
        public const string WoundFromStaminaTrack = "stamina";

        // ---------- Round-modifier keys owned by the shared framework ----------

        /// <summary>Prefix + Investigator def id: their next Involved Action counts as an
        /// Interact Action and does not end their turn (Spare Tools). Consumed by
        /// Game.EndTurn.</summary>
        public const string InvolvedAsInteractPrefix = "involved-as-interact:";

        /// <summary>Prefix + Investigator def id: they already spent an Involved Action this
        /// turn, so Spare Tools' "you may not take another Involved Action this turn" applies.</summary>
        public const string InvolvedActionUsedPrefix = "involved-action-used:";

        /// <summary>Prefix + Investigator def id: 1 Charge of their next Flashlight placement
        /// is paid from somewhere else (Spare Batteries' Supply token). Consumed by
        /// Game.PlaceFlashlight.</summary>
        public const string FlashlightChargeWaiverPrefix = "flashlight-charge-waiver:";

        /// <summary>Set once any Investigator has finished a turn this round holding more
        /// Charge or Stamina than they started it with (the Cult's Burning Heart end
        /// condition: "until no Investigators gain Charge or Lungs ... during their turns").</summary>
        public const string ResourceGainedKey = "charge-or-stamina-gained";

        // ---------- Conditions ----------

        /// <summary>True when this Investigator already holds that Condition card.</summary>
        public bool HasCondition(InvestigatorState inv, string conditionId) =>
            inv.Conditions.Contains(conditionId);

        /// <summary>
        /// Give an Investigator a Condition card. An Investigator may hold at most 1 copy of
        /// each Condition: gaining a duplicate has no effect and returns false. Cards whose
        /// text replaces the duplicate with something else (Bleeding's face-up Wound,
        /// Darkness' lost Charge) check <see cref="HasCondition"/> first and apply their own
        /// substitute instead of calling this.
        /// </summary>
        public bool GainCondition(InvestigatorState inv, string conditionId)
        {
            RequireConditionCard(conditionId);
            if (inv.Conditions.Contains(conditionId))
            {
                Log("condition", $"{inv.DefId} already has {conditionId} (no effect)");
                return false;
            }
            inv.Conditions.Add(conditionId);
            Log("condition", $"{inv.DefId} gained {conditionId}");
            return true;
        }

        /// <summary>Discard a Condition card. Returns false when it was not held.</summary>
        public bool DiscardCondition(InvestigatorState inv, string conditionId)
        {
            RequireConditionCard(conditionId);
            if (!inv.Conditions.Remove(conditionId))
            {
                return false;
            }
            Log("condition", $"{inv.DefId} discarded {conditionId}");
            return true;
        }

        private void RequireConditionCard(string conditionId)
        {
            if (!Db.Deck("condition").Any(c => c.Id == conditionId))
            {
                throw new InvalidOperationException($"Unknown Condition card '{conditionId}'.");
            }
        }

        // ---------- Board tokens ----------

        /// <summary>
        /// Place (or move) a card token on a space. <paramref name="tokenId"/> is an instance
        /// id whose kind prefix is how the owning card finds it again — "hellfire-1",
        /// "mucus-2", "desecrated-ground-1", "hatchling-1", "evil-eye-2".
        /// </summary>
        public void PlaceBoardToken(string tokenId, string spaceId)
        {
            Graph.Space(spaceId); // validates the space exists on this board
            State.BoardTokens[tokenId] = spaceId;
        }

        /// <summary>Remove one token instance. Returns false when it was not on the board.</summary>
        public bool RemoveBoardToken(string tokenId) => State.BoardTokens.Remove(tokenId);

        /// <summary>Remove every token whose id starts with <paramref name="prefix"/>. Returns how many went away.</summary>
        public int RemoveBoardTokens(string prefix)
        {
            var doomed = BoardTokenIds(prefix);
            foreach (string id in doomed)
            {
                State.BoardTokens.Remove(id);
            }
            return doomed.Count;
        }

        /// <summary>The space a token instance sits on, or null when it is not on the board.</summary>
        public string? BoardTokenSpace(string tokenId) =>
            State.BoardTokens.TryGetValue(tokenId, out string space) ? space : null;

        /// <summary>Token instance ids starting with <paramref name="prefix"/>, in id order.</summary>
        public List<string> BoardTokenIds(string prefix) =>
            State.BoardTokens.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal).ToList();

        /// <summary>Spaces holding a token of this kind, deduplicated and in space-id order.</summary>
        public List<string> BoardTokenSpaces(string prefix) =>
            BoardTokenIds(prefix).Select(id => State.BoardTokens[id]).Distinct()
                .OrderBy(s => s, StringComparer.Ordinal).ToList();

        /// <summary>True when a token of this kind is on <paramref name="spaceId"/>.</summary>
        public bool HasBoardTokenAt(string prefix, string spaceId) =>
            BoardTokenIds(prefix).Any(id => State.BoardTokens[id] == spaceId);

        /// <summary>Every token instance id on <paramref name="spaceId"/>, in id order.</summary>
        public List<string> BoardTokensAt(string spaceId) =>
            State.BoardTokens.Where(kv => kv.Value == spaceId).Select(kv => kv.Key)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();

        // ---------- Round-scoped modifiers ----------

        /// <summary>The value of a round modifier, or 0 when unset.</summary>
        public int RoundModifier(string key) =>
            State.RoundModifiers.TryGetValue(key, out int value) ? value : 0;

        public bool HasRoundModifier(string key) => State.RoundModifiers.ContainsKey(key);

        /// <summary>Set a modifier for the rest of this round. It is cleared when the next round begins.</summary>
        public void SetRoundModifier(string key, int value) => State.RoundModifiers[key] = value;

        /// <summary>Add to a round modifier, treating an unset key as 0. Returns the new value.</summary>
        public int AddRoundModifier(string key, int amount)
        {
            int value = RoundModifier(key) + amount;
            State.RoundModifiers[key] = value;
            return value;
        }

        public bool ClearRoundModifier(string key) => State.RoundModifiers.Remove(key);

        // ---------- Flashlight line-of-sight trims (shared by the TrimFlashlightBright decks) ----------

        /// <summary>
        /// Keep only the Bright spaces within <paramref name="range"/> spaces of
        /// <paramref name="fromSpace"/>. Returns how many spaces were dropped.
        /// </summary>
        private int TrimBrightToRange(string fromSpace, HashSet<string> bright, int range)
        {
            if (range <= 0)
            {
                return 0;
            }
            var within = Graph.DistancesFrom(fromSpace, range, State.Overlay);
            return bright.RemoveWhere(id => id != fromSpace && !within.ContainsKey(id));
        }

        /// <summary>
        /// Keep only the Bright spaces covered by the flashlight template's central sight
        /// lines. "Center line" is the digital reading of the printed template's middle sight
        /// line: the ray from <paramref name="fromSpace"/> along <paramref name="angleRadians"/>,
        /// keeping the spaces whose circle that ray passes through. <paramref name="lines"/>
        /// widens the corridor to that many parallel sight lines (1 = the middle line only,
        /// 3 = the middle line plus one to either side). The Investigator's own space always
        /// stays lit. Returns how many spaces were dropped.
        /// </summary>
        private int TrimBrightToCenterLines(string fromSpace, double angleRadians, HashSet<string> bright, int lines)
        {
            var origin = Graph.Space(fromSpace);
            double fx = Math.Cos(angleRadians);
            double fy = Math.Sin(angleRadians);
            double halfWidth = Graph.Def.SpaceRadius * lines;
            return bright.RemoveWhere(id =>
            {
                if (id == fromSpace)
                {
                    return false;
                }
                var space = Graph.Space(id);
                double dx = space.X - origin.X;
                double dy = space.Y - origin.Y;
                return dx * fx + dy * fy < 0 || Math.Abs(dx * -fy + dy * fx) > halfWidth;
            });
        }

        // ---------- Wound face-up plumbing ----------

        /// <summary>Flip a face-down Wound face-up and resolve its immediate text. No-op when
        /// the Wound is already face-up.</summary>
        public void FlipWoundFaceUp(InvestigatorState inv, WoundInstance wound)
        {
            if (wound.FaceUp)
            {
                return;
            }
            wound.FaceUp = true;
            Log("wound", $"{inv.DefId} flipped {wound.CardId} face-up");
            ResolveWoundFaceUp(inv, wound);
        }
    }
}
