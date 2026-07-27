using StiflingDark.Server;

var builder = WebApplication.CreateBuilder(args);
// Predictable local port; PORT overrides it (Railway, Fly, Cloud Run all inject one).
// 5226 rather than Lemonade Wars' 5225, so both sibling servers can run side by side.
string port = Environment.GetEnvironmentVariable("PORT") ?? "5226";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();

var db = GameDataLocator.LoadDatabase();
// DATA_DIR: room snapshots (mount a volume here to survive redeploys).
string dataDir = Environment.GetEnvironmentVariable("DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "rooms");
int botDelayMs = int.TryParse(Environment.GetEnvironmentVariable("BOT_DELAY_MS"), out int d)
    ? d
    : 1100;
var connections = new ConnectionRegistry();
var rooms = new RoomManager(db, dataDir, botDelayMs, connections.NotifyTurn);
var players = new PlayerRegistry(dataDir);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", () => "The Stifling Dark server is up.");
app.MapGet("/health", () => Results.Ok(new { ok = true, rooms = rooms.RoomCount }));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await ClientSession.RunAsync(socket, rooms, players, connections);
});

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program
{
}
