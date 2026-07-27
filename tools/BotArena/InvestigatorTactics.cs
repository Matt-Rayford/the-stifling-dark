using System.Globalization;
using StiflingDark.Engine.Core;

namespace BotArena;

/// <summary>
/// The designer's tactical layer: turtling (lock the doors, light the remaining entrances),
/// Item usage, Spirit play, the sacrificial screen, and the turn-order choice. Kept apart from
/// the goal/pathing logic in InvestigatorTeam.cs purely so both files stay readable.
/// </summary>
public sealed partial class InvestigatorTeam
{
    // ---------- Turn order (a choice the team makes every round) ----------

    /// <summary>
    /// Who acts next. Scouts and beamers go first so their light is already on the board when
    /// the objective runners move through it; the Investigator carrying the win condition goes
    /// last, inside the cover everybody else just laid down. Ties break toward the healthiest,
    /// who are the ones that should be walking into the unknown.
    /// </summary>
    public string? NextToAct()
    {
        var pending = S.Investigators
            .Where(i => !i.TurnTakenThisRound && !i.Escaped && (!i.Dead || i.SpiritId != null))
            .ToList();
        if (pending.Count == 0)
        {
            return null;
        }
        return pending
            .OrderBy(TurnOrderRank)
            .ThenBy(i => i.Wounds.Count)
            .ThenBy(i => i.DefId, StringComparer.Ordinal)
            .First()
            .DefId;
    }

    private int TurnOrderRank(InvestigatorState inv)
    {
        // 0: Spirits — they cannot be hurt, so they scout and light first of all.
        if (IsSpirit(inv))
        {
            return 0;
        }
        bool runner = inv.EvidenceCarried.Count > 0 ||
                      S.Objective.TokenCarriers.Any(kv => kv.Value == inv.DefId) ||
                      inv.Items.Contains("fuse") ||
                      inv.Items.Contains("the-hook");
        if (runner)
        {
            return 3; // acts last, once the board is lit
        }
        int cost = 1 + Math.Max(0, _g.RoundModifier(Game.FlashlightChargeSurchargeKey));
        bool canLight = inv.Charge >= cost || inv.Items.Contains("lantern") ||
                        inv.Items.Contains("emergency-flare");
        return canLight ? 1 : 2;
    }

    // ---------- Turtling ----------

    /// <summary>
    /// Deny the Adversary any way into the group's area for a round: Lock every Open Door
    /// beside you (a Locked Door costs them their once-per-turn Break Door, and twice over if
    /// they have to go through Damaged first), wedge a Security Bar into one that is already
    /// shut, and pour Kerosene across the gaps that have no door at all. What is left over is
    /// what the Flashlight has to cover, which is what <see cref="Entrances"/> feeds the beam
    /// scorer.
    /// </summary>
    private void Turtle(InvestigatorState inv)
    {
        // Only turtle when the Adversary was last seen within 2 spaces. Locking Doors all over
        // the map as the team walks past them just makes the team's own pathing longer.
        if (IsSpirit(inv) || Danger(inv.Space) < 2)
        {
            return;
        }
        foreach (string door in Nav.Neighbors(_g, inv.Space))
        {
            if (_g.Graph.Space(door).Kind != SpaceKind.Door)
            {
                continue;
            }
            var state = S.Overlay.DoorState(door);
            if (state == DoorState.Open && !Alive.Any(o => o != inv && o.Space == door))
            {
                string target = door;
                _act.Try("lock-door", () => _g.LockDoor(target));
                state = S.Overlay.DoorState(door);
            }
            if ((state == DoorState.Locked || state == DoorState.Damaged) && inv.Items.Contains("security-bar"))
            {
                string target = door;
                _act.Try("security-bar", () => _g.UseItem("security-bar", new List<string> { target }));
            }
        }
        if (inv.Items.Contains("kerosene"))
        {
            var pair = KeroseneGap(inv);
            if (pair != null)
            {
                _act.Try("kerosene", () => _g.UseItem("kerosene", new List<string> { pair.Value.A, pair.Value.B }));
            }
        }
    }

    /// <summary>Two spaces adjacent to me and to each other, as close to the threat as possible.</summary>
    private (string A, string B)? KeroseneGap(InvestigatorState inv)
    {
        var neighbours = Nav.Neighbors(_g, inv.Space).OrderByDescending(Danger)
            .ThenBy(n => n, StringComparer.Ordinal).ToList();
        foreach (string a in neighbours)
        {
            foreach (string b in neighbours)
            {
                if (a != b && _g.Graph.Edge(a, b) != null)
                {
                    return (a, b);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The spaces an Adversary could still step onto to reach anyone in the huddle: everything
    /// adjacent to a group member that is not already sealed by a Door token. A beam over these
    /// Reveals whatever walks in, and a Revealed Adversary may not Attack for the rest of the
    /// round — that is the whole trick.
    /// </summary>
    private HashSet<string> Entrances(IEnumerable<InvestigatorState> group)
    {
        var inside = group.Select(i => i.Space).ToHashSet();
        var open = new HashSet<string>();
        foreach (string space in inside)
        {
            foreach (string neighbour in Nav.Neighbors(_g, space))
            {
                if (!inside.Contains(neighbour) && S.Overlay.DoorState(neighbour) == DoorState.Open)
                {
                    open.Add(neighbour);
                }
            }
        }
        return open;
    }

    // ---------- Items ----------

    /// <summary>
    /// Drawn Items are not souvenirs. Light sources beat the Charge economy outright (a Lantern
    /// or an Emergency Flare costs no Charge and is not the Final Action), resource items undo a
    /// bad round, and the denial items are what makes turtling stick.
    /// </summary>
    private void UseTacticalItems(InvestigatorState inv)
    {
        if (!Active(inv) || IsSpirit(inv))
        {
            return;
        }
        UseMedkits(inv);

        // Convert an incoming face-up Wound before it bites.
        var faceUp = inv.Wounds.FirstOrDefault(w => w.FaceUp);
        if (faceUp != null && inv.Items.Contains("leather-jacket"))
        {
            _act.Try("leather-jacket", () => _g.UseItem("leather-jacket", new List<string> { faceUp.CardId }));
        }
        faceUp = inv.Wounds.FirstOrDefault(w => w.FaceUp);
        if (faceUp != null && inv.Items.Contains("tourniquet"))
        {
            _act.Try("tourniquet", () => _g.UseItem("tourniquet", new List<string> { faceUp.CardId }));
        }
        if (inv.Items.Contains("painkillers") && inv.Wounds.Any(w => w.FaceUp))
        {
            UsePainkillers(inv);
        }

        // Resource top-ups.
        // Spare Tools turns the batch turn-in into an Interact, so the runner keeps their turn.
        if (inv.Items.Contains("spare-tools") && WantsInvolvedAction(inv) &&
            !_g.HasRoundModifier(Game.InvolvedAsInteractPrefix + inv.DefId))
        {
            _act.Try("spare-tools", () => _g.UseItem("spare-tools"));
        }
        if (inv.Charge <= 1 && inv.Items.Contains("fresh-batteries"))
        {
            _act.Try("fresh-batteries", () => _g.UseItem("fresh-batteries"));
        }
        if (inv.Stamina <= 2 && inv.Items.Contains("energy-bar"))
        {
            _act.Try("energy-bar", () => _g.UseItem("energy-bar"));
        }

        if (_threatLevel == 0)
        {
            return;
        }

        // Light without paying Charge, aimed at the ring the Adversary has to cross.
        string? hot = Nav.Neighbors(_g, inv.Space)
            .OrderByDescending(Danger).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        if (hot != null && inv.Items.Contains("lantern"))
        {
            string spot = hot;
            _act.Try("lantern", () => _g.UseItem("lantern", new List<string> { spot }));
        }
        if (hot != null && inv.Items.Contains("emergency-flare"))
        {
            string spot = hot;
            double angle = BestFlashlight(inv).Angle;
            _act.Try("emergency-flare", () => _g.UseItem("emergency-flare",
                new List<string> { spot, angle.ToString("R", CultureInfo.InvariantCulture) }));
        }
        if (inv.Items.Contains("cross"))
        {
            _act.Try("cross", () => _g.UseItem("cross"));
        }
        // Firecrackers drag every Adversary figure 2 spaces toward a point — so pick the point
        // furthest from the group, not nearest.
        if (inv.Items.Contains("firecrackers"))
        {
            var reach = Nav.From(_g, inv.Space, 3);
            string? decoy = reach.Keys.Where(k => k != inv.Space)
                .OrderBy(Danger).ThenByDescending(k => Nav.Hops(reach, k))
                .ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (decoy != null)
            {
                string bang = decoy;
                _act.Try("firecrackers", () => _g.UseItem("firecrackers", new List<string> { bang }));
            }
        }
    }

    /// <summary>Swap our worst face-up Wound for the least bad of the two drawn.</summary>
    private void UsePainkillers(InvestigatorState inv)
    {
        if (!_act.Try("painkillers", () => _g.UseItem("painkillers")))
        {
            return;
        }
        string? marker = inv.Items.FirstOrDefault(i => i.StartsWith("marker:painkillers:", StringComparison.Ordinal));
        if (marker == null)
        {
            return;
        }
        var parts = marker.Split(':');
        var drawn = new[] { parts[2], parts[3] }.OrderBy(WoundRank).ToList();
        string? worst = inv.Wounds.Where(w => w.FaceUp).OrderByDescending(w => WoundRank(w.CardId))
            .Select(w => w.CardId).FirstOrDefault();
        if (worst != null && WoundRank(worst) > WoundRank(drawn[0]))
        {
            string keep = drawn[0];
            string replace = worst;
            _act.Try("resolve-painkillers", () => _g.ResolvePainkillers(replace, keep));
        }
        else
        {
            _act.Try("resolve-painkillers", () => _g.ResolvePainkillers(null, null));
        }
    }

    private static int WoundRank(string cardId)
    {
        int index = Array.IndexOf(WoundTreatmentOrder, cardId);
        return index < 0 ? 0 : WoundTreatmentOrder.Length - index;
    }

    /// <summary>Items that buy MP: worth it only when the goal is genuinely out of reach.</summary>
    private void UseTravelItems(InvestigatorState inv, Plan plan)
    {
        if (IsSpirit(inv) || !Active(inv))
        {
            return;
        }
        int cost = Nav.Hops(CostTo(plan.Space), inv.Space);
        if (cost == int.MaxValue || cost <= inv.MpRemaining)
        {
            return;
        }
        if (!inv.SprintedOrRested && inv.Items.Contains("adrenaline-shot"))
        {
            _act.Try("adrenaline-shot", () => _g.UseItem("adrenaline-shot"));
        }
        if (inv.Items.Contains("energy-drink"))
        {
            _act.Try("energy-drink", () => _g.UseItem("energy-drink"));
        }
        // Glowstick / Torch turn Dark spaces Dim, halving what the next few steps cost.
        bool darkAhead = _g.Graph.EffectiveLight(inv.Space, S.Overlay) == LightLevel.Dark ||
                         Nav.Neighbors(_g, inv.Space).Any(n => _g.Graph.EffectiveLight(n, S.Overlay) == LightLevel.Dark);
        if (darkAhead && inv.Items.Contains("glowstick"))
        {
            string spot = inv.Space;
            _act.Try("glowstick", () => _g.UseItem("glowstick", new List<string> { spot }));
        }
        if (darkAhead && inv.Items.Contains("torch"))
        {
            _act.Try("torch", () => _g.UseItem("torch"));
        }
    }

    /// <summary>Whistle pulls scattered team-mates two spaces closer — the cheapest regroup there is.</summary>
    private void UseRegroupItems(InvestigatorState inv)
    {
        if (_threatLevel == 0 || IsSpirit(inv) || !inv.Items.Contains("whistle"))
        {
            return;
        }
        var dist = Nav.From(_g, inv.Space, 8);
        var strays = Alive.Where(o => o != inv && Nav.Hops(dist, o.Space) is int d && d >= 2 && d <= 8)
            .OrderByDescending(o => Nav.Hops(dist, o.Space))
            .ThenBy(o => o.DefId, StringComparer.Ordinal)
            .Take(2).Select(o => o.DefId).ToList();
        if (strays.Count > 0)
        {
            _act.Try("whistle", () => _g.UseItem("whistle", strays));
        }
    }

    // ---------- Spirits ----------

    /// <summary>
    /// A dead Investigator whose player takes a Spirit card keeps playing: 4 MP, a free Sprint
    /// every round, no Wound slots to fill, and it walks through Locked Doors, Dark spaces and
    /// Map Hazards alike. It has no Wound slots to fill and cannot itself escape, but it can
    /// still scout, light, carry Evidence and drive objective steps toward the living team's
    /// escape (a death no longer caps the outcome at a Draw), so there is never a reason to
    /// decline one.
    /// </summary>
    public void AdoptSpiritsIfOffered()
    {
        foreach (var inv in S.Investigators.Where(i => i.Dead && i.SpiritId == null))
        {
            var free = _g.UnusedSpiritIds();
            if (free.Count == 0 || S.Phase == GamePhase.GameOver)
            {
                return;
            }
            string pick = free[0];
            string who = inv.DefId;
            _act.Try("adopt-spirit", () => _g.AdoptSpirit(who, pick));
        }
    }

    /// <summary>Up to 2 Spirit Abilities a turn; Minors are free, Majors spend a token that never returns.</summary>
    private void UseSpiritAbilities(InvestigatorState inv)
    {
        if (!IsSpirit(inv) || !Active(inv))
        {
            return;
        }
        // Energy Transfer hands every Investigator a Charge — the whole team's beam budget for
        // a round, for one of the two Major tokens. Worth it once the living are running dry.
        if (inv.SpiritMajorTokens > 0 && Alive.Count(o => o.Charge == 0) >= Math.Max(1, Alive.Count - 1))
        {
            _act.Try("spirit-energy-transfer", () => _g.UseSpiritAbility("energy-transfer"));
        }
        string? hot = Nav.Neighbors(_g, inv.Space)
            .OrderByDescending(Danger).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        if (hot != null)
        {
            string spot = hot;
            _act.Try("spirit-ghost-orbs", () => _g.UseSpiritAbility("ghost-orbs", new List<string> { spot }));
        }
        foreach (string ability in new[] { "emergency-lights", "cold-spot", "clairvoyance", "whirlwind" })
        {
            string name = ability;
            if (inv.SpiritAbilitiesUsedThisTurn >= Game.SpiritAbilitiesPerTurn)
            {
                return;
            }
            _act.Try("spirit-" + name, () => _g.UseSpiritAbility(name));
        }
    }

    // ---------- Bulk Evidence turn-ins ----------

    /// <summary>
    /// Designer-confirmed: human teams cash in 2-3 Evidence per trip, not one. The team picks a
    /// runner each round — whoever already carries the most, breaking ties toward whoever is
    /// closest to a Computer / Ticket Booth — and everyone else's job is to get tokens into
    /// that runner's hands rather than making their own walk to a feature.
    /// </summary>
    private void ElectRunner()
    {
        var carriers = Alive.Where(i => i.EvidenceCarried.Count > 0 && CanTakeInvolved(i)).ToList();
        if (carriers.Count == 0)
        {
            _runner = null;
            return;
        }
        _runner = carriers
            .OrderByDescending(i => i.EvidenceCarried.Count)
            .ThenBy(i => _turnInSpaces.Count == 0
                ? 0
                : _turnInSpaces.Min(f => Nav.Hops(CostFrom(i.Space, i), f)))
            .ThenBy(i => i.DefId, StringComparer.Ordinal)
            .First().DefId;
    }

    private int EvidenceStillNeeded()
    {
        int required = _g.Db.Config.ByInvestigatorCount[S.Investigators.Count].EvidenceRequiredForObjective;
        return Math.Max(0, required - S.Objective.EvidenceTurnedIn);
    }

    /// <summary>
    /// Hold the batch until it is worth the Involved Action: three tokens, or enough to clear
    /// the gate outright, or nothing else left within reach, or the clock forcing the issue.
    /// </summary>
    private bool ShouldTurnInNow(InvestigatorState inv)
    {
        int carried = inv.EvidenceCarried.Count;
        if (carried == 0)
        {
            return false;
        }
        int needed = EvidenceStillNeeded();
        if (carried >= needed || carried >= 3)
        {
            return true;
        }
        // Batching only pays when the gate is bigger than the party: if the team already holds
        // enough tokens between them, everybody cashing in their own is strictly faster than
        // funnelling them through one runner.
        if (Alive.Sum(i => i.EvidenceCarried.Count) >= needed)
        {
            return true;
        }
        if (S.Round >= _g.Db.Config.Rounds - 4 || S.Objective.SelectedEscapeCard != null)
        {
            return true; // out of time to be tidy, or the Evidence economy is over anyway
        }
        // Never die holding the batch: Evidence carried by a corpse is Evidence the team never
        // turned in, so bank it as soon as this Investigator is the one under threat.
        if (inv.Wounds.Count >= 2 && Danger(inv.Space) > 0)
        {
            return true;
        }
        // Anything else still worth collecting before the trip?
        var costs = CostFrom(inv.Space, inv);
        var loose = S.Evidence.Where(kv => kv.Value.Revealed).Select(kv => kv.Value.Space).ToList();
        var switches = _g.Graph.Def.Spaces
            .Where(sp => sp.Kind == SpaceKind.LightSwitch && sp.Zone != null &&
                         !S.FalteringZones.Contains(sp.Zone) &&
                         S.Evidence.TryGetValue(sp.Zone, out var e) && !e.Revealed)
            .Select(sp => sp.Id);
        int nearestMore = loose.Concat(switches).Select(sp => Nav.Hops(costs, sp))
            .DefaultIfEmpty(int.MaxValue).Min();
        int nearestFeature = _turnInSpaces.Select(f => Nav.Hops(costs, f))
            .DefaultIfEmpty(int.MaxValue).Min();
        if (nearestMore == int.MaxValue)
        {
            return true;
        }
        // Only detour for another token while the detour stays cheaper than a second whole trip.
        return nearestMore > nearestFeature + 10;
    }

    /// <summary>
    /// Ferrying: an Investigator who is not the runner walks their tokens to the runner instead
    /// of making a second trip to a feature. Ibraheem can do it from 5 spaces away with his
    /// Major, which is the difference between a hand-off and a two-round detour.
    /// </summary>
    private Plan? FerryPlan(InvestigatorState inv)
    {
        if (inv.EvidenceCarried.Count == 0)
        {
            return null;
        }
        // A face-up Ergophobia bars the Involved Action for good: whatever this Investigator is
        // carrying has to change hands or it never gets turned in.
        bool mustPass = !CanTakeInvolved(inv);
        if (!mustPass && (_runner == null || _runner == inv.DefId))
        {
            return null;
        }
        var courier = Alive.FirstOrDefault(o => o.DefId == _runner && o != inv &&
                                                _g.ActionBlockers(o.DefId, Game.ActionTrade).Count == 0)
                      ?? (mustPass
                          ? Alive.Where(o => o != inv && CanTakeInvolved(o) &&
                                             _g.ActionBlockers(o.DefId, Game.ActionTrade).Count == 0)
                              .OrderBy(o => Nav.Hops(CostFrom(inv.Space, inv), o.Space))
                              .ThenBy(o => o.DefId, StringComparer.Ordinal)
                              .FirstOrDefault()
                          : null);
        if (courier == null)
        {
            return null;
        }
        var costs = CostFrom(inv.Space, inv);
        int toCourier = Nav.Hops(costs, courier.Space);
        int toFeature = _turnInSpaces.Select(f => Nav.Hops(costs, f)).DefaultIfEmpty(int.MaxValue).Min();
        if (!mustPass && toCourier + 3 >= toFeature)
        {
            return null; // the hand-off is not actually saving the team a trip
        }
        var zones = inv.EvidenceCarried.ToList();
        string to = courier.DefId;
        return new Plan
        {
            Space = courier.Space,
            StopAt = 1,
            Label = "ferry-evidence",
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

    // ---------- Sacrificial screen ----------

    /// <summary>
    /// It is right to let the healthy one take the hit. When a team-mate is two Wounds from the
    /// skull and the Adversary's last known position is close, the least-wounded Investigator
    /// steps into the gap between them: an Adversary that can only reach the screen spends its
    /// Attack on somebody who can afford it, and the wounded one gets a round to break away.
    /// </summary>
    private Plan? ScreenPlan(InvestigatorState inv)
    {
        if (IsSpirit(inv) || inv.Wounds.Count > 1 || _threatLevel == 0)
        {
            return null;
        }
        var casualty = Alive
            .Where(o => o != inv && o.Wounds.Count >= 2 && Danger(o.Space) > 0)
            .OrderByDescending(o => o.Wounds.Count)
            .ThenBy(o => o.DefId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (casualty == null)
        {
            return null;
        }
        // Only the healthiest bystander screens, or the whole team piles into the same fire.
        // Asher ignores the face-up Wound in his first slot, so he is the body that should be
        // standing in the way when there is a choice.
        var healthier = Alive.Where(o => o != casualty && !IsSpirit(o) && o.Wounds.Count <= 1)
            .OrderBy(ScreenPreference).ThenBy(o => o.Wounds.Count)
            .ThenBy(o => o.DefId, StringComparer.Ordinal).ToList();
        if (healthier.Count == 0 || healthier[0] != inv)
        {
            return null;
        }
        // Stand on the hottest space next to them: whatever comes for the casualty meets us first.
        string? post = Nav.Neighbors(_g, casualty.Space)
            .Where(n => !Occupied(inv, n))
            .OrderByDescending(Danger)
            .ThenBy(n => Nav.Hops(CostFrom(inv.Space), n))
            .ThenBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
        return post == null ? null : new Plan { Space = post, Label = "screen-" + casualty.DefId };
    }
}
