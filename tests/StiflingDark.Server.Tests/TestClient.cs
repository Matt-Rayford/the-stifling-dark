using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StiflingDark.Server.Tests;

/// <summary>A headless client on a real WebSocket, with an inbox it can wait on.</summary>
public sealed class TestClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Channel<JObject> _inbox = Channel.CreateUnbounded<JObject>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;

    /// <summary>Every message this client has received, in order — for after-the-fact
    /// assertions about what the server did and did not put on the wire.</summary>
    public List<string> RawInbound { get; } = new();

    private TestClient(WebSocket socket)
    {
        _socket = socket;
        _receiveLoop = ReceiveLoopAsync();
    }

    public static async Task<TestClient> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.Server.CreateWebSocketClient();
        var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        return new TestClient(socket);
    }

    public async Task SendAsync(object message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Send one game command by its wire type name.</summary>
    public Task CommandAsync(string type, object? fields = null)
    {
        var command = fields == null ? new JObject() : JObject.FromObject(fields);
        command["$type"] = type;
        return SendAsync(new { type = "command", command });
    }

    /// <summary>Next message matching the predicate; earlier non-matching ones are dropped.</summary>
    public async Task<JObject> NextAsync(Func<JObject, bool> match, int timeoutSeconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (true)
        {
            var message = await _inbox.Reader.ReadAsync(timeout.Token);
            if (match(message))
            {
                return message;
            }
        }
    }

    public Task<JObject> NextOfTypeAsync(string type, int timeoutSeconds = 30) =>
        NextAsync(m => (string?)m["type"] == type, timeoutSeconds);

    /// <summary>Drain whatever has already arrived without waiting for more.</summary>
    public List<JObject> Drain()
    {
        var drained = new List<JObject>();
        while (_inbox.Reader.TryRead(out var message))
        {
            drained.Add(message);
        }
        return drained;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _inbox.Writer.TryComplete();
                        return;
                    }
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string text = Encoding.UTF8.GetString(stream.ToArray());
                lock (RawInbound)
                {
                    RawInbound.Add(text);
                }
                _inbox.Writer.TryWrite(JObject.Parse(text));
            }
        }
        catch
        {
            _inbox.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // CloseOutputAsync, NOT CloseAsync: CloseAsync performs an internal ReceiveAsync
            // (waiting for the peer's close ack) that races our receive loop's outstanding
            // ReceiveAsync — two concurrent receives violate the WebSocket contract and
            // deadlock TestWebSocket's buffer semaphore, wedging the whole test run.
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch
        {
            // already gone
        }
        // Let the receive loop drain the server's close reply before tearing the socket down
        // under it — bounded, so a silent server cannot wedge disposal.
        await Task.WhenAny(_receiveLoop, Task.Delay(2000));
        _cts.Cancel();
        _socket.Dispose();
    }
}
