using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using StiflingDark.Engine.Data;
using StiflingDark.Protocol;

namespace StiflingDark.Server;

/// <summary>Finds game-data/ via env var (containers) or by walking up (dev/tests).</summary>
public static class GameDataLocator
{
    public static GameDatabase LoadDatabase()
    {
        string? configured = Environment.GetEnvironmentVariable("GAME_DATA_DIR");
        if (!string.IsNullOrEmpty(configured))
        {
            return GameDatabase.Load(configured);
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "game-data");
            if (Directory.Exists(candidate))
            {
                return GameDatabase.Load(candidate);
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "game-data not found; set GAME_DATA_DIR or run from within the repo.");
    }
}

/// <summary>One connected socket: routes messages to a room until the socket closes.</summary>
public static class ClientSession
{
    private const int MaxMessageBytes = 256 * 1024;
    /// <summary>Per-identity cap on unfinished games — create-room spam guard.</summary>
    private const int MaxActiveGamesPerPlayer = 5;
    /// <summary>Identity churn guard: hello floods would mint registry records forever.</summary>
    private const int MaxHellosPerConnection = 5;

    /// <summary>Global room ceiling (MAX_ROOMS env) — the anonymous-spam backstop: identity
    /// caps do not bind clients that never say hello, this does.</summary>
    private static int MaxRooms =>
        int.TryParse(Environment.GetEnvironmentVariable("MAX_ROOMS"), out int r) ? r : 2000;

    /// <summary>Sustained messages/second per socket (RATE_LIMIT_PER_SEC; 0 disables — tests
    /// drive whole games through a single socket). Read per connection, not at type load, so
    /// test env setup can never race the static initializer.</summary>
    private static double RatePerSecond =>
        double.TryParse(Environment.GetEnvironmentVariable("RATE_LIMIT_PER_SEC"), out double r)
            ? r
            : 10;

    public static async Task RunAsync(WebSocket socket, RoomManager rooms,
        PlayerRegistry players, ConnectionRegistry connections)
    {
        var connection = new Connection(socket);
        Room? room = null;
        PlayerRegistry.Player? identity = null;
        int hellos = 0;
        // Token bucket: bursts are fine, a firehose is not.
        double rate = RatePerSecond;
        double burst = rate * 3;
        double tokens = burst;
        DateTime lastRefill = DateTime.UtcNow;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                string? text = await ReceiveTextAsync(socket);
                if (text == null)
                {
                    break;
                }

                if (rate > 0)
                {
                    var now = DateTime.UtcNow;
                    tokens = Math.Min(burst, tokens + (now - lastRefill).TotalSeconds * rate);
                    lastRefill = now;
                    if (tokens < 1)
                    {
                        await SendErrorAsync(connection, "Too many messages — slow down.");
                        continue;
                    }
                    tokens -= 1;
                }

                JObject message;
                try
                {
                    message = JObject.Parse(text);
                }
                catch
                {
                    await SendErrorAsync(connection, "Malformed JSON.");
                    continue;
                }

                switch ((string?)message["type"])
                {
                    case MessageType.Hello:
                    {
                        if (++hellos > MaxHellosPerConnection)
                        {
                            await SendErrorAsync(connection, "Too many identity changes.");
                            break;
                        }
                        identity = players.Identify(
                            (string?)message["playerKey"], (string?)message["name"]);
                        if (identity == null)
                        {
                            await SendErrorAsync(connection, "Invalid player key.");
                            break;
                        }
                        // Re-hello with a different key moves this connection's alerts.
                        connections.Unregister(connection);
                        connections.Register(identity.PlayerId, connection);
                        await connection.SendAsync(new WelcomeMessage
                        {
                            PlayerId = identity.PlayerId,
                            Name = identity.Name,
                            GamesList = rooms.GamesFor(identity.PlayerId),
                        });
                        break;
                    }
                    case MessageType.ListGames when identity != null:
                    {
                        await connection.SendAsync(new GamesMessage
                        {
                            GamesList = rooms.GamesFor(identity.PlayerId),
                        });
                        break;
                    }
                    case MessageType.CreateRoom:
                    {
                        if (identity != null &&
                            rooms.ActiveGameCount(identity.PlayerId) >= MaxActiveGamesPerPlayer)
                        {
                            await SendErrorAsync(connection,
                                "You have too many games going — finish or abandon some first.");
                            break;
                        }
                        if (rooms.RoomCount >= MaxRooms)
                        {
                            await SendErrorAsync(connection,
                                "The server is full right now — try again later.");
                            break;
                        }
                        var created = rooms.Create();
                        created.Join((string?)message["name"] ?? "", null, identity?.PlayerId,
                            connection, ParseRole(message["role"]), out string createError);
                        if (createError.Length > 0)
                        {
                            await SendErrorAsync(connection, createError);
                            break;
                        }
                        room = created;
                        break;
                    }
                    case MessageType.JoinRoom:
                    {
                        var found = rooms.Find((string?)message["code"] ?? "");
                        if (found == null)
                        {
                            await SendErrorAsync(connection, "No room with that code.");
                            break;
                        }
                        var seat = found.Join((string?)message["name"] ?? "",
                            (string?)message["token"], identity?.PlayerId, connection,
                            ParseRole(message["role"]), out string joinError);
                        if (seat == null)
                        {
                            await SendErrorAsync(connection, joinError);
                            break;
                        }
                        room = found;
                        break;
                    }
                    case MessageType.LeaveRoom when room != null:
                    {
                        await ReplyIfRefusedAsync(connection, room.Leave(connection));
                        room = null;
                        break;
                    }
                    case MessageType.Ready when room != null:
                    {
                        room.SetReady(room.SeatIndexOf(connection), (bool?)message["ready"] ?? true);
                        break;
                    }
                    case MessageType.AddBot when room != null:
                    {
                        await ReplyIfRefusedAsync(connection, room.AddBot(
                            room.SeatIndexOf(connection),
                            ParseRole(message["role"]) ?? SeatRole.Investigator,
                            (string?)message["investigatorId"]));
                        break;
                    }
                    case MessageType.SetSeat when room != null:
                    {
                        await ReplyIfRefusedAsync(connection, room.SetSeat(
                            room.SeatIndexOf(connection),
                            (int?)message["seat"] ?? -1,
                            ParseRole(message["role"]),
                            ParseFill(message["fill"]),
                            (string?)message["investigatorId"]));
                        break;
                    }
                    case MessageType.RemoveSeat when room != null:
                    {
                        await ReplyIfRefusedAsync(connection, room.RemoveSeat(
                            room.SeatIndexOf(connection), (int?)message["seat"] ?? -1));
                        break;
                    }
                    case MessageType.Configure when room != null:
                    {
                        await ReplyIfRefusedAsync(connection, room.Configure(
                            room.SeatIndexOf(connection),
                            (string?)message["scenarioId"],
                            (string?)message["adversaryId"],
                            message["startSpaces"]?.ToObject<Dictionary<string, string>>(),
                            message["medicalItemSpaces"]?.ToObject<List<string>>(),
                            (bool?)message["useMiniExpansionCards"]));
                        break;
                    }
                    case MessageType.SetSpeed when room != null:
                    {
                        await ReplyIfRefusedAsync(connection,
                            room.SetSpeed(room.SeatIndexOf(connection), (string?)message["speed"]));
                        break;
                    }
                    case MessageType.StartGame when room != null:
                    {
                        await ReplyIfRefusedAsync(connection,
                            room.Start(room.SeatIndexOf(connection)));
                        break;
                    }
                    case MessageType.Command when room != null:
                    {
                        if (message["command"] is JObject commandJson)
                        {
                            await room.HandleCommandAsync(
                                room.SeatIndexOf(connection), commandJson);
                        }
                        else
                        {
                            await SendErrorAsync(connection, "A command message needs a command.");
                        }
                        break;
                    }
                    case MessageType.Resync when room != null:
                    {
                        await room.ResyncAsync(room.SeatIndexOf(connection));
                        break;
                    }
                    default:
                        await SendErrorAsync(connection, "Unknown or out-of-order message.");
                        break;
                }
            }
        }
        catch (WebSocketException)
        {
            // Client vanished; fall through to detach.
        }
        finally
        {
            room?.Detach(connection);
            connections.Unregister(connection);
        }
    }

    private static SeatRole? ParseRole(JToken? token) =>
        Enum.TryParse((string?)token, ignoreCase: true, out SeatRole role) ? role : (SeatRole?)null;

    private static SeatFill? ParseFill(JToken? token) =>
        Enum.TryParse((string?)token, ignoreCase: true, out SeatFill fill) ? fill : (SeatFill?)null;

    private static Task ReplyIfRefusedAsync(Connection connection, string error) =>
        error.Length > 0 ? SendErrorAsync(connection, error) : Task.CompletedTask;

    private static async Task<string?> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[8 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxMessageBytes)
            {
                return null;
            }
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static Task SendErrorAsync(Connection connection, string message) =>
        connection.SendAsync(new ErrorMessage { Message = message });
}
