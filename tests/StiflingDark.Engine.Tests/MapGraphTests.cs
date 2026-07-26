using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class MapGraphTests
    {
        private static MapGraph Sawmill => new(TestData.Db.Map("sawmill"));
        private static MapGraph Park => new(TestData.Db.Map("amusement-park"));

        [Fact]
        public void Every_zone_has_a_general_space_for_evidence()
        {
            foreach (var map in TestData.Db.Maps)
            {
                var graph = new MapGraph(map);
                foreach (string zone in map.Zones.Keys)
                {
                    Assert.Contains(graph.ZoneSpaces(zone), s => s.Kind == SpaceKind.Normal);
                }
            }
        }

        [Fact]
        public void Dark_spaces_cost_2_for_investigators_and_1_for_the_adversary()
        {
            var graph = Sawmill;
            var overlay = new BoardOverlay();
            // S-18 and S-19 are adjacent dashed (Dark) Sawmill-zone spaces.
            Assert.Equal(2, graph.TryStep(FigureKind.Investigator, "S-18", "S-19", overlay)!.Cost);
            Assert.Equal(1, graph.TryStep(FigureKind.Adversary, "S-18", "S-19", overlay)!.Cost);
            Assert.Equal(1, graph.TryStep(FigureKind.Spirit, "S-18", "S-19", overlay)!.Cost);
        }

        [Fact]
        public void Zone_light_tokens_override_printed_light()
        {
            var graph = Sawmill;
            var overlay = new BoardOverlay();
            Assert.Equal(LightLevel.Dark, graph.EffectiveLight("S-18", overlay));

            overlay.DimZones.Add("S");
            Assert.Equal(LightLevel.Dim, graph.EffectiveLight("S-18", overlay));
            Assert.Equal(1, graph.TryStep(FigureKind.Investigator, "S-19", "S-18", overlay)!.Cost);

            overlay.BrightZones.Add("S");
            Assert.Equal(LightLevel.Bright, graph.EffectiveLight("S-18", overlay));
        }

        [Fact]
        public void Locked_and_damaged_doors_block_movement_but_destroyed_doors_do_not()
        {
            var graph = Sawmill;
            var overlay = new BoardOverlay();
            // S-20 is adjacent to the Door space S-23 (rulebook example).
            Assert.NotNull(graph.TryStep(FigureKind.Investigator, "S-20", "S-23", overlay));

            overlay.DoorStates["S-23"] = DoorState.Locked;
            Assert.Null(graph.TryStep(FigureKind.Investigator, "S-20", "S-23", overlay));
            Assert.Null(graph.TryStep(FigureKind.Adversary, "S-20", "S-23", overlay));

            overlay.DoorStates["S-23"] = DoorState.Damaged;
            Assert.Null(graph.TryStep(FigureKind.Investigator, "S-20", "S-23", overlay));

            overlay.DoorStates["S-23"] = DoorState.Destroyed;
            Assert.NotNull(graph.TryStep(FigureKind.Investigator, "S-20", "S-23", overlay));
        }

        [Fact]
        public void Locked_doors_break_within_X_counting()
        {
            var graph = Sawmill;
            var open = new BoardOverlay();
            var locked = new BoardOverlay();
            locked.DoorStates["S-23"] = DoorState.Locked;

            var distOpen = graph.DistancesFrom("S-20", 5, open);
            var distLocked = graph.DistancesFrom("S-20", 5, locked);
            Assert.Equal(1, distOpen["S-23"]);
            Assert.False(distLocked.ContainsKey("S-23"));
        }

        [Fact]
        public void Windows_are_passable_and_flagged()
        {
            var graph = Sawmill;
            var overlay = new BoardOverlay();
            // 117 - S-17 is a Window crossing; S-17 is Dark.
            var investigator = graph.TryStep(FigureKind.Investigator, "117", "S-17", overlay)!;
            Assert.True(investigator.CrossesWindow);
            Assert.Equal(2, investigator.Cost);

            // Adversary: +1 MP for the window on top of the 1 MP entry.
            var adversary = graph.TryStep(FigureKind.Adversary, "117", "S-17", overlay)!;
            Assert.True(adversary.CrossesWindow);
            Assert.Equal(2, adversary.Cost);

            // An Open Window token removes the penalty for everyone.
            overlay.OpenWindows.Add(BoardOverlay.EdgeKey("117", "S-17"));
            Assert.False(graph.TryStep(FigureKind.Investigator, "117", "S-17", overlay)!.CrossesWindow);
            Assert.Equal(1, graph.TryStep(FigureKind.Adversary, "117", "S-17", overlay)!.Cost);

            // A False Window token blocks the crossing entirely.
            overlay.OpenWindows.Clear();
            overlay.FalseWindows.Add(BoardOverlay.EdgeKey("117", "S-17"));
            Assert.Null(graph.TryStep(FigureKind.Investigator, "117", "S-17", overlay));
        }

        [Fact]
        public void Mirror_doors_only_pass_their_open_color()
        {
            var graph = Park;
            var overlay = new BoardOverlay { OpenMirrorColor = MirrorDoorColor.Red };
            // M-9 - M-14 is a blue mirror door.
            Assert.Null(graph.TryStep(FigureKind.Investigator, "M-9", "M-14", overlay));
            Assert.Null(graph.TryStep(FigureKind.Adversary, "M-9", "M-14", overlay));
            // Spirits ignore Mirror Maze doors.
            Assert.NotNull(graph.TryStep(FigureKind.Spirit, "M-9", "M-14", overlay));

            overlay.OpenMirrorColor = MirrorDoorColor.Blue;
            Assert.NotNull(graph.TryStep(FigureKind.Investigator, "M-9", "M-14", overlay));
            // M-11 - M-12 is a red mirror door, now closed.
            Assert.Null(graph.TryStep(FigureKind.Investigator, "M-11", "M-12", overlay));
        }

        [Fact]
        public void Water_loop_follows_the_rulebook_current()
        {
            var graph = Park;
            Assert.Equal("T-4", graph.WaterNext("T-1", 2));
            // The channel passes through the T-29 switch platform: T-33 -> T-29 -> T-27.
            Assert.Equal("T-27", graph.WaterNext("T-33", 2));
        }

        [Fact]
        public void Ride_rotation_matches_the_rulebook_example()
        {
            var graph = Park;
            // Mada: 134 rotates to 97; next turn 97 rotates to 65.
            Assert.Equal("97", graph.RideNext("134"));
            Assert.Equal("65", graph.RideNext("97"));
            Assert.Null(graph.RideNext("146"));
        }

        [Fact]
        public void Carriage_links_count_as_adjacent_for_adversary_abilities_only()
        {
            var graph = Park;
            var overlay = new BoardOverlay();
            // Rulebook: 66 is considered adjacent to 65 for Adversary Attacks/Abilities.
            Assert.Contains("66", graph.AdjacentForAdversaryAbilities("65", overlay));
            Assert.Null(graph.TryStep(FigureKind.Adversary, "65", "66", overlay));
            Assert.Null(graph.TryStep(FigureKind.Investigator, "65", "66", overlay));
        }

        [Fact]
        public void Sawmill_is_fully_reachable_for_investigators()
        {
            var graph = Sawmill;
            var reachable = Reachable(graph, "286", new BoardOverlay());
            Assert.Equal(426, reachable.Count);
        }

        [Fact]
        public void Park_is_fully_reachable_except_midair_carriages()
        {
            var graph = Park;
            var union = new HashSet<string>();
            foreach (MirrorDoorColor color in new[] { MirrorDoorColor.Red, MirrorDoorColor.Green, MirrorDoorColor.Blue })
            {
                union.UnionWith(Reachable(graph, "142", new BoardOverlay { OpenMirrorColor = color }));
            }
            var unreachable = graph.Def.Spaces.Select(s => s.Id).Where(id => !union.Contains(id)).OrderBy(id => id).ToList();
            // Upper/middle carriages are boarded via ride rotation, never by normal movement.
            Assert.Equal(new[] { "117", "65", "71", "72", "79", "87", "88", "97" }, unreachable);
        }

        private static HashSet<string> Reachable(MapGraph graph, string from, BoardOverlay overlay)
        {
            var seen = new HashSet<string> { from };
            var frontier = new Queue<string>();
            frontier.Enqueue(from);
            while (frontier.Count > 0)
            {
                string current = frontier.Dequeue();
                foreach (var edge in graph.Def.Edges)
                {
                    string? other = edge.A == current ? edge.B : edge.B == current ? edge.A : null;
                    if (other != null && !seen.Contains(other) &&
                        graph.TryStep(FigureKind.Investigator, current, other, overlay) != null)
                    {
                        seen.Add(other);
                        frontier.Enqueue(other);
                    }
                }
            }
            return seen;
        }
    }
}
