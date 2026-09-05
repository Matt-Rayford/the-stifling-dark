using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>
/// The Evidence economy as a TEAM problem. Each round the open jobs — collect a revealed
/// token, reveal a Zone's hidden token, bank a batch, fetch an optional item — are handed
/// out in one assignment that minimises the team's total walking, instead of each
/// Investigator grabbing the nearest errand in seat order and two of them crossing paths
/// to the same switch. Hidden tokens are found mostly by BEAMS: every space lit while a
/// Zone's token was still hidden is one the token cannot be on, so the search narrows
/// with each sweep, and the walk to the switch is only worth making when it is short.
/// (Measured before this: 36% of all Investigator turns were switch trips, 5.4 turns per
/// flip — designer review 2026-08-31.)
/// </summary>
public sealed partial class InvestigatorTeam
{
    private enum ErrandKind { Collect, Reveal, Support, Poi, Medical }

    private sealed class Errand
    {
        public ErrandKind Kind;
        /// <summary>The token space, the Zone letter, or the item space.</summary>
        public string Target = "";
        /// <summary>Where the walk actually goes (a Zone's hub or switch, or the token).</summary>
        public string Space = "";
        /// <summary>Added to the movement cost so optional errands come last.</summary>
        public int Offset;
        public bool Shared;

        public string Key => Kind + ":" + Target;
    }

    private readonly Dictionary<string, Errand> _errand = new();
    /// <summary>Spaces that were Bright while their Zone's Evidence was still hidden — the
    /// token was not there, or it would have been Revealed.</summary>
    private readonly HashSet<string> _sweptWhileHidden = new();

    private AdversaryPlaybook Playbook => _playbook;

    /// <summary>Note every space light has already ruled out for a still-hidden token.</summary>
    private void RecordSweeps()
    {
        foreach (var kv in S.Evidence)
        {
            if (kv.Value.Revealed)
            {
                continue;
            }
            string zone = kv.Key;
            if (S.Overlay.BrightZones.Contains(zone))
            {
                foreach (var space in _g.Graph.ZoneSpaces(zone))
                {
                    _sweptWhileHidden.Add(space.Id);
                }
                continue;
            }
            foreach (string space in S.Overlay.BrightSpaces)
            {
                if (_g.Graph.Space(space).Zone == zone)
                {
                    _sweptWhileHidden.Add(space);
                }
            }
        }
    }

    /// <summary>Spaces a beam could still find a hidden token on.</summary>
    private HashSet<string> EvidenceSearchSpaces()
    {
        var search = new HashSet<string>();
        foreach (var kv in S.Evidence)
        {
            if (kv.Value.Revealed)
            {
                continue;
            }
            foreach (var space in _g.Graph.ZoneSpaces(kv.Key))
            {
                if (space.Kind == SpaceKind.Normal && !_sweptWhileHidden.Contains(space.Id))
                {
                    search.Add(space.Id);
                }
            }
        }
        return search;
    }

    // ---------- Assignment ----------

    private bool ClockCrunch => _g.Db.Config.Rounds - S.Round <= Playbook.ClockCrunchRounds;

    private void AssignErrands()
    {
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _errand)
        {
            previous[kv.Key] = kv.Value.Key;
        }
        _errand.Clear();

        var jobs = OpenErrands();
        var workers = Alive.Where(i => !HasStandingEvidenceDuty(i)).ToList();
        if (jobs.Count == 0 || workers.Count == 0)
        {
            return;
        }

        // Cost of every worker on every job, up front: the search below is tiny (≤4 workers,
        // a dozen jobs) but each cost is a Dijkstra field.
        var cost = new Dictionary<(string Inv, string Job), int>();
        foreach (var inv in workers)
        {
            var field = CostFrom(inv.Space, inv);
            var buddy = _threatLevel > 0 || Playbook.BuddyAlways ? Buddy(inv) : null;
            var buddyField = buddy == null ? null : Nav.From(_g, buddy.Space);
            foreach (var job in jobs)
            {
                if (!Eligible(inv, job))
                {
                    continue;
                }
                int walk = Nav.Hops(field, job.Space);
                if (walk == int.MaxValue)
                {
                    continue;
                }
                int c = walk + job.Offset + ErrandPreference(inv);
                if (buddyField != null)
                {
                    int fromBuddy = Nav.Hops(buddyField, job.Space);
                    c += fromBuddy == int.MaxValue ? 10 : Math.Max(0, fromBuddy - 4);
                }
                // Hysteresis: last round's errand is worth several steps, or the turn order
                // changing round to round makes the team swap jobs mid-walk.
                if (previous.TryGetValue(inv.DefId, out string? was) && was == job.Key)
                {
                    c -= 5;
                }
                cost[(inv.DefId, job.Key)] = c;
            }
        }

        var best = new Dictionary<string, Errand>();
        int bestTotal = int.MaxValue;
        var current = new Dictionary<string, Errand>();
        Search(0);
        foreach (var kv in best)
        {
            _errand[kv.Key] = kv.Value;
        }

        void Search(int index)
        {
            if (index == workers.Count)
            {
                int total = current.Sum(kv => cost[(kv.Key, kv.Value.Key)]);
                // More workers on real jobs beats a cheaper partial assignment.
                total -= current.Count * 100;
                if (total < bestTotal)
                {
                    bestTotal = total;
                    best = new Dictionary<string, Errand>(current);
                }
                return;
            }
            var inv = workers[index];
            bool any = false;
            foreach (var job in jobs)
            {
                if (!cost.ContainsKey((inv.DefId, job.Key)))
                {
                    continue;
                }
                if (!job.Shared && current.Values.Any(e => e.Key == job.Key))
                {
                    continue;
                }
                any = true;
                current[inv.DefId] = job;
                Search(index + 1);
                current.Remove(inv.DefId);
            }
            if (!any)
            {
                Search(index + 1);
            }
        }
    }

    /// <summary>Already committed by state: carrying a batch worth banking, or ferrying.</summary>
    private bool HasStandingEvidenceDuty(InvestigatorState inv) =>
        inv.EvidenceCarried.Count > 0 &&
        (IsSpirit(inv) || !CanTakeInvolved(inv) || ShouldTurnInNow(inv));

    private bool Eligible(InvestigatorState inv, Errand job) => job.Kind switch
    {
        ErrandKind.Collect => !IsSpirit(inv),
        ErrandKind.Poi => !IsSpirit(inv),
        ErrandKind.Medical => !IsSpirit(inv),
        _ => true,
    };

    private List<Errand> OpenErrands()
    {
        var jobs = new List<Errand>();
        foreach (var kv in S.Evidence)
        {
            if (kv.Value.Revealed)
            {
                jobs.Add(new Errand { Kind = ErrandKind.Collect, Target = kv.Value.Space, Space = kv.Value.Space });
            }
            else if (_zoneHub.TryGetValue(kv.Key, out string? hub))
            {
                jobs.Add(new Errand { Kind = ErrandKind.Reveal, Target = kv.Key, Space = RevealApproach(kv.Key, hub), Offset = 2 });
            }
        }
        // Spare hands close up on an open Zone: their beams sweep it on the way in, and
        // whoever flips the switch is not the only one who can carry the token out. Priced
        // above every real errand so it only ever soaks up genuinely spare hands.
        foreach (var job in jobs.Where(j => j.Kind == ErrandKind.Reveal).ToList())
        {
            jobs.Add(new Errand { Kind = ErrandKind.Support, Target = job.Target, Space = job.Space, Offset = 9, Shared = true });
        }
        if (!ClockCrunch && S.Objective.SelectedEscapeCard == null)
        {
            foreach (var poi in S.PoiTokens.Where(p => p.Revealed && !p.Collected))
            {
                jobs.Add(new Errand { Kind = ErrandKind.Poi, Target = poi.TokenSpace, Space = poi.TokenSpace, Offset = 4 });
            }
            foreach (string medical in S.MedicalItemSpaces)
            {
                jobs.Add(new Errand { Kind = ErrandKind.Medical, Target = medical, Space = medical, Offset = 6 });
            }
        }
        return jobs;
    }

    /// <summary>Where a "reveal this Zone" errand walks: the switch while it is worth
    /// flipping, else the Zone's hub so the approach (and its beams) still happens.</summary>
    private string RevealApproach(string zone, string hub)
    {
        var lightSwitch = _g.Graph.ZoneSpaces(zone).FirstOrDefault(s => s.Kind == SpaceKind.LightSwitch);
        bool flippable = lightSwitch != null && !S.FalteringZones.Contains(zone) &&
                         !S.Overlay.BrightZones.Contains(zone);
        return flippable ? lightSwitch!.Id : hub;
    }

    /// <summary>The plan an assigned errand becomes for this Investigator right now.</summary>
    private Plan? ErrandPlan(InvestigatorState inv)
    {
        if (!_errand.TryGetValue(inv.DefId, out var job))
        {
            return null;
        }
        switch (job.Kind)
        {
            case ErrandKind.Collect:
                if (!S.Evidence.Any(kv => kv.Value.Revealed && kv.Value.Space == job.Target))
                {
                    return null; // somebody else got there first
                }
                _claims.Add(job.Target);
                return new Plan { Space = job.Target, Label = "collect-evidence" };
            case ErrandKind.Reveal:
            case ErrandKind.Support:
            {
                if (!S.Evidence.TryGetValue(job.Target, out var token) || token.Revealed)
                {
                    return null;
                }
                var lightSwitch = _g.Graph.ZoneSpaces(job.Target).FirstOrDefault(s => s.Kind == SpaceKind.LightSwitch);
                bool flippable = lightSwitch != null && !S.FalteringZones.Contains(job.Target) &&
                                 !S.Overlay.BrightZones.Contains(job.Target);
                if (flippable)
                {
                    // Walk straight at the switch: the approach IS the sweep (every beam on
                    // the way in narrows where the token can be, and a mid-walk find
                    // redirects the walk — see Travel), so a hub stop first only cost a round.
                    // Support hands walk the same way: whoever arrives first flips, and a
                    // spare hand standing at a hub was a spare hand doing nothing (measured
                    // 12% of all turns, 2026-08-31).
                    _claims.Add(job.Target);
                    return new Plan
                    {
                        Space = lightSwitch!.Id,
                        Label = job.Kind == ErrandKind.Reveal ? "light-switch" : "support-zone",
                    };
                }
                string hub = _zoneHub.TryGetValue(job.Target, out string? h) ? h : job.Space;
                return new Plan
                {
                    Space = hub,
                    StopAt = 1,
                    Label = job.Kind == ErrandKind.Reveal ? "stage-for-evidence" : "support-zone",
                };
            }
            case ErrandKind.Poi:
                if (!S.PoiTokens.Any(p => p.TokenSpace == job.Target && p.Revealed && !p.Collected))
                {
                    return null;
                }
                _claims.Add(job.Target);
                return new Plan { Space = job.Target, Label = "collect-poi" };
            case ErrandKind.Medical:
                if (!S.MedicalItemSpaces.Contains(job.Target))
                {
                    return null;
                }
                _claims.Add(job.Target);
                return new Plan { Space = job.Target, Label = "collect-medical" };
            default:
                return null;
        }
    }
}
