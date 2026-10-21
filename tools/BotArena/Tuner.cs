using System.Collections.Concurrent;
using System.Globalization;
using StiflingDark.Bots;
using StiflingDark.Engine.Data;

namespace BotArena;

/// <summary>
/// Coordinate ascent over <see cref="BotTuning"/>: each knob is nudged up and down, a fixed
/// block of seeds is replayed, and a nudge is kept only when the team's measured outcome
/// improves by more than the noise floor. The same seeds are used for every evaluation
/// (paired comparison), which cancels the setup lottery — maps, rosters, adversary draws —
/// and leaves only the effect of the knob. A separate held-out seed block reports whether
/// the improvement generalizes.
/// </summary>
public static class Tuner
{
    /// <summary>Per-game outcome the tuner climbs: tokens banked, reaching the Escape gate,
    /// winning, and not dying. Dense enough to move on 100-odd games; a win alone is not.</summary>
    public static double Outcome(GameRecord r) =>
        r.EvidenceTurnedIn
        + (r.EscapeSelectedRound > 0 ? 2.0 : 0.0)
        + (r.Result == "InvestigatorsWin" ? 6.0 : 0.0)
        - 0.5 * r.Kills;

    public static void Execute(GameDatabase db, int games, int passes, ulong firstSeed, string outputDir,
        BotTuning? start)
    {
        var tuning = start?.Clone() ?? BotTuning.Default;
        const double Step = 1.5;
        const double MinGain = 0.08;

        double baseline = Evaluate(db, tuning, games, firstSeed);
        Console.WriteLine($"start: {baseline:F3} per game over {games} games (seeds {firstSeed}..{firstSeed + (ulong)games - 1})");
        var knobs = BotTuning.Knobs;
        for (int pass = 1; pass <= passes; pass++)
        {
            int kept = 0;
            foreach (string knob in knobs)
            {
                double current = tuning.Get(knob);
                double bestValue = current, bestScore = baseline;
                foreach (double candidate in new[] { current * Step, current / Step })
                {
                    var trial = tuning.Clone();
                    trial.Set(knob, candidate);
                    double score = Evaluate(db, trial, games, firstSeed);
                    Console.WriteLine($"  pass {pass} {knob,-18} {candidate,8:0.###}  -> {score:F3}  (best {bestScore:F3})");
                    if (score > bestScore + MinGain)
                    {
                        bestScore = score;
                        bestValue = candidate;
                    }
                }
                if (bestValue != current)
                {
                    tuning.Set(knob, bestValue);
                    baseline = bestScore;
                    kept++;
                    Console.WriteLine($"  KEEP {knob} = {bestValue:0.###} ({baseline:F3})");
                }
            }
            Console.WriteLine($"pass {pass} done: {kept} knobs changed, {baseline:F3} per game");
            if (kept == 0)
            {
                break;
            }
        }

        ulong heldOut = firstSeed + 100_000;
        double defaultsHeldOut = Evaluate(db, BotTuning.Default, games, heldOut);
        double tunedHeldOut = Evaluate(db, tuning, games, heldOut);
        Console.WriteLine($"held-out seeds {heldOut}..: defaults {defaultsHeldOut:F3}, tuned {tunedHeldOut:F3}");

        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, "tuning.txt");
        File.WriteAllText(path,
            $"# tuned {DateTime.UtcNow:u}: {baseline:F3} per game on {games} seeds from {firstSeed}; " +
            $"held-out defaults {defaultsHeldOut:F3} vs tuned {tunedHeldOut:F3}\n" + tuning.ToText() + "\n");
        Console.WriteLine("written " + path);
        Console.WriteLine(tuning.ToText());
    }

    public static double Evaluate(GameDatabase db, BotTuning tuning, int games, ulong firstSeed)
    {
        var outcomes = new ConcurrentBag<double>();
        Parallel.For(0, games,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            index =>
            {
                var run = new GameRun(db, firstSeed + (ulong)index, tuning: tuning);
                GameRecord record;
                try
                {
                    record = run.Play();
                }
                catch (Exception)
                {
                    record = new GameRecord { Seed = firstSeed + (ulong)index, Result = "HarnessCrash" };
                }
                outcomes.Add(Outcome(record));
            });
        return outcomes.Average();
    }
}
