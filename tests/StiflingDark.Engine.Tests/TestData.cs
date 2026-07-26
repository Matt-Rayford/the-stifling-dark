using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Tests
{
    /// <summary>Locates game-data/ from the test output directory and caches one shared database.</summary>
    public static class TestData
    {
        private static readonly Lazy<GameDatabase> Cached = new(() => GameDatabase.Load(GameDataDir()));

        public static GameDatabase Db => Cached.Value;

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
