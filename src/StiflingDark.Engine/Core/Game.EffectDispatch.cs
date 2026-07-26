using System.Collections.Generic;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Implements the card-effect hooks declared in Game.Effects.cs as thin dispatchers so
    /// each deck's effects live in their own partial (Game.WoundConditionEffects /
    /// Game.ItemEffects / Game.EventEffects) without competing for the single allowed
    /// implementation of each partial method.
    /// </summary>
    public sealed partial class Game
    {
        // Per-deck sub-hooks; a deck partial implements the ones it needs.
        partial void WoundsOnTurnStart(InvestigatorState inv);
        partial void WoundsOnTurnEnd(InvestigatorState inv);
        partial void WoundsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void WoundsResolveFaceUp(InvestigatorState inv, WoundInstance wound);
        partial void ConditionsOnTurnStart(InvestigatorState inv);
        partial void ConditionsOnTurnEnd(InvestigatorState inv);
        partial void ConditionsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void ConditionsOnFlashlightPlaced(InvestigatorState inv);
        partial void ItemsOnTurnStart(InvestigatorState inv);
        partial void ItemsOnTurnEnd(InvestigatorState inv);
        partial void ItemsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void ItemsOnFlashlightPlaced(InvestigatorState inv);
        partial void ItemsOnRoundEnd();
        partial void EventsOnDrawn();
        partial void EventsOnTurnStart(InvestigatorState inv);
        partial void EventsOnMoveStep(InvestigatorState inv, string from, string to);
        partial void EventsOnFlashlightPlaced(InvestigatorState inv);
        partial void EventsOnRoundEnd();

        partial void OnInvestigatorTurnStart(InvestigatorState inv)
        {
            WoundsOnTurnStart(inv);
            ConditionsOnTurnStart(inv);
            ItemsOnTurnStart(inv);
            EventsOnTurnStart(inv);
        }

        partial void OnInvestigatorTurnEnd(InvestigatorState inv)
        {
            WoundsOnTurnEnd(inv);
            ConditionsOnTurnEnd(inv);
            ItemsOnTurnEnd(inv);
        }

        partial void OnInvestigatorMoveStep(InvestigatorState inv, string from, string to)
        {
            WoundsOnMoveStep(inv, from, to);
            ConditionsOnMoveStep(inv, from, to);
            ItemsOnMoveStep(inv, from, to);
            EventsOnMoveStep(inv, from, to);
        }

        partial void OnFlashlightPlaced(InvestigatorState inv)
        {
            ConditionsOnFlashlightPlaced(inv);
            ItemsOnFlashlightPlaced(inv);
            EventsOnFlashlightPlaced(inv);
        }

        partial void OnRoundEnd()
        {
            ItemsOnRoundEnd();
            EventsOnRoundEnd();
        }

        partial void ResolveWoundFaceUp(InvestigatorState inv, WoundInstance wound)
        {
            WoundsResolveFaceUp(inv, wound);
        }
    }
}
