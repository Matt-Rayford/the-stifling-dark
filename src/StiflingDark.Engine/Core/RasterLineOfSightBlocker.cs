using System;
using System.IO;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Line-of-sight blocking backed by the CV-extracted obstacle bitmask
    /// (game-data/maps/*-los-mask.bin). Format: "SDLM" magic, int32 width, int32 height,
    /// double scale (mask pixel -> board pixel), then row-major packed bits (MSB first),
    /// 1 = blocks sight. The mask encodes walls, obstacle outlines+interiors, and curtains;
    /// windows, mirror doors, and printed movement corridors are pre-cleared, and door
    /// spaces are open (door tokens block dynamically via game state, not the mask).
    /// </summary>
    public sealed class RasterLineOfSightBlocker : ILineOfSightBlocker
    {
        private readonly byte[] _bits;
        private readonly int _width;
        private readonly int _height;
        private readonly double _scale;

        public RasterLineOfSightBlocker(byte[] fileBytes)
        {
            if (fileBytes.Length < 20 || fileBytes[0] != (byte)'S' || fileBytes[1] != (byte)'D' ||
                fileBytes[2] != (byte)'L' || fileBytes[3] != (byte)'M')
            {
                throw new InvalidDataException("Not an SDLM line-of-sight mask.");
            }
            _width = BitConverter.ToInt32(fileBytes, 4);
            _height = BitConverter.ToInt32(fileBytes, 8);
            _scale = BitConverter.ToDouble(fileBytes, 12);
            _bits = new byte[fileBytes.Length - 20];
            Array.Copy(fileBytes, 20, _bits, 0, _bits.Length);
        }

        public static RasterLineOfSightBlocker Load(string path) =>
            new RasterLineOfSightBlocker(File.ReadAllBytes(path));

        private bool BlockedAt(int mx, int my)
        {
            if (mx < 0 || my < 0 || mx >= _width || my >= _height)
            {
                return false;
            }
            int index = my * _width + mx;
            return (_bits[index >> 3] & (0x80 >> (index & 7))) != 0;
        }

        /// <summary>True if the straight segment between two board-pixel points crosses a blocker.</summary>
        public bool Blocks(double x1, double y1, double x2, double y2)
        {
            double mx1 = x1 / _scale, my1 = y1 / _scale;
            double mx2 = x2 / _scale, my2 = y2 / _scale;
            double dx = mx2 - mx1, dy = my2 - my1;
            int steps = (int)(Math.Max(Math.Abs(dx), Math.Abs(dy)) / 0.5) + 1;
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                if (BlockedAt((int)(mx1 + dx * t), (int)(my1 + dy * t)))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
