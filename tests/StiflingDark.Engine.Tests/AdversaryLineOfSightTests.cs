using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Tests
{
    /// <summary>The Adversary's printed line of sight — "any part of your circle to any part
    /// of the target's" (Game.AdversaryHasLineOfSight) — against the Sawmill's raster mask.
    /// The centre line alone cut 29 of the Sawmill's 871 adjacent pairs (2026-10-27), which is
    /// how a Stalk skipped the Investigator standing right beside The Butcher.</summary>
    public class AdversaryLineOfSightTests
    {
        [Fact]
        public void Every_adjacent_pair_is_in_line_of_sight()
        {
            var game = TestData.NewSawmillGame();
            var hidden = game.Graph.Def.Edges
                .Where(e => e.Type == EdgeType.Move && !game.AdversaryHasLineOfSight(e.A, e.B))
                .Select(e => e.A + "-" + e.B)
                .ToList();
            Assert.Empty(hidden);
        }

        [Fact]
        public void The_rim_reading_sees_past_what_the_centre_line_misses()
        {
            var game = TestData.NewSawmillGame();
            var centreBlocked = game.Graph.Def.Edges.Where(e =>
                e.Type == EdgeType.Move && !game.CentreLineOfSightClear(e.A, e.B)).ToList();
            Assert.NotEmpty(centreBlocked);
            Assert.All(centreBlocked, e => Assert.True(game.AdversaryHasLineOfSight(e.A, e.B)));
        }
    }
}
