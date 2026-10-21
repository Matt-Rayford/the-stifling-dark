using System.Globalization;
using System.Reflection;

namespace StiflingDark.Bots;

/// <summary>
/// Every number the Investigator bots weigh a decision with, in one place, so the arena can
/// tune them against measured outcomes instead of a person guessing (tools/BotArena
/// <c>tune</c> mode). Defaults are the hand-set values the bots shipped with; a tuned set is
/// applied by assigning the fields before play. Plain public fields on purpose — the tuner
/// enumerates them by reflection (<see cref="Knobs"/>).
/// </summary>
public sealed class BotTuning
{
    // ---------- Beam scoring (InvestigatorTeam.ScoreFlashlightAngle) ----------

    /// <summary>Every newly lit space, so raw coverage is a first-class goal.</summary>
    public double CoverageBase = 1.0;
    /// <summary>An objective space that only works Bright (Altar, Grave, the Saw).</summary>
    public double MustLight = 40;
    /// <summary>An un-Bright space a Revealed figure could slink into and Disappear.</summary>
    public double Denial = 10;
    /// <summary>The walk from the Adversary's last known position to this figure — threatened / calm.</summary>
    public double ThreatApproachHot = 16;
    public double ThreatApproachCold = 6;
    public double Entrances = 14;
    public double GuardRing = 12;
    public double Lanes = 6;
    public double Choke = 4;
    /// <summary>Belief-cloud pruning: per lit cloud space, PruneScale / cloud size, capped.</summary>
    public double PruneScale = 60;
    public double PruneCap = 30;
    /// <summary>Multiplier on the playbook's per-space Evidence-search weight. (Tuned
    /// 2026-09-05 by the arena: 1.0 -> 1.5, held-out confirmed.)</summary>
    public double SearchScale = 1.5;
    /// <summary>How much a beam avoids taking spaces a teammate still to act could light better.</summary>
    public double TeamLookahead = 0.5;
    /// <summary>Score a beam must clear when nothing hostile can Attack next turn.</summary>
    public double IdleBeamBar = 30;
    /// <summary>Score at which a beam is worth the last point of Charge outright.</summary>
    public double EagerBeamScore = 24;

    // ---------- Turn planner (InvestigatorTurnPlanner) ----------

    public double ArrivalAction = 40;
    public double ErrandFinish = 12;
    public double ProgressPerCost = 4;
    public double PickUpEvidence = 30;
    public double PickUpPoi = 8;
    public double PickUpMedical = 5;
    public double SwitchNow = 25;
    /// <summary>Ending on a switch to flip at full MP next turn. (Tuned: 16 -> 24.)</summary>
    public double SwitchNext = 24;
    /// <summary>Banking a batch at a feature passed in reach. (Tuned: 25 -> 37.5.)</summary>
    public double Bank = 37.5;
    public double BeamShare = 0.3;
    public double Exposure = 18;
    public double KnownDanger = 5;
    public double DarkAlone = 4;
    public double BuddyGap = 1.5;
    public double UnusedMp = 1.0;

    public static BotTuning Default => new();

    private static readonly FieldInfo[] KnobFields = typeof(BotTuning)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(f => f.FieldType == typeof(double))
        .OrderBy(f => f.MetadataToken)
        .ToArray();

    /// <summary>Every tunable, in declaration order.</summary>
    public static IReadOnlyList<string> Knobs => KnobFields.Select(f => f.Name).ToList();

    public double Get(string knob) =>
        (double)(KnobFields.First(f => f.Name == knob).GetValue(this) ?? 0.0);

    public void Set(string knob, double value) =>
        KnobFields.First(f => f.Name == knob).SetValue(this, value);

    public BotTuning Clone()
    {
        var copy = new BotTuning();
        foreach (var field in KnobFields)
        {
            field.SetValue(copy, field.GetValue(this));
        }
        return copy;
    }

    /// <summary>"Name=value" pairs, one per line — what the tuner writes and the arena reads.</summary>
    public string ToText() =>
        string.Join("\n", KnobFields.Select(f =>
            f.Name + "=" + ((double)(f.GetValue(this) ?? 0.0)).ToString("0.###", CultureInfo.InvariantCulture)));

    public static BotTuning Parse(string text)
    {
        var tuning = new BotTuning();
        foreach (string raw in text.Split('\n', '\r', ';'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith("#") || eq <= 0)
            {
                continue;
            }
            string name = line.Substring(0, eq).Trim();
            var field = KnobFields.FirstOrDefault(f => f.Name == name)
                        ?? throw new FormatException($"Unknown bot tuning knob '{name}'.");
            field.SetValue(tuning, double.Parse(line.Substring(eq + 1).Trim(), CultureInfo.InvariantCulture));
        }
        return tuning;
    }
}
