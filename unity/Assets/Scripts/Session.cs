using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Protocol;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The room as the last <c>room</c> message described it. Re-read wholesale every time:
    /// the server re-indexes seats when one is removed, so a cached seat number goes stale.
    /// </summary>
    public sealed class RoomState
    {
        public string Code = "";
        public int YourSeat = -1;
        /// <summary>This seat's reconnect token. Persist it against <see cref="Code"/>.</summary>
        public string Token = "";
        public bool Started;
        public string Speed = "medium";
        public SetupInfo Setup = new SetupInfo();
        public List<SeatInfo> Seats = new List<SeatInfo>();

        public SeatInfo YourSeatInfo => Seats.FirstOrDefault(s => s.Seat == YourSeat);
        /// <summary>Seat 0 is the host, always — the only seat that may change the table.</summary>
        public bool YouAreHost => YourSeat == 0;
        public int InvestigatorSeats => Seats.Count(s => s.Role == SeatRole.Investigator);
        public bool HasAdversary => Seats.Any(s => s.Role == SeatRole.Adversary);
    }

    /// <summary>
    /// The client's whole conversation with the server: one <see cref="ClientWebSocket"/>, an
    /// inbox drained on the main thread by <see cref="Pump"/>, and one method per protocol
    /// verb. Deliberately free of any UnityEngine reference so it compiles (and is
    /// compile-checked) outside the editor — see tools/ClientCheck.
    ///
    /// The server is authoritative for every rule: this class never validates a command, it
    /// posts it and shows whatever <c>error</c> comes back.
    /// </summary>
    public sealed class ServerSession : IGameSession
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly ConcurrentQueue<JObject> _inbox = new ConcurrentQueue<JObject>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly JsonSerializer _serializer = JsonSerializer.Create(WireCodec.Settings);
        private readonly List<PlayerView.LogEntry> _log = new List<PlayerView.LogEntry>();
        private readonly List<Action> _afterWelcome = new List<Action>();

        private readonly string _playerKey;
        private readonly string _playerName;
        private bool _helloSent;

        public string Url { get; private set; } = "";
        public RoomState Room { get; } = new RoomState();
        public PlayerView View { get; private set; }
        public IReadOnlyList<int> ActingSeats { get; private set; } = new List<int>();
        public bool YourTurn { get; private set; }
        public IReadOnlyList<PlayerView.LogEntry> Log => _log;
        public List<GameSummary> GamesList { get; private set; } = new List<GameSummary>();

        /// <summary>Durable public id from <c>welcome</c>; empty until the greeting lands.</summary>
        public string PlayerId { get; private set; } = "";
        /// <summary>True once <c>welcome</c> has arrived — lobby verbs wait for it.</summary>
        public bool Greeted { get; private set; }
        public bool Connected => _socket.State == WebSocketState.Open;
        public bool Closed => _socket.State == WebSocketState.Closed ||
                              _socket.State == WebSocketState.Aborted;
        public string ConnectionError { get; private set; } = "";

        /// <summary>Bumped on every inbound change; the UI re-renders when it moves.</summary>
        public int Revision { get; private set; }

        public event Action RoomChanged;
        public event Action GameUpdated;
        /// <summary>Server <c>error</c> text, verbatim — usually the engine's own refusal.</summary>
        public event Action<string> ErrorReceived;
        /// <summary>A game you are not watching wants you; payload is its room code.</summary>
        public event Action<string> TurnAlert;
        /// <summary>A room you sit in was ended for everyone; payload is (code, who ended it).</summary>
        public event Action<string, string> RoomClosed;

        private ServerSession(string playerKey, string playerName)
        {
            _playerKey = playerKey;
            _playerName = playerName;
        }

        public static ServerSession Connect(string url, string playerKey, string playerName)
        {
            var session = new ServerSession(playerKey, playerName) { Url = url };
            _ = session.RunAsync(url);
            return session;
        }

        private async Task RunAsync(string url)
        {
            try
            {
                await _socket.ConnectAsync(new Uri(url), _cts.Token);
                Hello();
                var buffer = new byte[64 * 1024];
                while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    using (var stream = new System.IO.MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _socket.ReceiveAsync(
                                new ArraySegment<byte>(buffer), _cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                return;
                            }
                            stream.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        _inbox.Enqueue(JObject.Parse(Encoding.UTF8.GetString(stream.ToArray())));
                    }
                }
            }
            catch (Exception e)
            {
                ConnectionError = e.Message;
                _inbox.Enqueue(new JObject
                {
                    ["type"] = MessageType.Error,
                    ["message"] = "Connection lost: " + e.Message,
                });
            }
        }

        // ------------------------------------------------------------- verbs

        /// <summary>
        /// Identify first on every connection. The server keys My Games, turn alerts, and
        /// token-free seat reclaim off this, and refuses keys shorter than 16 characters.
        /// </summary>
        public void Hello()
        {
            if (_helloSent)
            {
                return;
            }
            _helloSent = true;
            Send(new { type = MessageType.Hello, playerKey = _playerKey, name = _playerName });
        }

        public void ListGames() => AfterWelcome(() => Send(new { type = MessageType.ListGames }));

        /// <summary>End a game for everyone (the My Games ✕). The server replies with a fresh
        /// games list, so the row disappears on its own.</summary>
        public void AbandonGame(string code) => AfterWelcome(() => Send(new
        {
            type = MessageType.AbandonGame,
            code,
        }));

        public void CreateRoom(string name, SeatRole role) => AfterWelcome(() => Send(new
        {
            type = MessageType.CreateRoom,
            name,
            role = RoleWire(role),
        }));

        public void JoinRoom(string code, string name, string token, SeatRole role) =>
            AfterWelcome(() => Send(new
            {
                type = MessageType.JoinRoom,
                code,
                name,
                token = string.IsNullOrEmpty(token) ? null : token,
                role = RoleWire(role),
            }));

        public void LeaveRoom() => Send(new { type = MessageType.LeaveRoom });

        public void AddBot(SeatRole role, string investigatorId = null) => Send(new
        {
            type = MessageType.AddBot,
            role = RoleWire(role),
            investigatorId,
        });

        /// <summary>Host only, pre-game. Unset fields keep their current value.</summary>
        public void SetSeat(int seat, SeatRole? role = null, SeatFill? fill = null,
            string investigatorId = null)
        {
            var message = new JObject { ["type"] = MessageType.SetSeat, ["seat"] = seat };
            if (role.HasValue)
            {
                message["role"] = RoleWire(role.Value);
            }
            if (fill.HasValue)
            {
                message["fill"] = fill.Value == SeatFill.Bot ? "bot" : "human";
            }
            if (investigatorId != null)
            {
                message["investigatorId"] = investigatorId;
            }
            Send(message);
        }

        public void RemoveSeat(int seat) =>
            Send(new JObject { ["type"] = MessageType.RemoveSeat, ["seat"] = seat });

        public void Configure(string scenarioId = null, string adversaryId = null,
            Dictionary<string, string> startSpaces = null, List<string> medicalItemSpaces = null,
            bool? useMiniExpansionCards = null)
        {
            var message = new JObject { ["type"] = MessageType.Configure };
            if (scenarioId != null)
            {
                message["scenarioId"] = scenarioId;
            }
            if (adversaryId != null)
            {
                message["adversaryId"] = adversaryId;
            }
            if (startSpaces != null)
            {
                message["startSpaces"] = JObject.FromObject(startSpaces);
            }
            if (medicalItemSpaces != null)
            {
                message["medicalItemSpaces"] = new JArray(medicalItemSpaces);
            }
            if (useMiniExpansionCards.HasValue)
            {
                message["useMiniExpansionCards"] = useMiniExpansionCards.Value;
            }
            Send(message);
        }

        public void SetSpeed(string speed) => Send(new { type = MessageType.SetSpeed, speed });

        public void SetReady(bool ready) =>
            Send(new JObject { ["type"] = MessageType.Ready, ["ready"] = ready });

        public void StartGame() => Send(new { type = MessageType.StartGame });

        public void Resync() => Send(new { type = MessageType.Resync });

        /// <summary>
        /// Post one game action. Legality is the engine's business: an illegal command comes
        /// back as an <c>error</c> carrying the engine's own message, which the log shows.
        /// </summary>
        public void Submit(GameCommand command) => Send(new JObject
        {
            ["type"] = MessageType.Command,
            ["command"] = WireCodec.EncodeCommand(command),
        });

        private static string RoleWire(SeatRole role) =>
            role == SeatRole.Adversary ? "adversary" : "investigator";

        /// <summary>
        /// Queue a lobby verb until <c>welcome</c> lands. A seat created before the greeting
        /// has no player identity, so it would never show up in My Games or reclaim itself.
        /// </summary>
        private void AfterWelcome(Action action)
        {
            if (Greeted)
            {
                action();
                return;
            }
            _afterWelcome.Add(action);
        }

        // -------------------------------------------------------------- pump

        /// <summary>Drain the inbox on the caller's thread. Call once a frame.</summary>
        public void Pump()
        {
            while (_inbox.TryDequeue(out var message))
            {
                Handle(message);
            }
        }

        private void Handle(JObject message)
        {
            switch ((string)message["type"])
            {
                case MessageType.Welcome:
                {
                    PlayerId = (string)message["playerId"] ?? "";
                    GamesList = ParseGames(message);
                    Greeted = true;
                    var queued = _afterWelcome.ToList();
                    _afterWelcome.Clear();
                    foreach (var action in queued)
                    {
                        action();
                    }
                    Revision++;
                    break;
                }
                case MessageType.Games:
                    GamesList = ParseGames(message);
                    Revision++;
                    break;
                case MessageType.Room:
                {
                    var room = message.ToObject<RoomMessage>(_serializer);
                    Room.Code = room.Code;
                    Room.YourSeat = room.YourSeat;
                    // Only messages addressed to this seat carry its token; never blank a
                    // token we already hold on a later snapshot that omits it.
                    if (!string.IsNullOrEmpty(room.Token))
                    {
                        Room.Token = room.Token;
                    }
                    Room.Started = room.Started;
                    Room.Speed = room.Speed;
                    Room.Setup = room.Setup ?? new SetupInfo();
                    Room.Seats = room.Seats ?? new List<SeatInfo>();
                    Revision++;
                    RoomChanged?.Invoke();
                    break;
                }
                case MessageType.Update:
                {
                    bool resync = (bool?)message["resync"] ?? false;
                    if (message["view"] is JObject viewJson)
                    {
                        View = viewJson.ToObject<PlayerView>(_serializer);
                    }
                    ActingSeats = message["actingSeats"] is JArray seats
                        ? seats.Select(t => (int)t).ToList()
                        : new List<int>();
                    YourTurn = (bool?)message["yourTurn"] ?? false;
                    // A resync carries the whole log, not a delta.
                    if (resync)
                    {
                        _log.Clear();
                    }
                    if (message["events"] is JArray events)
                    {
                        foreach (var entry in events.OfType<JObject>())
                        {
                            _log.Add(new PlayerView.LogEntry
                            {
                                Round = (int?)entry["round"] ?? 0,
                                Type = (string)entry["type"] ?? "",
                                Detail = (string)entry["detail"] ?? "",
                            });
                        }
                    }
                    Revision++;
                    GameUpdated?.Invoke();
                    break;
                }
                case MessageType.TurnAlert:
                {
                    string code = (string)message["code"] ?? "";
                    if (code.Length > 0)
                    {
                        TurnAlert?.Invoke(code);
                    }
                    break;
                }
                case MessageType.RoomClosed:
                {
                    string code = (string)message["code"] ?? "";
                    GamesList.RemoveAll(g => g.Code == code);
                    Revision++;
                    RoomClosed?.Invoke(code, (string)message["by"] ?? "");
                    break;
                }
                case MessageType.Error:
                {
                    string text = (string)message["message"] ?? "unknown error";
                    _log.Add(new PlayerView.LogEntry
                    {
                        Round = View?.Round ?? 0,
                        Type = "error",
                        Detail = text,
                    });
                    Revision++;
                    ErrorReceived?.Invoke(text);
                    break;
                }
            }
        }

        private List<GameSummary> ParseGames(JObject message)
        {
            var games = new List<GameSummary>();
            if (message["gamesList"] is JArray array)
            {
                foreach (var entry in array.OfType<JObject>())
                {
                    var summary = entry.ToObject<GameSummary>(_serializer);
                    if (summary != null)
                    {
                        games.Add(summary);
                    }
                }
            }
            return games;
        }

        private void Send(object message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                message is JObject json
                    ? json.ToString(Formatting.None)
                    : JsonConvert.SerializeObject(message, WireCodec.Settings));
            _ = SendPayloadAsync(payload);
        }

        private async Task SendPayloadAsync(byte[] payload)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text, true, _cts.Token);
                }
            }
            catch (Exception e)
            {
                ConnectionError = e.Message;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _socket.Dispose();
            }
            catch
            {
                // already gone
            }
        }
    }
}
