using Microsoft.AspNetCore.Mvc.Testing;
using Newtonsoft.Json.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Server.Tests;

/// <summary>
/// End-to-end games over real WebSockets: rooms, hybrid human/bot seats, per-seat views, and
/// reconnection. Every assertion here is made against what a client actually received, so a
/// redaction that leaks would be caught in the bytes rather than in a unit test's imagination.
/// </summary>
public class ServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    static ServerTests()
    {
        // Instant bots, no throttling, and an isolated data dir for the shared factory.
        Environment.SetEnvironmentVariable("BOT_DELAY_MS", "0");
        Environment.SetEnvironmentVariable("RATE_LIMIT_PER_SEC", "0");
        Environment.SetEnvironmentVariable("DATA_DIR",
            Path.Combine(Path.GetTempPath(), "sd-tests-" + Guid.NewGuid().ToString("N")));
    }

    public ServerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static readonly Lazy<GameDatabase> Database = new(GameDataLocator.LoadDatabase);

    // ----------------------------------------------------------- room lifecycle

    [Fact]
    public async Task A_new_room_seats_its_creator_and_reports_the_available_setup()
    {
        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "create_room", name = "Host" });
        var room = await host.NextOfTypeAsync("room");

        Assert.Equal(5, ((string)room["code"]!).Length);
        Assert.False((bool)room["started"]!);
        Assert.Equal(0, (int)room["yourSeat"]!);
        var seat = room["seats"]![0]!;
        Assert.Equal("investigator", (string)seat["role"]!);
        Assert.Equal("human", (string)seat["fill"]!);
        Assert.False(string.IsNullOrEmpty((string)seat["investigatorId"]!));

        var setup = room["setup"]!;
        Assert.Contains("sawmill", setup["availableScenarios"]!.Select(s => (string)s!));
        Assert.Contains("butcher", setup["availableAdversaries"]!.Select(s => (string)s!));
    }

    [Fact]
    public async Task Joining_a_missing_room_fails()
    {
        await using var client = await TestClient.ConnectAsync(_factory);
        await client.SendAsync(new { type = "join_room", code = "ZZZZZ", name = "Lost" });
        var error = await client.NextOfTypeAsync("error");
        Assert.Contains("No room", (string)error["message"]!);
    }

    [Fact]
    public async Task A_table_holds_exactly_one_adversary_and_at_most_four_investigators()
    {
        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "create_room", name = "Host", role = "adversary" });
        var room = await host.NextOfTypeAsync("room");
        string code = (string)room["code"]!;
        Assert.Equal("adversary", (string)room["seats"]![0]!["role"]!);

        // A second Adversary is refused; the same client may still take an Investigator seat.
        await using var second = await TestClient.ConnectAsync(_factory);
        await second.SendAsync(new { type = "join_room", code, name = "Rival", role = "adversary" });
        var refused = await second.NextOfTypeAsync("error");
        Assert.Contains("already has an Adversary", (string)refused["message"]!);

        for (int i = 0; i < 4; i++)
        {
            await host.SendAsync(new { type = "add_bot", role = "investigator" });
            await host.NextOfTypeAsync("room");
        }
        await host.SendAsync(new { type = "add_bot", role = "investigator" });
        var full = await host.NextOfTypeAsync("error");
        Assert.Contains("full", (string)full["message"]!);
    }

    [Fact]
    public async Task Only_the_host_may_reshape_the_table()
    {
        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "create_room", name = "Host" });
        string code = (string)(await host.NextOfTypeAsync("room"))["code"]!;

        await using var guest = await TestClient.ConnectAsync(_factory);
        await guest.SendAsync(new { type = "join_room", code, name = "Guest" });
        await guest.NextOfTypeAsync("room");

        await guest.SendAsync(new { type = "add_bot", role = "adversary" });
        var refused = await guest.NextOfTypeAsync("error");
        Assert.Contains("Only the host", (string)refused["message"]!);

        // Two Investigator seats cannot play the same Investigator.
        string taken = (string)(await Room(host, code))["seats"]![1]!["investigatorId"]!;
        await host.SendAsync(new { type = "set_seat", seat = 0, investigatorId = taken });
        var clash = await host.NextOfTypeAsync("error");
        Assert.Contains("already taken", (string)clash["message"]!);
    }

    [Fact]
    public async Task Starting_needs_two_investigators_an_adversary_and_everyone_ready()
    {
        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "create_room", name = "Host" });
        await host.NextOfTypeAsync("room");

        await host.SendAsync(new { type = "start_game" });
        Assert.Contains("Investigators", (string)(await host.NextOfTypeAsync("error"))["message"]!);

        await host.SendAsync(new { type = "add_bot", role = "investigator" });
        await host.NextOfTypeAsync("room");
        await host.SendAsync(new { type = "start_game" });
        Assert.Contains("Adversary", (string)(await host.NextOfTypeAsync("error"))["message"]!);

        await host.SendAsync(new { type = "add_bot", role = "adversary" });
        await host.NextOfTypeAsync("room");
        await host.SendAsync(new { type = "start_game" });

        // The bot Adversary runs its own secret setup, so the game reaches round 1 unaided.
        var update = await host.NextAsync(m => (string?)m["type"] == "update" &&
            (int?)m["view"]?["round"] >= 1);
        Assert.Equal("investigatorTurns", (string)update["view"]!["phase"]!);
    }

    [Fact]
    public async Task Configure_switches_the_scenario_and_rejects_unknown_ones()
    {
        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "create_room", name = "Host" });
        await host.NextOfTypeAsync("room");

        await host.SendAsync(new { type = "configure", scenarioId = "amusement-park",
            adversaryId = "cult-of-hunlow" });
        var room = await host.NextOfTypeAsync("room");
        Assert.Equal("amusement-park", (string)room["setup"]!["scenarioId"]!);
        Assert.Equal("cult-of-hunlow", (string)room["setup"]!["adversaryId"]!);

        await host.SendAsync(new { type = "configure", scenarioId = "atlantis" });
        Assert.Contains("not a scenario", (string)(await host.NextOfTypeAsync("error"))["message"]!);
    }

    [Fact]
    public async Task Bot_pacing_is_set_and_broadcast()
    {
        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "create_room", name = "Host" });
        Assert.Equal("medium", (string)(await host.NextOfTypeAsync("room"))["speed"]!);

        await host.SendAsync(new { type = "set_speed", speed = "fast" });
        Assert.Equal("fast", (string)(await host.NextOfTypeAsync("room"))["speed"]!);

        await host.SendAsync(new { type = "set_speed", speed = "glacial" });
        Assert.Contains("slow, medium, or fast",
            (string)(await host.NextOfTypeAsync("error"))["message"]!);
    }

    // ----------------------------------------------------------- hybrid seats

    [Fact]
    public async Task One_human_plays_several_rounds_alongside_three_bots()
    {
        await using var human = await TestClient.ConnectAsync(_factory);
        await human.SendAsync(new { type = "create_room", name = "Solo" });
        var room = await human.NextOfTypeAsync("room");
        string myInvestigator = (string)room["seats"]![0]!["investigatorId"]!;

        // Two bot Investigators beside the human, and a bot Adversary opposite.
        for (int i = 0; i < 2; i++)
        {
            await human.SendAsync(new { type = "add_bot", role = "investigator" });
            await human.NextOfTypeAsync("room");
        }
        await human.SendAsync(new { type = "add_bot", role = "adversary" });
        var seated = await human.NextOfTypeAsync("room");
        Assert.Equal(4, ((JArray)seated["seats"]!).Count);
        Assert.Equal(3, ((JArray)seated["seats"]!).Count(s => (string?)s["fill"] == "bot"));

        await human.SendAsync(new { type = "start_game" });
        var (final, turnsPlayed) = await PlayUntilAsync(human, myInvestigator, targetRound: 4);

        // The table really moved: several rounds went past, the human's own Investigator took
        // a turn in most of them, and the bot seats drove everything in between.
        Assert.True((int)final["view"]!["round"]! >= 4 ||
                    (string?)final["view"]!["phase"] == "gameOver");
        Assert.True(turnsPlayed >= 2, $"the human seat only completed {turnsPlayed} turn(s)");
        // One Event card per round, and the bot Adversary's turns closed each of them.
        var log = final["view"]!["log"]!;
        Assert.True(log.Count(e => (string?)e["type"] == "event") >= 3);
    }

    /// <summary>
    /// Play the human seat the laziest legal way — begin the turn, end it — until the round
    /// tracker reaches <paramref name="targetRound"/> or the game finishes. Returns the last
    /// update seen.
    /// </summary>
    private static async Task<(JObject Last, int Turns)> PlayUntilAsync(TestClient client,
        string investigatorId, int targetRound)
    {
        JObject? last = null;
        int turnsPlayed = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            var message = await client.NextAsync(
                m => (string?)m["type"] is "update" or "error", timeoutSeconds: 60);
            if ((string?)message["type"] == "error")
            {
                // A stale command (the bots moved first) is normal; the next update re-syncs us.
                continue;
            }
            last = message;
            var view = message["view"]!;
            string? phase = (string?)view["phase"];
            if (phase == "gameOver" || (int)view["round"]! >= targetRound)
            {
                return (message, turnsPlayed);
            }
            if (!(bool)message["yourTurn"]!)
            {
                continue;
            }
            var mine = view["investigators"]!.First(i => (string?)i["defId"] == investigatorId);
            if ((bool)mine["dead"]! && (string?)mine["spiritId"] == null)
            {
                continue; // out of the game; ride along until the round target
            }
            if ((bool)view["pendingWindowChoice"]!)
            {
                await client.CommandAsync("ResolveWindowCommand", new { stopAndLoseStamina = true });
                continue;
            }
            if ((string?)view["activeInvestigator"] == investigatorId)
            {
                await client.CommandAsync("EndTurnCommand");
                turnsPlayed++;
            }
            else if (view["activeInvestigator"] == null || view["activeInvestigator"]!.Type == JTokenType.Null)
            {
                await client.CommandAsync("BeginInvestigatorTurnCommand", new { investigatorId });
            }
        }
        Assert.Fail("The table stopped moving before it reached the target round.");
        return (last!, turnsPlayed);
    }

    [Fact]
    public async Task A_seat_cannot_send_the_other_sides_commands()
    {
        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "create_room", name = "Host" });
        await host.NextOfTypeAsync("room");
        await host.SendAsync(new { type = "add_bot", role = "investigator" });
        await host.NextOfTypeAsync("room");
        await host.SendAsync(new { type = "add_bot", role = "adversary" });
        await host.NextOfTypeAsync("room");
        await host.SendAsync(new { type = "start_game" });
        await host.NextAsync(m => (string?)m["type"] == "update" && (int?)m["view"]?["round"] >= 1);

        await host.CommandAsync("AdversaryEndTurnCommand");
        var refused = await host.NextOfTypeAsync("error");
        Assert.Contains("Adversary command", (string)refused["message"]!);

        await host.CommandAsync("NoSuchCommand");
        var unknown = await host.NextOfTypeAsync("error");
        Assert.Contains("Bad command", (string)unknown["message"]!);
    }

    // ------------------------------------------------- redaction on the wire

    [Fact]
    public async Task An_investigator_client_is_never_sent_the_hidden_adversarys_position()
    {
        var db = Database.Value;
        await using var adversary = await TestClient.ConnectAsync(_factory);
        await adversary.SendAsync(new { type = "create_room", name = "Monster", role = "adversary" });
        var room = await adversary.NextOfTypeAsync("room");
        string code = (string)room["code"]!;

        await using var investigator = await TestClient.ConnectAsync(_factory);
        await investigator.SendAsync(new { type = "join_room", code, name = "Scout" });
        await investigator.NextOfTypeAsync("room");
        await investigator.SendAsync(new { type = "ready", ready = true });
        await adversary.NextAsync(m => (string?)m["type"] == "room" &&
            m["seats"]!.Any(s => (bool)s["ready"]! && (string?)s["role"] == "investigator"));
        await adversary.SendAsync(new { type = "add_bot", role = "investigator" });
        await adversary.NextOfTypeAsync("room");
        await adversary.SendAsync(new { type = "start_game" });
        await adversary.NextAsync(m => (string?)m["type"] == "update" &&
            (string?)m["view"]?["phase"] == "adversarySetup");

        // The Adversary plays their own secret setup through the protocol. A far corner of
        // the Sawmill, chosen so nothing else on the board can name that space.
        var map = db.Map("sawmill");
        var graph = new MapGraph(map);
        const string lair = "S-25";
        foreach (string zone in map.Zones.Keys)
        {
            await adversary.CommandAsync("PlaceHiddenEvidenceCommand", new
            {
                zone,
                spaceId = graph.ZoneSpaces(zone).First(s => s.Kind == SpaceKind.Normal && s.Id != lair).Id,
            });
        }
        bool cursedPlaced = false;
        foreach (var poi in map.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
        {
            string target = graph.DistancesFrom(poi.Id, 2, new BoardOverlay()).Keys
                .First(id => graph.Space(id).Kind == SpaceKind.Normal && id != lair);
            await adversary.CommandAsync("PlacePoiTokenCommand", new
            {
                poiSpace = poi.Id,
                tokenSpace = target,
                cursedFront = !cursedPlaced,
            });
            cursedPlaced = true;
        }
        await adversary.CommandAsync("PlaceAdversaryCommand", new { spaceId = lair });
        await adversary.CommandAsync("SetupAdversaryCardsCommand", new
        {
            attackCardId = "rend",
            abilityCardIds = new[] { "decay" },
        });
        await adversary.CommandAsync("FinishAdversarySetupCommand");

        var own = await adversary.NextAsync(m => (string?)m["type"] == "update" &&
            (int?)m["view"]?["round"] >= 1);
        // The Adversary's own view holds the truth this test then hunts for.
        Assert.Equal(lair, (string)own["view"]!["adversary"]!["space"]!);
        Assert.False((bool)own["view"]!["adversary"]!["revealed"]!);

        var seen = await investigator.NextAsync(m => (string?)m["type"] == "update" &&
            (int?)m["view"]?["round"] >= 1);
        var hidden = seen["view"]!["adversary"]!;
        // Absent, not blanked: there is no "space" key at all to read.
        Assert.Null(hidden["space"]);
        Assert.False((bool)hidden["revealed"]!);
        // Nor the loadout: the Adversary has played nothing yet.
        Assert.Null(hidden["attackCard"]);
        Assert.Empty((JArray)hidden["activeAbilities"]!);
        Assert.Equal(1, (int)hidden["activeAbilityCount"]!);
        // Nor the hidden Evidence, which the Adversary just placed one token of per zone.
        Assert.Empty((JArray)seen["view"]!["evidence"]!);
        Assert.NotEmpty((JArray)own["view"]!["evidence"]!);

        // The strongest form of the claim: the string never crossed the wire at all.
        List<string> raw;
        lock (investigator.RawInbound)
        {
            raw = investigator.RawInbound.ToList();
        }
        Assert.NotEmpty(raw);
        Assert.DoesNotContain(raw, line => line.Contains($"\"{lair}\""));
        Assert.Contains(adversary.RawInbound, line => line.Contains($"\"{lair}\""));
    }

    // ------------------------------------------------------------- reconnect

    [Fact]
    public async Task Reconnecting_with_a_token_reclaims_the_seat_and_resyncs()
    {
        var creator = await TestClient.ConnectAsync(_factory);
        await creator.SendAsync(new { type = "create_room", name = "Flaky" });
        var room = await creator.NextOfTypeAsync("room");
        string code = (string)room["code"]!;
        string token = (string)room["token"]!;
        string myInvestigator = (string)room["seats"]![0]!["investigatorId"]!;
        Assert.False(string.IsNullOrEmpty(token));

        await creator.SendAsync(new { type = "add_bot", role = "investigator" });
        await creator.NextOfTypeAsync("room");
        await creator.SendAsync(new { type = "add_bot", role = "adversary" });
        await creator.NextOfTypeAsync("room");
        await creator.SendAsync(new { type = "start_game" });
        await creator.NextAsync(m => (string?)m["type"] == "update" && (int?)m["view"]?["round"] >= 1);

        // Drop mid-game and come back with the token.
        await creator.DisposeAsync();
        await using var back = await TestClient.ConnectAsync(_factory);
        await back.SendAsync(new { type = "join_room", code, name = "ignored", token });
        var rejoin = await back.NextOfTypeAsync("room");
        Assert.Equal(0, (int)rejoin["yourSeat"]!);
        Assert.True((bool)rejoin["started"]!);

        // Rejoining a running game re-sends the whole view, flagged as a resync, with the
        // complete log rather than the tail this client happened to miss.
        var resync = await back.NextAsync(m => (string?)m["type"] == "update");
        Assert.True((bool)resync["resync"]!);
        var events = (JArray)resync["events"]!;
        var log = (JArray)resync["view"]!["log"]!;
        Assert.NotEmpty(log);
        Assert.Equal(log.Count, events.Count);
        Assert.Contains(resync["view"]!["investigators"]!,
            i => (string?)i["defId"] == myInvestigator);

        // An explicit resync repeats the trick on demand.
        await back.SendAsync(new { type = "resync" });
        var again = await back.NextAsync(m => (string?)m["type"] == "update" && (bool)m["resync"]!);
        Assert.Equal(log.Count, ((JArray)again["events"]!).Count);
    }

    [Fact]
    public async Task Identity_reclaims_a_seat_mid_game_without_a_token()
    {
        string key = "sd-reclaim-" + Guid.NewGuid().ToString("N");

        var creator = await TestClient.ConnectAsync(_factory);
        await creator.SendAsync(new { type = "hello", playerKey = key, name = "Host" });
        await creator.NextOfTypeAsync("welcome");
        await creator.SendAsync(new { type = "create_room", name = "Host" });
        string code = (string)(await creator.NextOfTypeAsync("room"))["code"]!;
        await creator.SendAsync(new { type = "add_bot", role = "investigator" });
        await creator.NextOfTypeAsync("room");
        await creator.SendAsync(new { type = "add_bot", role = "adversary" });
        await creator.NextOfTypeAsync("room");
        await creator.SendAsync(new { type = "start_game" });
        await creator.NextAsync(m => (string?)m["type"] == "update" && (int?)m["view"]?["round"] >= 1);
        await creator.DisposeAsync();

        await using var comeback = await TestClient.ConnectAsync(_factory);
        await comeback.SendAsync(new { type = "hello", playerKey = key, name = "Host" });
        var welcome = await comeback.NextOfTypeAsync("welcome");
        var mine = ((JArray)welcome["gamesList"]!).FirstOrDefault(g => (string?)g["code"] == code);
        Assert.NotNull(mine);
        Assert.True((bool)mine!["started"]!);
        Assert.Equal("investigator", (string)mine["yourRole"]!);
        Assert.False((bool)mine["finished"]!);

        await comeback.SendAsync(new { type = "join_room", code, name = "Host" });
        Assert.Equal(0, (int)(await comeback.NextOfTypeAsync("room"))["yourSeat"]!);
        Assert.NotNull((await comeback.NextOfTypeAsync("update"))["view"]);
    }

    [Fact]
    public async Task Hello_is_durable_across_connections()
    {
        string key = "sd-identity-" + Guid.NewGuid().ToString("N");

        await using var first = await TestClient.ConnectAsync(_factory);
        await first.SendAsync(new { type = "hello", playerKey = key, name = "Matt" });
        var welcome = await first.NextOfTypeAsync("welcome");
        string playerId = (string)welcome["playerId"]!;
        Assert.False(string.IsNullOrEmpty(playerId));

        await using var second = await TestClient.ConnectAsync(_factory);
        await second.SendAsync(new { type = "hello", playerKey = key, name = "Matt R" });
        var again = await second.NextOfTypeAsync("welcome");
        Assert.Equal(playerId, (string)again["playerId"]!);
        Assert.Equal("Matt R", (string)again["name"]!);

        await using var bogus = await TestClient.ConnectAsync(_factory);
        await bogus.SendAsync(new { type = "hello", playerKey = "short", name = "X" });
        Assert.Contains("player key", (string)(await bogus.NextOfTypeAsync("error"))["message"]!);
    }

    [Fact]
    public async Task An_absent_player_gets_a_turn_alert_on_another_connection()
    {
        string hostKey = "sd-alert-host-" + Guid.NewGuid().ToString("N");
        string guestKey = "sd-alert-guest-" + Guid.NewGuid().ToString("N");

        await using var host = await TestClient.ConnectAsync(_factory);
        await host.SendAsync(new { type = "hello", playerKey = hostKey, name = "Host" });
        await host.NextOfTypeAsync("welcome");
        await host.SendAsync(new { type = "create_room", name = "Host", role = "adversary" });
        string code = (string)(await host.NextOfTypeAsync("room"))["code"]!;

        var guest = await TestClient.ConnectAsync(_factory);
        await guest.SendAsync(new { type = "hello", playerKey = guestKey, name = "Guest" });
        await guest.NextOfTypeAsync("welcome");
        await guest.SendAsync(new { type = "join_room", code, name = "Guest" });
        await guest.NextOfTypeAsync("room");
        await guest.SendAsync(new { type = "ready", ready = true });
        await host.NextAsync(m => (string?)m["type"] == "room" &&
            m["seats"]!.Any(s => (bool)s["ready"]! && (string?)s["role"] == "investigator"));
        await host.SendAsync(new { type = "add_bot", role = "investigator" });
        await host.NextOfTypeAsync("room");

        // The guest walks away from the table but keeps the app open elsewhere.
        await guest.DisposeAsync();
        await using var guestPhone = await TestClient.ConnectAsync(_factory);
        await guestPhone.SendAsync(new { type = "hello", playerKey = guestKey, name = "Guest" });
        await guestPhone.NextOfTypeAsync("welcome");

        // Starting puts the Adversary on the clock; once setup is done the Investigators are
        // awaited, and the absent guest gets exactly one nudge naming the room.
        await host.SendAsync(new { type = "start_game" });
        await StartSawmillSetupAsync(host);

        var alert = await guestPhone.NextOfTypeAsync("turn_alert");
        Assert.Equal(code, (string)alert["code"]!);
    }

    // ------------------------------------------------------------- helpers

    private static async Task<JObject> Room(TestClient client, string code)
    {
        await client.SendAsync(new { type = "join_room", code });
        return await client.NextOfTypeAsync("room");
    }

    /// <summary>Drive a human Adversary through the Sawmill's setup phase.</summary>
    private static async Task StartSawmillSetupAsync(TestClient adversary)
    {
        var map = Database.Value.Map("sawmill");
        var graph = new MapGraph(map);
        await adversary.NextAsync(m => (string?)m["type"] == "update" &&
            (string?)m["view"]?["phase"] == "adversarySetup");
        foreach (string zone in map.Zones.Keys)
        {
            await adversary.CommandAsync("PlaceHiddenEvidenceCommand", new
            {
                zone,
                spaceId = graph.ZoneSpaces(zone).First(s => s.Kind == SpaceKind.Normal).Id,
            });
        }
        bool cursedPlaced = false;
        foreach (var poi in map.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
        {
            await adversary.CommandAsync("PlacePoiTokenCommand", new
            {
                poiSpace = poi.Id,
                tokenSpace = graph.DistancesFrom(poi.Id, 2, new BoardOverlay()).Keys
                    .First(id => graph.Space(id).Kind == SpaceKind.Normal),
                cursedFront = !cursedPlaced,
            });
            cursedPlaced = true;
        }
        await adversary.CommandAsync("PlaceAdversaryCommand", new { spaceId = "S-25" });
        await adversary.CommandAsync("SetupAdversaryCardsCommand", new
        {
            attackCardId = "rend",
            abilityCardIds = new[] { "decay" },
        });
        await adversary.CommandAsync("FinishAdversarySetupCommand");
    }
}

/// <summary>Periodic room retirement, without the web host.</summary>
public class RoomSweepTests
{
    private static RoomManager FreshManager()
    {
        var db = GameDataLocator.LoadDatabase();
        string dir = Path.Combine(Path.GetTempPath(), "sd-sweep-" + Guid.NewGuid().ToString("N"));
        return new RoomManager(db, dir, 0);
    }

    [Fact]
    public void Fresh_lobbies_survive_the_sweep()
    {
        var manager = FreshManager();
        var room = manager.Create();
        Assert.Equal(0, manager.Sweep()); // default TTLs: a brand-new lobby stays
        Assert.NotNull(manager.Find(room.Code));
    }

    [Fact]
    public void Expired_empty_lobbies_are_retired_and_refuse_late_joins()
    {
        var manager = FreshManager();
        var room = manager.Create();
        Assert.Equal(1, manager.Sweep(lobbyTtl: TimeSpan.Zero));
        Assert.Null(manager.Find(room.Code));
        // The retire flag closes the race: a join hitting the ghost room errors cleanly
        // instead of taking a seat nobody can ever list or resume.
        var seat = room.Join("Late", null, null, null, null, out string error);
        Assert.Null(seat);
        Assert.Contains("expired", error);
    }

    [Fact]
    public void Corrupt_and_finished_snapshots_are_swept_at_boot()
    {
        var db = GameDataLocator.LoadDatabase();
        string dir = Path.Combine(Path.GetTempPath(), "sd-gc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        string oldGarbage = Path.Combine(dir, "AAAAA.json");
        File.WriteAllText(oldGarbage, "not json\n");
        File.SetLastWriteTimeUtc(oldGarbage, DateTime.UtcNow.AddDays(-10));

        string freshGarbage = Path.Combine(dir, "BBBBB.json");
        File.WriteAllText(freshGarbage, "not json\n");

        _ = new RoomManager(db, dir, 0);

        Assert.False(File.Exists(oldGarbage));  // past grace: deleted
        Assert.True(File.Exists(freshGarbage)); // unreadable but recent: kept for debugging
    }
}
