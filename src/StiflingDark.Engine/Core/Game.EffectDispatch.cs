using System.Collections.Generic;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Implements the card-effect hooks declared in Game.Effects.cs as thin dispatchers so
    /// each deck's effects live in their own partial (Game.WoundConditionEffects /
    /// Game.ItemEffects / Game.EventEffects) without competing for the single allowed
    /// implementation of each partial method. The per-adversary partials
    /// (Game.Butcher / Game.Horror / Game.Cult) hang off the same pattern.
    /// </summary>
    public sealed partial class Game
    {
        // Per-deck sub-hooks; a deck partial implements the ones it needs.
        partial void WoundsOnTurnStart(InvestigatorState inv);
        partial void WoundsOnTurnEnd(InvestigatorState inv);
        partial void WoundsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void WoundsResolveFaceUp(InvestigatorState inv, WoundInstance wound);
        partial void WoundsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers);
        partial void WoundsModifySprintRoll(InvestigatorState inv, List<int> rollBox);
        partial void WoundsTrimFlashlightBright(InvestigatorState inv, double angleRadians, HashSet<string> bright);
        partial void WoundsOnWoundGained(InvestigatorState inv, WoundInstance wound, string origin);
        partial void ConditionsOnTurnStart(InvestigatorState inv);
        partial void ConditionsOnTurnEnd(InvestigatorState inv);
        partial void ConditionsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void ConditionsOnFlashlightPlaced(InvestigatorState inv);
        partial void ConditionsOnRoundEnd();
        partial void ConditionsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers);
        partial void ConditionsModifySprintRoll(InvestigatorState inv, List<int> rollBox);
        partial void ConditionsTrimFlashlightBright(InvestigatorState inv, double angleRadians, HashSet<string> bright);
        partial void ConditionsOnWoundGained(InvestigatorState inv, WoundInstance wound, string origin);
        partial void ConditionsOnAdversaryTurnStart();
        partial void ItemsOnTurnStart(InvestigatorState inv);
        partial void ItemsOnTurnEnd(InvestigatorState inv);
        partial void ItemsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void ItemsOnFlashlightPlaced(InvestigatorState inv);
        partial void ItemsOnRoundEnd();
        partial void ItemsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers);
        partial void ItemsModifySprintRoll(InvestigatorState inv, List<int> rollBox);
        partial void EventsOnDrawn();
        partial void EventsOnTurnStart(InvestigatorState inv);
        partial void EventsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void EventsOnFlashlightPlaced(InvestigatorState inv);
        partial void EventsOnRoundEnd();
        partial void EventsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers);
        partial void EventsTrimFlashlightBright(InvestigatorState inv, double angleRadians, HashSet<string> bright);

        // Per-adversary sub-hooks for the shared Investigator/round timings. The turn-start
        // budget hooks stay where they are (BeginButcherTurn / BeginHorrorTurn / BeginCultTurn
        // in Game.Adversary.cs); these are the ones the shared framework fans out. Only the
        // adversary whose figures are in the game is dispatched to, so a card's tokens can
        // never fire for someone else's board.
        partial void ButcherOnRoundStart();
        partial void CultOnAdversaryTurnEnd();
        partial void ButcherOnInvestigatorMoveStep(InvestigatorState inv, string from, string to);
        partial void CultOnInvestigatorMoveStep(InvestigatorState inv, string from, string to);
        partial void ButcherOnInvestigatorTurnEnd(InvestigatorState inv);
        partial void CultOnInvestigatorTurnEnd(InvestigatorState inv);
        partial void HorrorOnRoundEnd();

        // Spirit sub-hooks (Game.Spirits.cs).
        partial void SpiritsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers);
        partial void SpiritsOnRoundEnd();
        partial void SpiritsOnInvestigatorDeath(InvestigatorState inv);

        partial void OnInvestigatorDeath(InvestigatorState inv)
        {
            SpiritsOnInvestigatorDeath(inv);
        }

        /// <summary>
        /// True when this row is a Spirit rather than an Investigator, and the per-Investigator
        /// card fanouts below must therefore be skipped: a Spirit has no Wound slots, no
        /// Conditions, no Stamina/Charge/MP penalties to suffer ("Spirits are not affected by
        /// ... anything that affects movement"), and is not a legal target for the Adversary's
        /// Investigator-facing card effects. Declared in Game.Spirits.cs.
        /// </summary>
        private static bool SkipCardHooksForSpirit(InvestigatorState inv) => IsSpirit(inv);

        partial void OnInvestigatorTurnStart(InvestigatorState inv)
        {
            if (SkipCardHooksForSpirit(inv))
            {
                return;
            }
            WoundsOnTurnStart(inv);
            ConditionsOnTurnStart(inv);
            ItemsOnTurnStart(inv);
            EventsOnTurnStart(inv);
        }

        partial void OnInvestigatorTurnEnd(InvestigatorState inv)
        {
            if (SkipCardHooksForSpirit(inv))
            {
                return;
            }
            WoundsOnTurnEnd(inv);
            ConditionsOnTurnEnd(inv);
            ItemsOnTurnEnd(inv);
            AdversaryOnInvestigatorTurnEnd(inv);
        }

        partial void OnInvestigatorMoveStep(InvestigatorState inv, string from, string to)
        {
            if (SkipCardHooksForSpirit(inv))
            {
                return;
            }
            WoundsOnMoveStep(inv, from, to);
            ConditionsOnMoveStep(inv, from, to);
            ItemsOnMoveStep(inv, from, to);
            EventsOnMoveStep(inv, from, to);
            AdversaryOnInvestigatorMoveStep(inv, from, to);
        }

        partial void OnFlashlightPlaced(InvestigatorState inv)
        {
            ConditionsOnFlashlightPlaced(inv);
            ItemsOnFlashlightPlaced(inv);
            EventsOnFlashlightPlaced(inv);
        }

        partial void OnRoundEnd()
        {
            ConditionsOnRoundEnd();
            ItemsOnRoundEnd();
            EventsOnRoundEnd();
            SpiritsOnRoundEnd();
            AdversaryOnRoundEnd();
        }

        partial void ResolveWoundFaceUp(InvestigatorState inv, WoundInstance wound)
        {
            WoundsResolveFaceUp(inv, wound);
        }

        partial void CollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers)
        {
            WoundsCollectActionBlockers(inv, actionKey, blockers);
            ConditionsCollectActionBlockers(inv, actionKey, blockers);
            ItemsCollectActionBlockers(inv, actionKey, blockers);
            EventsCollectActionBlockers(inv, actionKey, blockers);
            SpiritsCollectActionBlockers(inv, actionKey, blockers);
        }

        partial void ModifySprintRoll(InvestigatorState inv, List<int> rollBox)
        {
            WoundsModifySprintRoll(inv, rollBox);
            ConditionsModifySprintRoll(inv, rollBox);
            ItemsModifySprintRoll(inv, rollBox);
        }

        partial void TrimFlashlightBright(InvestigatorState inv, double angleRadians, HashSet<string> bright)
        {
            WoundsTrimFlashlightBright(inv, angleRadians, bright);
            ConditionsTrimFlashlightBright(inv, angleRadians, bright);
            EventsTrimFlashlightBright(inv, angleRadians, bright);
        }

        partial void OnWoundGained(InvestigatorState inv, WoundInstance wound, string origin)
        {
            WoundsOnWoundGained(inv, wound, origin);
            ConditionsOnWoundGained(inv, wound, origin);
        }

        partial void OnAdversaryTurnStart()
        {
            ConditionsOnAdversaryTurnStart();
        }

        partial void OnAdversaryTurnEnd()
        {
            if (State.Adversary.DefId == "cult-of-hunlow")
            {
                CultOnAdversaryTurnEnd();
            }
        }

        partial void OnRoundStart()
        {
            if (State.Adversary.DefId == "butcher")
            {
                ButcherOnRoundStart();
            }
        }

        private void AdversaryOnInvestigatorMoveStep(InvestigatorState inv, string from, string to)
        {
            switch (State.Adversary.DefId)
            {
                case "butcher": ButcherOnInvestigatorMoveStep(inv, from, to); break;
                case "cult-of-hunlow": CultOnInvestigatorMoveStep(inv, from, to); break;
            }
        }

        private void AdversaryOnInvestigatorTurnEnd(InvestigatorState inv)
        {
            switch (State.Adversary.DefId)
            {
                case "butcher": ButcherOnInvestigatorTurnEnd(inv); break;
                case "cult-of-hunlow": CultOnInvestigatorTurnEnd(inv); break;
            }
        }

        private void AdversaryOnRoundEnd()
        {
            if (State.Adversary.DefId == "insatiable-horror")
            {
                HorrorOnRoundEnd();
            }
        }
    }
}
