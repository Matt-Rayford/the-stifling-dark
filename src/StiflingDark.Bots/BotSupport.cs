using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>Thrown to abandon one game after an anomaly that makes further play meaningless.</summary>
public sealed class ArenaAbort : Exception
{
    public ArenaAbort(string message) : base(message) { }
}

/// <summary>Where a bot reports something the engine did that it did not expect.</summary>
public interface IAnomalySink
{
    void Anomaly(string kind, string description, System.Exception? exception = null);
}

/// <summary>
/// Every engine call the bots make funnels through here so that (a) actions can be counted,
/// (b) an InvalidOperationException from a bot probing an illegal action is swallowed, and
/// (c) anything else — or a refusal of an action the harness *requires* to succeed — is
/// recorded as an anomaly rather than silently lost.
/// </summary>
public sealed class Actor
{
    private readonly IAnomalySink _sink;

    public Actor(IAnomalySink sink) => _sink = sink;

    public int Actions { get; private set; }

    /// <summary>Set by probe mode to print every refused action with the engine's own message.</summary>
    public Action<string, string>? TraceRefusals { get; set; }

    /// <summary>
    /// Invoked after every action the engine ACCEPTED. The offline client snapshots the
    /// game after each one, so a bot's whole turn can be replayed to the human step by
    /// step instead of appearing all at once. Null (the arena) costs nothing.
    /// </summary>
    public Action? AfterAction { get; set; }

    /// <summary>Probe: an InvalidOperationException just means "not legal right now".</summary>
    public bool Try(string label, Action action)
    {
        try
        {
            action();
            Actions++;
            AfterAction?.Invoke();
            return true;
        }
        catch (InvalidOperationException e)
        {
            TraceRefusals?.Invoke(label, e.Message);
            // A deck running dry is a real physical-game limit, not a bot mis-step: 26 Wound
            // cards can genuinely run out at the table. Surface it wherever it happens.
            if ((e.Message.Contains("deck is empty") || e.Message.Contains("Wound cards remain")))
            {
                _sink.Anomaly("deck-exhausted", $"{label}: {e.Message}");
            }
            return false;
        }
        catch (NotImplementedException e)
        {
            _sink.Anomaly("not-implemented", $"{label}: {e.Message}", e);
            return false;
        }
        catch (Exception e)
        {
            _sink.Anomaly("unexpected-exception", $"{label}: {e.GetType().Name}: {e.Message}", e);
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
            AfterAction?.Invoke();
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
        catch (Exception e)
        {
            _sink.Anomaly("unexpected-exception", $"{label}: {e.GetType().Name}: {e.Message}", e);
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
            _sink.Anomaly(
                (e.Message.Contains("deck is empty") || e.Message.Contains("Wound cards remain")) ? "deck-exhausted"
                    : e is InvalidOperationException ? "required-action-refused"
                    : "unexpected-exception",
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
