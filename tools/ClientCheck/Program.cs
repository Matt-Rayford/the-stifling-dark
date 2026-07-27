using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Deterministic sanity check for the flashlight beam's angle math — the one thing a
    /// mirrored beam would get wrong, and the most likely explanation for the designer's report
    /// of a one-sided beam during the first playtest.
    ///
    /// Runs the REAL engine (StiflingDark.Engine.dll, referenced directly — not the UnityEngine
    /// stubs UiCheck uses) over the real sawmill map and the real flashlight template, and checks:
    ///
    ///   1. The engine's own FlashlightBeam.ComputeBright at a known angle, against exact,
    ///      independently-verified space ids — ground truth that "the engine itself is not
    ///      the bug" before blaming the client.
    ///   2. BoardModel.AngleFromWorldOffset — the pure function BoardView.cs calls to turn a
    ///      mouse position into the engine's board-space angle — feeding its own output back
    ///      into ComputeBright, so a left/right or up/down flip in that one line cannot go
    ///      unnoticed again.
    ///   3. A designer-validated ground-truth case reproducing a photo of the physical
    ///      template laid on the real board (space "179", the real sawmill LOS mask), pinned so
    ///      the 5.2-pitch scale or the teardrop coverage cannot silently drift later.
    ///
    /// `~/.dotnet/dotnet build tools/ClientCheck` runs this as a post-build step (see the
    /// RunSanityCheck target in ClientCheck.csproj) — the build fails if any assertion fails,
    /// not just if the code fails to compile.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                Run();
                Console.WriteLine("ClientCheck sanity checks passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ClientCheck sanity check FAILED: " + ex.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string gameDataDir = FindGameDataDir();
            var db = GameDatabase.Load(gameDataDir);
            var graph = new MapGraph(db.Map("sawmill"));
            var beam = new FlashlightBeam(db.Flashlight);

            // NoLineOfSightBlocker.None, not the raster mask: these first four checks are about
            // the angle math, not the sawmill's walls, and must not depend on the
            // (regeneratable, binary) LOS mask asset lining up with these exact space ids.
            var none = NoLineOfSightBlocker.None;

            // --- 1. Engine ground truth: aimed due east (0 rad) from space 232. ---
            var east = beam.ComputeBright(graph, "232", 0.0, none);
            AssertSetEqual(east, new[] { "232", "217", "218", "233", "234", "235", "253" },
                "ComputeBright(\"232\", 0 rad)");

            // --- 2. The client's angle helper: mouse RIGHT of the figure -> 0 rad. ---
            double rightAngle = BoardModel.AngleFromWorldOffset(10.0, 0.0);
            AssertAngle(0.0, rightAngle, "AngleFromWorldOffset(+x, 0) [mouse RIGHT]");

            // --- 3. Mouse UP (world +y) must map to board-UP (engine -90deg), NOT board-down.
            //
            // Board space is y-down; world is y-up; BoardView draws world = (x, -y) (see its
            // class comment). So board-up (smaller board Y, engine angle -90deg) renders at
            // LARGER world y (screen-up), and engine +90deg (board-down, larger board Y) renders
            // at world-DOWN. engine 90deg = board-down = world-down: a mapping that sent
            // mouse-up to engine +90deg would light the board-DOWN side instead of the
            // board-UP side -- a mirrored beam, and a very plausible read of "one-sided" from
            // the playtest. Space 232's board-down neighbours are {268,281,300,301,302} (all at
            // LARGER board Y than 232, see game-data/maps/sawmill.json); its board-up
            // neighbours are {168,169,170,182,183,198} (all at SMALLER board Y).
            double upAngle = BoardModel.AngleFromWorldOffset(0.0, 10.0);
            AssertAngle(-Math.PI / 2, upAngle, "AngleFromWorldOffset(0, +y) [mouse UP]");
            var up = beam.ComputeBright(graph, "232", upAngle, none);
            var southSide = new[] { "268", "281", "300", "301", "302" };
            var northSide = new[] { "168", "169", "170", "182", "183", "198" };
            var southLit = southSide.Where(up.Contains).ToList();
            if (southLit.Count > 0)
            {
                throw new Exception("Mouse UP lit the board-DOWN side (" + string.Join(",", southLit) +
                    ") -- the beam is MIRRORED.");
            }
            if (!northSide.Any(up.Contains))
            {
                throw new Exception("Mouse UP lit none of the expected board-UP side (" +
                    string.Join(",", northSide) + ") -- got " + string.Join(",", up.OrderBy(s => s)));
            }

            // --- 4. The mirror image: mouse DOWN (world -y) must map to engine +90deg, exactly
            // the {268,281,300,301,302} board-down side -- confirming where that set actually
            // belongs (DOWN, not UP), so there is no ambiguity about the correct mapping. ---
            double downAngle = BoardModel.AngleFromWorldOffset(0.0, -10.0);
            AssertAngle(Math.PI / 2, downAngle, "AngleFromWorldOffset(0, -y) [mouse DOWN]");
            var down = beam.ComputeBright(graph, "232", downAngle, none);
            AssertSetEqual(down, new[] { "232", "268", "281", "300", "301", "302" },
                "ComputeBright at the mouse-DOWN angle");

            // --- 5. Designer-validated ground truth from a photo of the physical template laid
            // on the real board: space "179", aimed at -2.908 rad, WITH the real sawmill LOS
            // mask (this one is about matching the physical template exactly, mask and all).
            // Pinned so the 5.2-pitch scale or the teardrop coverage can't silently drift. ---
            var sawmillMask = db.LosMask("sawmill") ?? (ILineOfSightBlocker)none;
            var physical = beam.ComputeBright(graph, "179", -2.908, sawmillMask);
            AssertSetEqual(physical,
                new[] { "179", "150", "151", "161", "162", "163", "164", "175", "176", "177", "178" },
                "ComputeBright(\"179\", -2.908 rad, real sawmill LOS mask)");
        }

        private static void AssertAngle(double expectedRadians, double actualRadians, string label)
        {
            double delta = Math.Abs(Delta(expectedRadians, actualRadians));
            if (delta > 1e-9)
            {
                throw new Exception(label + ": expected " + expectedRadians + " rad, got " +
                    actualRadians + " rad (delta " + delta + ")");
            }
        }

        private static double Delta(double a, double b)
        {
            double d = a - b;
            while (d > Math.PI)
            {
                d -= 2 * Math.PI;
            }
            while (d < -Math.PI)
            {
                d += 2 * Math.PI;
            }
            return d;
        }

        private static void AssertSetEqual(HashSet<string> actual, string[] expected, string label)
        {
            var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
            if (!expectedSet.SetEquals(actual))
            {
                throw new Exception(label + ": expected {" +
                    string.Join(",", expectedSet.OrderBy(s => s, StringComparer.Ordinal)) + "}, got {" +
                    string.Join(",", actual.OrderBy(s => s, StringComparer.Ordinal)) + "}");
            }
        }

        /// <summary>Walks up from the build output to find the repo's game-data/ folder, so this
        /// works whether it's run from the repo root or from within tools/ClientCheck.</summary>
        private static string FindGameDataDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "game-data");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "config.json")))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                "Could not find game-data/ above " + AppContext.BaseDirectory);
        }
    }
}
