using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>
/// Drives every BOT-filled seat in one room, using the same brains the arena plays with.
///
/// The unit of bot work is a TURN, not an action: <see cref="InvestigatorTeam.TakeTurn"/> and
/// <see cref="AdversaryBot.TakeTurn"/> both play a whole turn against the engine directly, so
/// the room paces bot seats one turn at a time rather than one action at a time.
/// </summary>
public sealed class BotTable : IAnomalySink
{
    private readonly Game _game;
    private readonly DeterministicRng _rng;
    private readonly Actor _actor;
    private readonly InvestigatorTeam _team;
    private readonly AdversaryBot _adversary;
    private readonly HashSet<string> _botInvestigators;
    private readonly bool _botAdversary;
    private readonly bool _wholeTeamIsBots;
    private readonly List<string> _startSpaces;

    private int _roundBegun;
    private string? _escapeCardSeen;

    /// <summary>Anything the engine did that a bot did not expect, newest last. Kept in
    /// memory only: a room is not the place to fail a game over a bot's confusion.</summary>
    public List<string> Anomalies { get; } = new();

    /// <summary>Fires after every engine-accepted bot action (see <see cref="Actor.AfterAction"/>):
    /// the offline client snapshots each one so bot turns replay visibly, step by step.</summary>
    public Action? AfterAction
    {
        get => _actor.AfterAction;
        set => _actor.AfterAction = value;
    }

    /// <summary>Probe hook: dumps each Investigator's chosen plan (see InvestigatorTeam.Trace).</summary>
    public Action<string>? TraceInvestigators
    {
        get => _team.Trace;
        set => _team.Trace = value;
    }

    /// <summary>Probe hook: every engine refusal the bots shrugged off (label, message).</summary>
    public Action<string, string>? TraceRefusals
    {
        get => _actor.TraceRefusals;
        set => _actor.TraceRefusals = value;
    }

    /// <summary>Probe hook: the label of every engine-accepted bot action, in order.</summary>
    public Action<string>? TraceActions
    {
        get => _actor.TraceActions;
        set => _actor.TraceActions = value;
    }

    public BotTable(Game game, ulong seed, IEnumerable<string> botInvestigatorIds,
        bool botAdversary, IEnumerable<string> startSpaces)
    {
        _game = game;
        // Off a different stream than the engine's own RNG, so bot choices and card shuffles
        // do not shadow each other — the same split the arena uses.
        _rng = new DeterministicRng(seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL);
        _actor = new Actor(this);
        _team = new InvestigatorTeam(game, _actor, _rng, this);
        _adversary = AdversaryBot.Create(game, _actor, _rng, this);
        _botInvestigators = botInvestigatorIds.ToHashSet(StringComparer.Ordinal);
        _botAdversary = botAdversary;
        _startSpaces = startSpaces.ToList();
        _wholeTeamIsBots = game.State.Investigators.All(i => _botInvestigators.Contains(i.DefId));
    }

    public void Anomaly(string kind, string description, Exception? exception = null) =>
        Anomalies.Add($"{kind}: {description}");

    /// <summary>
    /// Do one unit of bot work. Returns false when the game is waiting on a human seat (or on
    /// nothing at all), which is the room's signal to stop pumping and let the table breathe.
    /// </summary>
    public bool TryStep()
    {
        NotifyEscapeSelection();
        // Event choices belong to the Adversary and are armed during the INVESTIGATORS' phase,
        // so they are answered here rather than on the Adversary's turn — by then the round's
        // damage would be done, and at the end of it the offer expires unused.
        bool answeredEventChoice = _botAdversary && _adversary.AnswerEventChoices();
        // The engine holds the game while the team owes its Escape card: an all-bot team
        // picks now; a table with a human Investigator waits for their command.
        if (_game.EscapeChoicePending)
        {
            if (!_wholeTeamIsBots)
            {
                return answeredEventChoice;
            }
            _team.MaybeSelectEscapeCard();
            NotifyEscapeSelection();
        }
        var state = _game.State;
        switch (state.Phase)
        {
            case GamePhase.AdversarySetup:
                if (!_botAdversary)
                {
                    return answeredEventChoice;
                }
                AdversarySetup.Run(_game, _rng, _startSpaces);
                return true;

            case GamePhase.InvestigatorTurns:
                return TryInvestigatorStep(state) || answeredEventChoice;

            case GamePhase.AdversaryTurn:
                if (!_botAdversary)
                {
                    return answeredEventChoice;
                }
                _adversary.TakeTurn();
                return true;

            default:
                return answeredEventChoice;
        }
    }

    private bool TryInvestigatorStep(GameState state)
    {
        if (_roundBegun != state.Round)
        {
            _roundBegun = state.Round;
            _team.BeginRound(); // bookkeeping only; touches no game state
        }

        // Team-level decisions belong to the humans the moment there is one at the table:
        // adopting a Spirit and committing to an Escape card are choices a player came here
        // to make, so an all-bot team makes them and a mixed team waits for a command.
        if (_wholeTeamIsBots)
        {
            _team.AdoptSpiritsIfOffered();
            _team.MaybeSelectEscapeCard();
            NotifyEscapeSelection();
        }

        if (state.ActiveInvestigator != null)
        {
            // A human began their own turn; it is theirs to finish.
            return _botInvestigators.Contains(state.ActiveInvestigator) &&
                   ResumeAbandonedBotTurn(state.ActiveInvestigator);
        }

        // Turn order inside the round is the team's to choose, so the bot brain picks even
        // when its pick is a human — that seat is then simply the one the table waits on.
        string? next = _team.NextToAct();
        if (next == null || !_botInvestigators.Contains(next))
        {
            return false;
        }
        _team.TakeTurn(next);
        return true;
    }

    /// <summary>
    /// A bot seat's turn was left open — the server restarted mid-turn, or a seat flipped from
    /// human to bot. The team brain always opens its own turn, so the only safe move is to
    /// close this one and let the next step start cleanly.
    /// </summary>
    private bool ResumeAbandonedBotTurn(string investigatorId)
    {
        Anomaly("resumed-open-turn", $"{investigatorId}'s turn was already open; ending it");
        _actor.Try("end-abandoned-turn", () => _game.EndTurnWithoutFinalAction());
        return _game.State.ActiveInvestigator == null;
    }

    /// <summary>
    /// Some Escape cards demand Adversary-side placement the moment they are chosen (the
    /// Grave's decoy, the Altar's Ritual tokens, the Horror's Egg Sacs). The bot Adversary
    /// gets that hook whether the card was picked by a bot team or by a human one.
    /// </summary>
    private void NotifyEscapeSelection()
    {
        string? selected = _game.State.Objective.SelectedEscapeCard;
        if (selected == null || selected == _escapeCardSeen)
        {
            return;
        }
        _escapeCardSeen = selected;
        if (_botAdversary)
        {
            _adversary.OnEscapeCardSelected(selected);
        }
    }
}
