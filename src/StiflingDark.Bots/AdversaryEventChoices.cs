using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>
/// The Adversary side of the four Event cards that stop and ask (Fallen Tree, Flare-Up, Roll
/// Vortex, and Fire Tornado's 4-6 branch). The engine arms them from the round's Event draw —
/// during the INVESTIGATORS' phase — and expires them unanswered at the end of the round, so a
/// bot Adversary answers as soon as one is pending rather than waiting for its own turn.
///
/// Argument grammar per card is Game.ResolveEventChoice's; every pick below is validated
/// against the same rules its resolver enforces. A misread would still leave the choice
/// pending and be retried forever, so a refused answer falls back to declining (an empty
/// argument list, always legal), which clears the choice and lets the round move on.
/// </summary>
public static class AdversaryEventChoices
{
    /// <summary>Answers every pending Event choice. True when at least one was resolved.</summary>
    public static bool AnswerPending(Game game, Actor actor)
    {
        bool answered = false;
        foreach (string eventId in game.PendingEventChoices())
        {
            var args = Choose(game, eventId);
            string label = "event-choice:" + eventId;
            if (!actor.Try(label, () => game.ResolveEventChoice(eventId, args)))
            {
                actor.Try(label + ":decline", () => game.ResolveEventChoice(eventId, new List<string>()));
            }
            answered = true;
        }
        return answered;
    }

    private static List<string> Choose(Game game, string eventId) => eventId switch
    {
        "fallen-tree" => ChooseFallenTree(game),
        "flare-up" => ChooseFlareUp(game),
        "roll-vortex" => ChooseRollVortex(game),
        "fire-tornado" => ChooseFireTornado(game),
        _ => new List<string>(),
    };

    /// <summary>Drain the two best-charged Investigators; an empty battery is not worth a pick.</summary>
    private static List<string> ChooseFlareUp(Game game) =>
        OnBoard(game)
            .Where(inv => inv.Charge > 0)
            .OrderByDescending(inv => inv.Charge)
            .ThenBy(inv => inv.DefId, StringComparer.Ordinal)
            .Take(2)
            .Select(inv => inv.DefId)
            .ToList();

    /// <summary>
    /// A False Door on the empty Open Door space nearest an Investigator — the doorway they
    /// were most likely to use. With no Door free, a False Window on the nearest Window edge.
    /// </summary>
    private static List<string> ChooseFallenTree(Game game)
    {
        var reach = DistancesFromInvestigators(game);
        string? door = game.Graph.Def.Spaces
            .Where(space => space.Kind == SpaceKind.Door &&
                            game.State.Overlay.DoorState(space.Id) == DoorState.Open &&
                            !Occupied(game, space.Id))
            .Select(space => space.Id)
            .OrderBy(id => Hops(reach, id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (door != null)
        {
            return new List<string> { "door:" + door };
        }
        var window = game.Graph.Def.Edges
            .Where(edge => edge.Type == EdgeType.Window &&
                           !game.State.Overlay.FalseWindows.Contains(BoardOverlay.EdgeKey(edge.A, edge.B)))
            .OrderBy(edge => Math.Min(Hops(reach, edge.A), Hops(reach, edge.B)))
            .ThenBy(edge => edge.A, StringComparer.Ordinal)
            .ThenBy(edge => edge.B, StringComparer.Ordinal)
            .FirstOrDefault();
        return window == null
            ? new List<string>()
            : new List<string> { "window:" + window.A + "|" + window.B };
    }

    /// <summary>
    /// The busiest Zone, wrecking up to 2 of its Doors and opening 1 of its Windows: everyone
    /// standing there loses 1 Stamina and 1 Charge, which is the whole point of the card.
    /// </summary>
    private static List<string> ChooseRollVortex(Game game)
    {
        string? zone = BusiestZone(game);
        if (zone == null)
        {
            return new List<string>();
        }
        var args = new List<string> { zone };
        foreach (string door in ZoneDoors(game, zone)
            .Where(id => game.State.Overlay.DoorState(id) != DoorState.Destroyed)
            .Take(2))
        {
            args.Add("door:" + door);
        }
        var window = game.Graph.Def.Edges
            .Where(edge => edge.Type == EdgeType.Window &&
                           !game.State.Overlay.OpenWindows.Contains(BoardOverlay.EdgeKey(edge.A, edge.B)) &&
                           (game.Graph.Space(edge.A).Zone == zone || game.Graph.Space(edge.B).Zone == zone))
            .OrderBy(edge => edge.A, StringComparer.Ordinal)
            .ThenBy(edge => edge.B, StringComparer.Ordinal)
            .FirstOrDefault();
        if (window != null)
        {
            args.Add("window:" + window.A + "|" + window.B);
        }
        return args;
    }

    /// <summary>
    /// The busiest Zone again — every Door in it is Destroyed and everyone in it flips a
    /// face-down Wound — plus the free flip of one of those Doors to its False Door side,
    /// which only an empty space may take.
    /// </summary>
    private static List<string> ChooseFireTornado(Game game)
    {
        string? zone = BusiestZone(game);
        if (zone == null)
        {
            return new List<string>();
        }
        var args = new List<string> { zone };
        string? flip = ZoneDoors(game, zone).FirstOrDefault(id => !Occupied(game, id));
        if (flip != null)
        {
            args.Add("false:" + flip);
        }
        return args;
    }

    // ---------- shared picking ----------

    private static List<InvestigatorState> OnBoard(Game game) =>
        game.State.Investigators.Where(inv => !inv.Dead && !inv.Escaped).ToList();

    /// <summary>The Zone holding the most Investigators; null when none of them is in one.</summary>
    private static string? BusiestZone(Game game)
    {
        var heads = OnBoard(game)
            .Select(inv => game.Graph.Space(inv.Space).Zone)
            .Where(zone => zone != null)
            .GroupBy(zone => zone!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return heads?.Key;
    }

    private static List<string> ZoneDoors(Game game, string zone) =>
        game.Graph.ZoneSpaces(zone).Where(space => space.Kind == SpaceKind.Door)
            .Select(space => space.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>Hops from the nearest Investigator to each space they can reach.</summary>
    private static Dictionary<string, int> DistancesFromInvestigators(Game game)
    {
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var inv in OnBoard(game))
        {
            foreach (var pair in Nav.From(game, inv.Space))
            {
                if (!best.TryGetValue(pair.Key, out int hops) || pair.Value < hops)
                {
                    best[pair.Key] = pair.Value;
                }
            }
        }
        return best;
    }

    private static int Hops(Dictionary<string, int> distances, string space) =>
        distances.TryGetValue(space, out int hops) ? hops : int.MaxValue;

    /// <summary>Mirrors the engine's own "otherwise empty space" test.</summary>
    private static bool Occupied(Game game, string space)
    {
        var state = game.State;
        return state.Investigators.Any(inv => !inv.Dead && !inv.Escaped && inv.Space == space) ||
               state.Adversary.Space == space ||
               state.Adversary.Figures.Any(figure => figure.Alive && figure.Space == space);
    }
}
