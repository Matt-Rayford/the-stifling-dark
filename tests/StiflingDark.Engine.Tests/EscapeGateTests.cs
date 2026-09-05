using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// Designer ruling (2026-08-31): once enough Evidence is turned in, play STOPS until the
    /// team commits to an Escape card — no further Investigator turn begins and the round
    /// does not pass to the Adversary — so the objective tokens are on the board before
    /// anyone acts again.
    /// </summary>
    public class EscapeGateTests
    {
        private static Game NewGame()
        {
            var starts = new Dictionary<string, string>
            {
                ["aira"] = "285", ["lucy-belle"] = "286", ["mitchell"] = "305",
            };
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = 5,
                AdversaryId = "butcher",
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
            game.SetupAdversaryCards("rend", new List<string> { "decay", "escalating-terror" });
            game.FinishAdversarySetup();
            return game;
        }

        private static int Required(Game game) =>
            TestData.Db.Config.ByInvestigatorCount[game.State.Investigators.Count].EvidenceRequiredForObjective;

        [Fact]
        public void Meeting_the_evidence_gate_holds_every_further_turn_until_the_escape_card_is_chosen()
        {
            var game = NewGame();
            game.BeginInvestigatorTurn("aira");
            game.State.Objective.EvidenceTurnedIn = Required(game);
            game.EndTurnWithoutFinalAction();

            Assert.True(game.EscapeChoicePending);
            Assert.Throws<InvalidOperationException>(() => game.BeginInvestigatorTurn("lucy-belle"));

            game.SelectEscapeCard(game.DrawEscapeChoices()[0]);
            Assert.False(game.EscapeChoicePending);
            game.BeginInvestigatorTurn("lucy-belle");
            Assert.Equal("lucy-belle", game.State.ActiveInvestigator);
        }

        [Fact]
        public void The_round_does_not_pass_to_the_adversary_while_the_escape_card_is_owed()
        {
            var game = NewGame();
            foreach (string inv in new[] { "aira", "lucy-belle" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.BeginInvestigatorTurn("mitchell");
            game.State.Objective.EvidenceTurnedIn = Required(game);
            game.EndTurnWithoutFinalAction(); // the last turn of the round

            Assert.Equal(GamePhase.InvestigatorTurns, game.State.Phase);
            Assert.Null(game.State.ActiveInvestigator);

            game.SelectEscapeCard(game.DrawEscapeChoices()[0]);
            Assert.Equal(GamePhase.AdversaryTurn, game.State.Phase);
        }
    }
}
