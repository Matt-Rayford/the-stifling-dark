using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class GameFlowTests
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
            CompleteAdversarySetup(game);
            return game;
        }

        private static void CompleteAdversarySetup(Game game)
        {
            var evidenceSpaces = new Dictionary<string, string>
            {
                ["L"] = "L-1", ["K"] = "K-1", ["G"] = "G-1", ["S"] = "S-1", ["O"] = "O-2",
            };
            foreach (var (zone, space) in evidenceSpaces)
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
        }

        [Fact]
        public void Setup_produces_round_1_with_an_event_drawn()
        {
            var game = NewSawmillGame();
            Assert.Equal(GamePhase.InvestigatorTurns, game.State.Phase);
            Assert.Equal(1, game.State.Round);
            Assert.NotNull(game.State.CurrentEvent);
            // Event deck: 7 minors + 4 moderates + 1 random major, minus the one just drawn.
            Assert.Equal(11, game.State.EventDeck.Count);
            Assert.Equal(5, game.State.Evidence.Count);
            Assert.False(game.State.Adversary.Revealed);
        }

        [Fact]
        public void Rest_turns_advance_to_the_adversary_and_next_round()
        {
            var game = NewSawmillGame();
            foreach (string inv in new[] { "aira", "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.Rest();
                game.EndTurnWithoutFinalAction();
            }
            Assert.Equal(GamePhase.AdversaryTurn, game.State.Phase);
            game.AdversaryEndTurn();
            Assert.Equal(2, game.State.Round);
            Assert.Equal(GamePhase.InvestigatorTurns, game.State.Phase);
        }

        [Fact]
        public void Sprint_spends_stamina_and_grants_rolled_mp()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            Assert.Equal(4, aira.MpRemaining);
            game.Sprint();
            Assert.Equal(4, aira.Stamina);
            Assert.InRange(aira.MpRemaining, 6, 8); // 4 base + sprint die 2..4
            Assert.Throws<InvalidOperationException>(() => game.Rest());
        }

        [Fact]
        public void Light_switch_reveals_hidden_tokens_and_lets_evidence_be_collected()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "O-1"; // teleport for the test; O-1 is the Office light switch
            game.ActivateLightSwitch();
            Assert.Contains("O", game.State.Overlay.BrightZones);
            Assert.True(game.State.Evidence["O"].Revealed);

            aira.Space = "O-2";
            game.PickUpEvidence();
            Assert.Contains("O", aira.EvidenceCarried);
            Assert.False(game.State.Evidence.ContainsKey("O"));

            // The switch burns out at end of round and can never be used again.
            game.EndTurnWithoutFinalAction();
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
            Assert.Contains("O", game.State.FalteringZones);
            game.BeginInvestigatorTurn("aira");
            game.State.Investigators.First(i => i.DefId == "aira").Space = "O-1";
            Assert.Throws<InvalidOperationException>(() => game.ActivateLightSwitch());
        }

        [Fact]
        public void Flashlight_placement_costs_charge_lights_spaces_and_ends_the_turn()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            var preview = game.PreviewFlashlight("aira", 0);
            game.PlaceFlashlight(0);
            Assert.Equal(2, aira.Charge);
            Assert.Single(game.State.Flashlights);
            Assert.Equal(preview.OrderBy(s => s), game.State.Flashlights[0].BrightSpaces);
            Assert.Superset(new HashSet<string> { "285" }, game.State.Overlay.BrightSpaces);
            Assert.Null(game.State.ActiveInvestigator);
            Assert.True(aira.TurnTakenThisRound);
        }

        [Fact]
        public void Flashlight_over_the_adversary_reveals_them()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "S-24"; // near the hidden adversary at S-25
            // Find an angle whose beam covers S-25.
            double? hit = null;
            for (int i = 0; i < 48 && hit == null; i++)
            {
                double angle = i * Math.PI / 24;
                if (game.PreviewFlashlight("aira", angle).Contains("S-25"))
                {
                    hit = angle;
                }
            }
            Assert.NotNull(hit);
            game.PlaceFlashlight(hit!.Value);
            Assert.True(game.State.Adversary.Revealed);
        }

        [Fact]
        public void Window_crossing_forces_the_wound_or_stamina_choice()
        {
            var game = NewSawmillGame();

            // Wound path.
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "117";
            game.MoveStep("S-17");
            Assert.True(game.State.PendingWindowChoice);
            Assert.Throws<InvalidOperationException>(() => game.MoveStep("S-18"));
            game.ResolveWindow(stopAndLoseStamina: false);
            Assert.Single(aira.Wounds);
            Assert.False(aira.Wounds[0].FaceUp);
            game.EndTurnWithoutFinalAction();

            // Stamina path locks further movement.
            game.BeginInvestigatorTurn("lucy-belle");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            lucy.Space = "117";
            game.MoveStep("S-17");
            game.ResolveWindow(stopAndLoseStamina: true);
            Assert.Equal(4, lucy.Stamina);
            Assert.Empty(lucy.Wounds);
            Assert.Throws<InvalidOperationException>(() => game.MoveStep("S-18"));
        }

        [Fact]
        public void Four_wounds_kill_and_the_butcher_wins_immediately()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            for (int i = 0; i < 4; i++)
            {
                game.GainWound(aira, faceUp: false);
            }
            Assert.True(aira.Dead);
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.AdversaryWins, game.State.Result);
        }

        [Fact]
        public void Adversary_moves_hidden_and_breaks_doors()
        {
            var game = NewSawmillGame();
            foreach (string inv in new[] { "aira", "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            Assert.Equal(GamePhase.AdversaryTurn, game.State.Phase);

            game.AdversaryMoveStep("S-26"); // S-25 -> S-26
            Assert.False(game.State.Adversary.Revealed);
            game.State.Adversary.Space = "S-20"; // adjacent to the S-23 door
            game.AdversaryBreakDoor("S-23");
            Assert.Equal(DoorState.Damaged, game.State.Overlay.DoorState("S-23"));
            // Break Door is once per adversary turn; a second break needs the next round.
            Assert.Throws<InvalidOperationException>(() => game.AdversaryBreakDoor("S-23"));
            game.AdversaryEndTurn();
            foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped))
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.State.Adversary.Space = "S-20";
            game.AdversaryBreakDoor("S-23");
            Assert.Equal(DoorState.Destroyed, game.State.Overlay.DoorState("S-23"));
        }

        [Fact]
        public void Timeout_kills_everyone_still_on_the_board()
        {
            var game = NewSawmillGame();
            while (game.State.Phase != GamePhase.GameOver)
            {
                foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped))
                {
                    game.BeginInvestigatorTurn(inv.DefId);
                    game.EndTurnWithoutFinalAction();
                }
                game.AdversaryEndTurn();
            }
            Assert.Equal(17, game.State.Round);
            // No banish objective selected: unescaped Investigators count as killed.
            Assert.Equal(GameResult.AdversaryWins, game.State.Result);
        }

        [Fact]
        public void Water_float_carries_an_investigator_two_spaces_clockwise()
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "amusement-park",
                Seed = 7,
                AdversaryId = "butcher",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "142", ["lucy-belle"] = "153", ["mitchell"] = "166", ["vincent"] = "180",
                },
                MedicalItemSpaces = new List<string> { "68", "T-32" },
            });
            var evidence = new Dictionary<string, string>
            {
                ["G"] = "G-1", ["M"] = "M-1", ["C"] = "C-1", ["T"] = "T-35", ["F"] = "F-2",
            };
            foreach (var (zone, space) in evidence)
            {
                game.PlaceHiddenEvidence(zone, space);
            }
            bool cursed = false;
            foreach (var poi in game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
            {
                string target = game.Graph.DistancesFrom(poi.Id, 2, game.State.Overlay).Keys
                    .First(id => game.Graph.Space(id).Kind == SpaceKind.Normal);
                game.PlacePoiToken(poi.Id, target, cursedFront: !cursed);
                cursed = true;
            }
            game.PlaceAdversary("M-20");
            game.FinishAdversarySetup();

            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "141"; // beside the tunnel entrance
            game.MoveStep("T-3");
            // First water space this turn: floats 2 clockwise (T-3 -> T-1 -> T-2).
            Assert.Equal("T-2", aira.Space);
            Assert.True(aira.WaterFloatUsedThisTurn);
        }
    }
}
