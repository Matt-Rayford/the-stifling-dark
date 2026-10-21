using System.Linq;
using StiflingDark.Engine.Core;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>The left column: a card per Investigator, then the Adversary and Objective cards.</summary>
    public sealed partial class GameUi
    {
        private void RenderRoster(PlayerView view)
        {
            UiKit.Clear(_rosterBody);
            foreach (var panel in view.Investigators)
            {
                bool isMe = panel.DefId == MyInvestigatorId;
                var card = UiKit.CreatePanel(_rosterBody, panel.DefId,
                    isMe ? new Color(0.16f, 0.15f, 0.10f, 0.95f) : UiKit.PanelSoft);
                var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(10, 10, 6, 6);
                layout.spacing = 1;
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;

                string flags = string.Join(" ", new[]
                {
                    panel.Dead ? "DEAD" : null,
                    panel.Escaped ? "ESCAPED" : null,
                    panel.TurnTakenThisRound ? "turn taken" : null,
                    panel.Rested ? "rested" : null,
                    panel.SprintedOrRested && !panel.Rested ? "sprinted" : null,
                    panel.MovementLocked ? "movement locked" : null,
                    panel.SpineChillRound > 0 ? "spine chill r" + panel.SpineChillRound : null,
                }.Where(f => f != null));

                Line(card, _describe.Investigator(panel.DefId) + (isMe ? "   ← you" : ""), 17,
                    isMe ? UiKit.AccentColor : UiKit.TextColor);
                Line(card, "space " + panel.Space + "  ·  " +
                    _board.ZoneName(_board.SpaceOrNull(panel.Space)?.Zone), 14, UiKit.MutedColor);
                Line(card, "St " + panel.Stamina + "   Ch " + panel.Charge +
                    "   MP " + panel.MpRemaining + "   Major " + panel.MajorAbilityTokens +
                    "   Final " + Describe.FinalAction(panel.FinalAction), 14);
                Line(card, "Wounds " + WoundSummary(panel) + "   Evidence " +
                    (panel.EvidenceCarried.Count == 0 ? "—" : string.Join("/", panel.EvidenceCarried)),
                    14);
                if (panel.Items != null && panel.Items.Count > 0)
                {
                    Line(card, "Items: " + string.Join(", ", panel.Items.Select(_describe.Card)), 14,
                        UiKit.MutedColor);
                }
                if (panel.Conditions != null && panel.Conditions.Count > 0)
                {
                    Line(card, "Conditions: " +
                        string.Join(", ", panel.Conditions.Select(_describe.Card)), 14, UiKit.DangerColor);
                }
                if (panel.ConditionCount > (panel.Conditions?.Count ?? 0))
                {
                    Line(card, "+" + (panel.ConditionCount - (panel.Conditions?.Count ?? 0)) +
                        " hidden condition(s)", 14, UiKit.MutedColor);
                }
                if (panel.MapTokens.Count > 0)
                {
                    Line(card, "Map tokens: " + string.Join(", ", panel.MapTokens), 14, UiKit.MutedColor);
                }
                if (panel.SpiritId != null)
                {
                    Line(card, "Spirit: " + _describe.Card(panel.SpiritId) +
                        "  (major " + panel.SpiritMajorTokens + ")", 14, UiKit.GoodColor);
                }
                if (flags.Length > 0)
                {
                    Line(card, flags, 13, UiKit.MutedColor);
                }
            }

            RenderAdversaryPanel(view);
            RenderObjectivePanel(view);
        }

        private string WoundSummary(PlayerView.InvestigatorPanel panel)
        {
            if (panel.Wounds.Count == 0 && panel.NonSlotWounds.Count == 0)
            {
                return "none";
            }
            var parts = panel.Wounds
                .Select(w => w.FaceUp ? _describe.Card(w.CardId) : "face-down")
                .ToList();
            parts.AddRange(panel.NonSlotWounds
                .Select(w => (w.FaceUp ? _describe.Card(w.CardId) : "face-down") + " (unslotted)"));
            return string.Join(", ", parts);
        }

        private void RenderAdversaryPanel(PlayerView view)
        {
            var adversary = view.Adversary;
            var card = UiKit.CreatePanel(_rosterBody, "Adversary",
                new Color(0.16f, 0.08f, 0.09f, 0.95f));
            var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.spacing = 1;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            Line(card, Describe.Adversary(adversary.DefId), 17, new Color(0.95f, 0.62f, 0.58f));
            Line(card, adversary.Space == null
                ? "position hidden" + (adversary.Revealed ? " (revealed?!)" : "")
                : "space " + adversary.Space + (adversary.Revealed ? "  ·  REVEALED" : ""), 14);
            Line(card, "Kills " + adversary.Kills + "/" + adversary.KillsToWin +
                (adversary.MpRemaining.HasValue ? "   MP " + adversary.MpRemaining.Value : "") +
                (adversary.TurnStarted ? "   turn started" : ""), 14);
            if (adversary.Counters.Count > 0)
            {
                Line(card, string.Join("   ",
                    adversary.Counters.OrderBy(c => c.Key).Select(c => c.Key + " " + c.Value)),
                    13, UiKit.MutedColor);
            }
            string attack = adversary.AttackCard != null ? _describe.Card(adversary.AttackCard) : "unknown";
            Line(card, "Attack: " + attack +
                (adversary.AttackUsedThisTurn ? " (used)" : "") +
                (adversary.AttackLockedThisTurn ? " (locked)" : ""), 14);
            Line(card, "Abilities known: " +
                (adversary.ActiveAbilities.Count == 0
                    ? "—"
                    : string.Join(", ", adversary.ActiveAbilities.Select(_describe.Card))) +
                "   (" + adversary.ActiveAbilityCount + " active, " +
                adversary.FaceDownAbilityCount + " face-down)", 14, UiKit.MutedColor);
            var cooldowns = adversary.Cooldown1.Concat(adversary.Cooldown2)
                .Select(c => c.CardId == null ? "?" : _describe.Card(c.CardId)).ToList();
            if (cooldowns.Count > 0)
            {
                Line(card, "Cooldown: " + string.Join(", ", cooldowns), 13, UiKit.MutedColor);
            }
            var living = adversary.Figures.Where(f => f.Alive).ToList();
            if (living.Count > 0)
            {
                Line(card, "Figures: " + string.Join(", ", living.Select(f =>
                    f.Id + (f.Space == null ? " (hidden)" : " @" + f.Space))), 13, UiKit.MutedColor);
            }
        }

        private void RenderObjectivePanel(PlayerView view)
        {
            var objective = view.Objective;
            var card = UiKit.CreatePanel(_rosterBody, "Objective", UiKit.PanelSoft);
            var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.spacing = 1;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            SectionHeader(card, "OBJECTIVE");
            Line(card, "Evidence turned in " + objective.EvidenceTurnedIn + "/" +
                objective.EvidenceRequired +
                (objective.SelectedEscapeCard != null
                    ? "   ·   " + _describe.Card(objective.SelectedEscapeCard)
                    : ""), 15);
            if (objective.Tokens.Count > 0)
            {
                Line(card, string.Join("   ", objective.Tokens.Select(t => t.Key + "@" + t.Value)),
                    13, UiKit.MutedColor);
            }
            if (objective.TokenCarriers.Count > 0)
            {
                Line(card, "carried: " + string.Join(", ", objective.TokenCarriers
                    .Select(t => t.Key + " by " + _describe.ShortInvestigator(t.Value))), 13,
                    UiKit.MutedColor);
            }
            Line(card, "Supplies " + objective.Supplies + "   Parts " + objective.PartsInstalled +
                (objective.EscapeOpen ? "   ESCAPE OPEN" : "") +
                (objective.EscapeReadyRound.HasValue
                    ? "   ready r" + objective.EscapeReadyRound.Value : ""), 13, UiKit.MutedColor);
            var decks = view.Decks;
            Line(card, "decks — event " + decks.Event + "  gen " + decks.GeneralItem +
                "  cursed " + decks.CursedItem + "  wound " + decks.Wound +
                " (+" + decks.WoundDiscard + " discard)", 13, UiKit.MutedColor);
            if (view.PersistentMajorEvents.Count > 0)
            {
                Line(card, "major events: " +
                    string.Join(", ", view.PersistentMajorEvents.Select(_describe.Card)), 13,
                    UiKit.DangerColor);
            }
            if (view.RoundModifiers.Count > 0)
            {
                Line(card, "round modifiers: " + string.Join("  ",
                    view.RoundModifiers.Select(m => m.Key + "=" + m.Value)), 12, UiKit.MutedColor);
            }
        }
    }
}
