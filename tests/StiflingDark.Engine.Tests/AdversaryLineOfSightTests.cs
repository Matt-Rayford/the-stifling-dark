using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Tests
{
    /// <summary>The Adversary's printed line of sight — "any part of your circle to any part
    /// of the target's" (Game.AdversaryHasLineOfSight) — against the Sawmill's raster mask.
    /// The centre line alone misses a fifth of the Sawmill's two-apart pairs (2026-10-27) —
    /// and, until the Light Switch rings were scrubbed from the mask, the Investigator
    /// standing on a switch right beside The Butcher.</summary>
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
            var pairs = PairsTwoApart(game).ToList();
            int centreBlocked = pairs.Count(p => !game.CentreLineOfSightClear(p.a, p.b));
            int rimBlocked = pairs.Count(p => !game.AdversaryHasLineOfSight(p.a, p.b));
            Assert.True(centreBlocked > 0, "expected some two-apart pairs to lose the centre line");
            Assert.True(rimBlocked < centreBlocked,
                $"rim reading blocked {rimBlocked} pairs, centre line {centreBlocked}");
        }

        /// <summary>Every ordered pair of spaces exactly two steps apart.</summary>
        public static IEnumerable<(string a, string b)> PairsTwoApart(Game game) =>
            game.Graph.Def.Spaces.SelectMany(s =>
                game.Graph.DistancesFrom(s.Id, 2, game.State.Overlay)
                    .Where(kv => kv.Value == 2)
                    .Select(kv => (s.Id, kv.Key)));
    }
}
