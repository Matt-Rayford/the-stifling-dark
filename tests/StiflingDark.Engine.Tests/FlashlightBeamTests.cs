using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class FlashlightBeamTests
    {
        private static MapGraph Sawmill => new(TestData.Db.Map("sawmill"));
        private static FlashlightBeam Beam => new(TestData.Db.Flashlight);

        [Fact]
        public void Own_space_is_always_bright()
        {
            var bright = Beam.ComputeBright(Sawmill, "232", 0, NoLineOfSightBlocker.None);
            Assert.Contains("232", bright);
        }

        [Fact]
        public void Beam_is_directional()
        {
            var graph = Sawmill;
            // Space 232 sits in the open yard; 233 is its eastern neighbor, 231 its western.
            var east = Beam.ComputeBright(graph, "232", 0, NoLineOfSightBlocker.None);
            var west = Beam.ComputeBright(graph, "232", Math.PI, NoLineOfSightBlocker.None);

            Assert.Contains("233", east);
            Assert.DoesNotContain("231", east);
            Assert.Contains("231", west);
            Assert.DoesNotContain("233", west);
        }

        [Fact]
        public void Beam_reaches_several_spaces_but_not_past_its_length()
        {
            var graph = Sawmill;
            var bright = Beam.ComputeBright(graph, "232", 0, NoLineOfSightBlocker.None);
            // A sensible beam lights a handful of spaces, not the whole board.
            Assert.InRange(bright.Count, 3, 12);

            // Nothing farther than the template length (5.2 pitches) can be lit.
            var origin = graph.Space("232");
            double maxReach = TestData.Db.Flashlight.LengthInSpacePitches * graph.Def.SpacePitch;
            foreach (string id in bright)
            {
                var s = graph.Space(id);
                double d = Math.Sqrt((s.X - origin.X) * (s.X - origin.X) + (s.Y - origin.Y) * (s.Y - origin.Y));
                Assert.True(d <= maxReach, $"{id} is {d:F0}px away, beyond the beam length {maxReach:F0}px");
            }
        }

        [Fact]
        public void Rotating_the_beam_sweeps_the_full_circle()
        {
            var graph = Sawmill;
            var union = new HashSet<string>();
            for (int step = 0; step < 24; step++)
            {
                union.UnionWith(Beam.ComputeBright(graph, "232", step * Math.PI / 12, NoLineOfSightBlocker.None));
            }
            // All 6 hex neighbors of 232 must be coverable by some aim.
            foreach (string neighbor in new[] { "231", "233", "215", "216", "249", "250" })
            {
                Assert.Contains(neighbor, union);
            }
        }

        [Fact]
        public void Blocked_sight_lines_leave_spaces_dark()
        {
            var graph = Sawmill;
            var blockEverything = new BlockAll();
            var bright = Beam.ComputeBright(graph, "232", 0, blockEverything);
            Assert.Equal(new[] { "232" }, bright.OrderBy(s => s));
        }

        private sealed class BlockAll : ILineOfSightBlocker
        {
            public bool Blocks(double x1, double y1, double x2, double y2) => true;
        }

        /// <summary>
        /// Designer ruling (2026-08-27, physical-board comparison): the 7 PRINTED sight
        /// lines dictate line of sight. From 171 aimed into the Office, the straight ray to
        /// O-10's centre clips the wall by door O-11 — but a printed line threads the open
        /// door, so O-10 is Bright, exactly as the physical template shows.
        /// </summary>
        [Fact]
        public void Printed_sight_lines_reach_through_a_door_gap()
        {
            var graph = Sawmill;
            var mask = TestData.Db.LosMask("sawmill");
            Assert.NotNull(mask);
            double cx(string id) => graph.Space(id).X;
            double cy(string id) => graph.Space(id).Y;
            Assert.True(mask!.Blocks(cx("171"), cy("171"), cx("O-10"), cy("O-10")),
                "premise: the straight centre ray is wall-blocked");

            var bright = Beam.ComputeBright(graph, "171", 3.39, mask);
            Assert.Contains("O-10", bright);
        }
    }
}
