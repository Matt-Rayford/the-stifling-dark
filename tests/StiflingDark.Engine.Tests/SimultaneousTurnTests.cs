using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Tests
{
    /// <summary>Several Investigator turns open at once (GameState.SimultaneousTurns) and the
    /// acting-Investigator scope every command then resolves through (Game.ActingAs).</summary>
    public class SimultaneousTurnTests
    {
        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        [Fact]
        public void A_second_turn_is_refused_at_a_sequential_table()
        {
            var game = TestData.NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            var refused = Assert.Throws<InvalidOperationException>(() => game.BeginInvestigatorTurn("lucy-belle"));
            Assert.Contains("has not finished", refused.Message);
        }

        [Fact]
        public void Two_turns_run_side_by_side_and_each_command_names_its_actor()
        {
            var game = TestData.NewSawmillGame();
            game.State.SimultaneousTurns = true;
            game.BeginInvestigatorTurn("aira");
            game.BeginInvestigatorTurn("lucy-belle");
            Assert.Equal(new[] { "aira", "lucy-belle" }, game.State.OpenTurns);
            Assert.Null(game.State.ActiveInvestigator);

            var ambiguous = Assert.Throws<InvalidOperationException>(() => game.Sprint());
            Assert.Contains("Several turns", ambiguous.Message);
            using (game.ActingAs("mitchell"))
            {
                Assert.Contains("not mitchell's turn",
                    Assert.Throws<InvalidOperationException>(() => game.Sprint()).Message);
            }
            using (game.ActingAs("aira"))
            {
                game.Sprint();
            }
            Assert.True(Inv(game, "aira").SprintedOrRested);
            Assert.False(Inv(game, "lucy-belle").SprintedOrRested);

            Assert.Throws<InvalidOperationException>(() => game.BeginInvestigatorTurn("aira")); // already open

            using (game.ActingAs("aira"))
            {
                game.EndTurnWithoutFinalAction();
            }
            Assert.Equal(new[] { "lucy-belle" }, game.State.OpenTurns);
            Assert.Equal(GamePhase.InvestigatorTurns, game.State.Phase);
            game.EndTurnWithoutFinalAction(); // one open turn: no actor needed
            Assert.Empty(game.State.OpenTurns);

            game.BeginInvestigatorTurn("mitchell");
            game.EndTurnWithoutFinalAction();
            Assert.Equal(GamePhase.AdversaryTurn, game.State.Phase);
        }

        [Fact]
        public void A_window_crossing_waits_on_its_crosser_alone()
        {
            var game = TestData.NewSawmillGame();
            game.State.SimultaneousTurns = true;
            var window = game.Graph.Def.Edges.First(e => e.Type == EdgeType.Window &&
                game.Graph.Space(e.A).Kind == SpaceKind.Normal && game.Graph.Space(e.B).Kind == SpaceKind.Normal);
            var aira = Inv(game, "aira");
            aira.Space = window.A;
            game.BeginInvestigatorTurn("aira");
            game.BeginInvestigatorTurn("lucy-belle");

            using (game.ActingAs("aira"))
            {
                game.MoveStep(window.B);
            }
            Assert.True(game.State.PendingWindowChoice);
            Assert.Equal("aira", game.State.PendingWindowInvestigator);
            Assert.True(game.ViewFor(ViewRole.Investigator, "aira").PendingWindowChoice);
            Assert.False(game.ViewFor(ViewRole.Investigator, "lucy-belle").PendingWindowChoice);

            using (game.ActingAs("lucy-belle"))
            {
                game.Sprint(); // not her crossing: her turn goes on
                Assert.Contains("No window crossing",
                    Assert.Throws<InvalidOperationException>(() => game.ResolveWindow(true)).Message);
            }
            using (game.ActingAs("aira"))
            {
                Assert.Contains("Resolve the Window",
                    Assert.Throws<InvalidOperationException>(() => game.Sprint()).Message);
                game.ResolveWindow(stopAndLoseStamina: true);
            }
            Assert.False(game.State.PendingWindowChoice);
        }
    }
}
