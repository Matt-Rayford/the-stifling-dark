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

        /// <summary>End of a round, before the round counter advances and while the round's
        /// Bright spaces are still lit (token expiry, Neurotoxin's 2-Wound discard).</summary>
        partial void OnRoundEnd();

        /// <summary>A Wound card has just become face-up, either freshly gained face-up or
        /// flipped up later; the deck partial resolves the card's immediate text here
        /// (Discharge, Fear, Fumble, Spasm).</summary>
        partial void ResolveWoundFaceUp(InvestigatorState inv, WoundInstance wound);

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
