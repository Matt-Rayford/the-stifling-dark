using System;
using System.Collections.Generic;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The raster wall mask plus the LIVE door tokens: a Locked or Damaged Door (and a
    /// False Door) blocks sight across its whole strip like a wall — a Destroyed or plain
    /// Open Door does not (designer ruling 2026-08-31). The mask keeps door spaces
    /// permanently clear, so the closed states are re-walled here as a disc over the door
    /// space, read live from whatever supplies <paramref name="doorStates"/>.
    /// </summary>
    public sealed class DoorAwareLosBlocker : ILineOfSightBlocker
    {
        private readonly ILineOfSightBlocker _walls;
        private readonly MapGraph _graph;
        private readonly Func<IReadOnlyDictionary<string, DoorState>> _doorStates;

        public DoorAwareLosBlocker(ILineOfSightBlocker walls, MapGraph graph,
            Func<IReadOnlyDictionary<string, DoorState>> doorStates)
        {
            _walls = walls;
            _graph = graph;
            _doorStates = doorStates;
        }

        public bool Blocks(double x1, double y1, double x2, double y2)
        {
            if (_walls.Blocks(x1, y1, x2, y2))
            {
                return true;
            }
            double radius = _graph.Def.SpaceRadius;
            foreach (var pair in _doorStates())
            {
                if (pair.Value != DoorState.Locked && pair.Value != DoorState.Damaged &&
                    pair.Value != DoorState.False)
                {
                    continue;
                }
                if (!_graph.HasSpace(pair.Key))
                {
                    continue;
                }
                var door = _graph.Space(pair.Key);
                if (SegmentHitsDisc(x1, y1, x2, y2, door.X, door.Y, radius))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SegmentHitsDisc(double x1, double y1, double x2, double y2,
            double cx, double cy, double radius)
        {
            double sx = x2 - x1, sy = y2 - y1;
            double lengthSq = sx * sx + sy * sy;
            double t = lengthSq <= 0 ? 0 : ((cx - x1) * sx + (cy - y1) * sy) / lengthSq;
            t = Math.Max(0, Math.Min(1, t));
            double dx = cx - (x1 + sx * t), dy = cy - (y1 + sy * t);
            return dx * dx + dy * dy <= radius * radius;
        }
    }
}
