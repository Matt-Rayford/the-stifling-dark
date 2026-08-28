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
        private readonly List<double[]> _sightLines;

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
            _sightLines = def.SightLineSegments;
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
        /// Does the Investigator see this space? With sight-line data: any of the 7 printed
        /// lines passes through the space's circle, and the stretch of that line from its
        /// base (at the figure) to the space is unobstructed. Without data: the straight
        /// ray to the space's centre.
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

            foreach (var line in _sightLines)
            {
                double x1 = line[0], y1 = line[1], x2 = line[2], y2 = line[3];
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
                    continue; // this printed line misses the space entirely
                }
                // The line must make it THROUGH the space, not merely graze its near rim: a
                // wall cutting across the circle blocks it (designer: "if one of those lines
                // hits a grey-bordered wall, it should not see the space"). So walk from the
                // line's base end to where it EXITS the space's circle.
                double halfChord = Math.Sqrt(Math.Max(0, radiusT * radiusT - distSq));
                double tExit = Math.Max(0, Math.Min(1, t + halfChord / Math.Sqrt(lengthSq)));
                double ex = x1 + sx * tExit, ey = y1 + sy * tExit;
                // Template -> board, for the base end and the exit point. The base ends all
                // sit within the figure's own space around the notch.
                double baseBoardX = ox + (fx * (_originY - y1) + rx * (x1 - _originX)) * scale;
                double baseBoardY = oy + (fy * (_originY - y1) + ry * (x1 - _originX)) * scale;
                double exitBoardX = ox + (fx * (_originY - ey) + rx * (ex - _originX)) * scale;
                double exitBoardY = oy + (fy * (_originY - ey) + ry * (ex - _originX)) * scale;
                if (!blocker.Blocks(baseBoardX, baseBoardY, exitBoardX, exitBoardY))
                {
                    return true;
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
