using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>The CV-extracted LOS masks, validated against pairs whose blocked/clear
    /// status was independently confirmed during the map-extraction QA review.</summary>
    public class LosMaskTests
    {
        private static bool Blocked(string mapId, string a, string b)
        {
            var mask = TestData.Db.LosMask(mapId);
            Assert.NotNull(mask);
            var graph = new MapGraph(TestData.Db.Map(mapId));
            var sa = graph.Space(a);
            var sb = graph.Space(b);
            return mask!.Blocks(sa.X, sa.Y, sb.X, sb.Y);
        }

        [Theory]
        [InlineData("258", "259")] // building wall
        [InlineData("190", "191")] // building wall
        [InlineData("227", "228")] // log pile obstacle
        [InlineData("S-31", "S-36")] // saw machine
        [InlineData("32", "L-3")] // lumber shed wall
        [InlineData("L-8", "40")] // lumber shed wall
        public void Sawmill_walls_and_obstacles_block(string a, string b) =>
            Assert.True(Blocked("sawmill", a, b));

        [Theory]
        [InlineData("232", "233")] // open yard
        [InlineData("117", "S-17")] // window: never blocks sight
        [InlineData("285", "286")] // start spaces
        [InlineData("S-18", "S-19")] // open interior
        [InlineData("7", "8")] // window
        [InlineData("S-35", "S-40")] // orange outline: blocks movement, NOT sight
        public void Sawmill_open_lines_and_windows_are_clear(string a, string b) =>
            Assert.False(Blocked("sawmill", a, b));

        [Theory]
        [InlineData("M-1", "M-2")] // mirror wall
        [InlineData("104", "C-1")] // carousel rim
        [InlineData("22", "M-11")] // curtain: passable but opaque
        [InlineData("M-6", "M-11")] // hatched mirror wall
        public void Park_walls_and_curtains_block(string a, string b) =>
            Assert.True(Blocked("amusement-park", a, b));

        [Theory]
        [InlineData("M-9", "M-14")] // mirror door: never blocks sight
        [InlineData("71", "72")] // within one carriage
        [InlineData("G-3", "1")] // window
        [InlineData("142", "153")] // open midway (pennant art must not block)
        public void Park_doors_windows_and_open_ground_are_clear(string a, string b) =>
            Assert.False(Blocked("amusement-park", a, b));

        [Fact]
        public void Flashlight_beams_are_shadowed_by_walls()
        {
            var game = LosGame();
            // Aim from 190 at its wall-separated neighbor 191: fully in beam range yet dark.
            var inv = game.State.Investigators.First(i => i.DefId == "aira");
            inv.Space = "190";
            game.BeginInvestigatorTurn("aira");
            var graph = game.Graph;
            var s190 = graph.Space("190");
            var s191 = graph.Space("191");
            double angle = Math.Atan2(s191.Y - s190.Y, s191.X - s190.X);
            var bright = game.PreviewFlashlight("aira", angle);
            Assert.DoesNotContain("191", bright);
            Assert.Contains("190", bright); // own space always lit
        }

        [Fact]
        public void Butcher_cannot_stalk_through_a_wall()
        {
            var game = LosGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Space = "191"; // inside the south-east building
            foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped))
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.State.Adversary.Space = "176"; // outside the wall, within 8 spaces
            var error = Assert.Throws<InvalidOperationException>(
                () => game.ButcherStalk(new List<string> { "aira" }));
            Assert.Contains("line of sight", error.Message);
        }

        private static Game LosGame()
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = 77,
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
            game.SetupAdversaryCards("eviscerate", new List<string> { "sinister-gaze", "escalating-terror" });
            var evidence = new Dictionary<string, string>
            {
                ["L"] = "L-1", ["K"] = "K-1", ["G"] = "G-1", ["S"] = "S-1", ["O"] = "O-2",
            };
            foreach (var (zone, space) in evidence)
            {
                game.PlaceHiddenEvidence(zone, space);
            }
            bool cursed = false;
            foreach (var poi in game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
            {
                string target = game.Graph.DistancesFrom(poi.Id, 2, game.State.Overlay).Keys
                    .First(id => game.Graph.Space(id).Kind == SpaceKind.Normal);
                game.PlacePoiToken(poi.Id, target, cursedFront: !cursed);
                cursed = true;
            }
            game.PlaceAdversary("S-25");
            game.FinishAdversarySetup();
            return game;
        }
    }
}
