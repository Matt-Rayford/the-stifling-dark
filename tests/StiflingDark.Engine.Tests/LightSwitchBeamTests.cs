using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Tests
{
    /// <summary>A Flashlight can light a Light Switch space like any other. The CV-extracted
    /// masks had the magenta switch rings and icons marked as walls, so no beam could ever
    /// reach one (2026-10-27 playtest); tools/patch_los_mask.py scrubs those circles.</summary>
    public class LightSwitchBeamTests
    {
        [Theory]
        [InlineData("sawmill")]
        [InlineData("amusement-park")]
        public void Switch_circles_hold_no_obstacle_pixels(string mapId)
        {
            var mask = TestData.Db.LosMask(mapId);
            Assert.NotNull(mask);
            var graph = new MapGraph(TestData.Db.Map(mapId));
            double radius = graph.Def.SpaceRadius;
            foreach (var sw in graph.Def.Spaces.Where(s => s.Kind == SpaceKind.LightSwitch))
            {
                // Rays across the circle in every direction: all must be clear.
                for (int i = 0; i < 12; i++)
                {
                    double a = Math.PI * i / 12;
                    Assert.False(mask!.Blocks(sw.X - radius * Math.Cos(a), sw.Y - radius * Math.Sin(a),
                        sw.X + radius * Math.Cos(a), sw.Y + radius * Math.Sin(a)), sw.Id);
                }
            }
        }

        [Fact]
        public void Every_sawmill_switch_lights_from_a_neighbouring_space()
        {
            var game = TestData.NewSawmillGame();
            var graph = game.Graph;
            foreach (var sw in graph.Def.Spaces.Where(s => s.Kind == SpaceKind.LightSwitch))
            {
                bool lit = graph.DistancesFrom(sw.Id, 1, game.State.Overlay).Keys
                    .Where(n => n != sw.Id)
                    .Any(n =>
                    {
                        var from = graph.Space(n);
                        double angle = Math.Atan2(sw.Y - from.Y, sw.X - from.X);
                        return game.PreviewFlashlightAt(n, angle).Contains(sw.Id);
                    });
                Assert.True(lit, sw.Id + " cannot be lit from any neighbour");
            }
        }
    }
}
