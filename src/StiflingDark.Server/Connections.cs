using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using StiflingDark.Protocol;

namespace StiflingDark.Server;

/// <summary>
/// One client socket plus THE send lock for it. Rooms, session replies, and turn alerts can
/// all write to the same socket from different tasks; concurrent WebSocket sends throw, so
/// every write anywhere goes through this single gate.
/// </summary>
public sealed class Connection
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Connection(WebSocket socket)
    {
        _socket = socket;
    }

    public bool Open => _socket.State == WebSocketState.Open;

    public async Task SendAsync(object message)
    {
        if (!Open)
        {
            return;
        }
        byte[] payload = Encoding.UTF8.GetBytes(
            JsonConvert.SerializeObject(message, WireCodec.Settings));
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true,
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Connection died mid-send; the receive loop will clean up.
        }
        catch (ObjectDisposedException)
        {
            // Socket torn down under us — same story.
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

/// <summary>
/// Live identified connections, for cross-game turn alerts: a player at one table (or idling
/// on My Games) hears when a DIFFERENT game starts waiting on them.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Connection>> _byPlayer = new();

    public void Register(string playerId, Connection connection)
    {
        lock (_sync)
        {
            if (!_byPlayer.TryGetValue(playerId, out var list))
            {
                _byPlayer[playerId] = list = new List<Connection>();
            }
            if (!list.Contains(connection))
            {
                list.Add(connection);
            }
        }
    }

    public void Unregister(Connection connection)
    {
        lock (_sync)
        {
            foreach (var list in _byPlayer.Values)
            {
                list.Remove(connection);
            }
        }
    }

    /// <summary>Ping every live connection this player has; prune the dead ones.</summary>
    public void NotifyTurn(string playerId, string roomCode)
    {
        Connection[] targets;
        lock (_sync)
        {
            if (!_byPlayer.TryGetValue(playerId, out var list))
            {
                return;
            }
            list.RemoveAll(c => !c.Open);
            targets = list.ToArray();
        }
        foreach (var connection in targets)
        {
            _ = connection.SendAsync(new TurnAlertMessage { Code = roomCode });
        }
    }
}
