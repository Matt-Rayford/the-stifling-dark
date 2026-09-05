using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>
/// One-turn lookahead for the Investigator about to act. The plan machinery names an
/// errand and a destination; this asks the sharper question — of every space reachable
/// this turn, which one leaves the team best off at the end of it? Each candidate is
/// scored on what the turn would actually achieve (progress on the errand, a token picked
/// up, a switch flipped, the beam that could be placed from there) against what it would
/// risk (exposure to wherever the hidden Adversary could be, ending alone in the dark).
///
/// Deliberately runs on PUBLIC information only. A literal engine clone would let a
/// simulated beam Reveal the real hidden figure and the real hidden tokens — the bot
/// would be peeking. So legality and costs come from the engine's own pure rules on
/// hypothetical positions, and every value here is something a human at the table could
/// know: the belief cloud (AdversaryBelief), revealed tokens, known threats.
/// </summary>
public sealed partial class InvestigatorTeam
{
    /// <summary>Off, each turn walks straight at its errand — the arena's A/B baseline.</summary>
    public bool TurnPlannerEnabled { get; set; } = true;

    /// <summary>How many of the best-looking destinations get a full beam sweep; the rest
    /// are ranked on the cheap features alone.</summary>
    private const int PlannerBeamCandidates = 8;

    private const double ArrivalActionValue = 40;
    private const double ErrandFinishValue = 12;
    private const double ProgressPerCost = 4;
    private const double PickUpEvidenceValue = 30;
    private const double PickUpPoiValue = 8;
    private const double PickUpMedicalValue = 5;
    private const double SwitchNowValue = 25;
    private const double SwitchNextTurnValue = 16;
    private const double BankValue = 25;
    private const double BeamShare = 0.3;
    private const double ExposureWeight = 18;
    private const double KnownDangerWeight = 5;
    private const double DarkAloneValue = 4;
    private const double BuddyGapPerHop = 1.5;
    private const double UnusedMpValue = 1.0;

    /// <summary>Space -> how many of the cloud's spaces could reach it in one Adversary
    /// turn. Rebuilt each round with the belief (see <see cref="RefreshExposure"/>).</summary>
    private readonly Dictionary<string, int> _cloudReach = new(StringComparer.Ordinal);
    private int _cloudSize;

    private void RefreshExposure()
    {
        _cloudReach.Clear();
        var cloud = _belief.HiddenUnion();
        _cloudSize = cloud.Count;
        int reach = _belief.Budget;
        foreach (string source in cloud)
        {
            foreach (string space in Nav.From(_g, source, reach).Keys)
            {
                _cloudReach.TryGetValue(space, out int n);
                _cloudReach[space] = n + 1;
            }
        }
    }

    /// <summary>Fraction of the cloud able to reach <paramref name="space"/> next turn —
    /// flat when the team knows nothing, sharp once the cloud has been cornered.</summary>
    private double Exposure(string space) =>
        _cloudSize == 0 ? 0 : (_cloudReach.TryGetValue(space, out int n) ? n : 0) / (double)_cloudSize;

    /// <summary>
    /// Rework this turn's destination. The errand and its stickiness stay the plan's; only
    /// where the figure ends THIS turn is up for revision. A plan whose destination carries
    /// an arrival action keeps it when the planner agrees on the space, and loses it (this
    /// turn) when a better stop is found — the errand itself resumes next round.
    /// </summary>
    private Plan PlanTurn(InvestigatorState inv, Plan? plan)
    {
        var errand = plan ?? new Plan { Space = inv.Space, Label = "idle" };
        if (IsSpirit(inv) || !Active(inv) || !TurnPlannerEnabled)
        {
            return errand;
        }

        var field = CostFrom(inv.Space, inv);
        int budget = inv.MpRemaining + (SprintIsSafe(inv) && !inv.SprintedOrRested ? 2 : 0);
        var toTarget = CostTo(errand.Space, inv);
        int nowToTarget = Nav.Hops(toTarget, inv.Space);
        bool canBeam = !IsSpirit(inv) && inv.FinalAction == FinalActionKind.None &&
                       inv.Charge >= 1 + Math.Max(0, _g.RoundModifier(Game.FlashlightChargeSurchargeKey));
        bool attackImpossible = AttackImpossibleNextTurn();
        var buddy = _threatLevel > 0 || _playbook.BuddyAlways ? Buddy(inv) : null;
        var buddyField = buddy == null ? null : Nav.From(_g, buddy.Space);

        // The stop the errand itself would make this turn: the reachable space closest to
        // the target. Every other stop is measured against it — a detour may cost at most
        // one step of progress unless it achieves something concrete, or a wounded, slow
        // figure circles "good beam spots" for the rest of the game instead of arriving
        // (seed-35 trace, 2026-08-31: eight rounds of never reaching the switch).
        var reachable = field.Where(kv => kv.Value <= budget && (kv.Key == inv.Space || !Occupied(inv, kv.Key))).ToList();
        int plannedRemaining = reachable
            .Select(kv => Nav.Hops(toTarget, kv.Key))
            .DefaultIfEmpty(int.MaxValue).Min();

        var ranked = new List<(string Space, double Score)>();
        foreach (var kv in reachable)
        {
            string space = kv.Key;
            int cost = kv.Value;
            double score = 0;

            // ----- what the turn achieves -----
            int destToTarget = Nav.Hops(toTarget, space);
            if (nowToTarget != int.MaxValue && destToTarget != int.MaxValue)
            {
                score += (nowToTarget - destToTarget) * ProgressPerCost;
            }
            bool reachesTarget = errand.StopAt == 0
                ? space == errand.Space
                : Nav.Hops(Nav.From(_g, errand.Space, errand.StopAt), space) <= errand.StopAt;
            bool concrete = reachesTarget;
            if (reachesTarget)
            {
                score += errand.Arrive != null ? ArrivalActionValue : ErrandFinishValue;
            }
            if (S.Evidence.Any(e => e.Value.Revealed && e.Value.Space == space))
            {
                score += PickUpEvidenceValue;
                concrete = true;
            }
            if (S.PoiTokens.Any(p => p.TokenSpace == space && p.Revealed && !p.Collected))
            {
                score += PickUpPoiValue;
                concrete = true;
            }
            if (S.MedicalItemSpaces.Contains(space))
            {
                score += PickUpMedicalValue;
                concrete = true;
            }
            var def = _g.Graph.Space(space);
            if (def.Kind == SpaceKind.LightSwitch && def.Zone != null &&
                !S.FalteringZones.Contains(def.Zone) && !S.Overlay.BrightZones.Contains(def.Zone) &&
                S.Evidence.TryGetValue(def.Zone, out var hidden) && !hidden.Revealed)
            {
                // Arriving with movement to spare flips the lights now and works the lit
                // Zone; arriving spent means flipping at the start of next turn instead.
                score += inv.MpRemaining - cost >= 2 ? SwitchNowValue : SwitchNextTurnValue;
                concrete = true;
            }
            if (inv.EvidenceCarried.Count > 0 && CanTakeInvolved(inv) && _turnInSpaces.Contains(space) &&
                !reachesTarget)
            {
                score += BankValue;
                concrete = true;
            }
            if (!concrete && destToTarget != int.MaxValue && plannedRemaining != int.MaxValue &&
                destToTarget > plannedRemaining + 1)
            {
                continue; // a sideways stop that achieves nothing
            }

            // ----- what the turn risks -----
            if (!attackImpossible)
            {
                // Exposure only means something once the cloud has been narrowed: against
                // "could be anywhere" every space is equally exposed and the term would
                // merely herd everyone into corners.
                if (_cloudSize > 0 && _cloudSize * 2 <= _g.Graph.Def.Spaces.Count)
                {
                    score -= ExposureWeight * Exposure(space) * (1 + inv.Wounds.Count);
                }
                score -= KnownDangerWeight * Danger(space);
                bool dark = _g.Graph.EffectiveLight(space, S.Overlay) == LightLevel.Dark;
                bool alone = !Alive.Any(o => o != inv && Nav.Hops(Nav.From(_g, space, 2), o.Space) <= 2);
                if (dark && alone)
                {
                    score -= _threatLevel > 0 ? DarkAloneValue * 2 : DarkAloneValue;
                }
            }
            if (buddyField != null)
            {
                int gap = Nav.Hops(buddyField, space);
                score -= BuddyGapPerHop * Math.Max(0, (gap == int.MaxValue ? 12 : gap) - 4);
            }
            score -= UnusedMpValue * Math.Max(0, inv.MpRemaining - cost);

            ranked.Add((space, score));
        }
        if (ranked.Count == 0)
        {
            return errand;
        }

        // Light is the expensive feature: sweep it only for the short list, always
        // including where the plan itself would stop.
        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        var shortList = ranked.Take(PlannerBeamCandidates).ToList();
        var planned = ranked.FirstOrDefault(r => r.Space == errand.Space);
        if (planned.Space != null && shortList.All(r => r.Space != planned.Space))
        {
            shortList.Add(planned);
        }
        string best = errand.Space;
        double bestScore = double.NegativeInfinity;
        foreach (var (space, score) in shortList)
        {
            double total = score;
            if (canBeam)
            {
                total += BeamShare * BestFlashlightAngle(inv, avoid: null, at: space).Score;
            }
            if (total > bestScore)
            {
                bestScore = total;
                best = space;
            }
        }

        if (best == errand.Space)
        {
            return errand;
        }
        return new Plan { Space = best, Label = errand.Label + "~sim" };
    }
}
