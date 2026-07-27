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
/// Builds one fully set-up game from a seed: scenario, adversary, loadout, roster, starts,
/// medical tokens, hidden Evidence, POI tokens, the Adversary standee (and the Cult's
/// Cultists + Altar). Every choice comes from the seeded RNG so a seed replays exactly.
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

        // Hidden Evidence: 1 per Zone, on a random General space of that Zone.
        foreach (string zone in game.Graph.Def.Zones.Keys.OrderBy(z => z, StringComparer.Ordinal))
        {
            var candidates = game.Graph.ZoneSpaces(zone).Where(s => s.Kind == SpaceKind.Normal)
                .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            game.PlaceHiddenEvidence(zone, candidates[rng.Next(candidates.Count)]);
        }

        // POI tokens: 1 per printed Point of Interest, on a General space within 2, exactly 1 Cursed front.
        var pois = game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        int cursedIndex = rng.Next(pois.Count);
        for (int i = 0; i < pois.Count; i++)
        {
            var candidates = game.Graph.DistancesFrom(pois[i], 2, game.State.Overlay).Keys
                .Where(id => game.Graph.Space(id).Kind == SpaceKind.Normal)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            game.PlacePoiToken(pois[i], candidates[rng.Next(candidates.Count)], cursedFront: i == cursedIndex);
        }

        // Adversary standee: a General space at least 5 spaces from every Investigator.
        var investigatorSpaces = startSpaces.Values.ToHashSet();
        var general = game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.Normal)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var far = general.Where(id => !investigatorSpaces.Contains(id) && investigatorSpaces.All(
            inv => !game.Graph.DistancesFrom(id, 4, game.State.Overlay).ContainsKey(inv))).ToList();
        var advPool = far.Count > 0 ? far : general;
        string advSpace = advPool[rng.Next(advPool.Count)];
        List<string>? cultists = null;
        if (adversary == "cult-of-hunlow")
        {
            // Some General spaces sit in pockets too small to hold a connected Cultist group:
            // re-roll the standee's space until one works.
            for (int attempt = 0; attempt < 40 && cultists == null; attempt++)
            {
                cultists = TryGrowCultistGroup(game, advSpace, count, investigatorSpaces, rng);
                if (cultists == null)
                {
                    advSpace = advPool[rng.Next(advPool.Count)];
                }
            }
            if (cultists == null)
            {
                throw new InvalidOperationException("No space on this map can seat a connected Cultist group.");
            }
        }
        game.PlaceAdversary(advSpace);

        if (cultists != null)
        {
            var altarPool = game.Graph.Def.Spaces
                .Where(s => s.Kind == SpaceKind.Normal && s.Zone != null)
                .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            game.SetupCultists(cultists, altarPool[rng.Next(altarPool.Count)]);
        }

        string ownerKey = adversary switch
        {
            "butcher" => "butcher",
            "insatiable-horror" => "horror",
            _ => "cult",
        };
        var owned = db.Deck("adversary").Where(c => c.Owner == ownerKey).ToList();
        var attacks = owned.Where(c => c.AdversaryCardType == "attack")
            .Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var abilities = owned.Where(c => c.AdversaryCardType == "ability")
            .Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal)
            // adversaries.json bans these two for a 2-Investigator Horror; SetupAdversaryCards
            // does not enforce that, so the arena only ever builds legal loadouts itself.
            .Where(id => !(adversary == "insatiable-horror" && count == 2 &&
                           (id == "projectile-adhesive" || id == "occluded-lights")))
            .ToList();
        string attack = attacks[rng.Next(attacks.Count)];
        rng.Shuffle(abilities);
        int allowed = AllowedAbilityCount(adversary, count);
        var loadout = abilities.Take(allowed).ToList();
        game.SetupAdversaryCards(attack, loadout);

        game.FinishAdversarySetup();

        matchup = new Matchup
        {
            Seed = seed,
            Scenario = scenario,
            Adversary = adversary,
            Attack = attack,
            Abilities = loadout,
            Investigators = chosen,
        };
        return game;
    }

    /// <summary>
    /// A connected clump of Cultist spaces starting from a neighbour of Mor'gonnod, so
    /// SetupCultists' "single group, Mor'gonnod adjacent to one of them" check passes.
    /// </summary>
    private static List<string>? TryGrowCultistGroup(
        Game game, string advSpace, int needed, HashSet<string> avoid, DeterministicRng rng)
    {
        bool Free(string id) => id != advSpace && !avoid.Contains(id);

        var seeds = Nav.Neighbors(game, advSpace).Where(Free).ToList();
        if (seeds.Count == 0)
        {
            return null;
        }
        var group = new List<string> { seeds[rng.Next(seeds.Count)] };
        while (group.Count < needed)
        {
            var candidates = group
                .SelectMany(s => Nav.Neighbors(game, s))
                .Distinct()
                .Where(id => Free(id) && !group.Contains(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }
            group.Add(candidates[rng.Next(candidates.Count)]);
        }
        return group;
    }

    private static int AllowedAbilityCount(string adversary, int investigators) => adversary switch
    {
        "butcher" => investigators <= 2 ? 1 : 2,
        "insatiable-horror" => investigators <= 3 ? 1 : 2,
        _ => investigators <= 2 ? 1 : investigators == 3 ? 2 : 3,
    };

    private static string Pick(DeterministicRng rng, string[] options) => options[rng.Next(options.Length)];
}
