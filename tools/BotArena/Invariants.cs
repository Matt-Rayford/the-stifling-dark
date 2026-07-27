using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace BotArena;

/// <summary>
/// Targeted probes for engine invariants the 1000-game run cannot exercise because the bots
/// deliberately avoid tripping them. Each one checks the documented contract on Game — "illegal
/// actions throw InvalidOperationException and leave the state untouched" — against an action
/// that is refused. Run with: dotnet run --project tools/BotArena -- invariants
/// </summary>
public static class Invariants
{
    private static GameDatabase _db = null!;
    private static List<AnomalyRecord> _found = new();

    public static void Execute(GameDatabase db)
    {
        foreach (var record in Collect(db))
        {
            Console.WriteLine($"  [BUG ] {record.Description}");
        }
    }

    /// <summary>Runs every probe and returns one anomaly record per contract that is violated.</summary>
    public static List<AnomalyRecord> Collect(GameDatabase db)
    {
        _db = db;
        _found = new List<AnomalyRecord>();

        Check("AdversaryDisappear leaves no trace when refused", game =>
        {
            ToAdversaryTurn(game);
            // The Butcher starts Hidden, so this Disappear is illegal.
            string first = Refuse(() => game.AdversaryDisappear());
            game.State.Adversary.Revealed = true;
            string second = Refuse(() => game.AdversaryDisappear());
            return second.Contains("already used")
                ? $"FAIL: the refused call ({first}) still consumed the action — retry said \"{second}\""
                : "ok";
        });

        Check("AdversaryBreakDoor leaves no trace when refused", game =>
        {
            ToAdversaryTurn(game);
            string first = Refuse(() => game.AdversaryBreakDoor(game.State.Adversary.Space));
            string second = Refuse(() => game.AdversaryBreakDoor("S-23"));
            return second.Contains("already used")
                ? $"FAIL: the refused call ({first}) still consumed the action — retry said \"{second}\""
                : "ok";
        });

        Check("ButcherStalk leaves no trace when refused", game =>
        {
            ToAdversaryTurn(game);
            // Park an Investigator far away so the range check refuses the Stalk.
            game.State.Investigators[0].Space = "10";
            game.State.Adversary.Space = "L-1";
            string first = Refuse(() => game.ButcherStalk(new List<string> { game.State.Investigators[0].DefId }));
            game.State.Investigators[0].Space = "L-2";
            string second = Refuse(() => game.ButcherStalk(new List<string> { game.State.Investigators[0].DefId }));
            return second.Contains("already used")
                ? $"FAIL: the refused call ({first}) still consumed the action — retry said \"{second}\""
                : "ok";
        });

        Check("The Butcher may not Stalk or Attack after Disappearing", game =>
        {
            ToAdversaryTurn(game);
            game.State.Adversary.Revealed = true;
            game.State.Adversary.AttackLockedThisTurn = false;
            game.State.Adversary.Counters["stalk"] = 3;
            game.State.Adversary.AttackCard = "eviscerate";
            game.AdversaryDisappear();
            game.State.Investigators[0].Space = NeighbourOf(game, game.State.Adversary.Space);
            string stalk = Refuse(() => game.ButcherStalk(new List<string> { game.State.Investigators[0].DefId }));
            string attack = Refuse(() =>
                game.PlayAdversaryCard("eviscerate", new List<string> { game.State.Investigators[0].DefId }));
            if (stalk.Length == 0 && attack.Length == 0)
            {
                return "FAIL: both the Stalk and the Attack were allowed after Disappearing " +
                       "(the Butcher board forbids both)";
            }
            return stalk.Length == 0
                ? "FAIL: Stalk was allowed after Disappearing (the Butcher board forbids it)"
                : attack.Length == 0
                    ? "FAIL: the Attack card was allowed after Disappearing (the Butcher board forbids it)"
                    : "ok";
        });

        Check("SetupAdversaryCards honours the 2-Investigator Horror ability bans", game =>
        {
            // Fresh 2-Investigator Horror game, still in AdversarySetup.
            var fresh = NewGame(game.Db, "insatiable-horror", 2, finishSetup: false);
            string refusal = Refuse(() =>
                fresh.SetupAdversaryCards("bufotoxin", new List<string> { "projectile-adhesive" }));
            return refusal.Length == 0
                ? "FAIL: 'Projectile Adhesive' was accepted although adversaries.json bans it at 2 Investigators"
                : "ok";
        });

        return _found;
    }

    private static void Check(string name, Func<Game, string> probe)
    {
        var game = NewGame(_db, "butcher", 2, finishSetup: true);
        string result;
        try
        {
            result = probe(game);
        }
        catch (Exception e)
        {
            result = $"ERROR: {e.GetType().Name}: {e.Message}";
        }
        if (result != "ok")
        {
            _found.Add(new AnomalyRecord
            {
                Seed = 0,
                Scenario = "sawmill",
                Adversary = "butcher",
                Kind = "engine-invariant-probe",
                Description = $"{name} — {result}",
                Round = 1,
                Phase = "AdversaryTurn",
            });
        }
    }

    private static string Refuse(Action action)
    {
        try
        {
            action();
            return "";
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    private static string NeighbourOf(Game game, string space) =>
        game.Graph.DistancesFrom(space, 1, game.State.Overlay).Keys.First(k => k != space);

    private static void ToAdversaryTurn(Game game)
    {
        foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped).ToList())
        {
            game.BeginInvestigatorTurn(inv.DefId);
            game.EndTurnWithoutFinalAction();
        }
    }

    private static Game NewGame(GameDatabase db, string adversary, int investigators, bool finishSetup)
    {
        var starts = new Dictionary<string, string> { ["aira"] = "285", ["lucy-belle"] = "286" };
        var game = Game.NewGame(db, new GameSetup
        {
            ScenarioId = "sawmill",
            Seed = 4242,
            AdversaryId = adversary,
            InvestigatorStartSpaces = starts.Take(investigators).ToDictionary(kv => kv.Key, kv => kv.Value),
            MedicalItemSpaces = new List<string>(),
        });
        foreach (string zone in game.Graph.Def.Zones.Keys)
        {
            game.PlaceHiddenEvidence(zone, game.Graph.ZoneSpaces(zone).First(s => s.Kind == SpaceKind.Normal).Id);
        }
        bool cursed = false;
        foreach (var poi in game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
        {
            string target = game.Graph.DistancesFrom(poi.Id, 2, game.State.Overlay).Keys
                .First(id => game.Graph.Space(id).Kind == SpaceKind.Normal);
            game.PlacePoiToken(poi.Id, target, cursedFront: !cursed);
            cursed = true;
        }
        game.PlaceAdversary("S-25");
        if (!finishSetup)
        {
            return game;
        }
        if (adversary == "cult-of-hunlow")
        {
            game.SetupCultists(new List<string> { "S-24", "S-27" }.Take(investigators).ToList(), "S-18");
        }
        game.FinishAdversarySetup();
        return game;
    }
}
