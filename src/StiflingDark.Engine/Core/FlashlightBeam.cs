using System;
using System.Collections.Generic;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Blocks sight lines for flashlight/stalk purposes. Implemented over the obstacle
    /// mask once wall/obstacle geometry is extracted; until then use <see cref="None"/>.
    /// </summary>
    public interface ILineOfSightBlocker
    {
        /// <summary>True if the straight segment between two board points crosses an Obstacle.</summary>
        bool Blocks(double x1, double y1, double x2, double y2);
    }

    public sealed class NoLineOfSightBlocker : ILineOfSightBlocker
    {
        public static readonly NoLineOfSightBlocker None = new NoLineOfSightBlocker();
        public bool Blocks(double x1, double y1, double x2, double y2) => false;
    }

    /// <summary>
    /// The Small Flashlight beam: computes which spaces become Bright for a placement at
    /// any continuous angle around the Investigator's figure. A space is Bright when its
    /// circle is entirely covered by the beam shape AND one of the template's 7 PRINTED
    /// sight lines connects it to the Investigator without crossing an Obstacle — exactly
    /// the physical rule (designer ruling 2026-08-27; the earlier straight-ray stand-in
    /// missed spaces a printed line reaches through a door gap). When the def carries no
    /// sight-line data, the straight ray to the space's centre is the fallback.
    /// </summary>
    public sealed class FlashlightBeam
    {
        private readonly double[] _polyX;
        private readonly double[] _polyY;
        private readonly double _originX;
        private readonly double _originY;
        private readonly double _templateLengthPx;
        private readonly double _lengthInPitches;
        private readonly List<List<double[]>> _sightLines;

        public FlashlightBeam(FlashlightDef def)
        {
            _polyX = new double[def.OutlinePolygon.Count];
            _polyY = new double[def.OutlinePolygon.Count];
            for (int i = 0; i < def.OutlinePolygon.Count; i++)
            {
                _polyX[i] = def.OutlinePolygon[i][0];
                _polyY[i] = def.OutlinePolygon[i][1];
            }
            _originX = def.OriginX;
            _originY = def.OriginY;
            _templateLengthPx = def.ImageHeight;
            _lengthInPitches = def.LengthInSpacePitches;
            _sightLines = def.SightLinePaths;
        }

        /// <summary>
        /// Spaces made Bright by placing the flashlight on <paramref name="atSpace"/> aimed
        /// at <paramref name="angleRadians"/> (board coordinates, y-down; 0 = +x). The
        /// Investigator's own space is always included.
        /// </summary>
        public HashSet<string> ComputeBright(
            MapGraph graph, string atSpace, double angleRadians, ILineOfSightBlocker blocker)
        {
            var origin = graph.Space(atSpace);
            double pitch = graph.Def.SpacePitch;
            double radius = graph.Def.SpaceRadius;
            // Template pixels -> board pixels so the beam spans LengthInSpacePitches pitches.
            double scale = _lengthInPitches * pitch / _templateLengthPx;

            double fx = Math.Cos(angleRadians), fy = Math.Sin(angleRadians);
            double rx = -fy, ry = fx;

            double reach = _lengthInPitches * pitch + radius;
            var bright = new HashSet<string> { atSpace };
            foreach (var space in graph.Def.Spaces)
            {
                if (space.Id == atSpace)
                {
                    continue;
                }
                double dx = space.X - origin.X, dy = space.Y - origin.Y;
                if (dx * dx + dy * dy > reach * reach)
                {
                    continue;
                }
                if (CircleFullyInBeam(space.X, space.Y, radius, origin.X, origin.Y, fx, fy, rx, ry, scale) &&
                    HasSight(space.X, space.Y, radius, origin.X, origin.Y, fx, fy, rx, ry, scale, blocker))
                {
                    bright.Add(space.Id);
                }
            }
            return bright;
        }

        /// <summary>
        /// Does the Investigator see this space? With sight-line data: one of the 7 printed
        /// lines passes through the space's circle, and the WHOLE walk from the line's base
        /// at the notch — up a vertical, around its branch point for the angled ones — to
        /// where it exits the circle is unobstructed. Grazing the near rim before a wall is
        /// not enough: the line must make it through the space (designer: "if one of those
        /// lines hits a grey-bordered wall, it should not see the space"). Without data:
        /// the straight ray to the space's centre.
        /// </summary>
        private bool HasSight(
            double cx, double cy, double radius,
            double ox, double oy, double fx, double fy, double rx, double ry, double scale,
            ILineOfSightBlocker blocker)
        {
            if (_sightLines.Count == 0)
            {
                return !blocker.Blocks(ox, oy, cx, cy);
            }

            // The space's centre in template pixels, and its radius there.
            double dx = cx - ox, dy = cy - oy;
            double tx = _originX + (dx * rx + dy * ry) / scale;
            double ty = _originY - (dx * fx + dy * fy) / scale;
            double radiusT = radius / scale;

            (double X, double Y) ToBoard(double px, double py) => (
                ox + (fx * (_originY - py) + rx * (px - _originX)) * scale,
                oy + (fy * (_originY - py) + ry * (px - _originX)) * scale);

            foreach (var path in _sightLines)
            {
                for (int i = 1; i < path.Count; i++)
                {
                    double x1 = path[i - 1][0], y1 = path[i - 1][1];
                    double x2 = path[i][0], y2 = path[i][1];
                    double sx = x2 - x1, sy = y2 - y1;
                    double lengthSq = sx * sx + sy * sy;
                    if (lengthSq <= 0)
                    {
                        continue;
                    }
                    double t = ((tx - x1) * sx + (ty - y1) * sy) / lengthSq;
                    double clamped = Math.Max(0, Math.Min(1, t));
                    double nx = x1 + sx * clamped, ny = y1 + sy * clamped;
                    double distSq = (nx - tx) * (nx - tx) + (ny - ty) * (ny - ty);
                    if (distSq > radiusT * radiusT)
                    {
                        continue; // this leg misses the space entirely
                    }
                    double halfChord = Math.Sqrt(Math.Max(0, radiusT * radiusT - distSq));
                    double tExit = Math.Max(0, Math.Min(1, t + halfChord / Math.Sqrt(lengthSq)));
                    double ex = x1 + sx * tExit, ey = y1 + sy * tExit;

                    // Walk the path from its base: every earlier leg in full, then this leg
                    // up to the exit point.
                    bool clear = true;
                    for (int j = 1; j < i && clear; j++)
                    {
                        var a = ToBoard(path[j - 1][0], path[j - 1][1]);
                        var b = ToBoard(path[j][0], path[j][1]);
                        clear = !blocker.Blocks(a.X, a.Y, b.X, b.Y);
                    }
                    if (clear)
                    {
                        var start = ToBoard(x1, y1);
                        var exit = ToBoard(ex, ey);
                        if (!blocker.Blocks(start.X, start.Y, exit.X, exit.Y))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private bool CircleFullyInBeam(
            double cx, double cy, double radius,
            double ox, double oy, double fx, double fy, double rx, double ry, double scale)
        {
            const int RimSamples = 16;
            if (!PointInBeam(cx, cy, ox, oy, fx, fy, rx, ry, scale))
            {
                return false;
            }
            for (int i = 0; i < RimSamples; i++)
            {
                double a = 2 * Math.PI * i / RimSamples;
                if (!PointInBeam(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a), ox, oy, fx, fy, rx, ry, scale))
                {
                    return false;
                }
            }
            return true;
        }

        private bool PointInBeam(
            double px, double py,
            double ox, double oy, double fx, double fy, double rx, double ry, double scale)
        {
            // Board -> beam-local: forward distance along the aim, lateral offset to its right.
            double dx = px - ox, dy = py - oy;
            double forward = (dx * fx + dy * fy) / scale;
            double lateral = (dx * rx + dy * ry) / scale;
            // Beam-local -> template pixels: the template beam extends toward -y from the notch origin.
            double tx = _originX + lateral;
            double ty = _originY - forward;
            return PointInPolygon(tx, ty);
        }

        private bool PointInPolygon(double x, double y)
        {
            bool inside = false;
            int n = _polyX.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((_polyY[i] > y) != (_polyY[j] > y) &&
                    x < (_polyX[j] - _polyX[i]) * (y - _polyY[i]) / (_polyY[j] - _polyY[i]) + _polyX[i])
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }
}
