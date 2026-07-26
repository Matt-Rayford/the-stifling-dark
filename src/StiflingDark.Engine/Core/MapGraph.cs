using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Mutable board-token state that modifies the printed map: door tokens, zone light
    /// tokens, flashlight coverage, the open Mirror Maze color, window tokens, and secret
    /// passages. Owned by GameState; MapGraph queries take it as context.
    /// </summary>
    public sealed class BoardOverlay
    {
        /// <summary>Door space id -> state. Absent = Open (no token).</summary>
        public Dictionary<string, DoorState> DoorStates { get; } = new Dictionary<string, DoorState>();

        /// <summary>Zone letters whose lights are on (Bright token): every space in the zone is Bright.</summary>
        public HashSet<string> BrightZones { get; } = new HashSet<string>();

        /// <summary>Zone letters with a Dim token: every space in the zone is at least Dim.</summary>
        public HashSet<string> DimZones { get; } = new HashSet<string>();

        /// <summary>Individually Bright spaces (flashlight coverage and similar effects).</summary>
        public HashSet<string> BrightSpaces { get; } = new HashSet<string>();

        /// <summary>The Mirror Maze color that is Open this round (null outside the Amusement Park).</summary>
        public MirrorDoorColor? OpenMirrorColor { get; set; }

        /// <summary>Window edges (canonical key) carrying an Open Window token: no penalty for anyone.</summary>
        public HashSet<string> OpenWindows { get; } = new HashSet<string>();

        /// <summary>Window edges (canonical key) carrying a False Window token: impassable, no trading.</summary>
        public HashSet<string> FalseWindows { get; } = new HashSet<string>();

        /// <summary>Extra adjacencies created by Secret Passage tokens (canonical keys).</summary>
        public HashSet<string> SecretPassages { get; } = new HashSet<string>();

        public static string EdgeKey(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        public DoorState DoorState(string spaceId) =>
            DoorStates.TryGetValue(spaceId, out var s) ? s : Core.DoorState.Open;
    }

    /// <summary>The outcome of asking whether a figure may take one step between adjacent spaces.</summary>
    public sealed class MoveStep
    {
        public int Cost { get; internal set; }

        /// <summary>The step crosses a Window without an Open Window token. Investigators must
        /// then choose Wound vs Stamina (turn logic); the Adversary must place a Noise token.</summary>
        public bool CrossesWindow { get; internal set; }
    }

    /// <summary>
    /// Immutable runtime view of one board: fast lookups over spaces and edges, plus the
    /// movement/adjacency rules that depend only on map + overlay (not on turn state).
    /// </summary>
    public sealed class MapGraph
    {
        public MapDef Def { get; }

        private readonly Dictionary<string, SpaceDef> _spaces;
        private readonly Dictionary<string, List<EdgeDef>> _edgesBySpace;
        private readonly Dictionary<string, EdgeDef> _edgeByKey;
        private readonly Dictionary<string, int> _waterLoopIndex;
        private readonly Dictionary<string, string> _forcedRideNext;

        public MapGraph(MapDef def)
        {
            Def = def;
            _spaces = def.Spaces.ToDictionary(s => s.Id);
            _edgesBySpace = def.Spaces.ToDictionary(s => s.Id, _ => new List<EdgeDef>());
            _edgeByKey = new Dictionary<string, EdgeDef>();
            foreach (var e in def.Edges)
            {
                _edgesBySpace[e.A].Add(e);
                _edgesBySpace[e.B].Add(e);
                _edgeByKey[BoardOverlay.EdgeKey(e.A, e.B)] = e;
            }
            _waterLoopIndex = new Dictionary<string, int>();
            for (int i = 0; i < def.WaterFlowLoop.Count; i++)
            {
                _waterLoopIndex[def.WaterFlowLoop[i]] = i;
            }
            _forcedRideNext = def.Rides.Values
                .SelectMany(r => r.ForcedNext)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        public SpaceDef Space(string id) =>
            _spaces.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"No space '{id}' on map '{Def.Id}'.");

        public bool HasSpace(string id) => _spaces.ContainsKey(id);

        public EdgeDef? Edge(string a, string b) =>
            _edgeByKey.TryGetValue(BoardOverlay.EdgeKey(a, b), out var e) ? e : null;

        public IEnumerable<SpaceDef> ZoneSpaces(string zone) => Def.Spaces.Where(s => s.Zone == zone);

        /// <summary>
        /// Effective light level. Precedence per the rulebook: flashlight/Bright token,
        /// then Dim token or printed Dim, then Dark.
        /// </summary>
        public LightLevel EffectiveLight(string spaceId, BoardOverlay overlay)
        {
            var space = Space(spaceId);
            if (overlay.BrightSpaces.Contains(spaceId) ||
                (space.Zone != null && overlay.BrightZones.Contains(space.Zone)))
            {
                return LightLevel.Bright;
            }
            if (space.Zone != null && overlay.DimZones.Contains(space.Zone))
            {
                return LightLevel.Dim;
            }
            return space.PrintedLight;
        }

        /// <summary>
        /// One movement step for a figure, or null if illegal. Covers edge passability
        /// (edge type, mirror doors, false windows, secret passages), destination door
        /// tokens, and the light-based entry cost. Higher-level rules (MP budget, water
        /// float, carriage rotation, window wound choice, being Revealed) live in turn logic.
        /// </summary>
        public MoveStep? TryStep(FigureKind figure, string from, string to, BoardOverlay overlay)
        {
            if (from == to)
            {
                return null;
            }
            string key = BoardOverlay.EdgeKey(from, to);
            var edge = _edgeByKey.TryGetValue(key, out var e) ? e : null;
            bool viaSecretPassage = overlay.SecretPassages.Contains(key);

            if (edge == null && !viaSecretPassage)
            {
                return null;
            }
            bool crossesWindow = false;
            if (edge != null && !viaSecretPassage)
            {
                switch (edge.Type)
                {
                    case EdgeType.Move:
                        break;
                    case EdgeType.Window:
                        if (overlay.FalseWindows.Contains(key))
                        {
                            return null;
                        }
                        crossesWindow = !overlay.OpenWindows.Contains(key) && figure != FigureKind.Spirit;
                        break;
                    case EdgeType.MirrorDoor:
                        // Spirits ignore Mirror Maze doors entirely.
                        if (figure != FigureKind.Spirit && edge.Color != overlay.OpenMirrorColor)
                        {
                            return null;
                        }
                        break;
                    case EdgeType.AdversaryLink:
                        return null; // never a movement connection
                    default:
                        throw new InvalidOperationException($"Unhandled edge type {edge.Type}.");
                }
            }

            // Destination door tokens. Spirits are not affected by anything that affects movement.
            if (figure != FigureKind.Spirit && BlocksMovement(overlay.DoorState(to)))
            {
                return null;
            }

            int cost = EntryCost(figure, to, overlay);
            if (crossesWindow && figure == FigureKind.Adversary)
            {
                cost += 1;
            }
            return new MoveStep { Cost = cost, CrossesWindow = crossesWindow };
        }

        private static bool BlocksMovement(DoorState state) =>
            state == DoorState.Locked || state == DoorState.Damaged || state == DoorState.False;

        private int EntryCost(FigureKind figure, string to, BoardOverlay overlay)
        {
            if (figure != FigureKind.Investigator)
            {
                return 1; // Adversaries and Spirits pay 1 MP everywhere (per-adversary overrides live in adversary logic).
            }
            return EffectiveLight(to, overlay) == LightLevel.Dark ? 2 : 1;
        }

        /// <summary>
        /// Distances for "within X spaces" counting: light levels and Map Hazards do not
        /// matter, but movement blockers (door tokens, closed mirror doors) break the chain.
        /// Secret passages count as adjacent.
        /// </summary>
        public Dictionary<string, int> DistancesFrom(string from, int maxDistance, BoardOverlay overlay)
        {
            var dist = new Dictionary<string, int> { [from] = 0 };
            var frontier = new Queue<string>();
            frontier.Enqueue(from);
            while (frontier.Count > 0)
            {
                string current = frontier.Dequeue();
                int d = dist[current];
                if (d == maxDistance)
                {
                    continue;
                }
                foreach (string next in CountingNeighbors(current, overlay))
                {
                    if (!dist.ContainsKey(next))
                    {
                        dist[next] = d + 1;
                        frontier.Enqueue(next);
                    }
                }
            }
            return dist;
        }

        private IEnumerable<string> CountingNeighbors(string spaceId, BoardOverlay overlay)
        {
            foreach (var edge in _edgesBySpace[spaceId])
            {
                string other = edge.A == spaceId ? edge.B : edge.A;
                string key = BoardOverlay.EdgeKey(edge.A, edge.B);
                bool passable = edge.Type switch
                {
                    EdgeType.Move => true,
                    EdgeType.Window => !overlay.FalseWindows.Contains(key),
                    EdgeType.MirrorDoor => edge.Color == overlay.OpenMirrorColor,
                    EdgeType.AdversaryLink => false,
                    _ => false,
                };
                if (passable && !BlocksMovement(overlay.DoorState(other)))
                {
                    yield return other;
                }
            }
            foreach (string key in overlay.SecretPassages)
            {
                int sep = key.IndexOf('|');
                string a = key.Substring(0, sep);
                string b = key.Substring(sep + 1);
                if (a == spaceId && !BlocksMovement(overlay.DoorState(b)))
                {
                    yield return b;
                }
                else if (b == spaceId && !BlocksMovement(overlay.DoorState(a)))
                {
                    yield return a;
                }
            }
        }

        /// <summary>
        /// Spaces adjacent for Adversary Attacks and Abilities: normal adjacency plus the
        /// yellow-dashed carriage links (which never allow movement or trading).
        /// </summary>
        public IEnumerable<string> AdjacentForAdversaryAbilities(string spaceId, BoardOverlay overlay)
        {
            foreach (var edge in _edgesBySpace[spaceId])
            {
                string other = edge.A == spaceId ? edge.B : edge.A;
                if (edge.Type == EdgeType.AdversaryLink)
                {
                    yield return other;
                }
                else if (TryStep(FigureKind.Adversary, spaceId, other, overlay) != null)
                {
                    yield return other;
                }
            }
        }

        /// <summary>Follow the Tunnel of Love current the given number of spaces clockwise.</summary>
        public string WaterNext(string spaceId, int steps)
        {
            if (!_waterLoopIndex.TryGetValue(spaceId, out int index))
            {
                throw new ArgumentException($"'{spaceId}' is not on the water loop.", nameof(spaceId));
            }
            int n = Def.WaterFlowLoop.Count;
            return Def.WaterFlowLoop[(index + steps) % n];
        }

        /// <summary>Forced ride rotation target for a carriage space, or null if not a carriage.</summary>
        public string? RideNext(string spaceId) =>
            _forcedRideNext.TryGetValue(spaceId, out var next) ? next : null;
    }
}
