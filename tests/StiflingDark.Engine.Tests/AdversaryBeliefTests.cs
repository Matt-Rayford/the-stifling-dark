using StiflingDark.Bots;
using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// The Investigators' possibility set over the hidden Adversary. The one hard invariant —
    /// the true position is ALWAYS inside the set — is additionally verified over full games
    /// by the belief probe; these pin the mechanisms: Shadow-token collapse, movement-budget
    /// expansion, reveal-on-Bright pruning, and the Horror's bright-walking exception.
    /// </summary>
    public class AdversaryBeliefTests
    {
        private static Game NewGame(string adversary = "butcher")
        {
            var starts = new Dictionary<string, string>
            {
                ["aira"] = "285", ["lucy-belle"] = "286", ["mitchell"] = "305",
            };
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = 11,
                AdversaryId = adversary,
                InvestigatorStartSpaces = starts,
                MedicalItemSpaces = TestData.Db.Map("sawmill").Spaces
                    .Where(s => s.Kind == SpaceKind.MedicalItem)
                    .Take(TestData.Db.Config.ByInvestigatorCount[3].MedicalItemsOnBoard)
                    .Select(s => s.Id).ToList(),
            });
            foreach (string zone in game.Graph.Def.Zones.Keys)
            {
                game.PlaceHiddenEvidence(zone,
                    game.Graph.ZoneSpaces(zone).First(s => s.Kind == SpaceKind.Normal).Id);
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
            game.SetupAdversaryCards(
                adversary == "butcher" ? "rend" : "bufotoxin",
                adversary == "butcher"
                    ? new List<string> { "decay", "escalating-terror" }
                    : new List<string> { "devour" });
            game.FinishAdversarySetup();
            return game;
        }

        [Fact]
        public void A_new_shadow_token_collapses_the_set_to_one_turn_of_movement()
        {
            var game = NewGame();
            var belief = new AdversaryBelief(game);
            belief.Update();
            Assert.Contains("S-25", belief.Possible("main"));

            game.State.Round = 2;
            game.State.Adversary.ShadowTokens["main"] = "232";
            belief.Update();

            var possible = belief.Possible("main");
            Assert.Contains("232", possible);
            // Everything reachable in a turn stays possible; anything beyond it is ruled out.
            var reach = game.Graph.DistancesFrom("232", 9, game.State.Overlay);
            string far = game.Graph.Def.Spaces.Select(s => s.Id).First(id => !reach.ContainsKey(id));
            Assert.DoesNotContain(far, possible);
        }

        [Fact]
        public void Bright_spaces_are_pruned_but_not_for_the_horror()
        {
            var butcher = NewGame();
            var horror = NewGame("insatiable-horror");
            foreach (var game in new[] { butcher, horror })
            {
                game.State.Overlay.BrightZones.Add("O");
            }
            string officeSpace = butcher.Graph.ZoneSpaces("O").First(s => s.Kind == SpaceKind.Normal).Id;

            var butcherBelief = new AdversaryBelief(butcher);
            butcherBelief.Update();
            Assert.DoesNotContain(officeSpace, butcherBelief.Possible("main"));

            // The Horror walks Bright spaces hidden (it drops breadcrumb tokens instead of
            // being Revealed), so light rules nothing out for it.
            var horrorBelief = new AdversaryBelief(horror);
            horrorBelief.Update();
            Assert.Contains(officeSpace, horrorBelief.Possible("main"));
        }

        [Fact]
        public void A_revealed_figure_is_known_exactly_and_fuzzes_again_after_disappearing()
        {
            var game = NewGame();
            var belief = new AdversaryBelief(game);
            belief.Update();

            game.State.Round = 2;
            game.State.Adversary.Revealed = true;
            game.State.Adversary.Space = "232";
            belief.Update();
            Assert.Equal(new[] { "232" }, belief.Possible("main"));
            Assert.Empty(belief.HiddenUnion());

            // Disappear with no token left behind: it left from where it was last seen.
            game.State.Round = 3;
            game.State.Adversary.Revealed = false;
            game.State.Adversary.Space = "231";
            game.State.Adversary.ShadowTokens.Remove("main");
            belief.Update();
            Assert.Contains("231", belief.Possible("main"));
            Assert.True(belief.Possible("main").Count > 1, "hidden again means uncertain again");
        }
    }
}
