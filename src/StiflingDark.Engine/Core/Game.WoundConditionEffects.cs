using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The Wound (wounds.json, 26 cards) and Condition (conditions.json, 9 unique cards)
    /// deck effects, hung on the WoundsOn*/ConditionsOn* sub-hooks declared in
    /// Game.EffectDispatch.cs.
    ///
    /// Most printed clauses now have a real enforcement point: the per-action gate
    /// (<see cref="CollectActionBlockers"/>) carries every "you may not X" Wound and
    /// Condition, <see cref="ModifySprintRoll"/> carries Torn Ligament and Paranoid,
    /// <see cref="OnWoundGained"/>'s origin tag carries Punctured Lung and Mauled,
    /// <see cref="TrimFlashlightBright"/> carries Tunnel Vision and Bufotoxin, and
    /// <see cref="OnAdversaryTurnStart"/> plus <see cref="ConditionsOnRoundEnd"/> carry
    /// Possessed and Neurotoxin.
    ///
    /// What is left references systems the engine genuinely does not have (Obstruction
    /// tokens, a Major/Minor Ability action, Map Hazards). Those are logged with
    /// Log("todo", ...) rather than silently skipped or approximated into something the card
    /// doesn't say. Where an existing hook lets a *reasonable* approximation stand in for a
    /// clause the engine cannot enforce exactly (e.g. a flat MP reduction for "-N footprint
    /// when Moving", since MoveStep has no per-step interception point), the approximation is
    /// used and called out in a comment.
    /// </summary>
    public sealed partial class Game
    {
        // Log("todo", ...) dedup key -> already logged. Purely a log-hygiene convenience:
        // it is not part of GameState, so a save/load round-trip may log a repeat todo once
        // more, which is harmless (diagnostic only, never gameplay-affecting).
        private readonly HashSet<string> _loggedTodoOnce = new HashSet<string>();

        private void LogTodoOnce(string key, string detail)
        {
            if (_loggedTodoOnce.Add(key))
            {
                Log("todo", detail);
            }
        }

        /// <summary>True when a face-up copy of that Wound card is in effect on this
        /// Investigator. Neurotoxin's Wounds sit outside the Wound slots but the card is
        /// explicit that "you suffer the effects", so they count here too. Asher's Abilities
        /// suppress a Wound's effects outright, which is what
        /// <see cref="IgnoresFaceUpWound"/> filters out (Game.InvestigatorAbilities.cs).</summary>
        private bool FaceUpWound(InvestigatorState inv, string cardId) =>
            inv.Wounds.Any(w => w.FaceUp && w.CardId == cardId && !IgnoresFaceUpWound(inv, w)) ||
            inv.NonSlotWounds.Any(w => w.FaceUp && w.CardId == cardId && !IgnoresFaceUpWound(inv, w));

        // ---------- Condition grant with the printed duplicate-substitution rider ----------

        /// <summary>
        /// Grant a Condition, honoring the 2 cards whose text replaces a duplicate with
        /// something else instead of the default "no effect" (see Game.GainCondition):
        /// Bleeding -> a face-up Wound; Darkness -> lose 1 Charge. Every other Condition
        /// falls through to the normal duplicate rule. Adversary attacks and other callers
        /// that grant a Condition should call this instead of GainCondition directly.
        /// </summary>
        public void GrantConditionWithSubstitution(InvestigatorState inv, string conditionId)
        {
            if (HasCondition(inv, conditionId))
            {
                switch (conditionId)
                {
                    case "bleeding":
                        GainWound(inv, faceUp: true);
                        Log("condition", $"{inv.DefId} already has Bleeding; gained a face-up Wound instead");
                        return;
                    case "darkness":
                        inv.Charge = Math.Max(0, inv.Charge - 1);
                        Log("condition", $"{inv.DefId} already has Darkness; lost 1 Charge instead");
                        return;
                }
            }
            GainCondition(inv, conditionId);
        }

        // ---------- Commiserate (a Wound, but its effect is a discretionary discard action
        // rather than a timed hook, so it is exposed as a small public helper alongside
        // GrantConditionWithSubstitution rather than crammed into a hook it doesn't fit) ----------

        /// <summary>
        /// Commiserate: "When adjacent to another Investigator, you may discard this Wound
        /// if the other Investigator takes 2 face-down Wounds." Throws if not adjacent
        /// (reuses the Trade adjacency rule: windows are fine, closed mirror doors and
        /// carriage links are not). Returns false when <paramref name="inv"/> does not hold
        /// the card.
        /// </summary>
        public bool Commiserate(InvestigatorState inv, InvestigatorState other)
        {
            var wound = inv.Wounds.FirstOrDefault(w => w.CardId == "commiserate");
            if (wound == null)
            {
                return false;
            }
            RequireAdjacentForTrade(inv, other);
            inv.Wounds.Remove(wound);
            State.WoundDiscard.Add(wound.CardId);
            GainWound(other, faceUp: false);
            GainWound(other, faceUp: false);
            Log("wound", $"{inv.DefId} discarded Commiserate; {other.DefId} took 2 face-down Wounds");
            return true;
        }

        // ---------- Wounds: immediate "when you receive or flip this card face-up" text ----------

        partial void WoundsResolveFaceUp(InvestigatorState inv, WoundInstance wound)
        {
            switch (wound.CardId)
            {
                case "discharge":
                    // "Set your Charge to 0." No cap exists on regaining Charge afterwards
                    // (the card allows that anyway), so nothing further to do.
                    inv.Charge = 0;
                    Log("wound", $"{inv.DefId}'s Discharge sets Charge to 0");
                    break;

                case "spasm":
                    // "Lose 2 Stamina. Losing Stamina this way does not incur a face-down Wound."
                    inv.Stamina = Math.Max(0, inv.Stamina - 2);
                    Log("wound", $"{inv.DefId} loses 2 Stamina to Spasm");
                    break;

                case "fumble":
                    // "Discard a random Item card (and any associated tokens)."
                    if (inv.Items.Count > 0)
                    {
                        int idx = _rng.Next(inv.Items.Count);
                        string discarded = inv.Items[idx];
                        inv.Items.RemoveAt(idx);
                        SaveRng();
                        Log("wound", $"{inv.DefId}'s Fumble discards {discarded}");
                        Log("todo", "fumble: tokens associated with the discarded Item card are not modeled");
                    }
                    break;

                case "fear":
                    // "You must use your Major Ability on your next turn. If you do not have a
                    // Major Ability token (or if you are unable to use it), there is no effect."
                    // Armed here, checked and enforced at that next turn's start
                    // (AbilitiesOnTurnStart in Game.InvestigatorAbilities.cs) — the token could
                    // still be spent or gained between now and then.
                    ArmForcedMajorAbility(inv);
                    break;

                case "broken-battery":
                    Log("todo", "broken-battery: Obstruction tokens and a capped max-Charge are not modeled");
                    break;

                case "collapsed-lung":
                    Log("todo", "collapsed-lung: Obstruction tokens and a capped max-Stamina are not modeled");
                    break;

                case "dislocated-hip":
                    Log("todo", "dislocated-hip: Map Hazards are not modeled at all (no space or edge carries " +
                                "one), so there is nothing for the 'move' action gate to refuse travel through");
                    break;

                // disoriented has no immediate text; "You may not use your Major or Minor
                // Ability" is enforced by the per-action gate below (ActionUseAbility).

                case "nyctophobia":
                    Log("todo", "nyctophobia: the 'move' action gate is asked before the destination is known, " +
                                "so a Bright/Dim-to-Dark step cannot be singled out; the gate would need the " +
                                "destination space alongside the action key");
                    break;

                // The remaining Wounds have no immediate "receive or flip face-up" text; their
                // printed restrictions are enforced where they actually bite:
                //   claustrophobia, drain, ergophobia, mistrust, nyctophilia, mangled-hands
                //       -> WoundsCollectActionBlockers (the per-action gate).
                //   torn-ligament                -> WoundsModifySprintRoll.
                //   punctured-lung               -> WoundsOnWoundGained (the "sprint" origin tag).
                //   tunnel-vision                -> WoundsTrimFlashlightBright.
                //   hemorrhage                   -> FlipWoundFaceDown (Game.ItemEffects.cs), the single
                //                                   funnel every Wound flip-down goes through.
                //   breathless, dying-battery, panic, fractured-foot, pulled-hammy, slipped-disc
                //       -> the turn hooks below.
                //   commiserate                  -> the public Commiserate() helper above.
            }
        }

        // ---------- Wounds: ongoing effects while a face-up copy is held ----------

        partial void WoundsOnTurnStart(InvestigatorState inv)
        {
            // "-1 / -2 footprint when Moving." MoveStep has no per-step MP-cost interception
            // point available to this file, so the printed reduction is approximated as a
            // flat cut to the turn's whole MP budget, applied once here.
            if (FaceUpWound(inv, "fractured-foot"))
            {
                inv.MpRemaining = Math.Max(0, inv.MpRemaining - 1);
                Log("wound", $"{inv.DefId}'s Fractured Foot reduces this turn's MP by 1");
            }
            if (FaceUpWound(inv, "pulled-hammy"))
            {
                inv.MpRemaining = Math.Max(0, inv.MpRemaining - 2);
                Log("wound", $"{inv.DefId}'s Pulled Hammy reduces this turn's MP by 2");
            }

            // "You may carry only up to 2 total Item and/or Cursed Item cards ... choose
            // cards to discard until only 2 remain." There is no hook at the moment an Item
            // is gained (PickUpPoiToken / TradeItem live in Game.cs), so this is enforced as
            // a catch-up check at the top of this Investigator's own turn instead.
            if (FaceUpWound(inv, "slipped-disc"))
            {
                while (inv.Items.Count > 2)
                {
                    int idx = _rng.Next(inv.Items.Count);
                    string discarded = inv.Items[idx];
                    inv.Items.RemoveAt(idx);
                    SaveRng();
                    Log("wound", $"{inv.DefId}'s Slipped Disc forces discarding {discarded}");
                }
            }
        }

        partial void WoundsOnTurnEnd(InvestigatorState inv)
        {
            // "At the end of each of your turns, lose 1 Stamina. Losing Stamina this way
            // does not incur a face-down Wound." - direct decrement, bypassing LoseStamina's
            // wound-conversion check on purpose.
            if (FaceUpWound(inv, "breathless"))
            {
                inv.Stamina = Math.Max(0, inv.Stamina - 1);
                Log("wound", $"{inv.DefId} loses 1 Stamina to Breathless");
            }

            // "At the end of each of your turns, lose 1 Charge."
            if (FaceUpWound(inv, "dying-battery"))
            {
                inv.Charge = Math.Max(0, inv.Charge - 1);
                Log("wound", $"{inv.DefId} loses 1 Charge to Dying Battery");
            }

            // "You may no longer gain Stamina as part of your Final Action, even if you
            // choose to Rest." WoundsOnTurnEnd fires before EndTurn's Rest-driven
            // GainStamina call, so clearing Rested here suppresses it.
            if (FaceUpWound(inv, "panic"))
            {
                inv.Rested = false;
                Log("wound", $"{inv.DefId}'s Panic cancels any Stamina gain from Resting this turn");
            }
        }

        // ---------- Conditions ----------

        partial void ConditionsOnTurnStart(InvestigatorState inv)
        {
            if (HasCondition(inv, "choking-fear"))
            {
                // "On your next turn ... your footprint is reduced by 1." Approximated the
                // same way as the Fractured Foot / Pulled Hammy Wounds: a flat MP cut,
                // applied on the first turn-start seen after the Condition was granted
                // (which, for a Condition granted between this Investigator's turns, *is*
                // "your next turn").
                inv.MpRemaining = Math.Max(0, inv.MpRemaining - 1);
                Log("condition", $"{inv.DefId}'s Choking Fear reduces this turn's MP by 1");
            }

            // "At the start of each of your turns, place a face-up Wound below this card. You
            // suffer the effects of the Wound, but it does not take up a Wound slot."
            if (HasCondition(inv, "neurotoxin"))
            {
                var wound = new WoundInstance { CardId = DrawWound(), FaceUp = true };
                inv.NonSlotWounds.Add(wound);
                Log("condition",
                    $"{inv.DefId}'s Neurotoxin puts {wound.CardId} below the card ({inv.NonSlotWounds.Count}/2)");
                ResolveWoundFaceUp(inv, wound);
            }

            if (HasCondition(inv, "paranoid"))
            {
                int roll = _rng.Roll(6);
                SaveRng();
                Log("condition", $"{inv.DefId} rolls {roll} for Paranoid");
                if (roll <= 2)
                {
                    inv.MpRemaining /= 2;
                    // "Halve your footprint (including Sprint) this round": the latch lets
                    // ConditionsModifySprintRoll halve a Sprint rolled later in the turn too.
                    SetRoundModifier(ParanoidHalvedPrefix + inv.DefId, 1);
                    Log("condition", $"{inv.DefId}'s Paranoid halves this turn's MP");
                }
                else if (roll >= 5)
                {
                    DiscardCondition(inv, "paranoid");
                }
            }
        }

        partial void ConditionsOnTurnEnd(InvestigatorState inv)
        {
            if (HasCondition(inv, "bleeding"))
            {
                // "At the end of each of your turns, gain 1 face-up Wound. Once you've gained 2
                // face-up Wounds from this card, discard it." The per-card counter lives in the
                // same serializable Adversary Counters bag Bufotoxin's face-up state uses.
                string countKey = BleedingCountPrefix + inv.DefId;
                GainWound(inv, faceUp: true);
                int caused = (State.Adversary.Counters.TryGetValue(countKey, out int c) ? c : 0) + 1;
                State.Adversary.Counters[countKey] = caused;
                Log("condition", $"{inv.DefId}'s Bleeding causes a face-up Wound ({caused}/2)");
                if (caused >= 2)
                {
                    State.Adversary.Counters.Remove(countKey);
                    DiscardCondition(inv, "bleeding");
                }
            }

            if (HasCondition(inv, "choking-fear"))
            {
                // "Discard this Condition at the end of your next turn" - the next turn-end
                // seen after granting, matching the turn-start approximation above.
                DiscardCondition(inv, "choking-fear");
            }

            if (HasCondition(inv, "darkness"))
            {
                DiscardCondition(inv, "darkness");
            }

            if (HasCondition(inv, "gear-jam"))
            {
                // "You may not take the Charge Final Action unless you spend a Stamina." The
                // spend itself now happens the moment Charge is declared (ConditionsOnChargeDeclared,
                // fired from ChargeFlashlight before EndTurn) rather than here: this hook fires
                // after Breathless's own end-of-turn Stamina loss in the same OnInvestigatorTurnEnd
                // fanout, and paying late could try to spend Stamina Breathless already took,
                // throwing SpendStamina out of EndTurn and locking the turn. Only the D6
                // discard-check text ("at the end of each of your turns") still belongs here.
                int roll = _rng.Roll(6);
                SaveRng();
                Log("condition", $"{inv.DefId} rolls {roll} for Gear Jam");
                if (roll >= 4)
                {
                    DiscardCondition(inv, "gear-jam");
                }
            }
        }

        /// <summary>
        /// Gear Jam: "You may not take the Charge Final Action unless you spend a Stamina."
        /// The gate (<see cref="ConditionsCollectActionBlockers"/>'s ActionCharge case) already
        /// refuses the Action outright at 0 Stamina; this pays the cost the moment Charge is
        /// actually declared (see Game.cs' ChargeFlashlight), which is also before Breathless
        /// or any other end-of-turn Stamina loss has had a chance to run.
        /// </summary>
        partial void ConditionsOnChargeDeclared(InvestigatorState inv)
        {
            if (HasCondition(inv, "gear-jam"))
            {
                SpendStamina(inv, 1);
                Log("condition", $"{inv.DefId} spends 1 Stamina to Charge through Gear Jam");
            }
        }

        partial void ConditionsOnRoundEnd()
        {
            foreach (var inv in State.Investigators)
            {
                // Neurotoxin: "If there are 2 face-up Wounds below this card at the end of the
                // round, discard this Condition and both Wounds."
                if (HasCondition(inv, "neurotoxin") && inv.NonSlotWounds.Count >= 2)
                {
                    State.WoundDiscard.AddRange(inv.NonSlotWounds.Select(w => w.CardId));
                    inv.NonSlotWounds.Clear();
                    DiscardCondition(inv, "neurotoxin");
                    Log("condition", $"{inv.DefId}'s Neurotoxin runs its course; both Wounds below it are discarded");
                }

                // Bufotoxin: "Discard this Condition at the end of the next round" — the round
                // after the Adversary flipped it face-up, whose Flashlights it restricted.
                if (BufotoxinActiveRound(inv) == State.Round)
                {
                    State.Adversary.Counters.Remove(BufotoxinFaceUpPrefix + inv.DefId);
                    DiscardCondition(inv, "bufotoxin");
                }
            }
        }

        // ---------- The per-action gate (Game.cs RequireActionAllowed) ----------

        partial void WoundsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers)
        {
            switch (actionKey)
            {
                case ActionLockDoor:
                case ActionOpenDoor:
                    if (FaceUpWound(inv, "claustrophobia"))
                    {
                        blockers.Add("Claustrophobia: you may not Lock or Open Doors");
                    }
                    break;
                case ActionCharge:
                    if (FaceUpWound(inv, "drain"))
                    {
                        blockers.Add("Drain: you may no longer take the Charge Final Action");
                    }
                    break;
                case ActionInvolved:
                    if (FaceUpWound(inv, "ergophobia"))
                    {
                        blockers.Add("Ergophobia: you may no longer take the Involved Action Final Action");
                    }
                    break;
                case ActionTrade:
                    if (FaceUpWound(inv, "mistrust"))
                    {
                        blockers.Add($"Mistrust: {inv.DefId} may not Trade or be Traded with");
                    }
                    break;
                case ActionPickUpPoi:
                    if (FaceUpWound(inv, "mistrust"))
                    {
                        blockers.Add("Mistrust: you may not pick up Point of Interest tokens");
                    }
                    break;
                case ActionPlaceFlashlight:
                    if (FaceUpWound(inv, "nyctophilia"))
                    {
                        blockers.Add("Nyctophilia: you may no longer take the Place Flashlight Final Action");
                    }
                    break;
                case ActionUseItem:
                    if (FaceUpWound(inv, "mangled-hands"))
                    {
                        blockers.Add("Mangled Hands: you may not use Item or Cursed Item cards");
                    }
                    break;
                case ActionUseAbility:
                    if (FaceUpWound(inv, "disoriented"))
                    {
                        blockers.Add("Disoriented: you may not use your Major or Minor Ability");
                    }
                    break;
            }
        }

        partial void ConditionsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers)
        {
            switch (actionKey)
            {
                case ActionSprint:
                    if (HasCondition(inv, "choking-fear"))
                    {
                        blockers.Add("Choking Fear: you may not Sprint this turn");
                    }
                    break;
                case ActionPlaceFlashlight:
                    if (HasCondition(inv, "darkness"))
                    {
                        blockers.Add("Darkness: you may not use your Flashlight this turn");
                    }
                    break;
                case ActionCharge:
                    // "...unless you spend a Stamina": with none to spend the Action is simply
                    // unavailable. ConditionsOnTurnEnd does the spending.
                    if (HasCondition(inv, "gear-jam") && inv.Stamina < 1)
                    {
                        blockers.Add("Gear Jam: Charging costs a Stamina and you have none");
                    }
                    break;
            }
        }

        // ---------- Sprint roll adjustments (Game.cs Sprint) ----------

        partial void WoundsModifySprintRoll(InvestigatorState inv, List<int> rollBox)
        {
            // "Subtract 1 from your Sprint roll, down to a minimum of 1." Game.Sprint applies
            // the floor, so every card may simply subtract.
            if (FaceUpWound(inv, "torn-ligament"))
            {
                rollBox[0] -= 1;
                Log("wound", $"{inv.DefId}'s Torn Ligament subtracts 1 from the Sprint roll");
            }
        }

        partial void ConditionsModifySprintRoll(InvestigatorState inv, List<int> rollBox)
        {
            if (HasRoundModifier(ParanoidHalvedPrefix + inv.DefId))
            {
                rollBox[0] /= 2;
                Log("condition", $"{inv.DefId}'s Paranoid halves the Sprint roll too");
            }
        }

        // ---------- Flashlight line-of-sight limits (Game.cs PlaceFlashlight) ----------

        partial void WoundsTrimFlashlightBright(InvestigatorState inv, double angleRadians, HashSet<string> bright)
        {
            // "When you place your Flashlight, only the 3 center lines may be used for line of
            // sight" — the middle sight line plus one to either side (see TrimBrightToCenterLines).
            if (!FaceUpWound(inv, "tunnel-vision"))
            {
                return;
            }
            int dropped = TrimBrightToCenterLines(inv.Space, angleRadians, bright, lines: 3);
            if (dropped > 0)
            {
                Log("wound", $"{inv.DefId}'s Tunnel Vision drops {dropped} space(s) outside the 3 center lines");
            }
        }

        partial void ConditionsTrimFlashlightBright(InvestigatorState inv, double angleRadians, HashSet<string> bright)
        {
            // Bufotoxin, once the Adversary has flipped it face-up: "Next round, you can only
            // use the center line of your Flashlight for line of sight."
            if (BufotoxinActiveRound(inv) != State.Round)
            {
                return;
            }
            int dropped = TrimBrightToCenterLines(inv.Space, angleRadians, bright, lines: 1);
            if (dropped > 0)
            {
                Log("condition", $"{inv.DefId}'s Bufotoxin drops {dropped} space(s) outside the center line");
            }
        }

        // ---------- Wound origin reactions (Game.cs GainWound) ----------

        partial void WoundsOnWoundGained(InvestigatorState inv, WoundInstance wound, string origin)
        {
            // "Face-down Wounds gained from Sprinting are now face-up."
            if (origin == WoundFromSprint && !wound.FaceUp && FaceUpWound(inv, "punctured-lung"))
            {
                wound.FaceUp = true;
                Log("wound", $"{inv.DefId}'s Punctured Lung turns the Sprint Wound face-up");
            }
        }

        partial void ConditionsOnWoundGained(InvestigatorState inv, WoundInstance wound, string origin)
        {
            // Mauled: "Each time you gain 1 or more Wounds from the Adversary, gain 1
            // additional face-down Wound." The extra Wound is untagged, so it cannot cascade.
            if (origin != WoundFromAdversary || !HasCondition(inv, "mauled"))
            {
                return;
            }
            Log("condition", $"{inv.DefId}'s Mauled adds an extra face-down Wound");
            GainWound(inv, faceUp: false);
        }

        // ---------- Adversary-turn window (Game.Adversary.cs EnsureAdversaryTurnStarted) ----------

        /// <summary>Adversary Counters key prefix + Investigator def id: the round on which the
        /// Adversary flipped that Investigator's Bufotoxin face-up. Conditions carry no
        /// face-up state of their own, and this is the one serializable per-card bag the
        /// Adversary side already owns.</summary>
        private const string BufotoxinFaceUpPrefix = "bufotoxin-face-up:";

        /// <summary>Adversary Counters key prefix + Investigator def id: how many face-up Wounds
        /// their Bleeding has caused so far (it is discarded at 2).</summary>
        private const string BleedingCountPrefix = "bleeding-count:";

        /// <summary>Round-modifier prefix + Investigator def id: Paranoid rolled 1-2 for them
        /// this round, so a Sprint rolled later is halved as well.</summary>
        private const string ParanoidHalvedPrefix = "paranoid-halved:";

        /// <summary>The round in which a flipped Bufotoxin restricts that Investigator's
        /// Flashlight ("next round"), or 0 when the card is face-down or absent.</summary>
        private int BufotoxinActiveRound(InvestigatorState inv) =>
            HasCondition(inv, "bufotoxin") &&
            State.Adversary.Counters.TryGetValue(BufotoxinFaceUpPrefix + inv.DefId, out int flipped)
                ? flipped + 1
                : 0;

        partial void ConditionsOnAdversaryTurnStart()
        {
            foreach (var inv in State.Investigators.Where(i => !i.Dead && !i.Escaped))
            {
                if (HasCondition(inv, "bufotoxin") &&
                    !State.Adversary.Counters.ContainsKey(BufotoxinFaceUpPrefix + inv.DefId))
                {
                    Log("condition", $"the Adversary may flip {inv.DefId}'s Bufotoxin face-up (FlipBufotoxinFaceUp)");
                }
                if (HasCondition(inv, "possessed"))
                {
                    Log("condition", $"the Adversary may use {inv.DefId}'s Possessed this turn (UsePossessed)");
                }
            }
        }

        /// <summary>
        /// Bufotoxin: "The Adversary may flip it face-up on any of their turns." Doing so
        /// restricts that Investigator's Flashlight to its center line for the whole of the
        /// following round, after which the Condition is discarded
        /// (<see cref="ConditionsOnRoundEnd"/>).
        /// </summary>
        public void FlipBufotoxinFaceUp(string investigatorId)
        {
            RequirePhase(GamePhase.AdversaryTurn);
            var inv = Investigator(investigatorId);
            if (!HasCondition(inv, "bufotoxin"))
            {
                throw new InvalidOperationException($"{investigatorId} does not have the Bufotoxin Condition.");
            }
            if (State.Adversary.Counters.ContainsKey(BufotoxinFaceUpPrefix + inv.DefId))
            {
                throw new InvalidOperationException($"{investigatorId}'s Bufotoxin is already face-up.");
            }
            State.Adversary.Counters[BufotoxinFaceUpPrefix + inv.DefId] = State.Round;
            Log("condition", $"{inv.DefId}'s Bufotoxin is face-up: center line only in round {State.Round + 1}");
        }

        /// <summary>
        /// Possessed: "At the start of any Adversary turn, the Adversary may move you up to 4
        /// spaces and take the Bloodletting Action with you, discarding this Condition
        /// afterwards." The move is validated and applied here, then the ordinary
        /// <see cref="Bloodletting"/> Action runs with the named Cultist (so its own
        /// restrictions — round 1, Revealed, adjacency, the per-turn limit — all still apply,
        /// against the Investigator's new space).
        /// </summary>
        public void UsePossessed(string cultistId, string investigatorId, string destinationSpace)
        {
            EnsureAdversaryTurnStarted();
            var inv = Investigator(investigatorId);
            if (!HasCondition(inv, "possessed"))
            {
                throw new InvalidOperationException($"{investigatorId} does not have the Possessed Condition.");
            }
            if (inv.Dead || inv.Escaped)
            {
                throw new InvalidOperationException($"{investigatorId} is not on the board.");
            }
            if (destinationSpace != inv.Space &&
                !Graph.DistancesFrom(inv.Space, 4, State.Overlay).ContainsKey(destinationSpace))
            {
                throw new InvalidOperationException($"Possessed moves an Investigator up to 4 spaces; '{destinationSpace}' is further.");
            }
            if (State.Investigators.Any(o => o != inv && !o.Dead && !o.Escaped && o.Space == destinationSpace))
            {
                throw new InvalidOperationException($"'{destinationSpace}' is occupied by another Investigator.");
            }
            string from = inv.Space;
            inv.Space = destinationSpace;
            RemoveFlashlightIfForcedMove(inv.DefId);
            Log("condition", $"Possessed: the Adversary walks {inv.DefId} from {from} to {destinationSpace}");
            Bloodletting(cultistId, investigatorId);
            DiscardCondition(inv, "possessed");
            LogTodoOnce($"possessed-immunity:{inv.DefId}",
                "possessed: the trailing clause (once used, this Investigator cannot gain Wounds and cannot be " +
                "the target of Bloodletting or Severed Ear for the rest of the Adversary's turn) is not enforced — " +
                "GainWound and Bloodletting have no per-Investigator immunity latch to consult");
        }
    }
}
