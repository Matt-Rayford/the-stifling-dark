using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Protocol;
using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The Adversary seat's controls: minimal but complete. The designer's plan is to play
    /// Investigator seats against a bot Adversary, so this side gets buttons and pickers rather
    /// than a designed board experience — but every Adversary command in the protocol is
    /// reachable, including the per-adversary specials, so a rules bug on the monster's side can
    /// still be reproduced by hand.
    ///
    /// The Adversary's own view is unredacted about their figures, so their standee and Cultists
    /// are drawn on the same board as everything else and moving is a space click.
    /// </summary>
    public static class AdversaryUi
    {
        public static void Render(GameUi ui, RectTransform bar, PlayerView view)
        {
            var adversary = view.Adversary;
            bool setup = view.Phase == GamePhase.AdversarySetup;
            bool turn = view.Phase == GamePhase.AdversaryTurn;

            UiKit.CreateButton(bar, setup ? "FINISH SETUP" : "END ADVERSARY TURN", 17,
                () => ui.Send(setup
                    ? (GameCommand)new FinishAdversarySetupCommand()
                    : new AdversaryEndTurnCommand()),
                setup || turn, "Not the Adversary's phase.");
            UiKit.CreateButton(bar, "DISAPPEAR", 17,
                () => ui.Send(new AdversaryDisappearCommand()), turn, "Not the Adversary's turn.");

            if (setup)
            {
                RenderSetup(ui, view);
            }
            if (turn)
            {
                RenderTurn(ui, view, adversary);
            }
            RenderAlways(ui, view);
        }

        private static void RenderSetup(GameUi ui, PlayerView view)
        {
            ui.Header("SETUP  ·  place your secrets");
            ui.Note("Click a space on the board when a placement asks for one. " +
                "Evidence goes one per zone; the number of Points of Interest depends on the " +
                "Investigator count.");

            foreach (var zone in ui.Board.Map.Zones)
            {
                var captured = zone;
                ui.ActionButton("Hide Evidence for " + captured.Value + "…", () =>
                    ui.PickSpaces(1, "Click the space hiding " + captured.Value + " Evidence",
                        picked => ui.Send(new PlaceHiddenEvidenceCommand
                        {
                            Zone = captured.Key,
                            SpaceId = picked[0],
                        })), true);
            }

            foreach (var poi in view.PoiTokens.Where(p => p.TokenSpace == null))
            {
                var captured = poi;
                ui.ActionButton("POI " + captured.PoiSpace + " — place token, GENERAL front…", () =>
                    ui.PickSpaces(1, "Click the space for the POI token from " + captured.PoiSpace,
                        picked => ui.Send(new PlacePoiTokenCommand
                        {
                            PoiSpace = captured.PoiSpace,
                            TokenSpace = picked[0],
                            CursedFront = false,
                        })), true);
                ui.ActionButton("POI " + captured.PoiSpace + " — place token, CURSED front…", () =>
                    ui.PickSpaces(1, "Click the space for the CURSED POI token from " +
                        captured.PoiSpace,
                        picked => ui.Send(new PlacePoiTokenCommand
                        {
                            PoiSpace = captured.PoiSpace,
                            TokenSpace = picked[0],
                            CursedFront = true,
                        })), true);
            }

            ui.ActionButton("Place your standee…", () =>
                ui.PickSpaces(1, "Click your starting space",
                    picked => ui.Send(new PlaceAdversaryCommand { SpaceId = picked[0] })), true);

            if (view.Adversary.DefId == "cult-of-hunlow")
            {
                ui.ActionButton("Place the Cultists and the Altar…", () =>
                    ui.PickSpaces(5,
                        "Click 4 Cultist spaces, then the Altar space",
                        picked => ui.Send(new SetupCultistsCommand
                        {
                            Spaces = picked.Take(picked.Count - 1).ToList(),
                            AltarSpace = picked[picked.Count - 1],
                        })), true);
            }

            ui.ActionButton("Choose Attack + Ability loadout…", () =>
                ui.AskArgsPublic("Adversary card loadout",
                    "First argument is the Attack card id; the rest are Ability card ids.",
                    args => ui.Send(new SetupAdversaryCardsCommand
                    {
                        AttackCardId = args != null && args.Count > 0 ? args[0] : "",
                        AbilityCardIds = args != null && args.Count > 1
                            ? args.Skip(1).ToList()
                            : new List<string>(),
                    })), true);
        }

        private static void RenderTurn(GameUi ui, PlayerView view, PlayerView.AdversaryPanel adversary)
        {
            ui.Header("MOVE  ·  click a highlighted space");
            ui.Note("MP " + (adversary.MpRemaining?.ToString() ?? "?") +
                (adversary.SprintRolled.HasValue ? "   Sprint rolled " + adversary.SprintRolled : "") +
                (adversary.ActionsUsed.Count > 0
                    ? "\nActions used: " + string.Join(", ", adversary.ActionsUsed)
                    : ""));

            var overlay = BoardModel.OverlayFrom(view);
            if (adversary.Space != null)
            {
                foreach (string door in ui.Board.InteractRange(adversary.Space, overlay)
                    .Where(s => ui.Board.SpaceOrNull(s)?.Kind == SpaceKind.Door))
                {
                    string captured = door;
                    ui.CommandButton("Break the door at " + captured,
                        new AdversaryBreakDoorCommand { DoorSpace = captured }, true);
                }
            }

            ui.Header("CARDS");
            string attack = adversary.AttackCard;
            if (attack != null)
            {
                PlayCard(ui, view, attack, "Attack");
            }
            foreach (string ability in adversary.ActiveAbilities)
            {
                PlayCard(ui, view, ability, "Ability");
            }
            ui.ActionButton("Play a card by id…", () =>
                ui.AskArgsPublic("Play an Adversary card",
                    "First argument is the card id; the rest are its targets.",
                    args => ui.Send(new PlayAdversaryCardCommand
                    {
                        CardId = args != null && args.Count > 0 ? args[0] : "",
                        Targets = args != null && args.Count > 1 ? args.Skip(1).ToList() : null,
                    })), true);

            RenderSpecials(ui, view, adversary);
        }

        private static void PlayCard(GameUi ui, PlayerView view, string cardId, string kind)
        {
            ui.ActionButton("Play " + kind + ": " + ui.Describer.Card(cardId) + "…", () =>
                ui.AskArgsPublic("Play " + ui.Describer.Card(cardId),
                    ui.Describer.CardText(cardId) +
                    "\n\nTargets are Investigator ids or space ids, depending on the card.",
                    args => ui.Send(new PlayAdversaryCardCommand
                    {
                        CardId = cardId,
                        Targets = args,
                    })), true);
        }

        private static void RenderSpecials(GameUi ui, PlayerView view,
            PlayerView.AdversaryPanel adversary)
        {
            var living = view.Investigators.Where(i => !i.Dead && !i.Escaped).ToList();

            switch (adversary.DefId)
            {
                case "butcher":
                    ui.Header("THE BUTCHER");
                    ui.ActionButton("Stalk…", () =>
                    {
                        var options = living
                            .Select(i => new PromptOption(ui.Describer.Investigator(i.DefId),
                                () => ui.Send(new ButcherStalkCommand
                                {
                                    TargetInvestigatorIds = new List<string> { i.DefId },
                                })))
                            .ToList();
                        options.Add(new PromptOption("All of them",
                            () => ui.Send(new ButcherStalkCommand
                            {
                                TargetInvestigatorIds = living.Select(i => i.DefId).ToList(),
                            })));
                        ui.Modal.Show("stalk", "Stalk which Investigator(s)?", null, options,
                            () => { });
                    }, true);
                    ui.ActionButton("Place the Grave (actual, then decoy)…", () =>
                        ui.PickSpaces(2, "Click the ACTUAL Grave space, then the decoy",
                            picked => ui.Send(new PlaceGraveCommand
                            {
                                ActualSpace = picked[0],
                                DecoySpace = picked[1],
                            })), true);
                    ui.ActionButton("Answer Frayed Ropes…", () =>
                        ui.PickSpaces(1, "Click the space you answer with",
                            picked => ui.Send(new AnswerFrayedRopesCommand { Space = picked[0] })),
                        true);
                    break;

                case "insatiable-horror":
                    ui.Header("THE INSATIABLE HORROR");
                    foreach (var target in living)
                    {
                        var captured = target;
                        ui.ActionButton("Ambush " + ui.Describer.ShortInvestigator(captured.DefId) +
                            "…", () => ui.PickSpaces(1,
                                "Click where " + ui.Describer.ShortInvestigator(captured.DefId) +
                                " is dragged",
                                picked => ui.Send(new HorrorAmbushCommand
                                {
                                    InvestigatorToSpace = new Dictionary<string, string>
                                    {
                                        [captured.DefId] = picked[0],
                                    },
                                })), true);
                        ui.ActionButton("Enraged gather " +
                            ui.Describer.ShortInvestigator(captured.DefId) + "…", () =>
                            ui.PickSpaces(1, "Click where they are gathered to",
                                picked => ui.Send(new EnragedGatherCommand
                                {
                                    InvestigatorToSpace = new Dictionary<string, string>
                                    {
                                        [captured.DefId] = picked[0],
                                    },
                                })), true);
                    }
                    ui.ActionButton("Place an Egg Sac…", () =>
                        ui.PickSpaces(1, "Click the Egg Sac's space",
                            picked => ui.Send(new PlaceEggSacCommand { Space = picked[0] })), true);
                    break;

                case "cult-of-hunlow":
                    ui.Header("THE CULT OF HUNLOW");
                    foreach (var figure in adversary.Figures.Where(f => f.Alive))
                    {
                        var captured = figure;
                        ui.ActionButton("Move " + captured.Id + "…", () =>
                            ui.PickSpaces(1, "Click where " + captured.Id + " steps",
                                picked => ui.Send(new CultistMoveStepCommand
                                {
                                    CultistId = captured.Id,
                                    To = picked[0],
                                })), true);
                        ui.CommandButton(captured.Id + " disappears",
                            new CultistDisappearCommand { CultistId = captured.Id }, true);
                        ui.ActionButton(captured.Id + " breaks a door…", () =>
                            ui.PickSpaces(1, "Click the door space",
                                picked => ui.Send(new CultistBreakDoorCommand
                                {
                                    CultistId = captured.Id,
                                    DoorSpace = picked[0],
                                })), true);
                        foreach (var target in living)
                        {
                            var victim = target;
                            ui.CommandButton("Bloodletting: " + captured.Id + " on " +
                                ui.Describer.ShortInvestigator(victim.DefId),
                                new BloodlettingCommand
                                {
                                    CultistId = captured.Id,
                                    InvestigatorId = victim.DefId,
                                }, true);
                            ui.ActionButton("Possessed: " + captured.Id + " moves " +
                                ui.Describer.ShortInvestigator(victim.DefId) + "…", () =>
                                ui.PickSpaces(1, "Click the destination space",
                                    picked => ui.Send(new UsePossessedCommand
                                    {
                                        CultistId = captured.Id,
                                        InvestigatorId = victim.DefId,
                                        DestinationSpace = picked[0],
                                    })), true);
                        }
                    }
                    ui.ActionButton("Mor'gonnod corporeal step…", () =>
                        ui.PickSpaces(1, "Click the space to step to",
                            picked => ui.Send(new MorgonnodCorporealMoveStepCommand
                            {
                                To = picked[0],
                            })), true);
                    ui.CommandButton("The Final Sacrifice", new TheFinalSacrificeCommand(), true);
                    ui.ActionButton("Place the Ritual tokens (knife, then rope)…", () =>
                        ui.PickSpaces(2, "Click the Knife space, then the Rope space",
                            picked => ui.Send(new PlaceRitualTokensCommand
                            {
                                KnifeSpace = picked[0],
                                RopeSpace = picked[1],
                            })), true);
                    break;
            }
        }

        private static void RenderAlways(GameUi ui, PlayerView view)
        {
            if (view.BufotoxinFlipTargets.Count > 0)
            {
                ui.Header("BUFOTOXIN");
                foreach (string target in view.BufotoxinFlipTargets)
                {
                    string captured = target;
                    ui.CommandButton("Flip " + ui.Describer.Investigator(captured) +
                        "'s Bufotoxin face up",
                        new FlipBufotoxinFaceUpCommand { InvestigatorId = captured }, true);
                }
            }
        }
    }
}
