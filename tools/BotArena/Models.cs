using StiflingDark.Engine.Core;

namespace BotArena;

/// <summary>Thrown to abandon one game after an anomaly that makes further play meaningless.</summary>
public sealed class ArenaAbort : Exception
{
    public ArenaAbort(string message) : base(message) { }
}

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

/// <summary>
/// Every engine call the bots make funnels through here so that (a) actions can be counted,
/// (b) an InvalidOperationException from a bot probing an illegal action is swallowed, and
/// (c) anything else — or a refusal of an action the harness *requires* to succeed — is
/// recorded as an anomaly rather than silently lost.
/// </summary>
public sealed class Actor
{
    private readonly GameRun _run;

    public Actor(GameRun run) => _run = run;

    public int Actions { get; private set; }

    /// <summary>Set by probe mode to print every refused action with the engine's own message.</summary>
    public Action<string, string>? TraceRefusals { get; set; }

    /// <summary>Probe: an InvalidOperationException just means "not legal right now".</summary>
    public bool Try(string label, Action action)
    {
        try
        {
            action();
            Actions++;
            return true;
        }
        catch (InvalidOperationException e)
        {
            TraceRefusals?.Invoke(label, e.Message);
            return false;
        }
        catch (NotImplementedException e)
        {
            _run.Anomaly("not-implemented", $"{label}: {e.Message}", e);
            return false;
        }
        catch (Exception e)
        {
            _run.Anomaly("unexpected-exception", $"{label}: {e.GetType().Name}: {e.Message}", e);
            throw new ArenaAbort(label);
        }
    }

    /// <summary>Probe that keeps the refusal message (used to prove state leaks on failure).</summary>
    public string? TryMessage(string label, Action action)
    {
        try
        {
            action();
            Actions++;
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
        catch (Exception e)
        {
            _run.Anomaly("unexpected-exception", $"{label}: {e.GetType().Name}: {e.Message}", e);
            throw new ArenaAbort(label);
        }
    }

    /// <summary>An action the rules must allow; a refusal is itself the finding.</summary>
    public void Must(string label, Action action)
    {
        try
        {
            action();
            Actions++;
        }
        catch (Exception e)
        {
            _run.Anomaly(
                e is InvalidOperationException ? "required-action-refused" : "unexpected-exception",
                $"{label}: {e.GetType().Name}: {e.Message}",
                e);
            throw new ArenaAbort(label);
        }
    }
}

/// <summary>Small map/graph helpers shared by every bot.</summary>
public static class Nav
{
    public const int WholeMap = 400;

    public static Dictionary<string, int> From(Game g, string space, int max = WholeMap) =>
        g.Graph.DistancesFrom(space, max, g.State.Overlay);

    public static List<string> Neighbors(Game g, string space) =>
        From(g, space, 1).Keys.Where(k => k != space).OrderBy(k => k, StringComparer.Ordinal).ToList();

    public static int Hops(Dictionary<string, int> dist, string space) =>
        dist.TryGetValue(space, out int d) ? d : int.MaxValue;
}
