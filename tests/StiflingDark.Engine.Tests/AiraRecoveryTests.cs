using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>Aira's free Involved Action converts to an Interact (Game.EndTurn), so a turn
    /// that goes on afterwards still ends with the automatic Rest and Charge — designer
    /// confirmation 2026-10-27 that the Charge comes back too.</summary>
    public class AiraRecoveryTests
    {
        [Fact]
        public void Aira_recovers_after_a_free_involved_action()
        {
            var game = TestData.NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Charge = 1;
            aira.Stamina = 3;
            game.BeginInvestigatorTurn("aira");
            game.UseMinorAbility();
            game.TakeInvolvedAction();
            Assert.Equal(FinalActionKind.None, aira.FinalAction);
            game.EndTurnWithoutFinalAction();
            Assert.Equal(2, aira.Charge);
            Assert.Equal(4, aira.Stamina);
        }

        [Fact]
        public void Aira_recovers_after_a_free_evidence_turn_in_over_the_wire()
        {
            var game = TestData.NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Charge = 1;
            aira.Stamina = 3;
            aira.Space = game.Graph.Def.Spaces.First(s => s.Kind == SpaceKind.Computer).Id;
            aira.EvidenceCarried.Add("L");
            game.BeginInvestigatorTurn("aira");
            using (game.ActingAs("aira"))
            {
                game.UseMinorAbility();
                game.TurnInEvidence(new List<(string, string, string?, string?)> { ("L", "medical-item", null, null) });
                Assert.Equal(FinalActionKind.None, aira.FinalAction);
                Assert.Contains("aira", game.State.OpenTurns);
                game.EndTurnWithoutFinalAction();
            }
            Assert.Equal(2, aira.Charge);
            Assert.Equal(4, aira.Stamina);
        }
    }
}
