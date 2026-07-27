using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// Designer ruling: when the Wound deck runs out, reshuffle the Wound discard pile into a
    /// fresh stack and keep drawing, rather than treating the deck as a hard limit. Covers
    /// <see cref="Game"/>'s private DrawWound helper indirectly via <see cref="Game.GainWound"/>,
    /// the one public entry point every Wound draw funnels through.
    /// </summary>
    public class WoundDeckTests
    {
        private static Game NewSawmillGame(ulong seed = 1234)
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

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        [Fact]
        public void Gain_wound_reshuffles_the_discard_pile_once_the_deck_runs_dry_and_keeps_drawing()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            game.State.WoundDeck.Clear();
            game.State.WoundDeck.AddRange(new[] { "spasm", "fumble" });
            game.State.WoundDiscard.Clear();
            game.State.WoundDiscard.AddRange(new[] { "discharge", "fear", "torn-ligament" });

            game.GainWound(aira, faceUp: false); // "spasm"
            game.GainWound(aira, faceUp: false); // "fumble"; the deck is now empty

            Assert.Empty(game.State.WoundDeck);
            Assert.Equal(3, game.State.WoundDiscard.Count);
            Assert.DoesNotContain(game.State.Log, e => e.Type == "deck");

            // The deck is empty but the discard is not: this draw must reshuffle first, then
            // succeed, rather than throwing "the wound deck is empty".
            game.GainWound(aira, faceUp: false);

            Assert.Equal(3, aira.Wounds.Count);
            Assert.Empty(game.State.WoundDiscard);
            Assert.Equal(2, game.State.WoundDeck.Count); // 3 reshuffled, minus the 1 just drawn
            Assert.Contains(game.State.Log, e => e.Type == "deck" && e.Detail == "wound discards reshuffled");
            Assert.Contains(aira.Wounds[2].CardId, new[] { "discharge", "fear", "torn-ligament" });

            // Drawing continues normally off the reshuffled deck afterwards.
            game.GainWound(aira, faceUp: false);
            Assert.Single(game.State.WoundDeck);
        }

        [Fact]
        public void The_reshuffle_is_deterministic_for_a_given_seed_and_rng_state()
        {
            // 2 independent games, same seed and same sequence of operations up to the point
            // of reshuffle: the resulting deck order (which encodes the shuffle) and the drawn
            // card must match exactly, since both draw from the same DeterministicRng state.
            Game Setup()
            {
                var game = NewSawmillGame(seed: 777);
                game.State.WoundDeck.Clear();
                game.State.WoundDeck.Add("spasm");
                game.State.WoundDiscard.Clear();
                game.State.WoundDiscard.AddRange(new[] { "discharge", "fear", "torn-ligament", "commiserate" });
                return game;
            }

            var game1 = Setup();
            var game2 = Setup();
            var inv1 = Inv(game1, "aira");
            var inv2 = Inv(game2, "aira");

            game1.GainWound(inv1, faceUp: false); // drains the last deck card
            game2.GainWound(inv2, faceUp: false);
            game1.GainWound(inv1, faceUp: false); // triggers the reshuffle
            game2.GainWound(inv2, faceUp: false);

            Assert.Equal(game1.State.WoundDeck, game2.State.WoundDeck);
            Assert.Equal(inv1.Wounds[1].CardId, inv2.Wounds[1].CardId);
            Assert.Equal(game1.State.RngState, game2.State.RngState);
        }

        [Fact]
        public void Drawing_with_both_the_wound_deck_and_discard_pile_empty_throws()
        {
            var game = NewSawmillGame();
            var aira = Inv(game, "aira");
            game.State.WoundDeck.Clear();
            game.State.WoundDiscard.Clear();

            var error = Assert.Throws<InvalidOperationException>(() => game.GainWound(aira, faceUp: false));
            Assert.Contains("26", error.Message);
            Assert.Empty(aira.Wounds); // the refused draw must not have added a Wound
        }
    }
}
