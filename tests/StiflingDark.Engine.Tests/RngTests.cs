using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class RngTests
    {
        [Fact]
        public void Same_seed_produces_identical_sequences()
        {
            var a = new DeterministicRng(42);
            var b = new DeterministicRng(42);
            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(a.Next(6), b.Next(6));
            }
        }

        [Fact]
        public void Rolls_stay_in_bounds_and_hit_every_face()
        {
            var rng = new DeterministicRng(7);
            var seen = new HashSet<int>();
            for (int i = 0; i < 1000; i++)
            {
                int roll = rng.Roll(6);
                Assert.InRange(roll, 1, 6);
                seen.Add(roll);
            }
            Assert.Equal(6, seen.Count);
        }

        [Fact]
        public void Sprint_die_uses_configured_faces()
        {
            var rng = new DeterministicRng(11);
            var faces = TestData.Db.Config.SprintDieFaces;
            for (int i = 0; i < 200; i++)
            {
                Assert.Contains(rng.RollSprintDie(faces), faces);
            }
        }

        [Fact]
        public void Shuffle_is_deterministic_for_a_given_seed()
        {
            var listA = Enumerable.Range(0, 20).ToList();
            var listB = Enumerable.Range(0, 20).ToList();
            new DeterministicRng(99).Shuffle(listA);
            new DeterministicRng(99).Shuffle(listB);
            Assert.Equal(listA, listB);
            Assert.NotEqual(Enumerable.Range(0, 20).ToList(), listA);
        }
    }
}
