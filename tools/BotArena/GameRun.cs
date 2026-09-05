using StiflingDark.Bots;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace BotArena;

/// <summary>Plays one seeded game to its conclusion and records what happened.</summary>
public sealed class GameRun : IAnomalySink
{
    /// <summary>A game that needs more engine calls than this is recorded as a "stalled" anomaly.</summary>
    public const int ActionCap = 2000;

    private readonly GameDatabase _db;
    private readonly ulong _seed;
    private Game? _game;
    private Matchup? _matchup;
    private int _escapeSelectedRound;

    private readonly bool _trace;
    private readonly bool _passiveAdversary;

    public GameRun(GameDatabase db, ulong seed, bool trace = false, bool passiveAdversary = false)
    {
        _db = db;
        _seed = seed;
        _trace = trace;
        _passiveAdversary = passiveAdversary;
    }

    public List<AnomalyRecord> Anomalies { get; } = new();

    /// <summary>The game as it stands, for the single-seed probe mode.</summary>
    public Game? Game => _game;

    public void Anomaly(string kind, string description, Exception? exception = null)
    {
        Anomalies.Add(new AnomalyRecord
        {
            Seed = _seed,
            Scenario = _matchup?.Scenario ?? _game?.State.ScenarioId ?? "",
            Adversary = _matchup?.Adversary ?? _game?.State.Adversary.DefId ?? "",
            Kind = kind,
            Description = description,
            Stack = exception?.StackTrace,
            Round = _game?.State.Round ?? 0,
            Phase = _game?.State.Phase.ToString() ?? "",
        });
    }

    public GameRecord Play()
    {
        // Bot decisions are seeded from the game seed, but off a different stream than the
        // engine's own RNG so bot choices and card shuffles do not shadow each other.
        var rng = new DeterministicRng(_seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL);
        var record = new GameRecord { Seed = _seed };

        try
        {
            _game = GameFactory.Create(_db, _seed, rng, out var matchup);
            _matchup = matchup;
        }
        catch (Exception e)
        {
            Anomaly("setup-failed", $"{e.GetType().Name}: {e.Message}", e);
            record.Result = "SetupFailed";
            return record;
        }

        var game = _game!;
        record.Scenario = _matchup!.Scenario;
        record.Adversary = _matchup.Adversary;
        record.Attack = _matchup.Attack;
        record.Abilities = _matchup.Abilities;
        record.Investigators = _matchup.Investigators;
        record.InvestigatorCount = _matchup.InvestigatorCount;

        var act = new Actor(this);
        var team = new InvestigatorTeam(game, act, rng, this);
        var adversary = _passiveAdversary
            ? AdversaryBot.CreatePassive(game, act, rng, this)
            : AdversaryBot.Create(game, act, rng, this);
        if (_trace)
        {
            team.Trace = line => Console.WriteLine("   " + line);
            act.TraceRefusals = (label, message) =>
            {
                if (!label.StartsWith("move") && !label.StartsWith("start-adversary") &&
                    !label.StartsWith("adv-move") && !label.StartsWith("cultist-move") &&
                    !label.StartsWith("step-off") && !label.StartsWith("pick-up") && !label.StartsWith("light-switch"))
                {
                    Console.WriteLine($"     xx {label}: {message}");
                }
            };
        }

        try
        {
            int rounds = 0;
            while (game.State.Phase != GamePhase.GameOver)
            {
                if (act.Actions > ActionCap)
                {
                    Anomaly("stalled",
                        $"exceeded {ActionCap} engine actions (round {game.State.Round}, " +
                        $"{game.State.Objective.EvidenceTurnedIn} evidence turned in)");
                    break;
                }
                if (rounds++ > _db.Config.Rounds + 5)
                {
                    Anomaly("round-limit-overrun",
                        $"the harness took {rounds} rounds although the game ends after {_db.Config.Rounds}");
                    break;
                }

                int roundBefore = game.State.Round;
                team.BeginRound();
                SelectObjectiveIfDue(team, adversary);
                adversary.AnswerEventChoices();

                int turns = 0;
                while (game.State.Phase == GamePhase.InvestigatorTurns)
                {
                    // A dead Investigator's player may take a Spirit card and keep playing.
                    team.AdoptSpiritsIfOffered();
                    // Turn order is the team's choice each round, not the seating order.
                    string? next = team.NextToAct();
                    if (next == null)
                    {
                        ReportPhaseDeadlock(game);
                        throw new ArenaAbort("phase-deadlock");
                    }
                    team.TakeTurn(next);
                    if (DecidedButStillRunning(game))
                    {
                        throw new ArenaAbort("decided-game-kept-playing");
                    }
                    SelectObjectiveIfDue(team, adversary);
                    // Fire Tornado arms its Zone choice at the first Investigator turn of the
                    // round, not at the draw, so the Adversary is asked again after every turn.
                    adversary.AnswerEventChoices();
                    if (++turns > game.State.Investigators.Count * 3)
                    {
                        Anomaly("investigator-phase-loop",
                            $"{turns} Investigator turns inside round {game.State.Round}");
                        throw new ArenaAbort("investigator-phase-loop");
                    }
                }

                if (game.State.Phase == GamePhase.AdversaryTurn)
                {
                    adversary.TakeTurn();
                    if (DecidedButStillRunning(game))
                    {
                        break;
                    }
                }

                if (game.State.Phase != GamePhase.GameOver && game.State.Round == roundBefore)
                {
                    Anomaly("round-did-not-advance",
                        $"round {roundBefore} finished in phase {game.State.Phase} without the round tracker moving");
                    break;
                }
            }
        }
        catch (ArenaAbort)
        {
            // Already recorded; fall through and report whatever state the game reached.
        }
        catch (Exception e)
        {
            Anomaly("unexpected-exception", $"runner: {e.GetType().Name}: {e.Message}", e);
        }

        record.Result = game.State.Result == GameResult.Undecided && game.State.Phase != GamePhase.GameOver
            ? "Unfinished"
            : game.State.Result.ToString();
        record.Rounds = game.State.Round;
        record.Kills = game.State.Adversary.Kills;
        record.Escaped = game.State.Investigators.Count(i => i.Escaped);
        record.EvidenceTurnedIn = game.State.Objective.EvidenceTurnedIn;
        record.EscapeCard = game.State.Objective.SelectedEscapeCard;
        record.EscapeSelectedRound = _escapeSelectedRound;
        record.Actions = act.Actions;
        return record;
    }

    private void SelectObjectiveIfDue(InvestigatorTeam team, AdversaryBot adversary)
    {
        var game = _game!;
        if (game.State.Phase == GamePhase.GameOver || game.State.ActiveInvestigator != null)
        {
            return;
        }
        string? before = game.State.Objective.SelectedEscapeCard;
        team.MaybeSelectEscapeCard();
        string? after = game.State.Objective.SelectedEscapeCard;
        if (after != null && after != before)
        {
            _escapeSelectedRound = game.State.Round;
            adversary.OnEscapeCardSelected(after);
        }
    }

    /// <summary>
    /// GainWound sets Phase = GameOver the moment the Adversary's kill count is met, but
    /// Game.EndTurn then writes Phase = AdversaryTurn unconditionally, and BeginRound writes
    /// InvestigatorTurns after that. A game decided inside an end-of-turn hook therefore keeps
    /// running. Stop here and record it: playing on from a decided state is meaningless.
    /// </summary>
    private bool DecidedButStillRunning(Game game)
    {
        if (game.State.Result == GameResult.Undecided || game.State.Phase == GamePhase.GameOver)
        {
            return false;
        }
        var s = game.State;
        Anomaly("decided-game-kept-playing",
            $"Result is already {s.Result} (kills {s.Adversary.Kills}/{s.Adversary.KillsToWin}, " +
            $"{s.Investigators.Count(i => i.Dead)} dead, {s.Investigators.Count(i => i.Escaped)} escaped) " +
            $"but Phase went back to {s.Phase} in round {s.Round}: the GameOver written by GainWound was " +
            "overwritten by Game.EndTurn / Game.BeginRound.");
        return true;
    }

    private void ReportPhaseDeadlock(Game game)
    {
        var s = game.State;
        int dead = s.Investigators.Count(i => i.Dead);
        int escaped = s.Investigators.Count(i => i.Escaped);
        Anomaly("phase-deadlock",
            $"phase is {s.Phase} in round {s.Round} but no Investigator can take a turn " +
            $"({dead} dead, {escaped} escaped of {s.Investigators.Count}; kills {s.Adversary.Kills}/" +
            $"{s.Adversary.KillsToWin}). The Adversary turn cannot be reached, so the game can never finish.");
    }
}
