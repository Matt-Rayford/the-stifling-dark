using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>What the setup bot chose, so a caller can report or persist the loadout.</summary>
public sealed class AdversaryLoadout
{
    public string Attack { get; set; } = "";
    public List<string> Abilities { get; set; } = new();
    /// <summary>The Adversary standee's space.</summary>
    public string Space { get; set; } = "";
    /// <summary>Cultist spaces and the Altar, or null for single-figure Adversaries.</summary>
    public List<string>? Cultists { get; set; }
    public string? AltarSpace { get; set; }
}

/// <summary>
/// The Adversary's secret setup, played by a bot: hidden Evidence, the POI tokens and which
/// one hides the Cursed front, the standee (plus the Cult's Cultists and Altar), and the card
/// loadout. Used by the arena to build a game from a seed and by the server whenever the
/// Adversary seat is BOT-filled, so both take exactly the same decisions from the same seed.
/// </summary>
/// <remarks>
/// Every choice comes from the caller's <see cref="DeterministicRng"/>, in a fixed order, so
/// a seed replays exactly. Do not reorder the RNG draws.
/// </remarks>
public static class AdversarySetup
{
    /// <summary>
    /// Run the whole AdversarySetup phase and call <see cref="Game.FinishAdversarySetup"/>.
    /// The game must be freshly created and still in <see cref="GamePhase.AdversarySetup"/>.
    /// </summary>
    /// <param name="avoidSpaces">
    /// Spaces the standee must keep away from — normally the Investigators' Start spaces.
    /// </param>
    public static AdversaryLoadout Run(Game game, DeterministicRng rng,
        IEnumerable<string>? avoidSpaces = null)
    {
        var loadout = new AdversaryLoadout();
        string adversary = game.State.Adversary.DefId;
        int count = game.State.Investigators.Count;

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
        var investigatorSpaces = (avoidSpaces ?? game.State.Investigators.Select(i => i.Space)).ToHashSet();
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
        loadout.Space = advSpace;

        if (cultists != null)
        {
            var altarPool = game.Graph.Def.Spaces
                .Where(s => s.Kind == SpaceKind.Normal && s.Zone != null)
                .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            string altar = altarPool[rng.Next(altarPool.Count)];
            game.SetupCultists(cultists, altar);
            loadout.Cultists = cultists;
            loadout.AltarSpace = altar;
        }

        string ownerKey = adversary switch
        {
            "butcher" => "butcher",
            "insatiable-horror" => "horror",
            _ => "cult",
        };
        var owned = game.Db.Deck("adversary").Where(c => c.Owner == ownerKey).ToList();
        var attacks = owned.Where(c => c.AdversaryCardType == "attack")
            .Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var abilities = owned.Where(c => c.AdversaryCardType == "ability")
            .Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal)
            // adversaries.json bans these two for a 2-Investigator Horror; SetupAdversaryCards
            // does not enforce that, so the bot only ever builds legal loadouts itself.
            .Where(id => !(adversary == "insatiable-horror" && count == 2 &&
                           (id == "projectile-adhesive" || id == "occluded-lights")))
            .ToList();
        string attack = attacks[rng.Next(attacks.Count)];
        rng.Shuffle(abilities);
        var chosen = abilities.Take(AllowedAbilityCount(adversary, count)).ToList();
        game.SetupAdversaryCards(attack, chosen);
        loadout.Attack = attack;
        loadout.Abilities = chosen;

        game.FinishAdversarySetup();
        return loadout;
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

    /// <summary>Ability cards each Adversary takes at each Investigator count.</summary>
    public static int AllowedAbilityCount(string adversary, int investigators) => adversary switch
    {
        "butcher" => investigators <= 2 ? 1 : 2,
        "insatiable-horror" => investigators <= 3 ? 1 : 2,
        _ => investigators <= 2 ? 1 : investigators == 3 ? 2 : 3,
    };
}
