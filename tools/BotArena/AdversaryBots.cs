using StiflingDark.Engine.Core;

namespace BotArena;

/// <summary>Shared adversary plumbing: figure movement, line of sight, target picking.</summary>
public abstract class AdversaryBot
{
    protected readonly Game G;
    protected readonly Actor Act;
    protected readonly DeterministicRng Rng;
    protected readonly GameRun Run;
    private readonly RasterLineOfSightBlocker? _mask;

    protected AdversaryBot(Game g, Actor act, DeterministicRng rng, GameRun run)
    {
        G = g;
        Act = act;
        Rng = rng;
        Run = run;
        _mask = g.Db.LosMask(g.State.ScenarioId);
    }

    protected GameState S => G.State;
    protected AdversaryState Adv => G.State.Adversary;

    protected List<InvestigatorState> Targets =>
        S.Investigators.Where(i => !i.Dead && !i.Escaped).ToList();

    /// <summary>
    /// Harness self-check only: an Adversary that does nothing but end its turn (it still makes
    /// the placements a Banish objective needs, or that objective could never start). Used to
    /// prove the Investigator win path actually works before reading anything into win rates.
    /// </summary>
    public static AdversaryBot CreatePassive(Game g, Actor act, DeterministicRng rng, GameRun run) =>
        new PassiveBot(g, act, rng, run);

    public static AdversaryBot Create(Game g, Actor act, DeterministicRng rng, GameRun run) => g.State.Adversary.DefId switch
    {
        "butcher" => new ButcherBot(g, act, rng, run),
        "insatiable-horror" => new HorrorBot(g, act, rng, run),
        "cult-of-hunlow" => new CultBot(g, act, rng, run),
        _ => throw new InvalidOperationException($"No bot for '{g.State.Adversary.DefId}'."),
    };

    public abstract void TakeTurn();

    /// <summary>Adversary-side setup an Escape card demands the moment it is chosen.</summary>
    public virtual void OnEscapeCardSelected(string cardId)
    {
    }

    /// <summary>
    /// The engine starts the Adversary turn lazily, on its first action, and that is where the
    /// Sprint die is rolled and the MP budgets (including each Cultist's) are handed out. The
    /// bots need those numbers before they decide anything, so nudge the turn awake with a
    /// deliberately illegal zero-length move: EnsureAdversaryTurnStarted runs first, then the
    /// step is refused without touching any state.
    /// </summary>
    protected void StartTurn()
    {
        if (!Adv.TurnStarted && S.Phase == GamePhase.AdversaryTurn)
        {
            Act.Try("start-adversary-turn", () => G.AdversaryMoveStep(Adv.Space));
        }
    }

    protected void FinishTurn()
    {
        if (S.Phase == GamePhase.AdversaryTurn)
        {
            Act.Must("adversary-end-turn", () => G.AdversaryEndTurn());
        }
    }

    protected bool IsBright(string space) =>
        G.Graph.EffectiveLight(space, S.Overlay) == LightLevel.Bright;

    protected bool HasLos(string a, string b)
    {
        if (_mask == null)
        {
            return true;
        }
        var from = G.Graph.Space(a);
        var to = G.Graph.Space(b);
        return !_mask.Blocks(from.X, from.Y, to.X, to.Y);
    }

    protected HashSet<string> AttackAdjacency(string from) =>
        G.Graph.AdjacentForAdversaryAbilities(from, S.Overlay).ToHashSet();

    protected List<InvestigatorState> AdjacentTargets(string from)
    {
        var adjacency = AttackAdjacency(from);
        return Targets.Where(i => adjacency.Contains(i.Space)).ToList();
    }

    protected InvestigatorState? Nearest(string from)
    {
        var dist = Nav.From(G, from);
        return Targets.OrderBy(i => Nav.Hops(dist, i.Space))
            .ThenByDescending(i => i.Wounds.Count)
            .ThenBy(i => i.DefId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Greedy descent toward a space for the main figure.</summary>
    protected void MoveMainToward(string target, int stopAt, bool avoidBright)
    {
        var dist = Nav.From(G, target);
        for (int guard = 0; guard < 40; guard++)
        {
            if (S.Phase != GamePhase.AdversaryTurn || Adv.MpRemaining <= 0)
            {
                return;
            }
            int here = Nav.Hops(dist, Adv.Space);
            if (here == int.MaxValue || here <= stopAt)
            {
                return;
            }
            var options = Nav.Neighbors(G, Adv.Space)
                .Where(n => Nav.Hops(dist, n) < here)
                .Where(n => !avoidBright || !IsBright(n))
                .OrderBy(n => Nav.Hops(dist, n))
                .ThenBy(n => n, StringComparer.Ordinal)
                .ToList();
            bool moved = false;
            foreach (string next in options)
            {
                string to = next;
                if (Act.Try("adv-move", () => G.AdversaryMoveStep(to)))
                {
                    moved = true;
                    break;
                }
            }
            if (!moved)
            {
                return;
            }
        }
    }

    protected bool Active(string cardId) => Adv.ActiveAbilities.Contains(cardId);

    protected bool Play(string cardId, params string[] targets)
    {
        var list = targets.ToList();
        return Act.Try("card:" + cardId, () => G.PlayAdversaryCard(cardId, list));
    }

    /// <summary>General spaces near the Investigators, for token-placing Abilities.</summary>
    protected List<string> GeneralSpacesNearTargets(string origin, int range, int wanted)
    {
        var reach = Nav.From(G, origin, range);
        var occupied = Targets.Select(i => i.Space).ToHashSet();
        occupied.Add(Adv.Space);
        var picks = reach.Keys
            .Where(id => G.Graph.Space(id).Kind == SpaceKind.Normal && !occupied.Contains(id))
            .OrderBy(id => Targets.Count == 0 ? 0 : Targets.Min(t => Nav.Hops(Nav.From(G, t.Space, range + 2), id)))
            .ThenBy(id => id, StringComparer.Ordinal)
            .Take(wanted)
            .ToList();
        return picks;
    }
}

/// <summary>Close the distance while Hidden, Stalk on line of sight, Attack with Stalk when adjacent.</summary>
public sealed class ButcherBot : AdversaryBot
{
    public ButcherBot(Game g, Actor act, DeterministicRng rng, GameRun run) : base(g, act, rng, run)
    {
    }

    public override void OnEscapeCardSelected(string cardId)
    {
        if (cardId != "the-grave" || S.Objective.Tokens.ContainsKey("grave-actual"))
        {
            return;
        }
        // Within 10 of an Investigator, as far from all of them as that allows; decoy within 3.
        var candidates = new Dictionary<string, int>();
        foreach (var inv in Targets)
        {
            foreach (var kv in Nav.From(G, inv.Space, 10))
            {
                if (G.Graph.Space(kv.Key).Kind != SpaceKind.Normal)
                {
                    continue;
                }
                candidates[kv.Key] = Math.Max(candidates.TryGetValue(kv.Key, out int d) ? d : 0, kv.Value);
            }
        }
        if (candidates.Count == 0)
        {
            return;
        }
        string actual = candidates.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        var decoys = Nav.From(G, actual, 3).Keys
            .Where(id => id != actual && G.Graph.Space(id).Kind == SpaceKind.Normal)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        string decoy = decoys.Count > 0 ? decoys[Rng.Next(decoys.Count)] : actual;
        Act.Try("place-grave", () => G.PlaceGrave(actual, decoy));
    }

    public override void TakeTurn()
    {
        StartTurn();
        var target = Nearest(Adv.Space);
        if (target == null)
        {
            FinishTurn();
            return;
        }

        if (Adv.Revealed)
        {
            Rehide();
        }
        MoveMainToward(target.Space, stopAt: 1, avoidBright: !Adv.Revealed);

        int Stalk() => Adv.Counters.TryGetValue("stalk", out int s) ? s : 0;

        if (!Adv.Revealed)
        {
            if (Active("escalating-terror"))
            {
                Play("escalating-terror");
            }
            if (Active("vengeful-darkness") && S.Flashlights.Count > 0)
            {
                Play("vengeful-darkness");
            }
            if (Active("disturbed-presence"))
            {
                var drained = Nav.From(G, Adv.Space, 4);
                var within = Targets.Where(i => drained.ContainsKey(i.Space) && i.Stamina > 0)
                    .Select(i => i.DefId).ToArray();
                if (within.Length > 0)
                {
                    Play("disturbed-presence", within);
                }
            }
            if (Active("evil-eye"))
            {
                var spots = EvilEyeSpots();
                if (spots.Count == 2)
                {
                    Play("evil-eye", spots[0], spots[1]);
                }
            }
            DoStalk();
        }

        var adjacent = AdjacentTargets(Adv.Space)
            .OrderByDescending(i => i.Wounds.Count).ThenBy(i => i.DefId, StringComparer.Ordinal).ToList();
        if (!Adv.AttackLockedThisTurn && !Adv.AttackUsedThisTurn && !Adv.ActionsUsed.Contains("disappear") &&
            Stalk() >= 1 && adjacent.Count > 0)
        {
            switch (Adv.AttackCard)
            {
                case "onslaught":
                    Play("onslaught", adjacent.Select(i => i.DefId).ToArray());
                    break;
                case "eviscerate":
                    Play("eviscerate", adjacent[0].DefId);
                    break;
                case "rend":
                    Play("rend", adjacent[0].DefId);
                    break;
            }
        }

        if (Stalk() >= 2)
        {
            if (Active("sinister-gaze") && Targets.Count >= 2)
            {
                Play("sinister-gaze", Targets[0].DefId + ":choking-fear", Targets[1].DefId + ":darkness");
            }
            if (Active("decay") && Stalk() >= 2)
            {
                Play("decay");
            }
        }
        FinishTurn();
    }

    /// <summary>Get back under cover: a failed Disappear would burn the action, so check first.</summary>
    private void Rehide()
    {
        if (!IsBright(Adv.Space))
        {
            Act.Try("adv-disappear", () => G.AdversaryDisappear());
            return;
        }
        foreach (string next in Nav.Neighbors(G, Adv.Space).Where(n => !IsBright(n)))
        {
            string to = next;
            if (Act.Try("adv-move-to-cover", () => G.AdversaryMoveStep(to)))
            {
                Act.Try("adv-disappear", () => G.AdversaryDisappear());
                return;
            }
        }
    }

    private void DoStalk()
    {
        // The Butcher's board reads "After Disappearing, you may not Stalk or use your Attack
        // card". The engine does not enforce that for the Butcher (only the Horror checks the
        // flag), so the bot enforces it on itself rather than exploiting the gap.
        if (Adv.ActionsUsed.Contains("stalk") || Adv.ActionsUsed.Contains("disappear"))
        {
            return;
        }
        var reach = Nav.From(G, Adv.Space, 8);
        var candidates = Targets
            .Where(i => reach.ContainsKey(i.Space) && HasLos(Adv.Space, i.Space))
            .OrderByDescending(i => Adv.SpineChill.ContainsKey(i.DefId))
            .ThenBy(i => Nav.Hops(reach, i.Space))
            .ThenBy(i => i.DefId, StringComparer.Ordinal)
            .Select(i => i.DefId)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }
        string? refusal = Act.TryMessage("stalk", () => G.ButcherStalk(candidates));
        if (refusal == null)
        {
            return;
        }
        // The pre-checks said this was legal. Probe once more: if the engine now claims Stalk
        // was already used, the refused call mutated ActionsUsed before validating.
        string? second = Act.TryMessage("stalk-retry", () => G.ButcherStalk(new List<string> { candidates[0] }));
        if (second != null && second.Contains("already used"))
        {
            Run.Anomaly("state-leak-on-refusal",
                $"ButcherStalk refused ({refusal}) but had already marked the Stalk action as used: " +
                $"the retry was rejected with \"{second}\".");
        }
    }

    private List<string> EvilEyeSpots()
    {
        var spots = new List<string>();
        foreach (var inv in Targets.OrderBy(i => i.DefId, StringComparer.Ordinal))
        {
            foreach (string neighbour in Nav.Neighbors(G, inv.Space))
            {
                if (G.Graph.Space(neighbour).Kind == SpaceKind.Normal && !spots.Contains(neighbour))
                {
                    spots.Add(neighbour);
                    break;
                }
            }
            if (spots.Count == 2)
            {
                break;
            }
        }
        if (spots.Count < 2)
        {
            spots.AddRange(GeneralSpacesNearTargets(Adv.Space, 6, 2 - spots.Count).Where(s => !spots.Contains(s)));
        }
        return spots.Take(2).ToList();
    }
}

/// <summary>Stay Hidden, Ambush the nearest cluster, Attack, then reposition.</summary>
public sealed class HorrorBot : AdversaryBot
{
    public HorrorBot(Game g, Actor act, DeterministicRng rng, GameRun run) : base(g, act, rng, run)
    {
    }

    private bool Enraged => Adv.Counters.TryGetValue("enraged", out int e) && e == 1;

    public override void TakeTurn()
    {
        StartTurn();
        var target = Nearest(Adv.Space);
        if (target == null)
        {
            FinishTurn();
            return;
        }

        if (Enraged)
        {
            TakeEnragedTurn(target);
            return;
        }

        if (Adv.Revealed)
        {
            // The Horror may Disappear from any light level; being Hidden re-enables Ambush next turn.
            Act.Try("adv-disappear", () => G.AdversaryDisappear());
        }
        else if (S.Round > 1 && Adv.ActionsUsed.Count == 0)
        {
            TryAmbushAndAttack(range: 5);
        }

        PlayAbilities();
        MaybeLayEggSac();
        MoveMainToward(target.Space, stopAt: 1, avoidBright: !Adv.Revealed);
        FinishTurn();
    }

    private void TakeEnragedTurn(InvestigatorState target)
    {
        var pulls = BuildPulls(range: 2);
        if (pulls.Count > 0)
        {
            Act.Try("enraged-gather", () => G.EnragedGather(pulls));
            AttackAdjacent();
        }
        PlayAbilities();
        MoveMainToward(target.Space, stopAt: 1, avoidBright: false);
        FinishTurn();
    }

    private void TryAmbushAndAttack(int range)
    {
        var pulls = BuildPulls(range);
        while (pulls.Count > 0)
        {
            if (Act.Try("ambush", () => G.HorrorAmbush(pulls)))
            {
                break;
            }
            // Plain hop range over-counts the Bright-doubled Ambush range: drop the farthest.
            var dist = Nav.From(G, Adv.Space, range);
            string worst = pulls.Keys.OrderByDescending(id =>
                Nav.Hops(dist, S.Investigators.First(i => i.DefId == id).Space)).First();
            pulls.Remove(worst);
        }
        AttackAdjacent();
    }

    /// <summary>Investigator -> the free space adjacent to The Horror they get dragged onto.</summary>
    private Dictionary<string, string> BuildPulls(int range)
    {
        var reach = Nav.From(G, Adv.Space, range);
        var landing = AttackAdjacency(Adv.Space)
            .Where(s => G.Graph.TryStep(FigureKind.Adversary, Adv.Space, s, S.Overlay) != null)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var occupied = Targets.Select(i => i.Space).ToHashSet();
        occupied.Add(Adv.Space);

        var pulls = new Dictionary<string, string>();
        foreach (var inv in Targets
            .Where(i => reach.ContainsKey(i.Space))
            .OrderBy(i => Nav.Hops(reach, i.Space))
            .ThenBy(i => i.DefId, StringComparer.Ordinal))
        {
            if (landing.Contains(inv.Space))
            {
                pulls[inv.DefId] = inv.Space; // already in place
                continue;
            }
            string? spot = landing.FirstOrDefault(s => !occupied.Contains(s));
            if (spot == null)
            {
                break;
            }
            occupied.Remove(inv.Space);
            occupied.Add(spot);
            landing.Remove(spot);
            pulls[inv.DefId] = spot;
        }
        return pulls;
    }

    private void AttackAdjacent()
    {
        if (Adv.AttackLockedThisTurn || Adv.AttackUsedThisTurn || Adv.ActionsUsed.Contains("disappear"))
        {
            return;
        }
        var adjacent = AdjacentTargets(Adv.Space)
            .OrderByDescending(i => i.Wounds.Count).ThenBy(i => i.DefId, StringComparer.Ordinal).ToList();
        if (adjacent.Count == 0 || Adv.AttackCard == null)
        {
            return;
        }
        if (Adv.AttackCard == "gastric-secretions")
        {
            var occupied = Targets.Select(i => i.Space).ToHashSet();
            occupied.Add(Adv.Space);
            string? hatchling = AttackAdjacency(Adv.Space)
                .Where(s => !occupied.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();
            var args = adjacent.Select(i => i.DefId).ToList();
            if (hatchling != null)
            {
                args.Add(hatchling);
            }
            Play("gastric-secretions", args.ToArray());
            return;
        }
        Play(Adv.AttackCard, adjacent.Select(i => i.DefId).ToArray());
    }

    private void PlayAbilities()
    {
        if (Adv.ActionsUsed.Contains("disappear"))
        {
            return;
        }
        if (Active("projectile-adhesive"))
        {
            Play("projectile-adhesive");
        }
        if (Active("devour"))
        {
            Play("devour");
        }
        if (Active("fuming-fissure"))
        {
            Play("fuming-fissure");
        }
        if (Active("occluded-lights"))
        {
            var two = Targets.Select(i => i.DefId).Take(2).ToArray();
            if (two.Length == 2)
            {
                Play("occluded-lights", two);
            }
            else if (two.Length == 1)
            {
                string zone = G.Graph.Def.Zones.Keys.OrderBy(z => z, StringComparer.Ordinal).First();
                Play("occluded-lights", two[0], zone);
            }
        }
        if (Active("thick-mucus"))
        {
            var spots = GeneralSpacesNearTargets(Adv.Space, 3, 2);
            if (spots.Count == 2)
            {
                Play("thick-mucus", spots[0], spots[1]);
            }
        }
        if (Active("tunnel"))
        {
            var spots = GeneralSpacesNearTargets(Adv.Space, 3, 1);
            if (spots.Count == 1)
            {
                Play("tunnel", spots[0]);
            }
        }
    }

    private void MaybeLayEggSac()
    {
        if (S.Objective.SelectedEscapeCard != "the-eggs")
        {
            return;
        }
        int remaining = Adv.Counters.TryGetValue("eggsacs-remaining", out int r) ? r : 4;
        if (remaining <= 0)
        {
            return;
        }
        var reach = Nav.From(G, Adv.Space, 3);
        var far = reach.Keys
            .Where(id => G.Graph.Space(id).Kind == SpaceKind.Normal)
            .OrderByDescending(id => Targets.Count == 0 ? 0 : Targets.Min(t => Nav.Hops(Nav.From(G, t.Space, 12), id)))
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (far.Count > 0)
        {
            string spot = far[0];
            Act.Try("place-eggsac", () => G.PlaceEggSac(spot));
        }
    }
}

/// <summary>Cultists swarm and Bloodlet toward The Final Sacrifice, then Mor'gonnod hunts.</summary>
public sealed class CultBot : AdversaryBot
{
    public CultBot(Game g, Actor act, DeterministicRng rng, GameRun run) : base(g, act, rng, run)
    {
    }

    private int Counter(string key) => Adv.Counters.TryGetValue(key, out int v) ? v : 0;

    private bool Corporeal => Counter("corporeal") == 1;

    public override void OnEscapeCardSelected(string cardId)
    {
        if (cardId != "the-altar" || S.Objective.Tokens.ContainsKey("ritual-knife") ||
            !S.Objective.Tokens.TryGetValue("altar", out string altar))
        {
            return;
        }
        var pool = Nav.From(G, altar, 10).Keys
            .Where(id => G.Graph.Space(id).Kind == SpaceKind.Normal)
            .OrderByDescending(id => Targets.Count == 0 ? 0 : Targets.Min(t => Nav.Hops(Nav.From(G, t.Space, 30), id)))
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (pool.Count >= 2)
        {
            Act.Try("place-ritual-tokens", () => G.PlaceRitualTokens(pool[0], pool[1]));
        }
    }

    public override void TakeTurn()
    {
        StartTurn();
        var target = Nearest(Adv.Space);
        if (target == null)
        {
            FinishTurn();
            return;
        }

        if (Corporeal)
        {
            MoveMainToward(target.Space, stopAt: 1, avoidBright: false);
            AttackCorporeal();
            PlayMorgonnodAbilities();
            FinishTurn();
            return;
        }

        bool regroup = Counter("blood") >= 5;
        var cultists = Adv.Figures.Where(f => f.Alive)
            .OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        string anchor = cultists.Count > 0 ? cultists[0].Space : Adv.Space;

        foreach (var cultist in cultists)
        {
            if (S.Phase != GamePhase.AdversaryTurn)
            {
                return;
            }
            string goal = regroup ? anchor : (Nearest(cultist.Space)?.Space ?? target.Space);
            int stopAt = regroup ? (cultist == cultists[0] ? 0 : 1) : 1;
            MoveCultistToward(cultist, goal, stopAt);
            TryBloodletting(cultist);
            if (cultist.Revealed && !IsBright(cultist.Space) &&
                !Adv.ActionsUsed.Contains("bloodlet:" + cultist.Id))
            {
                string id = cultist.Id;
                Act.Try("cultist-disappear", () => G.CultistDisappear(id));
            }
        }

        if (Adv.Revealed && !IsBright(Adv.Space))
        {
            Act.Try("adv-disappear", () => G.AdversaryDisappear());
        }
        MoveMainToward(regroup ? anchor : cultists.FirstOrDefault()?.Space ?? target.Space, stopAt: 1, avoidBright: true);
        PlayMorgonnodAbilities();

        if (regroup && Act.Try("final-sacrifice", () => G.TheFinalSacrifice()))
        {
            return; // TheFinalSacrifice ends the Adversary turn itself
        }
        FinishTurn();
    }

    private void MoveCultistToward(AdversaryFigure cultist, string goal, int stopAt)
    {
        var dist = Nav.From(G, goal);
        for (int guard = 0; guard < 30; guard++)
        {
            if (S.Phase != GamePhase.AdversaryTurn || Counter("cmp:" + cultist.Id) <= 0)
            {
                return;
            }
            int here = Nav.Hops(dist, cultist.Space);
            if (here == int.MaxValue || here <= stopAt)
            {
                return;
            }
            var options = Nav.Neighbors(G, cultist.Space)
                .Where(n => Nav.Hops(dist, n) < here)
                .Where(n => !IsBright(n) || cultist.Revealed)
                .OrderBy(n => Nav.Hops(dist, n))
                .ThenBy(n => n, StringComparer.Ordinal)
                .ToList();
            bool moved = false;
            foreach (string next in options)
            {
                string id = cultist.Id;
                string to = next;
                if (Act.Try("cultist-move", () => G.CultistMoveStep(id, to)))
                {
                    moved = true;
                    break;
                }
            }
            if (!moved)
            {
                return;
            }
        }
    }

    private void TryBloodletting(AdversaryFigure cultist)
    {
        int allowed = Counter("shriveled-hand-round") == S.Round ? 2 : 1;
        if (S.Round <= 1 || cultist.Revealed || Counter("bloodlet-count") >= allowed)
        {
            return;
        }
        var adjacency = AttackAdjacency(cultist.Space);
        var victim = Targets.Where(i => adjacency.Contains(i.Space))
            .OrderByDescending(i => i.Wounds.Count).ThenBy(i => i.DefId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (victim == null)
        {
            return;
        }
        string id = cultist.Id;
        string invId = victim.DefId;
        Act.Try("bloodletting", () => G.Bloodletting(id, invId));
    }

    private void AttackCorporeal()
    {
        if (Adv.AttackUsedThisTurn || Adv.AttackCard == null)
        {
            return;
        }
        var adjacent = AdjacentTargets(Adv.Space)
            .OrderByDescending(i => i.Wounds.Count).ThenBy(i => i.DefId, StringComparer.Ordinal).ToList();
        if (adjacent.Count == 0)
        {
            return;
        }
        switch (Adv.AttackCard)
        {
            case "ravage":
                Play("ravage", adjacent[0].DefId);
                break;
            case "immolate":
                Play("immolate", adjacent.Take(2).Select(i => i.DefId).ToArray());
                break;
            case "flagellate":
                Play("flagellate", adjacent[0].DefId);
                break;
        }
    }

    private void PlayMorgonnodAbilities()
    {
        if (!Corporeal && (Adv.Revealed || Adv.ActionsUsed.Contains("disappear")))
        {
            return;
        }
        foreach (string free in new[] { "burning-heart", "dried-tongue", "shriveled-hand", "unblinking-eye", "spiked-vertebrae" })
        {
            if (Active(free))
            {
                Play(free);
            }
        }
        if (Active("twisted-horn"))
        {
            var spots = GeneralSpacesNearTargets(Adv.Space, 5, 3);
            if (spots.Count > 0)
            {
                Play("twisted-horn", spots.ToArray());
            }
        }
        if (Active("cleft-hoof"))
        {
            var victim = Targets.FirstOrDefault(i => i.Wounds.Any(w => w.FaceUp));
            var spots = GeneralSpacesNearTargets(Adv.Space, 5, 3);
            if (victim != null && spots.Count > 0)
            {
                Play("cleft-hoof", new[] { victim.DefId }.Concat(spots).ToArray());
            }
        }
        var adjacency = AttackAdjacency(Adv.Space);
        if (Active("razor-like-talons"))
        {
            var victim = Targets.FirstOrDefault(i => adjacency.Contains(i.Space) && i.Wounds.Any(w => !w.FaceUp));
            if (victim != null)
            {
                Play("razor-like-talons", victim.DefId);
            }
        }
        if (Active("severed-ear") && S.Round > 1)
        {
            var victim = Targets.FirstOrDefault(i => adjacency.Contains(i.Space));
            if (victim != null)
            {
                Play("severed-ear", victim.DefId);
            }
        }
    }
}

/// <summary>Harness self-check opponent: places what a Banish objective needs, then passes.</summary>
public sealed class PassiveBot : AdversaryBot
{
    private readonly AdversaryBot _placer;

    public PassiveBot(Game g, Actor act, DeterministicRng rng, GameRun run) : base(g, act, rng, run)
    {
        _placer = Create(g, act, rng, run);
    }

    public override void OnEscapeCardSelected(string cardId) => _placer.OnEscapeCardSelected(cardId);

    public override void TakeTurn()
    {
        StartTurn();
        if (S.Objective.SelectedEscapeCard == "the-eggs")
        {
            var reach = Nav.From(G, Adv.Space, 3).Keys
                .Where(id => G.Graph.Space(id).Kind == SpaceKind.Normal)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (reach.Count > 0)
            {
                string spot = reach[0];
                Act.Try("place-eggsac", () => G.PlaceEggSac(spot));
            }
        }
        FinishTurn();
    }
}
