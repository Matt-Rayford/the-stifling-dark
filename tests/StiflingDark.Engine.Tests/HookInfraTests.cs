using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// The shared intercept points the card decks hang off (Game.Effects.cs /
    /// Game.EffectDispatch.cs / Game.cs): the per-action gate, the Sprint-roll modifier, the
    /// Wound origin tags, the Flashlight charge/trim plumbing, and the Adversary-turn hooks.
    /// These test the mechanism itself; the per-card behavior lives with each deck's tests.
    /// </summary>
    public class HookInfraTests
    {
        private static Game NewSawmillGame(ulong seed = 1234, string adversary = "butcher")
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = adversary,
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
            var evidence = new Dictionary<string, string>
            {
                ["L"] = "L-1", ["K"] = "K-1", ["G"] = "G-1", ["S"] = "S-1", ["O"] = "O-2",
            };
            foreach (var (zone, space) in evidence)
            {
                game.PlaceHiddenEvidence(zone, space);
            }
            bool cursedPlaced = false;
            foreach (var poi in game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
            {
                string target = game.Graph.DistancesFrom(poi.Id, 2, game.State.Overlay).Keys
                    .First(id => game.Graph.Space(id).Kind == SpaceKind.Normal);
                game.PlacePoiToken(poi.Id, target, cursedFront: !cursedPlaced);
                cursedPlaced = true;
            }
            game.PlaceAdversary("S-25");
            game.FinishAdversarySetup();
            return game;
        }

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        private static void FinishRound(Game game)
        {
            if (game.State.ActiveInvestigator != null)
            {
                game.EndTurnWithoutFinalAction();
            }
            foreach (var inv in game.State.Investigators
                .Where(i => !i.Dead && !i.Escaped && !i.TurnTakenThisRound).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
        }

        // ---------- The per-action gate ----------

        [Fact]
        public void The_gate_reports_every_blocking_clause_at_once()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "nyctophilia", FaceUp = true });
            game.GrantConditionWithSubstitution(aira, "darkness");

            var blockers = game.ActionBlockers("aira", Game.ActionPlaceFlashlight);

            Assert.Equal(2, blockers.Count);
            Assert.Contains(blockers, b => b.Contains("Nyctophilia"));
            Assert.Contains(blockers, b => b.Contains("Darkness"));

            game.BeginInvestigatorTurn("aira");
            var error = Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));
            Assert.Contains("Nyctophilia", error.Message);
            Assert.Contains("Darkness", error.Message);
        }

        [Fact]
        public void A_gated_action_is_refused_before_anything_changes()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "nyctophilia", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            int charge = aira.Charge;
            Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));

            Assert.Equal(charge, aira.Charge);
            Assert.Equal(FinalActionKind.None, aira.FinalAction);
            Assert.Empty(game.State.Flashlights);
            Assert.Empty(game.State.Overlay.BrightSpaces);
            // The turn is still going; a legal Final Action is unaffected.
            game.ChargeFlashlight();
            Assert.Equal(FinalActionKind.Charge, aira.FinalAction);
        }

        [Fact]
        public void A_one_turn_block_lifts_again_the_following_round()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            game.GrantConditionWithSubstitution(aira, "darkness");

            game.BeginInvestigatorTurn("aira");
            Assert.NotEmpty(game.ActionBlockers("aira", Game.ActionPlaceFlashlight));
            Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));
            FinishRound(game);

            // Darkness discarded itself at the end of that turn, so the gate is clear again.
            Assert.Empty(game.ActionBlockers("aira", Game.ActionPlaceFlashlight));
            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);
            Assert.Single(game.State.Flashlights);
        }

        [Fact]
        public void An_unblocked_investigator_has_no_blockers_for_any_action_key()
        {
            var game = NewSawmillGame();
            foreach (string key in new[]
            {
                Game.ActionSprint, Game.ActionRest, Game.ActionCharge, Game.ActionPlaceFlashlight,
                Game.ActionLockDoor, Game.ActionOpenDoor, Game.ActionTrade, Game.ActionPickUpPoi,
                Game.ActionInvolved, Game.ActionUseItem, Game.ActionMove,
            })
            {
                Assert.Empty(game.ActionBlockers("aira", key));
            }
        }

        // ---------- The Sprint-roll modifier ----------

        [Fact]
        public void The_sprint_roll_modifier_sees_the_roll_and_can_halve_it()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            int baseMp = TestData.Db.Investigator("aira").Mp;

            for (int round = 1; round <= 30; round++)
            {
                if (!game.HasCondition(aira, "paranoid"))
                {
                    game.GrantConditionWithSubstitution(aira, "paranoid");
                }
                game.BeginInvestigatorTurn("aira");
                if (aira.MpRemaining <= baseMp / 2)
                {
                    // Paranoid rolled 1-2: "halve your footprint (including Sprint) this round."
                    int before = aira.MpRemaining;
                    game.Sprint();
                    Assert.Contains(game.State.Log,
                        e => e.Type == "condition" && e.Detail.Contains("halves the Sprint roll too"));
                    // The Sprint die is 2,2,3,3,3,4, so a halved roll is worth 1 or 2 MP.
                    Assert.InRange(aira.MpRemaining - before, 1, 2);
                    return;
                }
                FinishRound(game);
            }
            Assert.Fail("Paranoid never rolled 1-2 in 30 rounds");
        }

        // ---------- Wound origin tags ----------

        [Fact]
        public void A_sprints_stamina_wound_is_tagged_as_coming_from_the_sprint()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "punctured-lung", FaceUp = true });
            aira.Stamina = 2; // the next Stamina lost crosses a Wound icon (spaces 0 and 1)

            game.BeginInvestigatorTurn("aira");
            game.Sprint();

            // Punctured Lung only reacts to Sprint-origin Wounds, so a face-up second Wound
            // proves the Stamina-track Wound carried the Sprint tag through LoseStamina.
            Assert.Equal(2, aira.Wounds.Count);
            Assert.True(aira.Wounds[1].FaceUp);
        }

        [Fact]
        public void A_window_wound_is_tagged_as_a_window_and_not_as_a_sprint()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "punctured-lung", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            aira.Space = "117";
            game.MoveStep("S-17"); // a Window edge, per GameFlowTests
            Assert.True(game.State.PendingWindowChoice);
            game.ResolveWindow(stopAndLoseStamina: false);

            Assert.Equal(2, aira.Wounds.Count);
            Assert.False(aira.Wounds[1].FaceUp);
        }

        // ---------- Flashlight charge and trim ----------

        [Fact]
        public void An_unaffordable_flashlight_surcharge_refuses_the_placement_outright()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            aira.Charge = 2;
            game.SetRoundModifier(Game.FlashlightChargeSurchargeKey, 2); // 1 + 2 = 3 Charge

            game.BeginInvestigatorTurn("aira");
            var error = Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));

            Assert.Contains("3 Charge", error.Message);
            Assert.Equal(2, aira.Charge);
            Assert.Empty(game.State.Flashlights);
            Assert.Equal(FinalActionKind.None, aira.FinalAction);
        }

        [Fact]
        public void The_flashlight_surcharge_is_spent_up_front_when_it_can_be_paid()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            aira.Charge = 3;
            game.SetRoundModifier(Game.FlashlightChargeSurchargeKey, 1);

            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            Assert.Equal(1, aira.Charge);
        }

        [Fact]
        public void The_trim_hook_runs_before_anything_in_the_beam_is_revealed()
        {
            // Learn which spaces a center-line-only beam drops, then stand the Adversary on one
            // of them: the full beam would have caught them, the trimmed beam must not.
            var probe = NewSawmillGame();
            probe.SetRoundModifier(Game.FlashlightCenterLineOnlyKey, 1);
            var full = probe.PreviewFlashlight("aira", 0.0);
            probe.BeginInvestigatorTurn("aira");
            probe.PlaceFlashlight(0.0);
            var trimmed = probe.State.Flashlights.Single().BrightSpaces.ToHashSet();
            string dropped = full.First(s => !trimmed.Contains(s));

            var game = NewSawmillGame();
            game.SetRoundModifier(Game.FlashlightCenterLineOnlyKey, 1);
            game.State.Adversary.Space = dropped;
            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            Assert.False(game.State.Adversary.Revealed);
            Assert.DoesNotContain(dropped, game.State.Overlay.BrightSpaces);
        }

        // ---------- Adversary movement overrides ----------

        [Fact]
        public void The_ordinary_adversary_gets_its_printed_budget_plus_the_sprint_die()
        {
            var game = NewSawmillGame();
            FinishRound(game);
            foreach (var inv in game.State.Investigators)
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryMoveStep("S-26");

            var adv = game.State.Adversary;
            Assert.InRange(adv.SprintRolled, 2, 4);
            // The Butcher's base 5 + the Sprint die, less the 1 MP step just taken.
            Assert.Equal(5 + adv.SprintRolled - 1, adv.MpRemaining);
        }
    }
}
