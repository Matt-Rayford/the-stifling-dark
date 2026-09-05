using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>
/// The Investigators' possibility set: for every hidden hostile figure, every space it could
/// legally occupy given only what the team has publicly seen. This is the exact version of
/// the mental bookkeeping a good player does — "he Disappeared THERE two rounds ago, so he
/// is somewhere in this blob, and that beam just cleared the north half of it."
///
/// Built strictly from investigator-visible facts, even though the bot process can see the
/// whole GameState: Shadow tokens collapse a figure's set to one space, each quiet Adversary
/// turn expands it by a turn of movement, Noise tokens pin a Window crossing, and the
/// reveal-on-Bright rule removes every currently-Bright space (a hidden figure standing
/// there would have been Revealed — except The Insatiable Horror, which walks Bright spaces
/// hidden and leaves breadcrumb tokens instead).
///
/// Sets are kept as SUPERSETS of the truth: expansion ignores Doors and Barricades (the
/// Adversary can break or outwait them) and uses the maximum move budget. A false "he can't
/// be here" would misdirect the whole team; a loose set merely scores beams a little flatter.
/// </summary>
public sealed class AdversaryBelief
{
    /// <summary>Base MP plus the largest Sprint die face (4). Deliberately generous.</summary>
    private static readonly Dictionary<string, int> MovePerTurn = new(StringComparer.Ordinal)
    {
        ["butcher"] = 9,
        ["insatiable-horror"] = 8,
        ["cult-of-hunlow"] = 7,
    };

    private readonly Game _g;
    private readonly Dictionary<string, List<string>> _adjacency;
    private readonly List<string> _allSpaces;
    private readonly Dictionary<string, HashSet<string>> _possible = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _seenShadow = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revealedLastUpdate = new(StringComparer.Ordinal);
    private HashSet<string> _seenNoise = new(StringComparer.Ordinal);
    private Dictionary<string, DoorState> _seenDoors = new(StringComparer.Ordinal);
    private int _updatedRound = -1;

    public AdversaryBelief(Game g)
    {
        _g = g;
        _allSpaces = g.Graph.Def.Spaces.Select(s => s.Id).ToList();
        // Counting adjacency over every printed edge type: the hidden figure may use
        // adversary-only links, Windows, and Mirror Maze doors of any color.
        _adjacency = _allSpaces.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in g.Graph.Def.Edges)
        {
            _adjacency[edge.A].Add(edge.B);
            _adjacency[edge.B].Add(edge.A);
        }
    }

    private GameState S => _g.State;

    private bool IsHorror => S.Adversary.DefId == "insatiable-horror";

    private int Budget => MovePerTurn.TryGetValue(S.Adversary.DefId, out int mp) ? mp : 9;

    /// <summary>"main" plus every living named figure (Cultists, Hatchlings...).</summary>
    private IEnumerable<(string Key, string Space, bool Revealed)> Figures()
    {
        if (!string.IsNullOrEmpty(S.Adversary.Space))
        {
            yield return ("main", S.Adversary.Space, S.Adversary.Revealed);
        }
        foreach (var f in S.Adversary.Figures.Where(f => f.Alive))
        {
            yield return (f.Id, f.Space, f.Revealed);
        }
    }

    /// <summary>Where this figure could be. Exact single space while it is Revealed.</summary>
    public IReadOnlyCollection<string> Possible(string figureKey) =>
        _possible.TryGetValue(figureKey, out var set) ? set : Array.Empty<string>();

    /// <summary>Every space some HIDDEN hostile figure could occupy — the set a beam that
    /// wants information should bite into. Empty when everything hostile is Revealed.</summary>
    public HashSet<string> HiddenUnion()
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, _, revealed) in Figures())
        {
            if (!revealed && _possible.TryGetValue(key, out var set))
            {
                union.UnionWith(set);
            }
        }
        return union;
    }

    /// <summary>Advance the belief to the current round. Call once per round, after the
    /// Adversary's turn (round start for the Investigators). Idempotent within a round.</summary>
    public void Update()
    {
        if (S.Round == _updatedRound)
        {
            return;
        }
        bool first = _updatedRound < 0;
        int rounds = first ? 0 : Math.Max(1, S.Round - _updatedRound);
        var figureKeys = Figures().Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var newNoise = S.Adversary.NoiseTokens.Where(k => !_seenNoise.Contains(k)).ToList();

        foreach (var (key, space, revealed) in Figures())
        {
            if (revealed)
            {
                _possible[key] = new HashSet<string>(StringComparer.Ordinal) { space };
                continue;
            }
            if (first)
            {
                _possible[key] = InitialSet(key);
                continue;
            }

            // Anchors: this figure's own Shadow token when it changed, plus — for the main
            // figure — the un-keyed tokens (the Butcher's forced "frayed" drop, the Horror's
            // per-space breadcrumbs), which always mark the main figure's own path.
            var anchors = new List<string>();
            foreach (var pair in S.Adversary.ShadowTokens)
            {
                bool mine = pair.Key == key || (key == "main" && !figureKeys.Contains(pair.Key));
                if (mine && (!_seenShadow.TryGetValue(pair.Key, out string? seen) || seen != pair.Value))
                {
                    anchors.Add(pair.Value);
                }
            }

            HashSet<string> set;
            if (anchors.Count > 0)
            {
                set = Expand(anchors, Budget * rounds);
            }
            else if (_revealedLastUpdate.Contains(key) && _possible.TryGetValue(key, out var last))
            {
                // Disappeared without a token we can key: it left from where we last saw it.
                set = Expand(last, Budget * rounds);
            }
            else
            {
                set = Expand(_possible.TryGetValue(key, out var prev) ? prev : InitialSet(key),
                    Budget * rounds);
            }

            if (!IsHorror)
            {
                set.RemoveWhere(id => _g.Graph.EffectiveLight(id, S.Overlay) == LightLevel.Bright);
            }
            bool onlyHiddenFigure = key == "main" &&
                !S.Adversary.Figures.Any(f => f.Alive && !f.Revealed);
            if (onlyHiddenFigure && newNoise.Count > 0)
            {
                // A fresh Noise token means the (single) hidden figure crossed that Window
                // this past turn, so it is within a turn's movement of the frame.
                var endpoints = newNoise.SelectMany(k => k.Split('|')).Where(_adjacency.ContainsKey);
                var near = Expand(endpoints.ToList(), Budget);
                if (set.Overlaps(near))
                {
                    set.IntersectWith(near);
                }
            }
            if (onlyHiddenFigure)
            {
                // A Door that went Damaged since last round was Broken by the Adversary —
                // Investigators can only Lock and Open — so the figure stood at that Door
                // during its turn. (Damaged -> Destroyed is ambiguous: an Investigator's
                // Open does that too.)
                var broken = S.Overlay.DoorStates
                    .Where(d => d.Value == DoorState.Damaged &&
                                (!_seenDoors.TryGetValue(d.Key, out var was) || was != DoorState.Damaged))
                    .Select(d => d.Key)
                    .Where(_adjacency.ContainsKey)
                    .ToList();
                if (broken.Count > 0)
                {
                    var near = Expand(broken, Budget);
                    if (set.Overlaps(near))
                    {
                        set.IntersectWith(near);
                    }
                }
            }
            if (set.Count == 0)
            {
                // Over-pruned (an unmodelled ability moved it): reset rather than mislead.
                set = InitialSet(key);
            }
            _possible[key] = set;
        }

        _seenShadow.Clear();
        foreach (var pair in S.Adversary.ShadowTokens)
        {
            _seenShadow[pair.Key] = pair.Value;
        }
        _seenNoise = new HashSet<string>(S.Adversary.NoiseTokens, StringComparer.Ordinal);
        _seenDoors = new Dictionary<string, DoorState>(S.Overlay.DoorStates, StringComparer.Ordinal);
        _revealedLastUpdate.Clear();
        foreach (var f in Figures().Where(f => f.Revealed))
        {
            _revealedLastUpdate.Add(f.Key);
        }
        _updatedRound = S.Round;
    }

    /// <summary>"Could be anywhere" — minus what reveal-on-Bright rules out, and (for the
    /// Cult, whose setup rule is public) narrowed to building Zones at game start.</summary>
    private HashSet<string> InitialSet(string key)
    {
        IEnumerable<string> spaces = _allSpaces;
        if (S.Adversary.DefId == "cult-of-hunlow" && _updatedRound < 0)
        {
            spaces = _g.Graph.Def.Spaces.Where(s => s.Zone != null).Select(s => s.Id);
        }
        var set = new HashSet<string>(spaces, StringComparer.Ordinal);
        if (!IsHorror)
        {
            set.RemoveWhere(id => _g.Graph.EffectiveLight(id, S.Overlay) == LightLevel.Bright);
        }
        return set;
    }

    private HashSet<string> Expand(IReadOnlyCollection<string> from, int depth)
    {
        var dist = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new Queue<string>();
        foreach (string space in from.Where(_adjacency.ContainsKey))
        {
            dist[space] = 0;
            frontier.Enqueue(space);
        }
        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            int d = dist[current];
            if (d == depth)
            {
                continue;
            }
            foreach (string next in _adjacency[current])
            {
                if (!dist.ContainsKey(next))
                {
                    dist[next] = d + 1;
                    frontier.Enqueue(next);
                }
            }
        }
        return new HashSet<string>(dist.Keys, StringComparer.Ordinal);
    }
}
