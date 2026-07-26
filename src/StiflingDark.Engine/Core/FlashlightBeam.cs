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
    /// circle is entirely covered by the beam shape AND an unobstructed sight line
    /// connects it to the Investigator. The physical template's printed sight lines are
    /// modeled as a straight ray to the space (the digital interpretation agreed with the
    /// designer: the visual template is not shown; only the resulting Bright set matters).
    /// </summary>
    public sealed class FlashlightBeam
    {
        private readonly double[] _polyX;
        private readonly double[] _polyY;
        private readonly double _originX;
        private readonly double _originY;
        private readonly double _templateLengthPx;
        private readonly double _lengthInPitches;

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
                    !blocker.Blocks(origin.X, origin.Y, space.X, space.Y))
                {
                    bright.Add(space.Id);
                }
            }
            return bright;
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
