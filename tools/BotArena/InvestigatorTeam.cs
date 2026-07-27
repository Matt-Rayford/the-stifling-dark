using StiflingDark.Engine.Core;

namespace BotArena;

/// <summary>What one Investigator is trying to do this turn.</summary>
public sealed class Plan
{
    public string Space = "";
    public int StopAt;
    public string Label = "";
    /// <summary>Run once the Investigator is on (or within StopAt of) Space. May end the turn.</summary>
    public Func<bool>? Arrive;
}

/// <summary>
/// The Investigator side. One brain drives every Investigator.
///
/// Offence: flip Light Switches to reveal a Zone's hidden Evidence, walk Evidence to a Computer
/// / Ticket Booth, pick the Escape objective once the Evidence gate is met, then drive that
/// objective's token chain to an escape.
///
/// Defence, which is the point of the game: a beam that catches the Adversary disarms it for
/// the whole round, so the default Final Action is a Flashlight aimed at the ring of spaces an
/// Attack could come from and at the lanes leading into it from the Adversary's last known
/// position (its Shadow and Noise tokens). Investigators buddy up so one beam covers two
/// people, alternate who spends Charge so the group is never dark, and Lock the Doors behind
/// them so the Adversary has to spend its once-per-turn Break Door to follow.
/// </summary>
public sealed partial class InvestigatorTeam
{
    private readonly Game _g;
    private readonly Actor _act;
    private readonly DeterministicRng _rng;
    private readonly GameRun _run;
    private readonly SpaceKind _turnInKind;
    private readonly List<string> _turnInSpaces;
    private readonly Dictionary<string, int> _degree;
    private readonly Dictionary<string, List<string>> _adjacency;
    private readonly HashSet<string> _claims = new();

    /// <summary>Space -> "how close the Adversary was last seen", from public Shadow/Noise tokens.</summary>
    private readonly Dictionary<string, int> _danger = new();
    private readonly Dictionary<string, int> _fleeStreak = new();
    /// <summary>Flashlight swapping: who spent Charge on a beam last round, so buddy pairs can
    /// take turns lighting and topping up and the pair is never dark two rounds running.</summary>
    private HashSet<string> _beamedLastRound = new();
    private HashSet<string> _beamedThisRound = new();
    private readonly Dictionary<string, int> _woundsAtRoundStart = new();
    /// <summary>Last round's destination per Investigator: sticking to a goal beats re-deriving
    /// "nearest" every round, which makes two Investigators swap objectives as they pass.</summary>
    private readonly Dictionary<string, string> _goal = new();
    private int _threatLevel;
    private bool _teammateAttacked;

    public InvestigatorTeam(Game g, Actor act, DeterministicRng rng, GameRun run)
    {
        _g = g;
        _act = act;
        _rng = rng;
        _run = run;
        _turnInKind = g.State.ScenarioId == "sawmill" ? SpaceKind.Computer : SpaceKind.TicketBooth;
        _turnInSpaces = g.Graph.Def.Spaces.Where(s => s.Kind == _turnInKind)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        _degree = g.Graph.Def.Spaces.ToDictionary(s => s.Id, _ => 0);
        _adjacency = g.Graph.Def.Spaces.ToDictionary(s => s.Id, _ => new List<string>());
        foreach (var edge in g.Graph.Def.Edges.Where(e => e.Type != EdgeType.AdversaryLink))
        {
            _degree[edge.A] += 1;
            _degree[edge.B] += 1;
            _adjacency[edge.A].Add(edge.B);
            _adjacency[edge.B].Add(edge.A);
        }
    }

    /// <summary>Set by the single-seed probe mode to dump each Investigator's chosen plan.</summary>
    public Action<string>? Trace { get; set; }

    private GameState S => _g.State;

    private List<InvestigatorState> Alive =>
        S.Investigators.Where(i => !i.Dead && !i.Escaped).ToList();

    // ---------- Round bookkeeping ----------

    public void BeginRound()
    {
        _claims.Clear();
        _beamedLastRound = _beamedThisRound;
        _beamedThisRound = new HashSet<string>();
        _teammateAttacked = S.Investigators.Any(i =>
            _woundsAtRoundStart.TryGetValue(i.DefId, out int before) && i.Wounds.Count > before);
        foreach (var inv in S.Investigators)
        {
            _woundsAtRoundStart[inv.DefId] = inv.Wounds.Count;
        }
        RebuildDanger();
    }

    /// <summary>
    /// Everything the Investigators legitimately know about the Adversary's whereabouts: the
    /// Shadow tokens it left when it Stalked / Attacked / Disappeared / started its turn, the
    /// Noise tokens marking Windows it crossed, and any figure currently Revealed. Radius
    /// reflects how far that adversary reaches from there in one turn.
    /// </summary>
    private void RebuildDanger()
    {
        _danger.Clear();
        int radius = S.Adversary.DefId switch
        {
            "insatiable-horror" => 4, // Ambush drags Investigators in from 5 spaces
            "butcher" => 3,
            _ => 2,
        };
        var strong = new List<string>(S.Adversary.ShadowTokens.Values);
        if (S.Adversary.Revealed)
        {
            strong.Add(S.Adversary.Space);
        }
        strong.AddRange(S.Adversary.Figures.Where(f => f.Alive && f.Revealed).Select(f => f.Space));

        var weak = new List<string>();
        foreach (string key in S.Adversary.NoiseTokens)
        {
            int sep = key.IndexOf('|');
            if (sep > 0)
            {
                weak.Add(key.Substring(0, sep));
                weak.Add(key.Substring(sep + 1));
            }
        }

        Paint(strong, radius);
        Paint(weak, Math.Max(1, radius - 1));
        _threatLevel = Alive.Count == 0 ? 0 : Alive.Max(i => Danger(i.Space));
        if (_teammateAttacked)
        {
            _threatLevel += 1;
        }
    }

    private void Paint(IEnumerable<string> sources, int radius)
    {
        foreach (string source in sources.Distinct().Where(s => _g.Graph.HasSpace(s)))
        {
            foreach (var kv in Nav.From(_g, source, radius))
            {
                int level = radius + 1 - kv.Value;
                if (!_danger.TryGetValue(kv.Key, out int current) || current < level)
                {
                    _danger[kv.Key] = level;
                }
            }
        }
    }

    private int Danger(string space) => _danger.TryGetValue(space, out int d) ? d : 0;

    // ---------- Team-level decisions taken between turns ----------

    /// <summary>Draw and commit to an Escape card as soon as the Evidence gate is met.</summary>
    public void MaybeSelectEscapeCard()
    {
        if (S.Objective.SelectedEscapeCard != null || S.Phase == GamePhase.GameOver)
        {
            return;
        }
        int required = _g.Db.Config.ByInvestigatorCount[S.Investigators.Count].EvidenceRequiredForObjective;
        if (S.Objective.EvidenceTurnedIn < required)
        {
            return;
        }
        IReadOnlyList<string> choices;
        try
        {
            choices = _g.DrawEscapeChoices();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        // Banish flows need adversary counterplay the bots handle badly: take one 10% of the time.
        bool banish = _rng.Next(10) == 0;
        string pick = banish ? choices[choices.Count - 1] : choices[_rng.Next(choices.Count - 1)];
        _act.Must("select-escape-card", () => _g.SelectEscapeCard(pick));
    }

    // ---------- One Investigator's turn ----------

    public void TakeTurn(string invId)
    {
        _act.Must("begin-turn:" + invId, () => _g.BeginInvestigatorTurn(invId));
        var inv = S.Investigators.First(i => i.DefId == invId);

        ResolveWindow(inv);
        FreeInteracts(inv, afterTravel: false);
        UseTacticalItems(inv);
        UseRegroupItems(inv);
        UseSpiritAbilities(inv);

        var plan = ChoosePlan(inv);
        if (plan != null && plan.Label != "flee")
        {
            _goal[invId] = plan.Space;
        }
        Trace?.Invoke($"r{S.Round,2} {invId,-11} {plan?.Label ?? "idle",-22} -> {plan?.Space ?? "-",-6} " +
                      $"from {inv.Space,-6} mp={inv.MpRemaining} w={inv.Wounds.Count} st={inv.Stamina} " +
                      $"ch={inv.Charge} danger={Danger(inv.Space)} threat={_threatLevel}");
        if (plan != null)
        {
            UseTravelItems(inv, plan);
            MaybeSprint(inv, plan);
            Travel(inv, plan);
            if (Active(inv) && Arrived(inv, plan) && plan.Arrive != null)
            {
                _act.Try("arrive:" + plan.Label, () => plan.Arrive());
            }
        }
        else
        {
            MaybeRest(inv);
        }

        if (Active(inv))
        {
            FreeInteracts(inv, afterTravel: true);
            FinalAction(inv);
        }
        if (Active(inv))
        {
            EndTurn(inv);
        }
    }

    private bool Active(InvestigatorState inv) =>
        S.Phase == GamePhase.InvestigatorTurns && S.ActiveInvestigator == inv.DefId;

    private bool Arrived(InvestigatorState inv, Plan plan)
    {
        if (plan.StopAt == 0)
        {
            return inv.Space == plan.Space;
        }
        return Nav.Hops(Nav.From(_g, plan.Space, plan.StopAt), inv.Space) <= plan.StopAt;
    }

    // ---------- Free interacts (no MP, no Final Action) ----------

    private void FreeInteracts(InvestigatorState inv, bool afterTravel)
    {
        if (!Active(inv))
        {
            return;
        }
        ResolveWindow(inv);

        if (S.Evidence.Any(kv => kv.Value.Revealed && kv.Value.Space == inv.Space))
        {
            _act.Try("pick-up-evidence", () => _g.PickUpEvidence());
        }
        if (!IsSpirit(inv) && S.MedicalItemSpaces.Contains(inv.Space))
        {
            _act.Try("pick-up-medical", () => _g.PickUpMedicalItem());
        }
        if (S.PoiTokens.Any(p => p.TokenSpace == inv.Space && p.Revealed && !p.Collected))
        {
            _act.Try("pick-up-poi", () => _g.PickUpPoiToken());
        }
        var space = _g.Graph.Space(inv.Space);
        if (space.Kind == SpaceKind.LightSwitch && space.Zone != null &&
            !S.FalteringZones.Contains(space.Zone) && !S.Overlay.BrightZones.Contains(space.Zone))
        {
            _act.Try("light-switch", () => _g.ActivateLightSwitch());
        }
        foreach (string token in CarriableTokensAt(inv.Space))
        {
            string name = token;
            if (name == "ritual-knife" || name == "rope-circle")
            {
                _act.Try("pick-up-banish-token", () => _g.PickUpBanishToken(name));
            }
            else
            {
                _act.Try("pick-up-objective-token", () => _g.PickUpObjectiveToken(name));
            }
        }
        if (afterTravel)
        {
            Turtle(inv);
        }
    }

    /// <summary>
    /// Wounds that shut a whole system down are worth a Medkit far more than a generic one:
    /// Ergophobia in particular bars the Involved Action, which is every Evidence turn-in and
    /// every objective step, so an Investigator carrying it is out of the game until it is
    /// flipped back down.
    /// </summary>
    private static readonly string[] WoundTreatmentOrder =
    {
        "ergophobia", "nyctophilia", "hemorrhage", "drain", "mangled-hands", "mistrust",
        "dislocated-hip", "claustrophobia", "collapsed-lung", "broken-battery",
    };

    private void UseMedkits(InvestigatorState inv)
    {
        while (inv.Items.Contains("medkit") && Active(inv))
        {
            var patient = Alive
                .Where(o => o.Wounds.Any(w => w.FaceUp) && (o == inv || _g.Graph.Edge(inv.Space, o.Space) != null))
                .OrderByDescending(o => WorstWoundRank(o))
                .ThenByDescending(o => o.Wounds.Count)
                .ThenBy(o => o.DefId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (patient == null)
            {
                break;
            }
            string? worst = patient.Wounds.Where(w => w.FaceUp)
                .OrderBy(w => Array.IndexOf(WoundTreatmentOrder, w.CardId) is int i && i >= 0 ? i : 99)
                .Select(w => w.CardId).FirstOrDefault();
            var args = worst == null
                ? new List<string> { patient.DefId }
                : new List<string> { patient.DefId, worst };
            if (!_act.Try("medkit", () => _g.UseItem("medkit", args)))
            {
                break;
            }
        }
    }

    private static int WorstWoundRank(InvestigatorState inv) =>
        inv.Wounds.Where(w => w.FaceUp)
            .Select(w => Array.IndexOf(WoundTreatmentOrder, w.CardId))
            .Where(i => i >= 0)
            .Select(i => WoundTreatmentOrder.Length - i)
            .DefaultIfEmpty(0)
            .Max();

    private bool CanTakeInvolved(InvestigatorState inv) =>
        _g.ActionBlockers(inv.DefId, Game.ActionInvolved).Count == 0;

    private static bool IsSpirit(InvestigatorState inv) => inv.SpiritId != null;

    private void ResolveWindow(InvestigatorState inv)
    {
        int guard = 0;
        while (S.PendingWindowChoice && S.Phase != GamePhase.GameOver && guard++ < 4)
        {
            // Wounds are lethal at 4; a stop-and-lose-Stamina keeps the body intact but ends
            // movement — which is unplayable if the Window dropped you onto a teammate, because
            // a turn may not end there and Movement is now locked.
            bool stop = inv.Wounds.Count >= 1 && inv.Stamina >= 3 && !Occupied(inv, inv.Space);
            _act.Must("resolve-window", () => _g.ResolveWindow(stop));
        }
    }

    /// <summary>Objective tokens on this space that the team still wants carried.</summary>
    private List<string> CarriableTokensAt(string space)
    {
        var wanted = new List<string>();
        foreach (var kv in S.Objective.Tokens)
        {
            if (kv.Value != space)
            {
                continue;
            }
            switch (kv.Key)
            {
                case "lockbox":
                case "battery":
                case "repair-kit":
                case "spark-plug":
                case "flare-gun":
                case "ammo-1":
                case "ammo-2":
                case "angle-grinder":
                case "ritual-knife":
                case "rope-circle":
                    wanted.Add(kv.Key);
                    break;
            }
        }
        return wanted;
    }

    // ---------- Sprint / Rest ----------

    private void MaybeSprint(InvestigatorState inv, Plan plan)
    {
        int hops = Nav.Hops(CostTo(plan.Space), inv.Space);
        bool far = hops != int.MaxValue && hops > inv.MpRemaining;
        if (far && (IsSpirit(inv) || inv.Stamina >= 3) && _act.Try("sprint", () => _g.Sprint()))
        {
            return;
        }
        MaybeRest(inv);
    }

    private void MaybeRest(InvestigatorState inv)
    {
        if (!IsSpirit(inv) && !inv.SprintedOrRested && inv.Stamina < 5)
        {
            _act.Try("rest", () => _g.Rest());
        }
    }

    // ---------- Movement-point pathing ----------

    /// <summary>
    /// Hop counts are the wrong currency for an Investigator: a Dark space costs 2 MP and a
    /// Window costs a Wound or the rest of the turn. These two Dijkstra fields price a route in
    /// what it actually spends, which is worth roughly a fifth of the team's mileage over a game.
    /// </summary>
    private Dictionary<string, int> CostTo(string goal) => CostField(goal, toward: true);

    private Dictionary<string, int> CostFrom(string origin) => CostField(origin, toward: false);

    private const int WindowPenalty = 2;

    private Dictionary<string, int> CostField(string root, bool toward)
    {
        var best = new Dictionary<string, int> { [root] = 0 };
        var settled = new HashSet<string>();
        var frontier = new PriorityQueue<string, int>();
        frontier.Enqueue(root, 0);
        while (frontier.TryDequeue(out string? current, out int cost))
        {
            if (!settled.Add(current))
            {
                continue;
            }
            foreach (string other in _adjacency[current])
            {
                if (settled.Contains(other))
                {
                    continue;
                }
                // "toward": the field holds the cost of walking from `other` to the root, so the
                // step being priced is other -> current. Otherwise it is current -> other.
                var step = toward
                    ? _g.Graph.TryStep(FigureKind.Investigator, other, current, S.Overlay)
                    : _g.Graph.TryStep(FigureKind.Investigator, current, other, S.Overlay);
                if (step == null)
                {
                    continue;
                }
                int candidate = cost + step.Cost + (step.CrossesWindow ? WindowPenalty : 0);
                if (!best.TryGetValue(other, out int existing) || candidate < existing)
                {
                    best[other] = candidate;
                    frontier.Enqueue(other, candidate);
                }
            }
        }
        return best;
    }

    // ---------- Movement ----------

    private void Travel(InvestigatorState inv, Plan plan)
    {
        var hops = Nav.From(_g, plan.Space);
        if (Nav.Hops(hops, inv.Space) == int.MaxValue && OpenBlockingDoor(inv))
        {
            // Cut off — most likely by a Door the team Locked behind itself. Open it again.
            hops = Nav.From(_g, plan.Space);
        }
        var dist = CostTo(plan.Space);
        for (int guard = 0; guard < 40; guard++)
        {
            if (!Active(inv) || inv.Dead || inv.MovementLocked || inv.MpRemaining <= 0)
            {
                return;
            }
            int here = Nav.Hops(dist, inv.Space);
            if (here == int.MaxValue || Nav.Hops(hops, inv.Space) <= plan.StopAt)
            {
                break;
            }
            // Ending a turn stacked on another Investigator is illegal and, at 0 MP, inescapable:
            // never take a step onto an occupied space that would also exhaust the MP pool.
            var options = Nav.Neighbors(_g, inv.Space)
                .Where(n => Nav.Hops(dist, n) < here)
                // Leave enough MP to peel off again — a Dark step out costs 2.
                .Where(n => !Occupied(inv, n) || inv.MpRemaining >= StepCost(n) + 2)
                .OrderBy(n => Nav.Hops(dist, n))
                .ThenBy(n => Danger(n))
                .ThenBy(n => Occupied(inv, n) ? 1 : 0)
                .ThenBy(n => _g.Graph.EffectiveLight(n, S.Overlay) == LightLevel.Dark ? 1 : 0)
                .ThenBy(n => n, StringComparer.Ordinal)
                .ToList();
            bool moved = false;
            foreach (string next in options)
            {
                string to = next;
                if (_act.Try("move", () => _g.MoveStep(to)))
                {
                    moved = true;
                    break;
                }
            }
            if (!moved)
            {
                break;
            }
            ResolveWindow(inv);
            FreeInteracts(inv, afterTravel: false);
        }
        StepOffOccupied(inv);
    }

    private bool OpenBlockingDoor(InvestigatorState inv)
    {
        foreach (var edge in _g.Graph.Def.Edges.Where(e => e.A == inv.Space || e.B == inv.Space))
        {
            string other = edge.A == inv.Space ? edge.B : edge.A;
            var state = S.Overlay.DoorState(other);
            if (state != DoorState.Locked && state != DoorState.Damaged)
            {
                continue;
            }
            string door = other;
            if (_act.Try("open-door", () => _g.OpenDoor(door)))
            {
                return true;
            }
        }
        return false;
    }

    private bool Occupied(InvestigatorState self, string space) =>
        S.Investigators.Any(o => o != self && !o.Dead && !o.Escaped && o.Space == space);

    private int StepCost(string to) =>
        _g.Graph.EffectiveLight(to, S.Overlay) == LightLevel.Dark ? 2 : 1;

    /// <summary>A turn may not end stacked on another Investigator; peel off while MP remains.</summary>
    private void StepOffOccupied(InvestigatorState inv)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!Active(inv) || IsSpirit(inv) || !Occupied(inv, inv.Space))
            {
                return;
            }
            foreach (string next in Nav.Neighbors(_g, inv.Space).Where(n => !Occupied(inv, n)))
            {
                string to = next;
                if (_act.Try("step-off", () => _g.MoveStep(to)))
                {
                    ResolveWindow(inv);
                    return;
                }
            }
            // Out of MP on somebody else's space: a Sprint is the only legal way back out.
            if (!_act.Try("sprint-to-unstack", () => _g.Sprint()))
            {
                return;
            }
        }
    }

    // ---------- Final Action: the defensive beam ----------

    private void FinalAction(InvestigatorState inv)
    {
        if (!Active(inv) || inv.FinalAction != FinalActionKind.None || IsSpirit(inv))
        {
            return;
        }
        int cost = 1 + Math.Max(0, _g.RoundModifier(Game.FlashlightChargeSurchargeKey));
        if (inv.Charge < cost && inv.Items.Contains("spare-batteries"))
        {
            // Spend the card's Supply instead of Charge we do not have.
            _act.Try("spare-batteries", () => _g.UseItem("spare-batteries"));
            cost = Math.Max(0, cost - 1);
        }
        if (WantsBeam(inv, cost))
        {
            var (angle, score) = BestFlashlight(inv);
            double bestAngle = angle;
            if (score > 0 && _act.Try("place-flashlight", () => _g.PlaceFlashlight(bestAngle)))
            {
                _beamedThisRound.Add(inv.DefId);
                return;
            }
        }
        if (inv.Charge < _g.Db.Config.ChargeMax && _act.Try("charge", () => _g.ChargeFlashlight()))
        {
            return;
        }
    }

    /// <summary>
    /// Charge is the team's ammunition: 1 per beam, 1 back per Charge action, 3 max. Anyone with
    /// spare Charge beams every round; anyone down to their last point alternates by round and
    /// seat so half the group is always topping up and the other half is always lighting.
    /// </summary>
    private bool WantsBeam(InvestigatorState inv, int cost)
    {
        if (inv.Charge < cost)
        {
            return false;
        }
        // Flashlight swapping: if I lit the area last round and my partner can light it this
        // round, I top up instead, so the pair's cover never lapses and neither of us runs dry.
        var buddy = Buddy(inv);
        if (_threatLevel > 0 && _beamedLastRound.Contains(inv.DefId) && buddy != null &&
            !IsSpirit(buddy) && buddy.Charge >= cost && !_beamedLastRound.Contains(buddy.DefId) &&
            inv.Charge < _g.Db.Config.ChargeMax)
        {
            return false;
        }
        if (inv.Charge > cost || _threatLevel > 0)
        {
            return true; // with the Adversary about, cover beats banking Charge
        }
        int seat = S.Investigators.FindIndex(i => i.DefId == inv.DefId);
        return (S.Round + seat) % 2 == 0;
    }

    private (double Angle, double Score) BestFlashlight(InvestigatorState inv)
    {
        var darkZones = S.Evidence.Where(kv => !kv.Value.Revealed).Select(kv => kv.Key).ToHashSet();
        var mustLight = MustLightSpaces();
        bool threatened = _threatLevel > 0;

        // The ring an Attack could come from: every space this Investigator or a close teammate
        // stands on, plus everything adjacent to it. A beam over that ring means anything walking
        // in is Revealed on arrival, and a Revealed Adversary may not Attack for the rest of the round.
        var near = Nav.From(_g, inv.Space, 3);
        var buddies = Alive.Where(o => o == inv || Nav.Hops(near, o.Space) <= 2).ToList();
        var guardRing = new HashSet<string>();
        foreach (var friend in buddies)
        {
            guardRing.Add(friend.Space);
            foreach (string neighbour in Nav.Neighbors(_g, friend.Space))
            {
                guardRing.Add(neighbour);
            }
        }
        var lanes = near.Keys.Where(k => Danger(k) > 0).ToHashSet();
        // Turtling: after the Doors are Locked these are the only ways left in, so a beam over
        // them seals the huddle for the round.
        var entrances = Entrances(buddies);

        double bestAngle = 0;
        double best = 0;
        // 12 aims (30 degrees apart) — enough to pick out a specific corridor.
        for (int i = 0; i < 12; i++)
        {
            double angle = i * Math.PI / 6;
            HashSet<string> bright;
            try
            {
                bright = _g.PreviewFlashlight(inv.DefId, angle);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            double score = 0;
            foreach (string id in bright)
            {
                if (S.Overlay.BrightSpaces.Contains(id))
                {
                    continue; // a teammate already covers it; do not pay twice for the same space
                }
                score += 0.1;
                if (mustLight.Contains(id))
                {
                    score += 40;
                }
                if (entrances.Contains(id))
                {
                    score += threatened ? 14 : 2;
                }
                if (guardRing.Contains(id))
                {
                    score += threatened ? 12 : 2;
                }
                if (lanes.Contains(id))
                {
                    score += threatened ? 6 : 2;
                }
                // Choke points: a beam down a corridor mouth walls off a whole approach.
                if (_degree.TryGetValue(id, out int degree) && degree <= 2 && Nav.Hops(near, id) <= 3)
                {
                    score += threatened ? 4 : 1;
                }
                string? zone = _g.Graph.Space(id).Zone;
                if (zone != null && darkZones.Contains(zone))
                {
                    score += threatened ? 1 : 2;
                }
            }
            if (score > best)
            {
                best = score;
                bestAngle = angle;
            }
        }
        return (bestAngle, best);
    }

    /// <summary>Objective tokens that only work once their space has been made Bright.</summary>
    private HashSet<string> MustLightSpaces()
    {
        var set = new HashSet<string>();
        string? card = S.Objective.SelectedEscapeCard;
        if (card == "the-altar" && S.Objective.Tokens.TryGetValue("altar", out string altar) &&
            (S.Adversary.Counters.TryGetValue("altar-revealed", out int revealed) ? revealed : 0) != 1)
        {
            set.Add(altar);
        }
        if (card == "the-grave" && S.Objective.Tokens.TryGetValue("grave-actual", out string grave) &&
            !S.Adversary.Counters.ContainsKey("burning-until"))
        {
            set.Add(grave);
        }
        // Power the Gate's aid: pushing your luck on the Saw pays a Supply on 3+ when the Saw is
        // Bright but only on 5+ in the dark, so a teammate's beam nearly doubles the Lockbox rate.
        if ((card == "north-gate" || card == "south-gate") && S.Objective.Supplies < 4 &&
            S.Objective.Tokens.TryGetValue("saw", out string sawToLight))
        {
            set.Add(sawToLight);
        }
        return set;
    }

    // ---------- End of turn ----------

    private void EndTurn(InvestigatorState inv)
    {
        StepOffOccupied(inv);
        if (!Active(inv))
        {
            return;
        }
        if (Occupied(inv, inv.Space) && !IsSpirit(inv))
        {
            _run.Anomaly("cannot-end-turn-stacked",
                $"{inv.DefId} is on {inv.Space} with {inv.MpRemaining} MP left (sprint/rest used: " +
                $"{inv.SprintedOrRested}, movement locked: {inv.MovementLocked}) and another Investigator " +
                "occupies that space: EndTurnWithoutFinalAction refuses, and no action in the API can end " +
                "the turn from there.");
            throw new ArenaAbort("stacked-turn-end");
        }
        _act.Must("end-turn:" + inv.DefId, () => _g.EndTurnWithoutFinalAction());
    }

    // ---------- Goal selection ----------

    private Plan? ChoosePlan(InvestigatorState inv)
    {
        var flee = FleePlan(inv);
        if (flee != null)
        {
            return flee;
        }
        var screen = ScreenPlan(inv);
        if (screen != null)
        {
            return screen;
        }
        var objective = ObjectivePlan(inv);
        if (objective != null)
        {
            return objective;
        }
        var regroup = RegroupPlan(inv);
        if (regroup != null)
        {
            return regroup;
        }
        return EvidencePlan(inv);
    }

    /// <summary>
    /// Break contact when the Adversary's last known position is close and Wounds are piling
    /// up. A Spine Chill token is a second warning: The Butcher had line of sight last turn.
    /// </summary>
    private Plan? FleePlan(InvestigatorState inv)
    {
        if (IsSpirit(inv))
        {
            return null;
        }
        int here = Danger(inv.Space);
        if (here == 0)
        {
            return null;
        }
        bool stalked = S.Adversary.SpineChill.ContainsKey(inv.DefId);
        int wounds = inv.Wounds.Count;
        // here >= 3 means "within 1 space of where the Adversary last showed itself".
        bool scared = (wounds >= 3 && here >= 2) || (wounds == 2 && here >= 3) || (wounds >= 1 && stalked && here >= 3);
        if (!scared)
        {
            return null;
        }
        // Running forever loses on the clock just as surely: after two rounds of retreating,
        // get back to work.
        _fleeStreak.TryGetValue(inv.DefId, out int streak);
        if (streak >= 2)
        {
            _fleeStreak[inv.DefId] = 0;
            return null;
        }
        _fleeStreak[inv.DefId] = streak + 1;
        var reachable = Nav.From(_g, inv.Space, Math.Max(1, inv.MpRemaining + 2));
        string target = reachable.Keys
            .Where(k => !Occupied(inv, k))
            .OrderBy(Danger)
            .ThenByDescending(k => Nav.Hops(reachable, k))
            .ThenBy(k => k, StringComparer.Ordinal)
            .First();
        return target == inv.Space ? null : new Plan { Space = target, Label = "flee" };
    }

    /// <summary>
    /// Buddy system: while the Adversary is about and nothing objective-critical is in hand,
    /// close back up on your partner so one beam covers both of you (and so The Butcher's
    /// Onslaught cannot catch either of you alone).
    /// </summary>
    private Plan? RegroupPlan(InvestigatorState inv)
    {
        if (_threatLevel == 0 || IsSpirit(inv))
        {
            return null;
        }
        var alive = Alive;
        int seat = alive.FindIndex(i => i.DefId == inv.DefId);
        if (seat % 2 == 0)
        {
            return null; // only the junior partner closes the gap, or the pair chases its own tail
        }
        var buddy = Buddy(inv);
        if (buddy == null || (Danger(inv.Space) == 0 && Danger(buddy.Space) == 0))
        {
            return null; // nobody is actually in reach of the Adversary: keep working
        }
        if (inv.EvidenceCarried.Count > 0 || S.Objective.TokenCarriers.Any(kv => kv.Value == inv.DefId))
        {
            return null;
        }
        var dist = Nav.From(_g, buddy.Space);
        if (Nav.Hops(dist, inv.Space) <= 3)
        {
            return null; // close enough for one beam to cover both of you
        }
        string spot = Nav.Neighbors(_g, buddy.Space).FirstOrDefault(n => !Occupied(inv, n) && !_claims.Contains(n))
                      ?? buddy.Space;
        _claims.Add(spot);
        return new Plan { Space = spot, StopAt = 1, Label = "regroup-" + buddy.DefId };
    }

    /// <summary>Pair Investigators off in seat order: 0 with 1, 2 with 3.</summary>
    private InvestigatorState? Buddy(InvestigatorState inv)
    {
        var alive = Alive;
        int index = alive.FindIndex(i => i.DefId == inv.DefId);
        if (index < 0)
        {
            return null;
        }
        int partner = index % 2 == 0 ? index + 1 : index - 1;
        return partner >= 0 && partner < alive.Count ? alive[partner] : null;
    }

    /// <summary>Reveal a Zone's Evidence, collect it, walk it to a turn-in feature.</summary>
    private Plan? EvidencePlan(InvestigatorState inv)
    {
        // Pull toward the partner only when the danger is local: a Shadow token on the far side
        // of the map should not stop four Investigators working four different Zones.
        var buddy = Danger(inv.Space) > 0 ? Buddy(inv) : null;

        // A face-up Ergophobia bars the Involved Action for good: this Investigator can never
        // turn Evidence in again, so pass what they carry to somebody who can.
        if (inv.EvidenceCarried.Count > 0 && !CanTakeInvolved(inv))
        {
            var courier = Alive
                .Where(o => o != inv && CanTakeInvolved(o) && _g.ActionBlockers(o.DefId, Game.ActionTrade).Count == 0)
                .OrderBy(o => Nav.Hops(Nav.From(_g, inv.Space), o.Space))
                .ThenBy(o => o.DefId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (courier != null)
            {
                var zones = inv.EvidenceCarried.ToList();
                string to = courier.DefId;
                return new Plan
                {
                    Space = courier.Space,
                    StopAt = 1,
                    Label = "hand-off-evidence",
                    Arrive = () =>
                    {
                        foreach (string zone in zones)
                        {
                            _g.TradeEvidence(to, zone);
                        }
                        return true;
                    },
                };
            }
        }

        if (inv.EvidenceCarried.Count > 0 && CanTakeInvolved(inv))
        {
            string? feature = Sticky(inv, _turnInSpaces, buddy);
            if (feature != null)
            {
                var carried = inv.EvidenceCarried.ToList();
                return new Plan
                {
                    Space = feature,
                    Label = "turn-in-evidence",
                    Arrive = () =>
                    {
                        _g.TurnInEvidence(BuildTurnIns(carried));
                        return true;
                    },
                };
            }
        }

        var revealed = S.Evidence.Where(kv => kv.Value.Revealed).Select(kv => kv.Value.Space)
            .Where(s => !_claims.Contains(s)).ToList();
        string? token = Sticky(inv, revealed, buddy);
        if (token != null)
        {
            _claims.Add(token);
            return new Plan { Space = token, Label = "collect-evidence" };
        }

        var switches = _g.Graph.Def.Spaces
            .Where(s => s.Kind == SpaceKind.LightSwitch && s.Zone != null &&
                        !S.FalteringZones.Contains(s.Zone) &&
                        !S.Overlay.BrightZones.Contains(s.Zone) &&
                        S.Evidence.TryGetValue(s.Zone, out var e) && !e.Revealed &&
                        !_claims.Contains(s.Zone))
            .Select(s => s.Id).ToList();
        string? lightSwitch = Sticky(inv, switches, buddy);
        if (lightSwitch != null)
        {
            string? zone = _g.Graph.Space(lightSwitch).Zone;
            if (zone != null)
            {
                _claims.Add(zone);
            }
            return new Plan { Space = lightSwitch, Label = "light-switch" };
        }

        var poi = S.PoiTokens.Where(p => p.Revealed && !p.Collected && !_claims.Contains(p.TokenSpace))
            .Select(p => p.TokenSpace).ToList();
        string? poiSpace = Sticky(inv, poi, buddy);
        if (poiSpace != null)
        {
            _claims.Add(poiSpace);
            return new Plan { Space = poiSpace, Label = "collect-poi" };
        }

        string? medical = Sticky(inv, S.MedicalItemSpaces.Where(s => !_claims.Contains(s)).ToList(), buddy);
        if (medical != null)
        {
            _claims.Add(medical);
            return new Plan { Space = medical, Label = "collect-medical" };
        }
        return null;
    }

    private List<(string zone, string reward, string? arg, string? arg2)> BuildTurnIns(List<string> zones)
    {
        var used = new HashSet<string>(S.Objective.OncePerGameRewardsUsed);
        var list = new List<(string, string, string?, string?)>();
        foreach (string zone in zones)
        {
            if (!used.Contains("medical-item"))
            {
                used.Add("medical-item");
                list.Add((zone, "medical-item", null, null));
                continue;
            }
            var poi = S.PoiTokens.FirstOrDefault(p => !p.Revealed && !p.Collected);
            if (poi != null)
            {
                list.Add((zone, "reveal-poi", poi.PoiSpace, null));
                continue;
            }
            list.Add(S.GeneralItemDeck.Count > 0
                ? (zone, "general-item", null, null)
                : (zone, "open-window-token", null, null));
        }
        return list;
    }

    /// <summary>Keep last round's destination when it is still on the table.</summary>
    private string? Sticky(InvestigatorState inv, IReadOnlyList<string> candidates, InvestigatorState? buddy)
    {
        if (_goal.TryGetValue(inv.DefId, out string remembered) &&
            candidates.Contains(remembered) && !_claims.Contains(remembered))
        {
            return remembered;
        }
        return Nearest(inv.Space, candidates, buddy);
    }

    /// <summary>
    /// Closest candidate, with a pull toward the buddy's half of the board while the Adversary
    /// is about: working the same area is what makes the buddy system affordable.
    /// </summary>
    private string? Nearest(string from, IReadOnlyList<string> candidates, InvestigatorState? buddy = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }
        var dist = CostFrom(from);
        var buddyDist = buddy == null ? null : Nav.From(_g, buddy.Space);
        string? best = null;
        long bestScore = long.MaxValue;
        foreach (string candidate in candidates.OrderBy(c => c, StringComparer.Ordinal))
        {
            int hops = Nav.Hops(dist, candidate);
            if (hops == int.MaxValue)
            {
                continue;
            }
            long score = hops * 2;
            if (buddyDist != null)
            {
                int fromBuddy = Nav.Hops(buddyDist, candidate);
                score += fromBuddy == int.MaxValue ? 20 : Math.Max(0, fromBuddy - 4);
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    // ---------- Objective drives ----------

    private Plan? ObjectivePlan(InvestigatorState inv)
    {
        string? card = S.Objective.SelectedEscapeCard;
        if (card == null)
        {
            return null;
        }
        return card switch
        {
            "north-gate" or "south-gate" => PowerTheGatePlan(inv),
            "garage" or "sawmill" => FixTheTruckPlan(inv),
            "the-zipper" or "ferris-wheel" => FireTheFlarePlan(inv, card),
            "tunnel-of-love" or "mirror-maze" => ServiceTunnelsPlan(inv),
            "the-grave" => GravePlan(inv),
            "the-eggs" => EggsPlan(inv),
            "the-altar" => AltarPlan(inv),
            _ => null,
        };
    }

    private bool Carries(InvestigatorState inv, string token) =>
        S.Objective.TokenCarriers.TryGetValue(token, out string carrier) && carrier == inv.DefId;

    private bool OnBoard(string token) => S.Objective.Tokens.ContainsKey(token);

    /// <summary>
    /// Objective tokens cannot be traded, so one Investigator has to fetch the whole kit. Pick
    /// whoever already holds part of it, else whoever is closest to the first piece.
    /// </summary>
    private InvestigatorState? Collector(params string[] tokens)
    {
        var alive = Alive;
        if (alive.Count == 0)
        {
            return null;
        }
        var holder = alive.FirstOrDefault(i => tokens.Any(t => Carries(i, t)));
        if (holder != null)
        {
            return holder;
        }
        // Every objective step is an Involved Action, so never hand the job to somebody whose
        // Wounds forbid it (Ergophobia).
        var able = alive.Where(CanTakeInvolved).ToList();
        if (able.Count == 0)
        {
            able = alive;
        }
        // Survival over greed: hand the errand to somebody who can afford to be caught with it.
        var healthy = able.Where(i => i.Wounds.Count <= 1).ToList();
        if (healthy.Count > 0)
        {
            able = healthy;
        }
        string? anchor = tokens.FirstOrDefault(OnBoard);
        if (anchor == null)
        {
            return able[0];
        }
        var dist = Nav.From(_g, S.Objective.Tokens[anchor]);
        return able.OrderBy(i => Nav.Hops(dist, i.Space)).ThenBy(i => i.DefId, StringComparer.Ordinal).First();
    }

    private Plan? PowerTheGatePlan(InvestigatorState inv)
    {
        string? escape = S.Objective.Tokens.TryGetValue("locked-escape", out string e) ? e : null;
        if (S.Objective.EscapeOpen && escape != null)
        {
            return new Plan
            {
                Space = escape,
                Label = "escape-gate",
                Arrive = () => { _g.EscapeThroughGate(); return true; },
            };
        }
        if (S.Objective.Supplies >= 4)
        {
            if (inv.Items.Contains("fuse") && escape != null)
            {
                return new Plan
                {
                    Space = escape,
                    Label = "power-the-gate",
                    Arrive = () => { _g.PowerTheGate(); return true; },
                };
            }
            return escape == null ? null : new Plan { Space = escape, StopAt = 1, Label = "wait-at-gate" };
        }
        if (S.Objective.Tokens.TryGetValue("saw", out string saw))
        {
            if (Carries(inv, "lockbox"))
            {
                // Push your luck only when the Saw is lit: 3+ pays a second Supply there, while
                // in the dark it takes a 5+ and a failure is a face-up Wound.
                bool lit = _g.Graph.EffectiveLight(saw, S.Overlay) == LightLevel.Bright;
                return new Plan
                {
                    Space = saw,
                    Label = "open-lockbox",
                    Arrive = () =>
                    {
                        _g.OpenLockbox(pushYourLuck: lit && inv.Wounds.Count <= 1);
                        return true;
                    },
                };
            }
            if (OnBoard("lockbox") && Collector("lockbox") == inv)
            {
                return new Plan { Space = S.Objective.Tokens["lockbox"], Label = "fetch-lockbox" };
            }
            // Everyone else keeps a beam on the Saw rather than standing at the gate for 9 rounds.
            if (S.Objective.Supplies >= 1)
            {
                return new Plan { Space = saw, StopAt = 1, Label = "light-the-saw" };
            }
        }
        return null; // nothing objective-critical yet: keep working the Evidence economy
    }

    private Plan? FixTheTruckPlan(InvestigatorState inv)
    {
        if (S.Objective.Tokens.TryGetValue("escape", out string exit))
        {
            return new Plan
            {
                Space = exit,
                Label = "escape-truck-exit",
                Arrive = () => { _g.EscapeAtTruckExit(); return true; },
            };
        }
        if (!S.Objective.Tokens.TryGetValue("truck", out string truck))
        {
            return null;
        }
        string[] parts = { "battery", "repair-kit", "spark-plug" };
        string? mine = parts.FirstOrDefault(p => Carries(inv, p));
        if (mine != null)
        {
            string part = mine;
            return new Plan
            {
                Space = truck,
                Label = "install-part",
                Arrive = () => { _g.InstallPart(part); return true; },
            };
        }
        var loose = parts.Where(OnBoard).Select(p => S.Objective.Tokens[p])
            .Where(s => !_claims.Contains(s)).ToList();
        string? fetch = Sticky(inv, loose, null);
        if (fetch != null)
        {
            _claims.Add(fetch);
            return new Plan { Space = fetch, Label = "fetch-part" };
        }

        // Every Part is installed (or unreachable): gather on and around the Truck, then start it.
        bool ready = S.Objective.PartsInstalled >= 3 ||
                     (S.Objective.PartsInstalled >= 1 && S.Round >= _g.Db.Config.Rounds - 3);
        var truckArea = new List<string> { truck };
        truckArea.AddRange(Nav.Neighbors(_g, truck));
        string spot = AssignSpot(inv, truckArea);
        bool everyoneClose = Alive.All(o => o.Space == truck || _g.Graph.Edge(o.Space, truck) != null);
        if (ready && (everyoneClose || S.Round >= _g.Db.Config.Rounds - 1))
        {
            string gate = PickTruckGate(truck);
            return new Plan
            {
                Space = spot,
                Label = "start-truck",
                Arrive = () => { _g.StartTruck(gate); return true; },
            };
        }
        return new Plan { Space = spot, Label = "gather-at-truck" };
    }

    private string PickTruckGate(string truck)
    {
        var dist = Nav.From(_g, truck);
        return Nav.Hops(dist, "10") <= Nav.Hops(dist, "306") ? "10" : "306";
    }

    private Plan? FireTheFlarePlan(InvestigatorState inv, string card)
    {
        string rideId = card == "the-zipper" ? "zipper" : "ferrisWheel";
        var carriages = _g.Graph.Def.Rides[rideId].Carriages.SelectMany(c => c)
            .Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();

        if (S.Objective.EscapeReadyRound != null)
        {
            string seat = AssignSpot(inv, carriages);
            bool ready = S.Round >= S.Objective.EscapeReadyRound.Value;
            return new Plan
            {
                Space = seat,
                Label = "helicopter",
                Arrive = ready ? () => { _g.EscapeByHelicopter(); return true; } : null,
            };
        }
        if (!S.Objective.Tokens.TryGetValue("locked-escape", out string pad))
        {
            return null;
        }
        bool hasGun = Carries(inv, "flare-gun");
        bool hasAmmo = Carries(inv, "ammo-1") || Carries(inv, "ammo-2");
        if (hasGun && hasAmmo)
        {
            return new Plan
            {
                Space = pad,
                Label = "fire-flare",
                Arrive = () => { _g.FireFlareGun(); return true; },
            };
        }
        if (Collector("flare-gun", "ammo-1", "ammo-2") == inv)
        {
            var wanted = new List<string>();
            if (!hasGun && OnBoard("flare-gun"))
            {
                wanted.Add(S.Objective.Tokens["flare-gun"]);
            }
            if (!hasAmmo)
            {
                foreach (string ammo in new[] { "ammo-1", "ammo-2" })
                {
                    if (OnBoard(ammo))
                    {
                        wanted.Add(S.Objective.Tokens[ammo]);
                    }
                }
            }
            string? fetch = Nearest(inv.Space, wanted);
            if (fetch != null)
            {
                return new Plan { Space = fetch, Label = "fetch-flare-kit" };
            }
        }
        return null; // the flare has not been fired: keep working rather than sitting in a carriage
    }

    private Plan? ServiceTunnelsPlan(InvestigatorState inv)
    {
        if (!S.Objective.Tokens.TryGetValue("locked-escape", out string hatch))
        {
            return null;
        }
        if (S.Objective.EscapeReadyRound != null)
        {
            bool ready = S.Round >= S.Objective.EscapeReadyRound.Value;
            return new Plan
            {
                Space = hatch,
                Label = "escape-tunnel",
                Arrive = ready ? () => { _g.EscapeThroughTunnel(); return true; } : null,
                StopAt = ready ? 0 : 1,
            };
        }
        bool hasGrinder = Carries(inv, "angle-grinder");
        bool hasParts = Carries(inv, "ride-parts-1") || Carries(inv, "ride-parts-2");
        if (hasGrinder && hasParts)
        {
            return new Plan
            {
                Space = hatch,
                Label = "open-service-tunnel",
                Arrive = () => { _g.OpenServiceTunnel(); return true; },
            };
        }
        if (Collector("angle-grinder", "ride-parts-1", "ride-parts-2") == inv)
        {
            if (!hasGrinder && OnBoard("angle-grinder"))
            {
                return new Plan { Space = S.Objective.Tokens["angle-grinder"], Label = "fetch-grinder" };
            }
            var parts = new[] { "ride-parts-1", "ride-parts-2" }.Where(OnBoard)
                .Select(p => (Token: p, Space: S.Objective.Tokens[p])).ToList();
            if (!hasParts && parts.Count > 0)
            {
                var pick = parts.OrderBy(p => Nav.Hops(Nav.From(_g, inv.Space), p.Space)).First();
                string tokenName = pick.Token;
                return new Plan
                {
                    Space = pick.Space,
                    Label = "fetch-ride-parts",
                    Arrive = () => { _g.PickUpRideParts(tokenName); return true; },
                };
            }
        }
        return null; // nothing to do at the hatch until the Grinder and Ride Parts are in hand
    }

    private Plan? GravePlan(InvestigatorState inv)
    {
        if (!S.Objective.Tokens.TryGetValue("grave-actual", out string grave))
        {
            return null;
        }
        if (inv.Items.Contains("the-hook"))
        {
            var adjacent = Nav.Neighbors(_g, inv.Space);
            if (adjacent.Count > 0)
            {
                string guess = adjacent[_rng.Next(adjacent.Count)];
                return new Plan
                {
                    Space = inv.Space,
                    Label = "use-the-hook",
                    Arrive = () => { _g.UseTheHook(guess); return true; },
                };
            }
        }
        if (!S.Adversary.Counters.ContainsKey("burning-until"))
        {
            // Digging needs the Grave's space Bright at that moment, and a Flashlight is a Final
            // Action: one Investigator lights it, and the digger — last in seat order, so it acts
            // after the beam is down — takes the Involved Action in the same round.
            var alive = Alive;
            var digger = alive[alive.Count - 1];
            return inv == digger
                ? new Plan
                {
                    Space = grave,
                    Label = "dig-up-grave",
                    Arrive = () => { _g.DigUpGrave(); return true; },
                }
                : new Plan { Space = grave, StopAt = 1, Label = "light-the-grave" };
        }
        return new Plan { Space = grave, StopAt = 1, Label = "wait-for-burn" };
    }

    private Plan? EggsPlan(InvestigatorState inv)
    {
        var sacs = S.Objective.Tokens.Where(kv => kv.Key.StartsWith("eggsac-", StringComparison.Ordinal))
            .Select(kv => kv.Value).Where(s => !_claims.Contains(s)).ToList();
        string? sac = Nearest(inv.Space, sacs);
        if (sac != null)
        {
            _claims.Add(sac);
            return new Plan
            {
                Space = sac,
                Label = "destroy-eggsac",
                Arrive = () => { _g.DestroyEggSac(); return true; },
            };
        }
        bool enraged = S.Adversary.Counters.TryGetValue("enraged", out int e) && e == 1;
        if (enraged)
        {
            return new Plan
            {
                Space = S.Adversary.Space,
                StopAt = 1,
                Label = "banish-the-horror",
                Arrive = () => { _g.BanishTheHorror(); return true; },
            };
        }
        return EvidencePlan(inv);
    }

    private Plan? AltarPlan(InvestigatorState inv)
    {
        if (!S.Objective.Tokens.TryGetValue("altar", out string altar))
        {
            return null;
        }
        int supplies = S.Adversary.Counters.TryGetValue("banish-supplies", out int s) ? s : 0;
        if (supplies >= 3)
        {
            if (Carries(inv, "rope-circle"))
            {
                bool grouped = Alive.All(o => o.Space == altar || _g.Graph.Edge(o.Space, altar) != null);
                return new Plan
                {
                    Space = altar,
                    Label = "cut-rope-circle",
                    Arrive = grouped ? () => { _g.CutRopeCircle(); return true; } : null,
                };
            }
            return new Plan { Space = AssignSpot(inv, Nav.Neighbors(_g, altar)), Label = "gather-at-altar" };
        }
        if (Carries(inv, "ritual-knife"))
        {
            // Standing on the Altar and lighting your own space Reveals it, which latches for
            // the rest of the game; the Knife then works from the next round on.
            return new Plan
            {
                Space = altar,
                Label = "use-ritual-knife",
                Arrive = () => { _g.UseRitualKnife(inv.Wounds.Any(w => !w.FaceUp)); return true; },
            };
        }
        foreach (string token in new[] { "ritual-knife", "rope-circle" })
        {
            if (OnBoard(token) && !_claims.Contains(token))
            {
                _claims.Add(token);
                return new Plan { Space = S.Objective.Tokens[token], Label = "fetch-" + token };
            }
        }
        return null; // the ritual tokens are in other hands: keep working
    }

    /// <summary>Hand out distinct spaces from a shared pool so Investigators do not stack.</summary>
    private string AssignSpot(InvestigatorState inv, List<string> pool)
    {
        var dist = Nav.From(_g, inv.Space);
        var free = pool.Where(p => !_claims.Contains(p) && !Occupied(inv, p))
            .OrderBy(p => Nav.Hops(dist, p)).ThenBy(p => p, StringComparer.Ordinal).ToList();
        string spot = free.Count > 0
            ? free[0]
            : pool.OrderBy(p => Nav.Hops(dist, p)).ThenBy(p => p, StringComparer.Ordinal).First();
        _claims.Add(spot);
        return spot;
    }
}
