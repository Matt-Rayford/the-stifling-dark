namespace BotArena;

/// <summary>One row of bot-results/results.json.</summary>
public sealed class GameRecord
{
    public ulong Seed { get; set; }
    public string Scenario { get; set; } = "";
    public string Adversary { get; set; } = "";
    public string Attack { get; set; } = "";
    public List<string> Abilities { get; set; } = new();
    public int InvestigatorCount { get; set; }
    public List<string> Investigators { get; set; } = new();
    public string Result { get; set; } = "Undecided";
    public int Rounds { get; set; }
    public int Kills { get; set; }
    public int Escaped { get; set; }
    public int EvidenceTurnedIn { get; set; }
    public string? EscapeCard { get; set; }
    /// <summary>Round the Escape card was committed to, or 0 if the Evidence gate was never met.</summary>
    public int EscapeSelectedRound { get; set; }
    public int Actions { get; set; }

    /// <summary>Loadout key used by the summary table.</summary>
    public string Loadout => $"{Adversary} | {Attack} + {string.Join("+", Abilities)}";
}

/// <summary>One row of bot-results/anomalies.json.</summary>
public sealed class AnomalyRecord
{
    public ulong Seed { get; set; }
    public string Scenario { get; set; } = "";
    public string Adversary { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Stack { get; set; }
    public int Round { get; set; }
    public string Phase { get; set; } = "";
}
