using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>Covers the shared card-effect infrastructure: Conditions, board tokens,
    /// round-scoped modifiers, and the Wound face-up plumbing.</summary>
    public class EffectsInfraTests
    {
        private static Game NewSawmillGame(ulong seed = 4321)
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = "butcher",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
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
            return game;
        }

        private static void PlayOutRound(Game game)
        {
            foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
        }

        private static InvestigatorState Aira(Game game) =>
            game.State.Investigators.First(i => i.DefId == "aira");

        // ---------- Conditions ----------

        [Fact]
        public void Gaining_a_condition_adds_it_once()
        {
            var game = NewSawmillGame();
            var inv = Aira(game);
            Assert.True(game.GainCondition(inv, "bleeding"));
            Assert.True(game.HasCondition(inv, "bleeding"));
            Assert.Equal(new List<string> { "bleeding" }, inv.Conditions);
        }

        [Fact]
        public void A_duplicate_condition_has_no_effect()
        {
            var game = NewSawmillGame();
            var inv = Aira(game);
            game.GainCondition(inv, "darkness");
            Assert.False(game.GainCondition(inv, "darkness"));
            Assert.Single(inv.Conditions);
            Assert.Contains(game.State.Log, e => e.Type == "condition" && e.Detail.Contains("no effect"));
        }

        [Fact]
        public void Different_conditions_stack_and_are_tracked_per_investigator()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            game.GainCondition(aira, "mauled");
            game.GainCondition(aira, "paranoid");
            game.GainCondition(lucy, "mauled");
            Assert.Equal(2, aira.Conditions.Count);
            Assert.Single(lucy.Conditions);
            Assert.False(game.HasCondition(lucy, "paranoid"));
        }

        [Fact]
        public void Discarding_a_condition_removes_it_and_is_idempotent()
        {
            var game = NewSawmillGame();
            var inv = Aira(game);
            game.GainCondition(inv, "gear-jam");
            Assert.True(game.DiscardCondition(inv, "gear-jam"));
            Assert.Empty(inv.Conditions);
            Assert.False(game.DiscardCondition(inv, "gear-jam"));
        }

        [Fact]
        public void An_unknown_condition_card_is_rejected()
        {
            var game = NewSawmillGame();
            var inv = Aira(game);
            Assert.Throws<InvalidOperationException>(() => game.GainCondition(inv, "not-a-condition"));
            Assert.Throws<InvalidOperationException>(() => game.DiscardCondition(inv, "not-a-condition"));
            Assert.Empty(inv.Conditions);
        }

        // ---------- Board tokens ----------

        [Fact]
        public void Board_tokens_are_placed_moved_and_removed()
        {
            var game = NewSawmillGame();
            game.PlaceBoardToken("hellfire-1", "S-1");
            Assert.Equal("S-1", game.BoardTokenSpace("hellfire-1"));
            game.PlaceBoardToken("hellfire-1", "S-2");
            Assert.Equal("S-2", game.BoardTokenSpace("hellfire-1"));
            Assert.Single(game.State.BoardTokens);
            Assert.True(game.RemoveBoardToken("hellfire-1"));
            Assert.Null(game.BoardTokenSpace("hellfire-1"));
            Assert.False(game.RemoveBoardToken("hellfire-1"));
        }

        [Fact]
        public void Board_tokens_are_queried_by_prefix()
        {
            var game = NewSawmillGame();
            game.PlaceBoardToken("mucus-1", "S-1");
            game.PlaceBoardToken("mucus-2", "S-2");
            game.PlaceBoardToken("desecrated-ground-1", "S-1");
            Assert.Equal(new List<string> { "mucus-1", "mucus-2" }, game.BoardTokenIds("mucus"));
            Assert.Equal(new List<string> { "S-1", "S-2" }, game.BoardTokenSpaces("mucus"));
            Assert.True(game.HasBoardTokenAt("mucus", "S-2"));
            Assert.False(game.HasBoardTokenAt("desecrated-ground", "S-2"));
            Assert.Equal(new List<string> { "desecrated-ground-1", "mucus-1" }, game.BoardTokensAt("S-1"));
        }

        [Fact]
        public void Removing_by_prefix_leaves_other_token_kinds_alone()
        {
            var game = NewSawmillGame();
            game.PlaceBoardToken("mucus-1", "S-1");
            game.PlaceBoardToken("mucus-2", "S-2");
            game.PlaceBoardToken("hatchling-1", "S-3");
            Assert.Equal(2, game.RemoveBoardTokens("mucus"));
            Assert.Equal(0, game.RemoveBoardTokens("mucus"));
            Assert.Equal(new List<string> { "hatchling-1" }, game.BoardTokenIds(""));
        }

        [Fact]
        public void A_board_token_needs_a_real_space()
        {
            var game = NewSawmillGame();
            // Graph.Space is the engine-wide space validator; it reports unknown ids as KeyNotFound.
            Assert.Throws<KeyNotFoundException>(() => game.PlaceBoardToken("evil-eye-1", "nowhere"));
            Assert.Empty(game.State.BoardTokens);
        }

        [Fact]
        public void Board_tokens_survive_the_round_boundary()
        {
            var game = NewSawmillGame();
            game.PlaceBoardToken("desecrated-ground-1", "S-1");
            PlayOutRound(game);
            Assert.Equal(2, game.State.Round);
            Assert.Equal("S-1", game.BoardTokenSpace("desecrated-ground-1"));
        }

        // ---------- Round modifiers ----------

        [Fact]
        public void Round_modifiers_read_as_zero_until_set()
        {
            var game = NewSawmillGame();
            Assert.Equal(0, game.RoundModifier("flashlight-charge-surcharge"));
            Assert.False(game.HasRoundModifier("flashlight-charge-surcharge"));
            game.SetRoundModifier("flashlight-charge-surcharge", 1);
            Assert.Equal(1, game.RoundModifier("flashlight-charge-surcharge"));
            Assert.True(game.HasRoundModifier("flashlight-charge-surcharge"));
            Assert.Equal(3, game.AddRoundModifier("flashlight-charge-surcharge", 2));
            Assert.True(game.ClearRoundModifier("flashlight-charge-surcharge"));
            Assert.Equal(0, game.RoundModifier("flashlight-charge-surcharge"));
        }

        [Fact]
        public void Round_modifiers_are_cleared_when_the_next_round_begins()
        {
            var game = NewSawmillGame();
            game.SetRoundModifier("mp-penalty", 1);
            game.AddRoundModifier("sprint-forbidden", 1);
            PlayOutRound(game);
            Assert.Equal(2, game.State.Round);
            // The dictionary is not empty in general — round 2 draws its own Event card, which
            // may write modifiers of its own — but nothing set before the boundary survives it.
            Assert.False(game.HasRoundModifier("mp-penalty"));
            Assert.False(game.HasRoundModifier("sprint-forbidden"));
            Assert.Equal(0, game.RoundModifier("mp-penalty"));
        }

        // ---------- Wound face-up plumbing ----------

        [Fact]
        public void Flipping_a_wound_face_up_is_logged_once()
        {
            var game = NewSawmillGame();
            var inv = Aira(game);
            game.GainWound(inv, faceUp: false);
            var wound = inv.Wounds[0];
            game.FlipWoundFaceUp(inv, wound);
            Assert.True(wound.FaceUp);
            int flips = game.State.Log.Count(e => e.Type == "wound" && e.Detail.Contains("face-up"));
            Assert.Equal(1, flips);
            game.FlipWoundFaceUp(inv, wound);
            Assert.Equal(1, game.State.Log.Count(e => e.Type == "wound" && e.Detail.Contains("face-up")));
        }

        [Fact]
        public void The_turn_hooks_do_not_disturb_the_normal_turn_flow()
        {
            var game = NewSawmillGame();
            var inv = Aira(game);
            game.BeginInvestigatorTurn("aira");
            game.Rest();
            game.EndTurnWithoutFinalAction();
            Assert.True(inv.TurnTakenThisRound);
            Assert.Null(game.State.ActiveInvestigator);
        }
    }
}
