using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Tests
{
    /// <summary>Locates game-data/ from the test output directory and caches one shared database.</summary>
    public static class TestData
    {
        private static readonly Lazy<GameDatabase> Cached = new(() => GameDatabase.Load(GameDataDir()));

        public static GameDatabase Db => Cached.Value;

        /// <summary>A fully set-up 3-Investigator Sawmill game against The Butcher, ready for
        /// the first Investigator turn — for tests that need a legal board, not a scenario.</summary>
        public static Game NewSawmillGame(ulong seed = 5) =>
            NewSawmillGame(new[] { "aira", "lucy-belle", "mitchell" }, seed);

        /// <summary>The Sawmill has exactly 4 Start spaces, so up to 4 Investigators are seated.</summary>
        private static readonly string[] SawmillStartSpaces = { "285", "286", "305", "307" };

        public static Game NewSawmillGame(IReadOnlyList<string> investigatorIds, ulong seed = 5)
        {
            var starts = new Dictionary<string, string>();
            for (int i = 0; i < investigatorIds.Count; i++)
            {
                starts[investigatorIds[i]] = SawmillStartSpaces[i];
            }
            var game = Game.NewGame(Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = "butcher",
                InvestigatorStartSpaces = starts,
                MedicalItemSpaces = Db.Map("sawmill").Spaces
                    .Where(s => s.Kind == SpaceKind.MedicalItem)
                    .Take(Db.Config.ByInvestigatorCount[investigatorIds.Count].MedicalItemsOnBoard)
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

        public static string GameDataDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "game-data");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not find game-data/ above the test directory.");
        }
    }
}
