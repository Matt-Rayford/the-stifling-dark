using System;
using System.Collections.Generic;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Deterministic, serializable RNG (splitmix64). Given the same seed and the same
    /// sequence of calls, every platform produces identical results — the foundation for
    /// replays, network sync, and reproducible tests. Never use System.Random in the engine.
    /// </summary>
    public sealed class DeterministicRng
    {
        /// <summary>Current internal state; serialize this to snapshot the RNG.</summary>
        public ulong State { get; private set; }

        public DeterministicRng(ulong seed)
        {
            State = seed;
        }

        private ulong NextUInt64()
        {
            State += 0x9E3779B97F4A7C15UL;
            ulong z = State;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform integer in [0, exclusiveMax).</summary>
        public int Next(int exclusiveMax)
        {
            if (exclusiveMax <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
            }
            // Rejection sampling to avoid modulo bias.
            ulong bound = (ulong)exclusiveMax;
            ulong threshold = ulong.MaxValue - ulong.MaxValue % bound;
            ulong value;
            do
            {
                value = NextUInt64();
            } while (value >= threshold);
            return (int)(value % bound);
        }

        /// <summary>Plain die roll: uniform in [1, sides].</summary>
        public int Roll(int sides) => Next(sides) + 1;

        /// <summary>Sprint die roll: returns the MP value of a random face.</summary>
        public int RollSprintDie(IReadOnlyList<int> faces) => faces[Next(faces.Count)];

        /// <summary>In-place Fisher-Yates shuffle.</summary>
        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
