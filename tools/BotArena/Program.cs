using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BotArena;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StiflingDark.Engine.Data;

// Self-play bot arena for The Stifling Dark.
//
//   dotnet run -c Release --project tools/BotArena -- [games] [firstSeed] [outputDir]
//
// Each seed randomizes scenario, adversary, adversary loadout, Investigator count and roster,
// starts, medical tokens, hidden Evidence, POI tokens and the Adversary's own placement, then
// plays the game out with heuristic bots on both sides. Results and anomalies are written as
// JSON; a summary table goes to stdout.

// Invariant probes: targeted checks of engine contracts the bots avoid tripping.
//   dotnet run -c Release --project tools/BotArena -- invariants
if (args.Length > 0 && args[0] == "invariants")
{
    Invariants.Execute(LoadDatabase());
    return;
}

// Probe mode: replay one seed and dump its event log.
//   dotnet run -c Release --project tools/BotArena -- probe <seed> [logFilter]
if (args.Length > 1 && (args[0] == "probe" || args[0] == "probe-passive"))
{
    Probe(ulong.Parse(args[1], CultureInfo.InvariantCulture), args.Length > 2 ? args[2] : null,
        args[0] == "probe-passive");
    return;
}

// Harness self-check: the same Investigator bots against an Adversary that only passes, to
// confirm the objective chains can actually be driven to a win.
//   dotnet run -c Release --project tools/BotArena -- selftest [games]
bool passive = args.Length > 0 && args[0] == "selftest";
if (passive)
{
    args = args.Skip(1).ToArray();
}

int games = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 1000;
ulong firstSeed = args.Length > 1 ? ulong.Parse(args[1], CultureInfo.InvariantCulture) : 1UL;
string outputDir = args.Length > 2 ? args[2] : FindRepoRoot() is { } root ? Path.Combine(root, "bot-results") : "bot-results";

var db = LoadDatabase();
var records = new ConcurrentBag<GameRecord>();
var anomalies = new ConcurrentBag<AnomalyRecord>();
int finished = 0;
var clock = Stopwatch.StartNew();

Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, index =>
{
    ulong seed = firstSeed + (ulong)index;
    var run = new GameRun(db, seed, passiveAdversary: passive);
    GameRecord record;
    try
    {
        record = run.Play();
    }
    catch (Exception e)
    {
        run.Anomaly("harness-crash", $"{e.GetType().Name}: {e.Message}", e);
        record = new GameRecord { Seed = seed, Result = "HarnessCrash" };
    }
    records.Add(record);
    foreach (var anomaly in run.Anomalies)
    {
        anomalies.Add(anomaly);
    }
    int done = Interlocked.Increment(ref finished);
    if (done % 100 == 0)
    {
        Console.WriteLine($"  {done}/{games} games ({clock.Elapsed.TotalSeconds:F0}s)");
    }
});

var all = records.OrderBy(r => r.Seed).ToList();
var allAnomalies = anomalies.OrderBy(a => a.Seed).ThenBy(a => a.Kind, StringComparer.Ordinal).ToList();
// Contracts the bots deliberately never trip get their own targeted probes, recorded alongside.
allAnomalies.AddRange(Invariants.Collect(db));

var json = new JsonSerializerSettings
{
    ContractResolver = new CamelCasePropertyNamesContractResolver(),
    Formatting = Formatting.Indented,
};
Directory.CreateDirectory(outputDir);
File.WriteAllText(Path.Combine(outputDir, "results.json"), JsonConvert.SerializeObject(all, json));
File.WriteAllText(Path.Combine(outputDir, "anomalies.json"), JsonConvert.SerializeObject(allAnomalies, json));

PrintSummary(all, allAnomalies, clock.Elapsed);

static void Probe(ulong seed, string? filter, bool passive)
{
    var db = LoadDatabase();
    var run = new GameRun(db, seed, trace: true, passiveAdversary: passive);
    var record = run.Play();
    Console.WriteLine(JsonConvert.SerializeObject(record, Formatting.Indented));
    foreach (var anomaly in run.Anomalies)
    {
        Console.WriteLine($"!! {anomaly.Kind}: {anomaly.Description}");
    }
    var game = run.Game;
    if (game == null)
    {
        return;
    }
    Console.WriteLine($"-- adversary {game.State.Adversary.DefId} at {game.State.Adversary.Space} " +
                      $"revealed={game.State.Adversary.Revealed} counters=" +
                      string.Join(",", game.State.Adversary.Counters.Select(kv => $"{kv.Key}={kv.Value}")));
    foreach (var inv in game.State.Investigators)
    {
        Console.WriteLine($"-- {inv.DefId} at {inv.Space} wounds={inv.Wounds.Count} stamina={inv.Stamina} " +
                          $"charge={inv.Charge} dead={inv.Dead} escaped={inv.Escaped} " +
                          $"evidence=[{string.Join(",", inv.EvidenceCarried)}] conditions=[{string.Join(",", inv.Conditions)}]");
    }
    Console.WriteLine($"-- objective tokens: {string.Join(", ", game.State.Objective.Tokens.Select(kv => kv.Key + "@" + kv.Value))}");
    Console.WriteLine($"-- carried: {string.Join(", ", game.State.Objective.TokenCarriers.Select(kv => kv.Key + "->" + kv.Value))}");
    foreach (var entry in game.State.Log)
    {
        if (filter == null || entry.Type.Contains(filter) || entry.Detail.Contains(filter))
        {
            Console.WriteLine($"r{entry.Round,2} {entry.Type,-10} {entry.Detail}");
        }
    }
}

static void PrintSummary(List<GameRecord> all, List<AnomalyRecord> anomalies, TimeSpan elapsed)
{
    Console.WriteLine();
    Console.WriteLine($"=== {all.Count} games in {elapsed.TotalSeconds:F0}s ===");
    Console.WriteLine();

    Console.WriteLine("overall");
    foreach (var group in all.GroupBy(r => r.Result).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"  {group.Key,-18} {group.Count(),5}  {(double)group.Count() / all.Count,7:P1}");
    }
    Console.WriteLine($"  avg rounds {all.Average(r => r.Rounds):F1} | avg actions {all.Average(r => r.Actions):F0} " +
                      $"| avg evidence {all.Average(r => r.EvidenceTurnedIn):F2} | avg escaped {all.Average(r => r.Escaped):F2}");
    Console.WriteLine();

    Table("by adversary", all.GroupBy(r => r.Adversary));
    Table("by investigator count", all.GroupBy(r => r.InvestigatorCount + " investigators"));
    Table("by scenario", all.GroupBy(r => r.Scenario));
    Table("by escape card", all.Where(r => r.EscapeCard != null).GroupBy(r => r.EscapeCard!));

    Console.WriteLine("how the Adversary's wins were reached");
    Console.WriteLine($"  {"",-24} {"wins",6} {"by kills",9} {"by round limit",15} {"avg round",10}");
    foreach (var group in all.Where(r => r.Result == "AdversaryWins" && r.Adversary.Length > 0)
        .GroupBy(r => r.Adversary).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        int byKills = group.Count(r => r.Kills >= 1 && r.Rounds < 17);
        Console.WriteLine($"  {group.Key,-24} {group.Count(),6} {byKills,9} {group.Count() - byKills,15} " +
                          $"{group.Average(r => r.Rounds),10:F1}");
    }
    Console.WriteLine();

    var loadouts = all.Where(r => r.Adversary.Length > 0).GroupBy(r => r.Loadout)
        .Where(g => g.Count() >= 10)
        .Select(g => new
        {
            Key = g.Key,
            N = g.Count(),
            AdvWin = g.Count(r => r.Result == "AdversaryWins") / (double)g.Count(),
            InvWin = g.Count(r => r.Result == "InvestigatorsWin") / (double)g.Count(),
            Rounds = g.Average(r => r.Rounds),
            Kills = g.Average(r => (double)r.Kills),
        })
        .ToList();

    Console.WriteLine("top 10 adversary loadouts by Adversary win rate (n >= 10)");
    Console.WriteLine($"  {"loadout",-58} {"n",4} {"advW",7} {"invW",7} {"rounds",7} {"kills",6}");
    foreach (var row in loadouts.OrderByDescending(x => x.AdvWin).ThenBy(x => x.Rounds).Take(10))
    {
        Console.WriteLine($"  {Trim(row.Key, 58),-58} {row.N,4} {row.AdvWin,7:P1} {row.InvWin,7:P1} " +
                          $"{row.Rounds,7:F1} {row.Kills,6:F2}");
    }
    Console.WriteLine();

    // With win rate saturated, speed is the only thing left that separates loadouts.
    Console.WriteLine("top 10 adversary loadouts by kill speed (n >= 10, fewest rounds)");
    Console.WriteLine($"  {"loadout",-58} {"n",4} {"advW",7} {"invW",7} {"rounds",7} {"kills",6}");
    foreach (var row in loadouts.OrderBy(x => x.Rounds).Take(10))
    {
        Console.WriteLine($"  {Trim(row.Key, 58),-58} {row.N,4} {row.AdvWin,7:P1} {row.InvWin,7:P1} " +
                          $"{row.Rounds,7:F1} {row.Kills,6:F2}");
    }
    Console.WriteLine();

    Console.WriteLine($"anomalies: {anomalies.Count} across {anomalies.Select(a => a.Seed).Distinct().Count()} games");
    foreach (var group in anomalies.GroupBy(a => a.Kind).OrderByDescending(g => g.Count()))
    {
        var seeds = group.Select(a => a.Seed).Distinct().Take(5);
        Console.WriteLine($"  {group.Key,-26} {group.Count(),5}  seeds: {string.Join(", ", seeds)}");
        foreach (var sub in group.GroupBy(a => Normalize(a.Description)).OrderByDescending(g => g.Count()).Take(4))
        {
            Console.WriteLine($"      {sub.Count(),4}x {Trim(sub.Key, 110)}");
        }
    }
}

static void Table(string title, IEnumerable<IGrouping<string, GameRecord>> groups)
{
    Console.WriteLine(title);
    Console.WriteLine($"  {"",-24} {"n",5} {"invWin",8} {"advWin",8} {"draw",8} {"other",8} {"rounds",7}");
    foreach (var group in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        int n = group.Count();
        int inv = group.Count(r => r.Result == "InvestigatorsWin");
        int adv = group.Count(r => r.Result == "AdversaryWins");
        int draw = group.Count(r => r.Result == "Draw");
        Console.WriteLine($"  {group.Key,-24} {n,5} {inv / (double)n,8:P1} {adv / (double)n,8:P1} " +
                          $"{draw / (double)n,8:P1} {(n - inv - adv - draw) / (double)n,8:P1} {group.Average(r => r.Rounds),7:F1}");
    }
    Console.WriteLine();
}

/// <summary>Collapse seed/space specifics so anomaly descriptions group together.</summary>
static string Normalize(string description)
{
    var chars = description.Select(c => char.IsDigit(c) ? '#' : c).ToArray();
    string collapsed = new string(chars);
    while (collapsed.Contains("##"))
    {
        collapsed = collapsed.Replace("##", "#");
    }
    return collapsed;
}

static string Trim(string value, int max) => value.Length <= max ? value : value.Substring(0, max - 1) + "…";

static GameDatabase LoadDatabase()
{
    string? root = FindRepoRoot();
    if (root == null)
    {
        throw new DirectoryNotFoundException("game-data/ not found above the arena binary.");
    }
    return GameDatabase.Load(Path.Combine(root, "game-data"));
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "game-data")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return null;
}
