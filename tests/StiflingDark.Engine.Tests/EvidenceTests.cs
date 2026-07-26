using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class EvidenceTests
    {
        // ---------- Setup helpers (copied from GameFlowTests; kept private to this file) ----------

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

        private static Game NewAmusementParkGame(ulong seed = 7, string adversary = "butcher")
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "amusement-park",
                Seed = seed,
                AdversaryId = adversary,
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

        /// <summary>Starts the given Investigator's turn standing on a sawmill Computer space.</summary>
        private static InvestigatorState BeginTurnOnComputer(Game game, string invId = "aira", string computerSpace = "O-9")
        {
            game.BeginInvestigatorTurn(invId);
            var inv = game.State.Investigators.First(i => i.DefId == invId);
            inv.Space = computerSpace;
            return inv;
        }

        private static List<(string zone, string reward, string? arg, string? arg2)> OneTurnIn(
            string zone, string reward, string? arg = null, string? arg2 = null) =>
            new List<(string, string, string?, string?)> { (zone, reward, arg, arg2) };

        // ---------- Validation ----------

        [Fact]
        public void Turn_in_requires_the_right_space_kind()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "L-1"; // a General space, not a Computer
            aira.EvidenceCarried.Add("L");
            Assert.Throws<InvalidOperationException>(() => game.TurnInEvidence(OneTurnIn("L", "general-item")));
        }

        [Fact]
        public void Turn_in_requires_carried_evidence()
        {
            var game = NewSawmillGame();
            BeginTurnOnComputer(game);
            Assert.Throws<InvalidOperationException>(() => game.TurnInEvidence(OneTurnIn("L", "general-item")));
        }

        [Fact]
        public void Turn_in_rejects_spending_the_same_zone_twice_in_one_action()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            var turnIns = new List<(string zone, string reward, string? arg, string? arg2)>
            {
                ("L", "general-item", null, null),
                ("L", "general-item", null, null),
            };
            Assert.Throws<InvalidOperationException>(() => game.TurnInEvidence(turnIns));
        }

        // ---------- Counter & basic flow ----------

        [Fact]
        public void Turn_in_increments_the_counter_and_removes_the_carried_token()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "general-item"));
            Assert.Equal(1, game.State.Objective.EvidenceTurnedIn);
            Assert.DoesNotContain("L", aira.EvidenceCarried);
        }

        [Fact]
        public void Turn_in_can_spend_several_tokens_in_one_action()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            aira.EvidenceCarried.Add("K");
            var turnIns = new List<(string zone, string reward, string? arg, string? arg2)>
            {
                ("L", "general-item", null, null),
                ("K", "open-window-token", null, null),
            };
            game.TurnInEvidence(turnIns);
            Assert.Equal(2, game.State.Objective.EvidenceTurnedIn);
            Assert.Empty(aira.EvidenceCarried);
            Assert.Equal(2, aira.Items.Count + aira.MapTokens.Count);
        }

        [Fact]
        public void Turn_in_is_an_involved_action_that_ends_the_turn_without_the_rest_bonus()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            int staminaBeforeTurnIn = aira.Stamina;
            game.Rest();
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "general-item"));
            Assert.Equal(staminaBeforeTurnIn, aira.Stamina); // Rest's +1 Stamina is suppressed by the Involved Action.
            Assert.Equal(FinalActionKind.InvolvedAction, aira.FinalAction);
            Assert.True(aira.TurnTakenThisRound);
            Assert.Null(game.State.ActiveInvestigator);
        }

        // ---------- Repeatable rewards ----------

        [Fact]
        public void General_item_reward_draws_from_the_top_of_the_deck()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            string expected = game.State.GeneralItemDeck[0];
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "general-item"));
            Assert.Contains(expected, aira.Items);
        }

        [Fact]
        public void Repeatable_reward_can_be_claimed_more_than_once()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "open-window-token"));

            game.BeginInvestigatorTurn("lucy-belle");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            lucy.Space = "S-32"; // a different Computer space so ending aira's turn on O-9 is still legal
            lucy.EvidenceCarried.Add("K");
            game.TurnInEvidence(OneTurnIn("K", "open-window-token"));

            Assert.Contains("open-window", aira.MapTokens);
            Assert.Contains("open-window", lucy.MapTokens);
        }

        [Fact]
        public void Reveal_poi_reward_reveals_the_chosen_token()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            var poi = game.State.PoiTokens.First();
            Assert.False(poi.Revealed);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "reveal-poi", poi.PoiSpace));
            Assert.True(poi.Revealed);
        }

        // ---------- Once-per-game rewards ----------

        [Fact]
        public void Once_per_game_reward_cannot_be_claimed_twice()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "cursed-item"));
            Assert.Contains("cursed-item", game.State.Objective.OncePerGameRewardsUsed);

            game.BeginInvestigatorTurn("lucy-belle");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            lucy.Space = "S-32"; // a different Computer space so ending aira's turn on O-9 is still legal
            lucy.EvidenceCarried.Add("K");
            Assert.Throws<InvalidOperationException>(() => game.TurnInEvidence(OneTurnIn("K", "cursed-item")));
        }

        [Fact]
        public void Medical_item_reward_draws_a_medkit()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "medical-item"));
            Assert.Single(aira.Items);
        }

        [Fact]
        public void Major_ability_token_reward_targets_any_investigator_and_caps_at_one()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            var vincent = game.State.Investigators.First(i => i.DefId == "vincent");
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "major-ability-token", "vincent"));
            Assert.Equal(1, vincent.MajorAbilityTokens);
        }

        // ---------- Token rewards land in MapTokens ----------

        [Fact]
        public void Dim_and_secret_passage_rewards_land_in_map_tokens()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "dim-token"));
            Assert.Contains("dim", aira.MapTokens);

            game.BeginInvestigatorTurn("lucy-belle");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            lucy.Space = "S-32"; // a different Computer space so ending aira's turn on O-9 is still legal
            lucy.EvidenceCarried.Add("K");
            game.TurnInEvidence(OneTurnIn("K", "secret-passage-token"));
            Assert.Contains("secret-passage", lucy.MapTokens);
        }

        // ---------- Amusement Park: TicketBooth + Mirror Maze extra reward ----------

        [Fact]
        public void Amusement_park_turn_in_requires_a_ticket_booth()
        {
            var game = NewAmusementParkGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "G-1"; // a General space, not a Ticket Booth
            aira.EvidenceCarried.Add("G");
            Assert.Throws<InvalidOperationException>(() => game.TurnInEvidence(OneTurnIn("G", "general-item")));
        }

        [Fact]
        public void Rearrange_mirror_doors_reward_only_applies_at_the_amusement_park()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            Assert.Throws<InvalidOperationException>(() => game.TurnInEvidence(OneTurnIn("L", "rearrange-mirror-doors", "red")));
        }

        [Fact]
        public void Rearrange_mirror_doors_reward_sets_the_open_color()
        {
            var game = NewAmusementParkGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "G-11"; // Gift Shop Ticket Booth
            aira.EvidenceCarried.Add("G");
            game.TurnInEvidence(OneTurnIn("G", "rearrange-mirror-doors", "blue"));
            Assert.Equal(MirrorDoorColor.Blue, game.State.Overlay.OpenMirrorColor);
        }

        // ---------- Placement actions (free interacts on a later turn) ----------

        [Fact]
        public void Placement_actions_consume_the_token_and_mutate_the_overlay()
        {
            var game = NewSawmillGame();

            // Round 1: earn all three map tokens, one per Investigator, without ending the game round oddly.
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "open-window-token"));

            game.BeginInvestigatorTurn("lucy-belle");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            lucy.Space = "S-32"; // a different Computer space so ending aira's turn on O-9 is still legal
            lucy.EvidenceCarried.Add("K");
            game.TurnInEvidence(OneTurnIn("K", "dim-token"));

            game.BeginInvestigatorTurn("mitchell");
            var mitchell = game.State.Investigators.First(i => i.DefId == "mitchell");
            mitchell.Space = "K-3"; // yet another Computer space, distinct from aira's and lucy's
            mitchell.EvidenceCarried.Add("G");
            game.TurnInEvidence(OneTurnIn("G", "secret-passage-token"));

            game.BeginInvestigatorTurn("vincent");
            game.EndTurnWithoutFinalAction();
            game.AdversaryEndTurn();
            Assert.Equal(2, game.State.Round);

            // Round 2: cash the tokens in as free interacts (they must not end the turn).
            game.BeginInvestigatorTurn("aira");
            aira.Space = "117"; // one endpoint of the 117-S-17 Window edge
            game.PlaceOpenWindowToken("117", "S-17");
            Assert.DoesNotContain("open-window", aira.MapTokens);
            Assert.Contains(BoardOverlay.EdgeKey("117", "S-17"), game.State.Overlay.OpenWindows);
            Assert.Equal("aira", game.State.ActiveInvestigator); // still mid-turn: this was a free action
            game.EndTurnWithoutFinalAction();

            game.BeginInvestigatorTurn("lucy-belle");
            lucy.Space = "L-1"; // stands in zone L
            game.PlaceDimToken("L");
            Assert.DoesNotContain("dim", lucy.MapTokens);
            Assert.Contains("L", game.State.Overlay.DimZones);
            game.EndTurnWithoutFinalAction();

            game.BeginInvestigatorTurn("mitchell");
            mitchell.Space = "117";
            game.PlaceSecretPassage("117", "S-32");
            Assert.DoesNotContain("secret-passage", mitchell.MapTokens);
            Assert.Contains(BoardOverlay.EdgeKey("117", "S-32"), game.State.Overlay.SecretPassages);
        }

        [Fact]
        public void Placement_actions_require_carrying_the_matching_token()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "117";
            Assert.Throws<InvalidOperationException>(() => game.PlaceOpenWindowToken("117", "S-17"));
        }

        [Fact]
        public void Place_open_window_token_requires_a_window_edge_adjacent_to_the_investigator()
        {
            var game = NewSawmillGame();
            var aira = BeginTurnOnComputer(game);
            aira.EvidenceCarried.Add("L");
            game.TurnInEvidence(OneTurnIn("L", "open-window-token"));

            game.BeginInvestigatorTurn("lucy-belle");
            game.EndTurnWithoutFinalAction();
            game.BeginInvestigatorTurn("mitchell");
            game.EndTurnWithoutFinalAction();
            game.BeginInvestigatorTurn("vincent");
            game.EndTurnWithoutFinalAction();
            game.AdversaryEndTurn();

            game.BeginInvestigatorTurn("aira");
            aira.Space = "L-1"; // nowhere near the 117-S-17 Window edge
            Assert.Throws<InvalidOperationException>(() => game.PlaceOpenWindowToken("117", "S-17"));
        }
    }
}
