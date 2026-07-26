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
    /// A number of printed clauses reference systems the engine does not implement yet
    /// (Obstruction tokens, a Major/Minor Ability action, an Item "use" action, per-action
    /// gating hooks for Sprint/Trade/Doors/Final Actions, an origin tag on GainWound, an
    /// Adversary-turn-start hook, ...). Those are logged with Log("todo", ...) rather than
    /// silently skipped or approximated into something the card doesn't say. Where an
    /// existing hook lets a *reasonable* approximation stand in for a clause the engine
    /// cannot enforce exactly (e.g. a flat MP reduction for "-N footprint when Moving",
    /// since MoveStep has no per-step interception point), the approximation is used and
    /// called out in a comment.
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

        private static bool FaceUpWound(InvestigatorState inv, string cardId) =>
            inv.Wounds.Any(w => w.FaceUp && w.CardId == cardId);

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
                    Log("todo", "fear: no Major/Minor Ability action exists yet to force its use next turn");
                    break;

                case "broken-battery":
                    Log("todo", "broken-battery: Obstruction tokens and a capped max-Charge are not modeled");
                    break;

                case "collapsed-lung":
                    Log("todo", "collapsed-lung: Obstruction tokens and a capped max-Stamina are not modeled");
                    break;

                case "claustrophobia":
                    Log("todo", "claustrophobia: LockDoor/OpenDoor have no per-investigator gating hook");
                    break;

                case "dislocated-hip":
                    Log("todo", "dislocated-hip: MoveStep has no validation hook to block travel through Map Hazards");
                    break;

                case "disoriented":
                    Log("todo", "disoriented: no Major/Minor Ability action exists yet to restrict");
                    break;

                case "drain":
                    Log("todo", "drain: ChargeFlashlight has no per-investigator gating hook");
                    break;

                case "ergophobia":
                    Log("todo", "ergophobia: TakeInvolvedAction has no per-investigator gating hook");
                    break;

                case "hemorrhage":
                    Log("todo", "hemorrhage: nothing intercepts other Wounds being flipped face-down " +
                                "(e.g. Adversary cards set WoundInstance.FaceUp directly) to block it");
                    break;

                case "mangled-hands":
                    Log("todo", "mangled-hands: no Item/Cursed Item 'use' action exists yet to restrict");
                    break;

                case "mistrust":
                    Log("todo", "mistrust: TradeItem/TradeEvidence/PickUpPoiToken have no per-investigator gating hook");
                    break;

                case "nyctophilia":
                    Log("todo", "nyctophilia: PlaceFlashlight has no per-investigator gating hook");
                    break;

                case "nyctophobia":
                    Log("todo", "nyctophobia: MoveStep validates and commits the move before WoundsOnMoveStep " +
                                "fires, too late to block a Bright/Dim-to-Dark move");
                    break;

                case "punctured-lung":
                    Log("todo", "punctured-lung: GainWound has no origin tag distinguishing Sprint-caused Wounds");
                    break;

                case "torn-ligament":
                    Log("todo", "torn-ligament: Sprint has no hook to subtract 1 from the rolled die");
                    break;

                case "tunnel-vision":
                    Log("todo", "tunnel-vision: Wounds have no OnFlashlightPlaced hook (only Conditions/Items/" +
                                "Events do), so Flashlight line-of-sight cannot be restricted");
                    break;

                // breathless, dying-battery, panic, fractured-foot, pulled-hammy, slipped-disc:
                // no "receive or flip face-up" text - they are ongoing, handled in the turn hooks below.
                // commiserate: a discretionary discard action - see the public Commiserate() helper above.
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
            if (HasCondition(inv, "bufotoxin"))
            {
                LogTodoOnce($"bufotoxin:{inv.DefId}",
                    "bufotoxin: Conditions have no face-down/face-up state, and there is no Adversary-turn " +
                    "hook to flip this card and apply its next-round Flashlight line-of-sight restriction");
            }

            if (HasCondition(inv, "choking-fear"))
            {
                // "On your next turn ... your footprint is reduced by 1." Approximated the
                // same way as the Fractured Foot / Pulled Hammy Wounds: a flat MP cut,
                // applied on the first turn-start seen after the Condition was granted
                // (which, for a Condition granted between this Investigator's turns, *is*
                // "your next turn").
                inv.MpRemaining = Math.Max(0, inv.MpRemaining - 1);
                Log("condition", $"{inv.DefId}'s Choking Fear reduces this turn's MP by 1");
                LogTodoOnce($"choking-fear-sprint:{inv.DefId}", "choking-fear: Sprint has no gating hook to forbid it");
            }

            if (HasCondition(inv, "darkness"))
            {
                LogTodoOnce($"darkness:{inv.DefId}",
                    "darkness: PlaceFlashlight has no per-investigator gating hook to forbid using the Flashlight");
            }

            if (HasCondition(inv, "gear-jam"))
            {
                LogTodoOnce($"gear-jam:{inv.DefId}",
                    "gear-jam: ChargeFlashlight has no per-investigator gating hook to require spending a Stamina");
            }

            if (HasCondition(inv, "mauled"))
            {
                LogTodoOnce($"mauled:{inv.DefId}",
                    "mauled: GainWound has no origin tag distinguishing Adversary-inflicted Wounds, so the " +
                    "additional face-down Wound cannot be triggered");
            }

            if (HasCondition(inv, "neurotoxin"))
            {
                LogTodoOnce($"neurotoxin:{inv.DefId}",
                    "neurotoxin: there is no non-Wound-slot Wound pool on InvestigatorState and no " +
                    "ConditionsOnRoundEnd hook, so the below-this-card Wounds and their 2-Wound discard cannot be modeled");
            }

            if (HasCondition(inv, "paranoid"))
            {
                int roll = _rng.Roll(6);
                SaveRng();
                Log("condition", $"{inv.DefId} rolls {roll} for Paranoid");
                if (roll <= 2)
                {
                    inv.MpRemaining /= 2;
                    Log("condition", $"{inv.DefId}'s Paranoid halves this turn's MP");
                    LogTodoOnce($"paranoid-sprint:{inv.DefId}",
                        "paranoid: Sprint has no hook to also halve a Sprint roll gained later this round");
                }
                else if (roll >= 5)
                {
                    DiscardCondition(inv, "paranoid");
                }
            }

            if (HasCondition(inv, "possessed"))
            {
                LogTodoOnce($"possessed:{inv.DefId}",
                    "possessed: there is no Adversary-turn-start hook to let the Adversary move this " +
                    "Investigator and Bloodlet with them");
            }
        }

        partial void ConditionsOnTurnEnd(InvestigatorState inv)
        {
            if (HasCondition(inv, "bleeding"))
            {
                GainWound(inv, faceUp: true);
                Log("condition", $"{inv.DefId}'s Bleeding causes a face-up Wound");
                LogTodoOnce($"bleeding-count:{inv.DefId}",
                    "bleeding: no per-condition counter exists to discard this card once it has caused 2 Wounds");
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
                int roll = _rng.Roll(6);
                SaveRng();
                Log("condition", $"{inv.DefId} rolls {roll} for Gear Jam");
                if (roll >= 4)
                {
                    DiscardCondition(inv, "gear-jam");
                }
            }
        }
    }
}
