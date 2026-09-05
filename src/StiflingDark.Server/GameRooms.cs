using System.Collections.Concurrent;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StiflingDark.Bots;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;
using StiflingDark.Protocol;

namespace StiflingDark.Server;

public sealed class Seat
{
    /// <summary>Mutable only pre-game: removing a seat re-indexes the rest.</summary>
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    /// <summary>Durable public identity (PlayerRegistry); null for bots and anonymous clients.</summary>
    public string? PlayerId { get; set; }
    public SeatRole Role { get; set; } = SeatRole.Investigator;
    public SeatFill Fill { get; set; } = SeatFill.Human;
    /// <summary>The Investigator this seat plays; empty on the Adversary seat.</summary>
    public string InvestigatorId { get; set; } = "";
    /// <summary>Bots are always ready; humans toggle in the lobby.</summary>
    public bool Ready { get; set; }
    public Connection? Client { get; set; }

    /// <summary>Log lines already pushed to this seat, so an update carries only the delta.</summary>
    public int LogCursor { get; set; }

    public bool IsBot => Fill == SeatFill.Bot;
    public bool Connected => Client is { Open: true };

    public Task SendAsync(object message) => Client?.SendAsync(message) ?? Task.CompletedTask;

    public ViewRole ViewRole =>
        Role == SeatRole.Adversary ? Engine.Core.ViewRole.Adversary : Engine.Core.ViewRole.Investigator;
}

/// <summary>
/// One table: seats, an optional running <see cref="Game"/>, and serialized command
/// processing. All game/seat mutations happen under <see cref="_sync"/>; sends happen outside
/// it. Any subset of seats may be BOT-filled — the humans play through the protocol either way.
/// </summary>
public sealed class Room
{
    /// <summary>A bot pump that has taken this many turns is stuck, not thinking.</summary>
    private const int MaxBotSteps = 2000;

    public string Code { get; }

    private readonly GameDatabase _db;
    private readonly object _sync = new();
    private readonly List<Seat> _seats = new();
    private readonly string? _storePath;
    private readonly int _botDelayMs;
    private readonly Action<string, string>? _turnNotifier;
    /// <summary>Seats already alerted for their current wait — one nudge per turn edge.</summary>
    private readonly HashSet<int> _alertedSeats = new();
    /// <summary>Seats owed a full view + whole log on the next broadcast, because they just
    /// (re)joined or asked. Per seat, so one client's resync does not mislabel everyone else's
    /// incremental update.</summary>
    private readonly HashSet<int> _pendingResync = new();

    private SetupInfo _setup = new();
    private ulong _seed;
    private Game? _game;
    private BotTable? _bots;
    /// <summary>The Escape shortlist, drawn once (it consumes engine RNG) and held until the
    /// Investigators commit to one of the three.</summary>
    private List<string>? _escapeChoices;
    private bool _botPumpActive;
    private bool _retired;

    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Room save file, so the sweeper can delete it after retirement.</summary>
    public string? StorePath => _storePath;

    /// <summary>Room-wide bot pacing: "slow" / "medium" / "fast".</summary>
    public string BotSpeed { get; private set; } = "medium";
    private int _botDelayOverrideMs = -1;

    public Room(string code, GameDatabase db, string? storePath, int botDelayMs,
        Action<string, string>? turnNotifier = null)
    {
        Code = code;
        _db = db;
        _storePath = storePath;
        _botDelayMs = botDelayMs;
        _turnNotifier = turnNotifier;
        _setup = DefaultSetup(db);
    }

    public bool Started
    {
        get
        {
            lock (_sync)
            {
                return _game != null;
            }
        }
    }

    // ------------------------------------------------------------- defaults

    private static SetupInfo DefaultSetup(GameDatabase db) => new SetupInfo
    {
        ScenarioId = "sawmill",
        AdversaryId = "butcher",
        AvailableScenarios = db.Maps.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
        // The three v1 Adversaries; Game.NewGame refuses anything else.
        AvailableAdversaries = new List<string> { "butcher", "cult-of-hunlow", "insatiable-horror" },
        AvailableInvestigators = db.Investigators.Where(i => i.Set == "base")
            .Select(i => i.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
    };

    // --------------------------------------------------------------- lobby

    /// <summary>
    /// Take a seat, or reclaim one. A token always reclaims (it is an explicit credential);
    /// identity reclaims only when the seat is unheld or the game is running, so two clients
    /// on one machine get two lobby seats instead of fighting over one.
    /// </summary>
    public Seat? Join(string name, string? token, string? playerId, Connection? connection,
        SeatRole? preferredRole, out string error)
    {
        List<Seat> toNotify;
        Seat seat;
        lock (_sync)
        {
            if (_retired)
            {
                error = "That room has expired.";
                return null;
            }
            var existing = _seats.FirstOrDefault(s =>
                (!string.IsNullOrEmpty(token) && s.Token == token) ||
                (playerId != null && s.PlayerId == playerId &&
                 (_game != null || !s.Connected)));
            if (existing != null)
            {
                existing.Client = connection;
                // A resumed seat replays the whole log, not the tail it missed.
                existing.LogCursor = 0;
                _pendingResync.Add(existing.Index);
                seat = existing;
            }
            else
            {
                if (_game != null)
                {
                    error = "That game already started.";
                    return null;
                }
                var role = preferredRole ?? SeatRole.Investigator;
                if (role == SeatRole.Adversary && _seats.Any(s => s.Role == SeatRole.Adversary))
                {
                    error = "This table already has an Adversary.";
                    return null;
                }
                if (role == SeatRole.Investigator && InvestigatorSeats().Count >= MaxInvestigators)
                {
                    error = "That room is full.";
                    return null;
                }
                seat = new Seat
                {
                    Index = _seats.Count,
                    Name = SanitizeName(name),
                    PlayerId = playerId,
                    Role = role,
                    Client = connection,
                    InvestigatorId = role == SeatRole.Investigator ? NextFreeInvestigator() : "",
                };
                _seats.Add(seat);
            }
            LastActivityUtc = DateTime.UtcNow;
            toNotify = _seats.ToList();
        }

        error = "";
        BroadcastRoom(toNotify);
        if (Started)
        {
            _ = BroadcastGameAsync(); // rejoin: a fresh, complete view
        }
        return seat;
    }

    /// <summary>Investigator counts the rules support.</summary>
    private const int MinInvestigators = 2;
    private const int MaxInvestigators = 4;

    private List<Seat> InvestigatorSeats() =>
        _seats.Where(s => s.Role == SeatRole.Investigator).ToList();

    private string NextFreeInvestigator()
    {
        var taken = _seats.Select(s => s.InvestigatorId).ToHashSet(StringComparer.Ordinal);
        return _setup.AvailableInvestigators.FirstOrDefault(id => !taken.Contains(id)) ?? "";
    }

    /// <summary>Host adds a bot seat on either side of the table.</summary>
    public string AddBot(int requestingSeat, SeatRole role, string? investigatorId)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            string refusal = RequireHostInLobby(requestingSeat);
            if (refusal.Length > 0)
            {
                return refusal;
            }
            if (role == SeatRole.Adversary && _seats.Any(s => s.Role == SeatRole.Adversary))
            {
                return "This table already has an Adversary.";
            }
            if (role == SeatRole.Investigator && InvestigatorSeats().Count >= MaxInvestigators)
            {
                return "The room is full.";
            }
            string chosen = role == SeatRole.Investigator
                ? (string.IsNullOrEmpty(investigatorId) ? NextFreeInvestigator() : investigatorId!)
                : "";
            string seatError = ValidateInvestigator(role, chosen, null);
            if (seatError.Length > 0)
            {
                return seatError;
            }
            _seats.Add(new Seat
            {
                Index = _seats.Count,
                Name = BotName(role, chosen),
                Role = role,
                Fill = SeatFill.Bot,
                InvestigatorId = chosen,
                Ready = true,
            });
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
        return "";
    }

    private string BotName(SeatRole role, string investigatorId) =>
        role == SeatRole.Adversary
            ? _setup.AdversaryId + " (bot)"
            : (investigatorId.Length > 0 ? _db.Investigator(investigatorId).Name : "Investigator") + " (bot)";

    /// <summary>Change a seat's side, Investigator, or human/bot fill. Host only, pre-game.</summary>
    public string SetSeat(int requestingSeat, int targetSeat, SeatRole? role, SeatFill? fill,
        string? investigatorId)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            string refusal = RequireHostInLobby(requestingSeat);
            if (refusal.Length > 0)
            {
                return refusal;
            }
            var seat = _seats.FirstOrDefault(s => s.Index == targetSeat);
            if (seat == null)
            {
                return "No such seat.";
            }
            var newRole = role ?? seat.Role;
            if (newRole == SeatRole.Adversary &&
                _seats.Any(s => s.Role == SeatRole.Adversary && s.Index != targetSeat))
            {
                return "This table already has an Adversary.";
            }
            if (newRole == SeatRole.Investigator &&
                InvestigatorSeats().Count(s => s.Index != targetSeat) >= MaxInvestigators)
            {
                return "The room is full.";
            }
            string chosen = newRole == SeatRole.Adversary
                ? ""
                : investigatorId ?? (seat.InvestigatorId.Length > 0
                    ? seat.InvestigatorId
                    : NextFreeInvestigator());
            string seatError = ValidateInvestigator(newRole, chosen, targetSeat);
            if (seatError.Length > 0)
            {
                return seatError;
            }
            // A seat that was holding a human's connection cannot quietly become a bot under
            // them: dropping the client first makes the takeover explicit.
            if (fill == SeatFill.Bot && seat.Fill == SeatFill.Human && seat.Connected)
            {
                return "That seat still has a player in it.";
            }
            seat.Role = newRole;
            seat.InvestigatorId = chosen;
            if (fill is SeatFill newFill)
            {
                seat.Fill = newFill;
                seat.Ready = newFill == SeatFill.Bot || seat.Ready;
                if (newFill == SeatFill.Bot)
                {
                    seat.Name = BotName(newRole, chosen);
                    seat.Client = null;
                }
            }
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
        return "";
    }

    private string ValidateInvestigator(SeatRole role, string investigatorId, int? ignoreSeat)
    {
        if (role == SeatRole.Adversary)
        {
            return "";
        }
        if (investigatorId.Length == 0)
        {
            return "No Investigator left to assign.";
        }
        if (!_setup.AvailableInvestigators.Contains(investigatorId))
        {
            return $"'{investigatorId}' is not in the roster.";
        }
        if (_seats.Any(s => s.Index != ignoreSeat && s.InvestigatorId == investigatorId))
        {
            return $"'{investigatorId}' is already taken.";
        }
        return "";
    }

    /// <summary>Remove a seat (host only, pre-game). Remaining seats are re-indexed.</summary>
    public string RemoveSeat(int requestingSeat, int targetSeat)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            string refusal = RequireHostInLobby(requestingSeat);
            if (refusal.Length > 0)
            {
                return refusal;
            }
            if (targetSeat == 0)
            {
                return "The host's seat cannot be removed.";
            }
            var seat = _seats.FirstOrDefault(s => s.Index == targetSeat);
            if (seat == null)
            {
                return "No such seat.";
            }
            if (!seat.IsBot && seat.Connected)
            {
                return "That seat still has a player in it.";
            }
            _seats.Remove(seat);
            for (int i = 0; i < _seats.Count; i++)
            {
                _seats[i].Index = i;
            }
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
        return "";
    }

    /// <summary>Host dials in the scenario. Unset fields keep their current value.</summary>
    public string Configure(int requestingSeat, string? scenarioId, string? adversaryId,
        Dictionary<string, string>? startSpaces, List<string>? medicalItemSpaces,
        bool? useMiniExpansionCards)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            string refusal = RequireHostInLobby(requestingSeat);
            if (refusal.Length > 0)
            {
                return refusal;
            }
            if (scenarioId != null)
            {
                if (!_setup.AvailableScenarios.Contains(scenarioId))
                {
                    return $"'{scenarioId}' is not a scenario on this server.";
                }
                _setup.ScenarioId = scenarioId;
                // Board-specific placements do not survive a board change.
                _setup.StartSpaces.Clear();
                _setup.MedicalItemSpaces.Clear();
            }
            if (adversaryId != null)
            {
                if (!_setup.AvailableAdversaries.Contains(adversaryId))
                {
                    return $"'{adversaryId}' is not an Adversary on this server.";
                }
                _setup.AdversaryId = adversaryId;
                foreach (var bot in _seats.Where(s => s.IsBot && s.Role == SeatRole.Adversary))
                {
                    bot.Name = BotName(SeatRole.Adversary, "");
                }
            }
            if (startSpaces != null)
            {
                _setup.StartSpaces = new Dictionary<string, string>(startSpaces);
            }
            if (medicalItemSpaces != null)
            {
                _setup.MedicalItemSpaces = medicalItemSpaces.ToList();
            }
            if (useMiniExpansionCards is bool mini)
            {
                _setup.UseMiniExpansionCards = mini;
            }
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
        return "";
    }

    /// <summary>
    /// Any seated human may change the pace, before or during the game — the player drowning
    /// in bot turns is rarely the host.
    /// </summary>
    public string SetSpeed(int requestingSeat, string? speed)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            if (requestingSeat < 0)
            {
                return "You are not seated in this room.";
            }
            switch ((speed ?? "").Trim().ToLowerInvariant())
            {
                case "slow":
                    BotSpeed = "slow";
                    _botDelayOverrideMs = 2200;
                    break;
                case "fast":
                    BotSpeed = "fast";
                    _botDelayOverrideMs = 500;
                    break;
                case "medium":
                    BotSpeed = "medium";
                    _botDelayOverrideMs = -1; // server default (BOT_DELAY_MS)
                    break;
                default:
                    return "Speed must be slow, medium, or fast.";
            }
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
        return "";
    }

    public void SetReady(int seatIndex, bool ready)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            var seat = _seats.FirstOrDefault(s => s.Index == seatIndex && !s.IsBot);
            if (seat == null || _game != null)
            {
                return;
            }
            seat.Ready = ready;
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
    }

    private string RequireHostInLobby(int requestingSeat)
    {
        if (requestingSeat != 0)
        {
            return "Only the host can change the table.";
        }
        return _game != null ? "The game already started." : "";
    }

    // ---------------------------------------------------------------- start

    public string Start(int requestingSeat)
    {
        lock (_sync)
        {
            string refusal = RequireHostInLobby(requestingSeat);
            if (refusal.Length > 0)
            {
                return refusal;
            }
            var investigators = InvestigatorSeats();
            if (investigators.Count < MinInvestigators)
            {
                return $"Need at least {MinInvestigators} Investigators.";
            }
            if (!_seats.Any(s => s.Role == SeatRole.Adversary))
            {
                return "The table needs an Adversary — seat a player or add a bot.";
            }
            var notReady = _seats.Where(s => !s.Ready && s.Index != requestingSeat).ToList();
            if (notReady.Count > 0)
            {
                return "Waiting for: " + string.Join(", ", notReady.Select(s => s.Name));
            }

            try
            {
                _seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
                _game = BuildGame(_seed);
            }
            catch (InvalidOperationException e)
            {
                return "Cannot start: " + e.Message;
            }
            StartBots();
            Save();
        }

        lock (_sync)
        {
            // The first update after a start is everyone's baseline.
            foreach (var seat in _seats)
            {
                _pendingResync.Add(seat.Index);
            }
        }
        BroadcastRoom(SeatsSnapshot());
        _ = BroadcastThenPumpAsync();
        return "";
    }

    /// <summary>Fill in whatever the host left unset, then hand the engine a legal setup.</summary>
    private Game BuildGame(ulong seed)
    {
        var map = _db.Map(_setup.ScenarioId);
        var starts = map.Spaces.Where(s => s.Kind == SpaceKind.Start)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var medicalPool = map.Spaces.Where(s => s.Kind == SpaceKind.MedicalItem)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

        var roster = InvestigatorSeats().Select(s => s.InvestigatorId).ToList();
        var startSpaces = new Dictionary<string, string>();
        for (int i = 0; i < roster.Count; i++)
        {
            startSpaces[roster[i]] =
                _setup.StartSpaces.TryGetValue(roster[i], out string? chosen) && chosen.Length > 0
                    ? chosen
                    : starts[i % starts.Count];
        }

        int medicalCount = _db.Config.ByInvestigatorCount[roster.Count].MedicalItemsOnBoard;
        var medical = _setup.MedicalItemSpaces.Count == medicalCount
            ? _setup.MedicalItemSpaces.ToList()
            : medicalPool.Take(medicalCount).ToList();

        // Keep the resolved placements in the room snapshot so a client sees what was used.
        _setup.StartSpaces = startSpaces;
        _setup.MedicalItemSpaces = medical;

        return Game.NewGame(_db, new GameSetup
        {
            ScenarioId = _setup.ScenarioId,
            Seed = seed,
            AdversaryId = _setup.AdversaryId,
            InvestigatorStartSpaces = startSpaces,
            MedicalItemSpaces = medical,
            UseMiniExpansionCards = _setup.UseMiniExpansionCards,
            // Short-handed starting Items all go to the first human Investigator seat
            // (an all-bot team defaults to its first seat).
            StartingItemsInvestigatorId = InvestigatorSeats()
                .FirstOrDefault(s => !s.IsBot)?.InvestigatorId,
        });
    }

    private void StartBots()
    {
        var botInvestigators = _seats
            .Where(s => s.IsBot && s.Role == SeatRole.Investigator)
            .Select(s => s.InvestigatorId);
        bool botAdversary = _seats.Any(s => s.IsBot && s.Role == SeatRole.Adversary);
        _bots = new BotTable(_game!, _seed, botInvestigators, botAdversary,
            _setup.StartSpaces.Values);
    }

    public void Detach(Connection connection)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            var seat = _seats.FirstOrDefault(s => s.Client == connection);
            if (seat == null)
            {
                return;
            }
            seat.Client = null;
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
    }

    /// <summary>
    /// End the table for everyone. Any seated human may do it (the async group is presumed to
    /// agree — and it is the only way to clear a table whose other seats are bots). The room
    /// refuses all further joins and commands; every other connected member is told who pulled
    /// the plug. RoomManager.Abandon does the removal and snapshot deletion.
    /// </summary>
    public string Abandon(string? playerId)
    {
        List<Seat> toNotify;
        string by;
        lock (_sync)
        {
            if (_retired)
            {
                return "That room has expired.";
            }
            var seat = playerId == null
                ? null
                : _seats.FirstOrDefault(s => s.PlayerId == playerId);
            if (seat == null)
            {
                return "You are not seated in that game.";
            }
            _retired = true;
            by = seat.Name;
            toNotify = _seats.Where(s => s != seat && s.Connected).ToList();
        }
        foreach (var member in toNotify)
        {
            _ = member.SendAsync(new RoomClosedMessage { Code = Code, By = by });
        }
        return "";
    }

    /// <summary>Give up a lobby seat entirely (a started game keeps it for reconnects).</summary>
    public string Leave(Connection connection)
    {
        List<Seat> toNotify;
        lock (_sync)
        {
            var seat = _seats.FirstOrDefault(s => s.Client == connection);
            if (seat == null)
            {
                return "You are not seated in this room.";
            }
            if (_game != null)
            {
                seat.Client = null;
            }
            else
            {
                _seats.Remove(seat);
                for (int i = 0; i < _seats.Count; i++)
                {
                    _seats[i].Index = i;
                }
            }
            toNotify = _seats.ToList();
        }
        BroadcastRoom(toNotify);
        return "";
    }

    // ------------------------------------------------------------- commands

    public async Task HandleCommandAsync(int seatIndex, JObject commandJson)
    {
        GameCommand command;
        try
        {
            command = WireCodec.DecodeCommand(commandJson);
        }
        catch (JsonException e)
        {
            await SendErrorAsync(seatIndex, $"Bad command: {e.Message}");
            return;
        }

        lock (_sync)
        {
            if (_retired)
            {
                _ = SendErrorAsync(seatIndex, "This game has been ended.");
                return;
            }
            if (_game == null)
            {
                _ = SendErrorAsync(seatIndex, "The game has not started.");
                return;
            }
            var seat = _seats.FirstOrDefault(s => s.Index == seatIndex);
            if (seat == null)
            {
                _ = SendErrorAsync(seatIndex, "You are not seated in this room.");
                return;
            }
            if (seat.IsBot)
            {
                _ = SendErrorAsync(seatIndex, "That seat is played by a bot.");
                return;
            }
            // A seat may only ever act as its own side of the table.
            var required = seat.Role == SeatRole.Adversary
                ? CommandSide.Adversary
                : CommandSide.Investigator;
            if (command.Side != required)
            {
                _ = SendErrorAsync(seatIndex,
                    $"That is an {command.Side} command and you hold the {seat.Role} seat.");
                return;
            }
            try
            {
                command.Apply(_game);
            }
            catch (InvalidOperationException e)
            {
                _ = SendErrorAsync(seatIndex, e.Message);
                return;
            }
            if (command is DrawEscapeChoicesCommand draw)
            {
                _escapeChoices = draw.Choices;
            }
            if (_game.State.Objective.SelectedEscapeCard != null)
            {
                _escapeChoices = null; // the shortlist is spent
            }
            LastActivityUtc = DateTime.UtcNow;
            Save();
        }

        await BroadcastThenPumpAsync();
    }

    /// <summary>Re-send this seat's whole view and log — the reconnect path.</summary>
    public Task ResyncAsync(int seatIndex)
    {
        lock (_sync)
        {
            var seat = _seats.FirstOrDefault(s => s.Index == seatIndex);
            if (seat == null)
            {
                return SendErrorAsync(seatIndex, "You are not seated in this room.");
            }
            seat.LogCursor = 0;
            _pendingResync.Add(seat.Index);
        }
        return Started
            ? BroadcastGameAsync()
            : SendErrorAsync(seatIndex, "The game has not started.");
    }

    // ------------------------------------------------------------ bot pump

    /// <summary>Kick the bot pump (used after rehydrating a room mid-bot-turn).</summary>
    public void PumpBots() => _ = EnsureBotPumpAsync();

    private async Task BroadcastThenPumpAsync()
    {
        await BroadcastGameAsync();
        await EnsureBotPumpAsync();
    }

    /// <summary>
    /// Paced bot seats: one bot TURN per delay with a broadcast after each, so humans watch
    /// the table move instead of the whole round flashing past. At most one pump runs per
    /// room; human commands interleave safely through the room lock.
    /// </summary>
    private async Task EnsureBotPumpAsync()
    {
        lock (_sync)
        {
            if (_botPumpActive || _game == null)
            {
                return;
            }
            _botPumpActive = true;
        }

        try
        {
            for (int steps = 0; steps < MaxBotSteps; steps++)
            {
                lock (_sync)
                {
                    if (_retired || _game == null ||
                        _game.State.Phase == GamePhase.GameOver || _bots == null)
                    {
                        return;
                    }
                    DrawEscapeChoicesIfDue();
                    bool acted;
                    try
                    {
                        acted = _bots.TryStep();
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException)
                    {
                        // A bot walked into a rule it misread. Recording it beats wedging the
                        // pump in a retry loop that would burn a core.
                        _bots.Anomalies.Add($"bot-step-failed: {e.Message}");
                        return;
                    }
                    if (!acted)
                    {
                        return;
                    }
                    LastActivityUtc = DateTime.UtcNow;
                    Save();
                }
                await BroadcastGameAsync();
                int delayMs = _botDelayOverrideMs >= 0 ? _botDelayOverrideMs : _botDelayMs;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _botPumpActive = false;
            }
        }
    }

    /// <summary>
    /// A human Investigator team gets its three-card Escape shortlist drawn for it the moment
    /// the Evidence gate is met; committing to one of the three stays their decision. An
    /// all-bot team draws and picks inside its own brain instead.
    /// </summary>
    private void DrawEscapeChoicesIfDue()
    {
        if (_game == null || _escapeChoices != null ||
            _game.State.Objective.SelectedEscapeCard != null ||
            _seats.Where(s => s.Role == SeatRole.Investigator).All(s => s.IsBot))
        {
            return;
        }
        try
        {
            _escapeChoices = _game.DrawEscapeChoices().ToList();
        }
        catch (InvalidOperationException)
        {
            // The gate is not open yet; ask again next time round.
        }
    }

    // ---------------------------------------------------------- broadcast

    private async Task BroadcastGameAsync()
    {
        List<(Seat Seat, UpdateMessage Message)> outbox = new();
        List<(string PlayerId, string Code)>? alerts = null;
        lock (_sync)
        {
            if (_game == null)
            {
                return;
            }
            DrawEscapeChoicesIfDue();
            var acting = ActingSeats();

            // Turn alerts, rising-edge: a seat that is newly awaited AND away from the table
            // gets one nudge on its player's other connections.
            foreach (var seat in _seats.Where(s => !s.IsBot))
            {
                if (!acting.Contains(seat.Index) || seat.Connected)
                {
                    _alertedSeats.Remove(seat.Index);
                }
                else if (seat.PlayerId != null && _alertedSeats.Add(seat.Index))
                {
                    (alerts ??= new List<(string, string)>()).Add((seat.PlayerId, Code));
                }
            }

            foreach (var seat in _seats.Where(s => !s.IsBot))
            {
                var view = _game.ViewFor(seat.ViewRole, seat.InvestigatorId, _escapeChoices);
                var delta = view.Log.Skip(seat.LogCursor).ToList();
                seat.LogCursor = view.Log.Count;
                outbox.Add((seat, new UpdateMessage
                {
                    Events = WireCodec.EncodeLog(delta),
                    View = WireCodec.EncodeView(view),
                    ActingSeats = acting,
                    YourTurn = acting.Contains(seat.Index),
                    Resync = _pendingResync.Remove(seat.Index),
                }));
            }
        }
        if (alerts != null && _turnNotifier != null)
        {
            foreach (var (playerId, code) in alerts)
            {
                _turnNotifier(playerId, code);
            }
        }
        await Task.WhenAll(outbox.Select(o => o.Seat.SendAsync(o.Message)));
    }

    /// <summary>
    /// Seats the game is waiting on right now. During Investigator turns that is the seat
    /// whose turn is open, or — between turns — every Investigator who has not gone yet,
    /// because turn order inside the round is the team's to choose.
    /// </summary>
    private List<int> ActingSeats()
    {
        if (_game == null)
        {
            return new List<int>();
        }
        var state = _game.State;
        switch (state.Phase)
        {
            case GamePhase.AdversarySetup:
            case GamePhase.AdversaryTurn:
                return _seats.Where(s => s.Role == SeatRole.Adversary)
                    .Select(s => s.Index).ToList();

            case GamePhase.InvestigatorTurns:
                if (state.ActiveInvestigator is string active)
                {
                    return _seats.Where(s => s.InvestigatorId == active).Select(s => s.Index).ToList();
                }
                var pending = state.Investigators
                    .Where(i => !i.TurnTakenThisRound && !i.Escaped && (!i.Dead || i.SpiritId != null))
                    .Select(i => i.DefId).ToHashSet(StringComparer.Ordinal);
                return _seats.Where(s => pending.Contains(s.InvestigatorId))
                    .Select(s => s.Index).ToList();

            default:
                return new List<int>();
        }
    }

    private void BroadcastRoom(List<Seat> seats)
    {
        bool started = Started;
        // Copy under the lock: a concurrent configure must not mutate the collections while
        // Newtonsoft walks them on the way out.
        SetupInfo setup;
        lock (_sync)
        {
            setup = new SetupInfo
            {
                ScenarioId = _setup.ScenarioId,
                AdversaryId = _setup.AdversaryId,
                StartSpaces = new Dictionary<string, string>(_setup.StartSpaces),
                MedicalItemSpaces = _setup.MedicalItemSpaces.ToList(),
                UseMiniExpansionCards = _setup.UseMiniExpansionCards,
                AvailableScenarios = _setup.AvailableScenarios,
                AvailableAdversaries = _setup.AvailableAdversaries,
                AvailableInvestigators = _setup.AvailableInvestigators,
            };
        }
        foreach (var seat in seats.Where(s => !s.IsBot))
        {
            var message = new RoomMessage
            {
                Code = Code,
                YourSeat = seat.Index,
                Token = seat.Token,
                Started = started,
                Speed = BotSpeed,
                Setup = setup,
                Seats = seats.Select(s => new SeatInfo
                {
                    Seat = s.Index,
                    Name = s.Name,
                    Role = s.Role,
                    Fill = s.Fill,
                    InvestigatorId = s.InvestigatorId,
                    Connected = s.IsBot || s.Connected,
                    Ready = s.Ready,
                }).ToList(),
            };
            _ = seat.SendAsync(message);
        }
    }

    private Task SendErrorAsync(int seatIndex, string message)
    {
        Seat? seat;
        lock (_sync)
        {
            seat = _seats.FirstOrDefault(s => s.Index == seatIndex);
        }
        return seat?.SendAsync(new ErrorMessage { Message = message }) ?? Task.CompletedTask;
    }

    private List<Seat> SeatsSnapshot()
    {
        lock (_sync)
        {
            return _seats.ToList();
        }
    }

    public int SeatIndexOf(Connection connection)
    {
        lock (_sync)
        {
            return _seats.FirstOrDefault(s => s.Client == connection)?.Index ?? -1;
        }
    }

    // ------------------------------------------------------------ my games

    public GameSummary? SummaryFor(string playerId)
    {
        lock (_sync)
        {
            var seat = _seats.FirstOrDefault(s => s.PlayerId == playerId);
            if (seat == null)
            {
                return null;
            }
            bool finished = _game != null && _game.State.Phase == GamePhase.GameOver;
            var acting = _game != null && !finished ? ActingSeats() : new List<int>();
            return new GameSummary
            {
                Code = Code,
                Players = _seats.Select(s => s.Name).ToList(),
                YourSeat = seat.Index,
                YourRole = seat.Role,
                Started = _game != null,
                Finished = finished,
                YourTurn = acting.Contains(seat.Index),
                Round = _game?.State.Round ?? 0,
                ScenarioId = _setup.ScenarioId,
                AdversaryId = _setup.AdversaryId,
            };
        }
    }

    /// <summary>Not finished (lobby or running) — counts against the per-player cap.</summary>
    public bool IsActiveFor(string playerId)
    {
        lock (_sync)
        {
            return (_game == null || _game.State.Phase != GamePhase.GameOver) &&
                   _seats.Any(s => s.PlayerId == playerId);
        }
    }

    // --------------------------------------------------------- persistence

    /// <summary>
    /// GameState round-trips through Newtonsoft by design, so a room is saved as a snapshot
    /// rather than as a replayable command log: bot seats drive the engine directly, not
    /// through commands, so there is nothing to replay them from.
    /// </summary>
    private static readonly JsonSerializerSettings PersistenceSettings = new()
    {
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        Formatting = Formatting.None,
    };

    private void Save()
    {
        // An abandoned (retired) room must never write again: a racing bot turn could
        // otherwise resurrect the snapshot file right after Abandon deleted it.
        if (_storePath == null || _game == null || _retired)
        {
            return;
        }
        var snapshot = new JObject
        {
            ["seed"] = _seed.ToString(),
            ["speed"] = BotSpeed,
            ["setup"] = JObject.FromObject(_setup, JsonSerializer.Create(PersistenceSettings)),
            ["seats"] = new JArray(_seats.Select(s => new JObject
            {
                ["name"] = s.Name,
                ["token"] = s.Token,
                ["playerId"] = s.PlayerId ?? "",
                ["role"] = s.Role.ToString(),
                ["fill"] = s.Fill.ToString(),
                ["investigatorId"] = s.InvestigatorId,
            })),
            ["escapeChoices"] = _escapeChoices == null ? null : new JArray(_escapeChoices),
            ["state"] = JObject.FromObject(_game.State, JsonSerializer.Create(PersistenceSettings)),
        };
        try
        {
            // Write-then-rename: a crash mid-save leaves the previous snapshot intact.
            string temporary = _storePath + ".tmp";
            File.WriteAllText(temporary, snapshot.ToString(Formatting.None));
            File.Move(temporary, _storePath, overwrite: true);
        }
        catch (IOException)
        {
            // Persistence is best-effort; a full disk must not kill the game.
        }
    }

    /// <summary>Rebuild a started room from its snapshot. Returns null for finished or
    /// unreadable games — better to drop a room than to refuse to boot.</summary>
    public static Room? LoadFromFile(string code, GameDatabase db, string path, int botDelayMs,
        Action<string, string>? turnNotifier = null)
    {
        try
        {
            var snapshot = JObject.Parse(File.ReadAllText(path));
            var serializer = JsonSerializer.Create(PersistenceSettings);
            var state = snapshot["state"]!.ToObject<GameState>(serializer)!;
            if (state.Phase == GamePhase.GameOver)
            {
                return null;
            }

            var room = new Room(code, db, path, botDelayMs, turnNotifier);
            room._seed = ulong.Parse((string)snapshot["seed"]!);
            room._setup = snapshot["setup"]!.ToObject<SetupInfo>(serializer)!;
            room.BotSpeed = (string?)snapshot["speed"] ?? "medium";
            room._escapeChoices = (snapshot["escapeChoices"] as JArray)?
                .Select(c => (string)c!).ToList();
            int index = 0;
            foreach (var seat in (JArray)snapshot["seats"]!)
            {
                string playerId = (string?)seat["playerId"] ?? "";
                room._seats.Add(new Seat
                {
                    Index = index++,
                    Name = (string)seat["name"]!,
                    Token = (string)seat["token"]!,
                    PlayerId = playerId.Length > 0 ? playerId : null,
                    Role = Enum.Parse<SeatRole>((string)seat["role"]!),
                    Fill = Enum.Parse<SeatFill>((string)seat["fill"]!),
                    InvestigatorId = (string?)seat["investigatorId"] ?? "",
                    Ready = true,
                });
            }
            room._game = Game.FromState(db, state);
            // Bot memory is heuristic, not authoritative: a rehydrated brain simply starts
            // planning again from the board in front of it.
            room.StartBots();
            room.LastActivityUtc = File.GetLastWriteTimeUtc(path);
            return room;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically mark this room dead if it qualifies: an empty lobby past its TTL, a finished
    /// game past its grace, or an abandoned running game past the stale window. Once retired,
    /// joins are refused, so the sweeper cannot race a returning player.
    /// </summary>
    public bool TryRetire(TimeSpan lobbyTtl, TimeSpan finishedTtl, TimeSpan staleTtl)
    {
        lock (_sync)
        {
            if (_retired)
            {
                return true;
            }
            var age = DateTime.UtcNow - LastActivityUtc;
            bool anyConnected = _seats.Any(s => !s.IsBot && s.Connected);
            bool finished = _game != null && _game.State.Phase == GamePhase.GameOver;
            bool eligible =
                (_game == null && !anyConnected && age > lobbyTtl) ||
                (finished && age > finishedTtl) ||
                (_game != null && !finished && !anyConnected && age > staleTtl);
            if (!eligible)
            {
                return false;
            }
            _retired = true;
            return true;
        }
    }

    private static string SanitizeName(string name)
    {
        name = (name ?? "").Trim();
        return name.Length == 0 ? "Player" : name[..Math.Min(20, name.Length)];
    }
}

public sealed class RoomManager
{
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O/1/I/L

    /// <summary>Grace before deleting finished snapshots — post-game review.</summary>
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromHours(12);
    /// <summary>The async play-by-turn window: touch the game within 3 days or it expires.</summary>
    private static readonly TimeSpan StaleRetention = TimeSpan.FromDays(3);
    /// <summary>Never-started lobbies live only in memory; empty ones expire fast.</summary>
    private static readonly TimeSpan LobbyTtl = TimeSpan.FromHours(1);

    private readonly GameDatabase _db;
    private readonly string _dataDir;
    private readonly int _botDelayMs;
    private readonly Action<string, string>? _turnNotifier;
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    // Long-lived servers sweep periodically, not just at boot. Held so it is not collected.
    private readonly Timer _sweepTimer;

    public RoomManager(GameDatabase db, string dataDir, int botDelayMs,
        Action<string, string>? turnNotifier = null)
    {
        _db = db;
        _dataDir = dataDir;
        _botDelayMs = botDelayMs;
        _turnNotifier = turnNotifier;
        Directory.CreateDirectory(dataDir);
        LoadPersistedRooms();
        _sweepTimer = new Timer(_ => Sweep(), null,
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public int RoomCount => _rooms.Count;

    public Room Create()
    {
        while (true)
        {
            var code = new string(Enumerable.Range(0, 5)
                .Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)])
                .ToArray());
            var room = new Room(code, _db, Path.Combine(_dataDir, code + ".json"),
                _botDelayMs, _turnNotifier);
            if (_rooms.TryAdd(code, room))
            {
                return room;
            }
        }
    }

    public Room? Find(string code) =>
        _rooms.TryGetValue((code ?? "").Trim().ToUpperInvariant(), out var room) ? room : null;

    /// <summary>My Games: needs-you first, then running, then lobbies, then finished.</summary>
    public List<GameSummary> GamesFor(string playerId) =>
        _rooms.Values
            .Select(room => (Room: room, Summary: room.SummaryFor(playerId)))
            .Where(pair => pair.Summary != null)
            .OrderByDescending(pair => pair.Summary!.YourTurn)
            .ThenBy(pair => pair.Summary!.Finished)
            .ThenByDescending(pair => pair.Room.LastActivityUtc)
            .Select(pair => pair.Summary!)
            .ToList();

    /// <summary>Unfinished rooms this player occupies — the create-room spam guard.</summary>
    public int ActiveGameCount(string playerId) =>
        _rooms.Values.Count(room => room.IsActiveFor(playerId));

    /// <summary>Player-initiated close (the My Games ✕): retire the room, drop it from the
    /// registry, and delete its snapshot. Empty string on success, else the refusal.</summary>
    public string Abandon(string code, string? playerId)
    {
        var room = Find(code);
        if (room == null)
        {
            return "No room with that code.";
        }
        string error = room.Abandon(playerId);
        if (error.Length > 0)
        {
            return error;
        }
        if (_rooms.TryRemove(room.Code, out _) && room.StorePath != null)
        {
            TryDelete(room.StorePath);
        }
        return "";
    }

    /// <summary>Retire dead rooms. Returns how many were removed.</summary>
    public int Sweep(TimeSpan? lobbyTtl = null, TimeSpan? finishedTtl = null,
        TimeSpan? staleTtl = null)
    {
        int removed = 0;
        foreach (var pair in _rooms)
        {
            if (!pair.Value.TryRetire(lobbyTtl ?? LobbyTtl, finishedTtl ?? FinishedRetention,
                    staleTtl ?? StaleRetention))
            {
                continue;
            }
            if (_rooms.TryRemove(pair.Key, out var room))
            {
                removed++;
                if (room.StorePath != null)
                {
                    TryDelete(room.StorePath); // no-op when the game never started
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// Rebuild every unfinished game from its snapshot (e.g. after a redeploy), and sweep the
    /// graveyard: finished or corrupt files past their grace period are deleted, not loaded.
    /// </summary>
    private void LoadPersistedRooms()
    {
        foreach (string path in Directory.GetFiles(_dataDir, "*.json"))
        {
            string code = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            var room = age <= StaleRetention
                ? Room.LoadFromFile(code, _db, path, _botDelayMs, _turnNotifier)
                : null;
            if (room != null)
            {
                _rooms.TryAdd(code, room);
                room.PumpBots(); // in case a bot was mid-turn when the server died
            }
            else if (age > FinishedRetention)
            {
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A locked or vanished file is not worth failing boot over.
        }
    }
}
