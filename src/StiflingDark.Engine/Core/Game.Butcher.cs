using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The Butcher of Manchac Swamp: the Stalk core action and Spine Chill economy, the 3
    /// Attack cards and 6 Ability cards (game-data/cards/adversary-cards.json, owner
    /// "butcher"), and the Grave banish objective (game-data/cards/escape-cards.json
    /// "the-grave" + the "butcher-banish" player aid).
    ///
    /// State reuse note: AdversaryState has no Butcher-specific fields beyond Counters /
    /// SpineChill / ShadowTokens, and this file may not add any (see Game.Adversary.cs /
    /// GameState.cs — existing files). Everything below is modeled with those three
    /// generic dictionaries plus ObjectiveState.Tokens and InvestigatorState.Items:
    ///   Counters["stalk"]                  Stalk track (0-8).
    ///   Counters["escalating-terror-pending"]  1 while Escalating Terror is armed.
    ///   Counters["decay-active-round"]     Round Decay's Flashlight surcharge applies (logged only; see below).
    ///   Counters["vengeful-darkness-armed"] 1 while Vengeful Darkness is waiting to resolve at next turn start.
    ///   Counters["vengeful-darkness-supply"] Supply tokens sitting on the Vengeful Darkness card.
    ///   Counters["burning-until"]          Round number at which the dug-up Grave is fully burned.
    ///   Counters["hook-used-round"]        Round The Hook was last attempted (once per round for the team).
    ///   Counters["frayed-ropes-uses"]      Times Frayed Ropes has been used (max 3).
    ///   Counters["frayed-ropes-pending"]   1 while the Adversary owes a forced Shadow placement.
    ///   ShadowTokens["main"]               The Butcher's own Shadow token (existing convention).
    ///   ShadowTokens["frayed"]             The face-down Shadow token forced by Frayed Ropes.
    ///   BoardTokens["evil-eye-1"/"-2"]     Evil Eye token spaces (shared card-token API, see
    ///                                      Game.Effects.cs; removed in BeginButcherTurn).
    ///   Objective.Tokens["grave-actual"/"grave-decoy"]  The Grave banish tokens (explicitly requested in the brief).
    /// </summary>
    public sealed partial class Game
    {
        // ---------- Turn start ----------

        partial void BeginButcherTurn()
        {
            var adv = State.Adversary;

            // adversaries.json startOfTurn: "Remove all Noise AND Shadow tokens from the board."
            adv.NoiseTokens.Clear();
            adv.ShadowTokens.Clear();
            // Evil Eye: "Remove all Evil Eye tokens at the beginning of your next turn."
            RemoveBoardTokens("evil-eye");

            // Spine Chill tokens last until the end of the NEXT round: a token given in round R
            // expires once a turn begins in round R+2 (the Investigator was not re-Stalked the
            // following round, so the token "returns to The Butcher").
            foreach (string invId in adv.SpineChill
                .Where(kv => State.Round >= kv.Value + 2)
                .Select(kv => kv.Key)
                .ToList())
            {
                adv.SpineChill.Remove(invId);
                Log("adversary", $"{invId}'s Spine Chill token returns to The Butcher (not re-Stalked in time)");
            }

            // Vengeful Darkness: "place a Supply token on this card for each Flashlight on the
            // board at the beginning of your next turn." Resolves here, one turn after it was played.
            if (adv.Counters.TryGetValue("vengeful-darkness-armed", out int armed) && armed == 1)
            {
                adv.Counters.Remove("vengeful-darkness-armed");
                int flashlights = State.Flashlights.Count;
                int supply = (adv.Counters.TryGetValue("vengeful-darkness-supply", out int existing) ? existing : 0) + flashlights;
                Log("adversary", $"Vengeful Darkness places {flashlights} Supply token(s) ({supply} on the card)");
                // "Remove 2 Supply tokens to gain 1 Stalk" — Supply persists through Cooldown with
                // no other spend timing given, so we convert automatically as soon as 2+ accrue.
                while (supply >= 2)
                {
                    supply -= 2;
                    AddStalk(1);
                    Log("adversary", "Vengeful Darkness converts 2 Supply into 1 Stalk");
                }
                adv.Counters["vengeful-darkness-supply"] = supply;
            }

        }

        /// <summary>
        /// Decay: "Using a Flashlight costs 1 extra Charge next round." The Butcher's own turn
        /// runs at the *end* of a round, long after that round's Flashlights were placed, so the
        /// surcharge is written from the shared round-start hook instead — after BeginRound has
        /// cleared the round's modifiers and resolved its Event card, so a Foggy in the same
        /// round stacks with it rather than being overwritten.
        /// </summary>
        partial void ButcherOnRoundStart()
        {
            if (State.Adversary.Counters.TryGetValue("decay-active-round", out int decayRound) &&
                decayRound == State.Round)
            {
                int total = AddRoundModifier(FlashlightChargeSurchargeKey, 1);
                Log("adversary", $"Decay: Flashlight placements cost {total} extra Charge this round");
            }
        }

        // ---------- Stalk (core action) ----------

        /// <summary>
        /// Stalk core action: once per Adversary turn, target any number of Investigators within
        /// 8 spaces (line of sight is not yet modeled — see the TODO below), place The Butcher's
        /// Shadow token on his current space, then resolve Spine Chill per target.
        /// </summary>
        public void ButcherStalk(List<string> targetInvIds)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            if (adv.Revealed)
            {
                throw new InvalidOperationException("The Butcher cannot Stalk while Revealed.");
            }
            if (targetInvIds == null || targetInvIds.Count == 0)
            {
                throw new InvalidOperationException("Stalk requires at least one target.");
            }
            if (!adv.ActionsUsed.Add("stalk"))
            {
                throw new InvalidOperationException("Stalk was already used this turn.");
            }

            var withinRange = Graph.DistancesFrom(adv.Space, 8, State.Overlay);
            var from = Graph.Space(adv.Space);
            var targets = new List<InvestigatorState>();
            foreach (string id in targetInvIds)
            {
                var inv = Investigator(id);
                if (!withinRange.ContainsKey(inv.Space))
                {
                    throw new InvalidOperationException($"{id} is not within 8 spaces of The Butcher to Stalk.");
                }
                var to = Graph.Space(inv.Space);
                if (_losBlocker.Blocks(from.X, from.Y, to.X, to.Y))
                {
                    throw new InvalidOperationException($"{id} is not in The Butcher's line of sight.");
                }
                targets.Add(inv);
            }

            adv.ShadowTokens["main"] = adv.Space;

            int gained = 0;
            foreach (var inv in targets)
            {
                if (adv.SpineChill.Remove(inv.DefId))
                {
                    gained += 1;
                    Log("adversary", $"{inv.DefId} discards their Spine Chill token; The Butcher's Stalk track rises");
                }
                else
                {
                    adv.SpineChill[inv.DefId] = State.Round;
                    Log("adversary", $"{inv.DefId} is given a Spine Chill token");
                }
            }
            if (gained > 0)
            {
                GainStalkFromStalkAction(gained);
            }
        }

        // ---------- Stalk-track helpers ----------

        private void SpendStalk(int amount)
        {
            var adv = State.Adversary;
            int current = adv.Counters.TryGetValue("stalk", out int s) ? s : 0;
            if (current < amount)
            {
                throw new InvalidOperationException("Not enough Stalk to use this card.");
            }
            adv.Counters["stalk"] = current - amount;
        }

        private void AddStalk(int amount)
        {
            var adv = State.Adversary;
            int current = adv.Counters.TryGetValue("stalk", out int s) ? s : 0;
            adv.Counters["stalk"] = Math.Min(8, current + amount);
        }

        /// <summary>Escalating Terror doubles only Stalk gained from the Stalk Action itself.</summary>
        private void GainStalkFromStalkAction(int amount)
        {
            var adv = State.Adversary;
            if (adv.Counters.TryGetValue("escalating-terror-pending", out int pending) && pending == 1)
            {
                amount *= 2;
                adv.Counters.Remove("escalating-terror-pending");
                Log("adversary", "Escalating Terror doubles the Stalk gained");
            }
            AddStalk(amount);
        }

        private void RequireAdjacentToButcher(InvestigatorState inv)
        {
            if (inv.Dead || inv.Escaped)
            {
                throw new InvalidOperationException($"{inv.DefId} cannot be targeted.");
            }
            if (!AdversaryAdjacentTo(State.Adversary.Space, inv.Space))
            {
                throw new InvalidOperationException($"{inv.DefId} is not adjacent to The Butcher.");
            }
        }

        // ---------- Card dispatch ----------

        partial void ApplyButcherCard(string cardId, List<string> targets)
        {
            switch (cardId)
            {
                case "decay": ApplyDecay(); break;
                case "disturbed-presence": ApplyDisturbedPresence(targets); break;
                case "escalating-terror": ApplyEscalatingTerror(); break;
                case "evil-eye": ApplyEvilEye(targets); break;
                case "sinister-gaze": ApplySinisterGaze(targets); break;
                case "vengeful-darkness": ApplyVengefulDarkness(); break;
                case "eviscerate": ApplyEviscerate(targets); break;
                case "onslaught": ApplyOnslaught(targets); break;
                case "rend": ApplyRend(targets); break;
                default:
                    throw new InvalidOperationException($"'{cardId}' is not a Butcher Attack or Ability card.");
            }
        }

        // ---------- Abilities ----------

        /// <summary>"Using a Flashlight costs 1 extra Charge next round."</summary>
        private void ApplyDecay()
        {
            SpendStalk(1);
            State.Adversary.Counters["decay-active-round"] = State.Round + 1;
            Log("adversary", $"Decay: Flashlight placements cost 1 extra Charge in round {State.Round + 1}");
        }

        /// <summary>
        /// "If you end your turn within 4 spaces of 1+ Investigators, you may reduce each of
        /// their Lungs by 1 (no Wound). If you remove 2+ Lungs at once, gain 1 Stalk."
        /// Resolved immediately against the chosen targets rather than waiting for a genuine
        /// end-of-turn hook, since the Adversary turn framework has none to offer here.
        /// </summary>
        private void ApplyDisturbedPresence(List<string> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                throw new InvalidOperationException("Disturbed Presence requires at least one target.");
            }
            var withinRange = Graph.DistancesFrom(State.Adversary.Space, 4, State.Overlay);
            var affected = new List<InvestigatorState>();
            foreach (string id in targets)
            {
                var inv = Investigator(id);
                if (!withinRange.ContainsKey(inv.Space))
                {
                    throw new InvalidOperationException($"{id} is not within 4 spaces of The Butcher.");
                }
                inv.Stamina = Math.Max(0, inv.Stamina - 1); // loses Lungs without the usual Wound-icon-space check
                affected.Add(inv);
                Log("adversary", $"Disturbed Presence drains 1 Lung from {id}");
            }
            if (affected.Count >= 2)
            {
                AddStalk(1);
                Log("adversary", "Disturbed Presence drained 2+ Lungs at once: gain 1 Stalk");
            }
        }

        /// <summary>"The next time you gain 1+ Stalk from the Stalk Action, double the amount gained."</summary>
        private void ApplyEscalatingTerror()
        {
            State.Adversary.Counters["escalating-terror-pending"] = 1;
            Log("adversary", "Escalating Terror is armed for the next Stalk gain");
        }

        /// <summary>
        /// "Place 2 Evil Eye tokens on any General spaces. If an Investigator Moves onto or ends
        /// their turn on one, gain 1 Stalk and remove it. Remove all Evil Eye tokens at the
        /// beginning of your next turn." The tokens live in the shared card-token map
        /// (Game.PlaceBoardToken) and BeginButcherTurn removes them, matching the card's own
        /// cleanup timing; the trigger half rides the Butcher's sub-hooks off
        /// OnInvestigatorMoveStep / OnInvestigatorTurnEnd (see <see cref="TripEvilEye"/>).
        /// </summary>
        private void ApplyEvilEye(List<string> targets)
        {
            if (targets == null || targets.Count != 2 || targets[0] == targets[1])
            {
                throw new InvalidOperationException("Evil Eye needs 2 different General spaces.");
            }
            foreach (string space in targets)
            {
                if (Graph.Space(space).Kind != SpaceKind.Normal)
                {
                    throw new InvalidOperationException($"'{space}' is not a General space.");
                }
            }
            PlaceBoardToken("evil-eye-1", targets[0]);
            PlaceBoardToken("evil-eye-2", targets[1]);
            Log("adversary", $"Evil Eye tokens placed on {targets[0]} and {targets[1]}");
        }

        partial void ButcherOnInvestigatorMoveStep(InvestigatorState inv, string from, string to)
        {
            // inv.Space rather than `to`: a carriage rotation or water float may have carried
            // them onward after the step, and the token only triggers where they end up.
            TripEvilEye(inv);
        }

        partial void ButcherOnInvestigatorTurnEnd(InvestigatorState inv)
        {
            TripEvilEye(inv);
        }

        /// <summary>"If an Investigator Moves onto or ends their turn on an Evil Eye token, gain
        /// 1 Stalk and remove the token." Not Stalk from the Stalk Action, so Escalating Terror
        /// does not double it.</summary>
        private void TripEvilEye(InvestigatorState inv)
        {
            string? token = BoardTokenIds("evil-eye").FirstOrDefault(id => State.BoardTokens[id] == inv.Space);
            if (token == null)
            {
                return;
            }
            RemoveBoardToken(token);
            AddStalk(1);
            Log("adversary", $"{inv.DefId} stepped on {token}: The Butcher gains 1 Stalk and removes it");
        }

        /// <summary>
        /// "Give 2 different Investigators 1 of Choking Fear/Darkness each." Each target is
        /// either a bare Investigator def id (Choking Fear, the default) or
        /// "&lt;investigatorId&gt;:choking-fear" / "&lt;investigatorId&gt;:darkness" to name the
        /// Condition the Adversary picks for that Investigator.
        /// </summary>
        private void ApplySinisterGaze(List<string> targets)
        {
            if (targets == null || targets.Count != 2)
            {
                throw new InvalidOperationException("Sinister Gaze needs 2 different Investigators.");
            }
            var picks = targets.Select(SplitSinisterGazeTarget).ToList();
            if (picks[0].Inv.DefId == picks[1].Inv.DefId)
            {
                throw new InvalidOperationException("Sinister Gaze needs 2 different Investigators.");
            }
            SpendStalk(1);
            foreach (var (inv, conditionId) in picks)
            {
                GrantConditionWithSubstitution(inv, conditionId);
            }
        }

        private (InvestigatorState Inv, string ConditionId) SplitSinisterGazeTarget(string target)
        {
            int sep = target.IndexOf(':');
            string invId = sep < 0 ? target : target.Substring(0, sep);
            string conditionId = sep < 0 ? "choking-fear" : target.Substring(sep + 1);
            if (conditionId != "choking-fear" && conditionId != "darkness")
            {
                throw new InvalidOperationException("Sinister Gaze gives Choking Fear or Darkness.");
            }
            return (Investigator(invId), conditionId);
        }

        /// <summary>
        /// "Place a Supply token on this card for each Flashlight on the board at the beginning
        /// of your next turn ... Remove 2 Supply tokens to gain 1 Stalk." Playing the card only
        /// arms it; BeginButcherTurn resolves the Supply gain (and its conversion) one turn later.
        /// </summary>
        private void ApplyVengefulDarkness()
        {
            State.Adversary.Counters["vengeful-darkness-armed"] = 1;
            Log("adversary", "Vengeful Darkness is armed for the start of next turn");
        }

        // ---------- Attacks ----------

        /// <summary>"When adjacent, give Bleeding. If already Bleeding, give a face-up Wound
        /// instead." That is exactly Bleeding's printed duplicate rider, so the shared
        /// GrantConditionWithSubstitution handles both branches.</summary>
        private void ApplyEviscerate(List<string> targets)
        {
            if (targets == null || targets.Count != 1)
            {
                throw new InvalidOperationException("Eviscerate targets exactly 1 Investigator.");
            }
            var target = Investigator(targets[0]);
            RequireAdjacentToButcher(target);
            SpendStalk(1);
            State.Adversary.ShadowTokens["main"] = State.Adversary.Space;
            GrantConditionWithSubstitution(target, "bleeding");
        }

        /// <summary>
        /// "When adjacent: if within 4 of another Investigator, gain 2 extra footprint this turn
        /// and give them a face-down Wound + Mauled; otherwise give 2 face-up Wounds. Repeat for 0
        /// Stalk on any number of different Investigators." One PlayAdversaryCard call resolves
        /// every repetition via the targets list; the Stalk cost is paid once for the whole card.
        /// </summary>
        private void ApplyOnslaught(List<string> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                throw new InvalidOperationException("Onslaught targets at least 1 Investigator.");
            }
            if (targets.Distinct().Count() != targets.Count)
            {
                throw new InvalidOperationException("Onslaught cannot Attack the same Investigator twice.");
            }
            var invs = targets.Select(Investigator).ToList();
            foreach (var inv in invs)
            {
                RequireAdjacentToButcher(inv);
            }
            SpendStalk(1);
            State.Adversary.ShadowTokens["main"] = State.Adversary.Space;
            foreach (var inv in invs)
            {
                bool nearAnother = State.Investigators.Any(other => other != inv && !other.Dead && !other.Escaped &&
                    Graph.DistancesFrom(inv.Space, 4, State.Overlay).ContainsKey(other.Space));
                if (nearAnother)
                {
                    State.Adversary.MpRemaining += 2;
                    DealAttackWounds(inv.DefId, 1, faceUp: false);
                    GrantConditionWithSubstitution(inv, "mauled");
                }
                else
                {
                    DealAttackWounds(inv.DefId, 2, faceUp: true);
                }
            }
        }

        /// <summary>"When adjacent, draw 3 face-up Wounds. Give them 1 and Mauled, discard the rest."</summary>
        private void ApplyRend(List<string> targets)
        {
            if (targets == null || targets.Count != 1)
            {
                throw new InvalidOperationException("Rend targets exactly 1 Investigator.");
            }
            var target = Investigator(targets[0]);
            RequireAdjacentToButcher(target);
            SpendStalk(1);
            State.Adversary.ShadowTokens["main"] = State.Adversary.Space;
            GainWound(target, faceUp: true);
            Draw(State.WoundDeck, "wound"); // drawn only to be discarded
            Draw(State.WoundDeck, "wound"); // drawn only to be discarded
            GrantConditionWithSubstitution(target, "mauled");
        }

        // ---------- Grave banish ----------

        partial void SetupGraveBanish()
        {
            _banishSetupDone = true;
        }

        /// <summary>
        /// The Adversary's placement step for the-grave: the real Grave (face-up on the
        /// mini-map, within 10 of any Investigator) and its main-board decoy (face-down, within
        /// 3 of the real one). Stored on Objective.Tokens as explicitly directed by the brief.
        /// </summary>
        public void PlaceGrave(string actualSpace, string decoySpace)
        {
            if (State.Objective.SelectedEscapeCard != "the-grave")
            {
                throw new InvalidOperationException("The Grave is not the selected Objective.");
            }
            if (State.Objective.Tokens.ContainsKey("grave-actual"))
            {
                throw new InvalidOperationException("The Grave tokens have already been placed.");
            }
            Graph.Space(actualSpace);
            Graph.Space(decoySpace);
            bool nearAnInvestigator = State.Investigators.Any(inv =>
                Graph.DistancesFrom(actualSpace, 10, State.Overlay).ContainsKey(inv.Space));
            if (!nearAnInvestigator)
            {
                throw new InvalidOperationException("The actual Grave must be placed within 10 spaces of an Investigator.");
            }
            if (!Graph.DistancesFrom(actualSpace, 3, State.Overlay).ContainsKey(decoySpace))
            {
                throw new InvalidOperationException("The decoy Grave must be placed within 3 spaces of the actual Grave.");
            }
            State.Objective.Tokens["grave-actual"] = actualSpace;
            State.Objective.Tokens["grave-decoy"] = decoySpace;
            Log("adversary", $"places the Grave at {actualSpace} (decoy at {decoySpace})");
        }

        /// <summary>Involved Action on the revealed (Bright) actual Grave: grants the Objective
        /// items and starts the Burning token's 2-round countdown.</summary>
        public void DigUpGrave()
        {
            var inv = BeginInvolvedAction();
            if (!State.Objective.Tokens.TryGetValue("grave-actual", out string graveSpace))
            {
                throw new InvalidOperationException("The Grave has not been placed yet.");
            }
            if (inv.Space != graveSpace)
            {
                throw new InvalidOperationException("You must be on the Grave's space.");
            }
            if (!IsBright(graveSpace))
            {
                throw new InvalidOperationException("The Grave must be Revealed (its space made Bright) first.");
            }
            if (State.Adversary.Counters.ContainsKey("burning-until"))
            {
                throw new InvalidOperationException("The Grave has already been dug up.");
            }
            inv.Items.Add("the-hook");
            inv.Items.Add("frayed-ropes");
            State.Adversary.Counters["burning-until"] = State.Round + 2;
            Log("objective", $"{inv.DefId} digs up the Grave and takes The Hook and Frayed Ropes; it is now Burning");
            FinishInvolvedAction(inv);
        }

        /// <summary>Involved Action, once per round for the whole team: guess an adjacent space.
        /// Correct while the Grave is burned out banishes The Butcher; wrong just says so.</summary>
        public void UseTheHook(string chosenSpace)
        {
            var inv = BeginInvolvedAction();
            if (!inv.Items.Contains("the-hook"))
            {
                throw new InvalidOperationException($"{inv.DefId} is not carrying The Hook.");
            }
            if (!State.Adversary.Counters.TryGetValue("burning-until", out int until) || State.Round < until)
            {
                throw new InvalidOperationException("The Grave has not finished burning yet.");
            }
            var adv = State.Adversary;
            if (adv.Counters.TryGetValue("hook-used-round", out int lastUsed) && lastUsed == State.Round)
            {
                throw new InvalidOperationException("The Hook may only be attempted once per round.");
            }
            if (Graph.Edge(inv.Space, chosenSpace) == null)
            {
                throw new InvalidOperationException("Choose a space adjacent to you.");
            }
            adv.Counters["hook-used-round"] = State.Round;
            if (chosenSpace == adv.Space)
            {
                State.Phase = GamePhase.GameOver;
                State.Result = GameResult.InvestigatorsWin;
                Log("gameover", $"{inv.DefId} banishes The Butcher with The Hook at {chosenSpace}");
            }
            else
            {
                Log("adversary", $"The Hook finds nothing at {chosenSpace}: \"he is not there.\"");
            }
            FinishInvolvedAction(inv);
        }

        /// <summary>
        /// Free action (does not end the turn): spend 1 of Frayed Ropes' 3 Supply to force the
        /// Adversary player to place a face-down Shadow token within 3 of their real location.
        /// The Adversary side of the choice is resolved by <see cref="AnswerFrayedRopes"/>.
        /// </summary>
        public void UseFrayedRopes()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (!inv.Items.Contains("frayed-ropes"))
            {
                throw new InvalidOperationException($"{inv.DefId} is not carrying Frayed Ropes.");
            }
            var adv = State.Adversary;
            int uses = adv.Counters.TryGetValue("frayed-ropes-uses", out int u) ? u : 0;
            if (uses >= 3)
            {
                throw new InvalidOperationException("Frayed Ropes has no Supply left.");
            }
            adv.Counters["frayed-ropes-uses"] = uses + 1;
            adv.Counters["frayed-ropes-pending"] = 1;
            Log("objective", $"{inv.DefId} uses Frayed Ropes ({uses + 1}/3)");
        }

        /// <summary>The Adversary's forced response to <see cref="UseFrayedRopes"/>.</summary>
        public void AnswerFrayedRopes(string space)
        {
            var adv = State.Adversary;
            if (!adv.Counters.TryGetValue("frayed-ropes-pending", out int pending) || pending != 1)
            {
                throw new InvalidOperationException("Frayed Ropes has not been used.");
            }
            if (!Graph.DistancesFrom(adv.Space, 3, State.Overlay).ContainsKey(space))
            {
                throw new InvalidOperationException("The forced Shadow token must be within 3 spaces of The Butcher.");
            }
            adv.ShadowTokens["frayed"] = space;
            adv.Counters.Remove("frayed-ropes-pending");
            Log("adversary", $"places a face-down Shadow token at {space} (Frayed Ropes)");
        }
    }
}
