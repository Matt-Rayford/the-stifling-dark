using StiflingDark.Bots;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace BotArena;

/// <summary>Everything randomized for one game, kept so the result row can report it.</summary>
public sealed class Matchup
{
    public ulong Seed { get; set; }
    public string Scenario { get; set; } = "";
    public string Adversary { get; set; } = "";
    public string Attack { get; set; } = "";
    public List<string> Abilities { get; set; } = new();
    public List<string> Investigators { get; set; } = new();
    public int InvestigatorCount => Investigators.Count;
}

/// <summary>
/// Builds one fully set-up game from a seed: scenario, roster, starts and medical tokens here,
/// then <see cref="AdversarySetup"/> for the Adversary's own hidden placements and card
/// loadout. Every choice comes from the seeded RNG so a seed replays exactly.
/// </summary>
public static class GameFactory
{
    private static readonly string[] Scenarios = { "sawmill", "amusement-park" };
    private static readonly string[] Adversaries = { "butcher", "insatiable-horror", "cult-of-hunlow" };

    public static Game Create(GameDatabase db, ulong seed, DeterministicRng rng, out Matchup matchup)
    {
        string scenario = Pick(rng, Scenarios);
        string adversary = Pick(rng, Adversaries);
        int count = 2 + rng.Next(3);

        var roster = db.Investigators.Where(i => i.Set == "base")
            .Select(i => i.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        rng.Shuffle(roster);
        var chosen = roster.Take(count).ToList();

        var map = db.Map(scenario);
        var starts = map.Spaces.Where(s => s.Kind == SpaceKind.Start)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        rng.Shuffle(starts);

        var startSpaces = new Dictionary<string, string>();
        for (int i = 0; i < count; i++)
        {
            startSpaces[chosen[i]] = starts[i % starts.Count];
        }

        int medicalCount = db.Config.ByInvestigatorCount[count].MedicalItemsOnBoard;
        var medicalPool = map.Spaces.Where(s => s.Kind == SpaceKind.MedicalItem)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        rng.Shuffle(medicalPool);

        var game = Game.NewGame(db, new GameSetup
        {
            ScenarioId = scenario,
            Seed = seed,
            AdversaryId = adversary,
            InvestigatorStartSpaces = startSpaces,
            MedicalItemSpaces = medicalPool.Take(medicalCount).ToList(),
        });

        // The Adversary's own secret setup is the shared bot brain, so the arena and a
        // BOT-filled Adversary seat on the server take identical decisions from a seed.
        var loadout = AdversarySetup.Run(game, rng, startSpaces.Values);

        matchup = new Matchup
        {
            Seed = seed,
            Scenario = scenario,
            Adversary = adversary,
            Attack = loadout.Attack,
            Abilities = loadout.Abilities,
            Investigators = chosen,
        };
        return game;
    }

    private static string Pick(DeterministicRng rng, string[] options) => options[rng.Next(options.Length)];
}
