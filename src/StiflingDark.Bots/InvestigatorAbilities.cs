using System.Globalization;
using StiflingDark.Engine.Core;

namespace StiflingDark.Bots;

/// <summary>
/// Composition-aware play of the ten printed Investigator Abilities. The passive Minors need
/// nothing but a pathing/threshold that knows about them (Dylan's Dark shortcuts, Ibraheem's
/// Footprint floor, Marci's Stamina track, Asher's ignored first Wound, Lucy's Sprint); the
/// activated Minors are free and fire whenever they help; the Majors are one token each, so
/// they are spent on the three things a token is worth — a defensive panic button, a tempo
/// surge on the objective, or a piece of utility nothing else can buy.
/// </summary>
public sealed partial class InvestigatorTeam
{
    /// <summary>Def ids whose Minor Ability is passive: nothing to call, but plenty to exploit.</summary>
    private static bool HasPassiveMinor(string defId) =>
        defId is "asher" or "dylan" or "ibraheem" or "lucy-belle" or "marci";

    // ---------- Passive Minors, exploited ----------

    /// <summary>Dylan treats up to 3 Dark spaces a turn as Dim, so he should be the one routed
    /// through the dark: price his paths that way and he takes shortcuts nobody else can.</summary>
    private static bool RoutesThroughDark(InvestigatorState inv) => inv.DefId == "dylan";

    /// <summary>
    /// Sprinting is safe while the Stamina space you land on carries no Wound icon. That is
    /// space 2 for everybody except Marci Jo, whose track only marks space 0 — so she can run
    /// herself down to 1 without paying for it.
    /// </summary>
    private bool SprintIsSafe(InvestigatorState inv)
    {
        if (IsSpirit(inv))
        {
            return true;
        }
        var track = _g.Db.Investigator(inv.DefId).StaminaTrack;
        return inv.Stamina >= 2 && !track.WoundIconSpaces.Contains(inv.Stamina - 1);
    }

    /// <summary>Asher ignores the face-up Wound in his first slot, which makes him the natural
    /// body for the sacrificial screen; Ibraheem's Footprint never drops below 4, which makes
    /// him the natural courier for the long errands.</summary>
    private static int ScreenPreference(InvestigatorState inv) => inv.DefId == "asher" ? 0 : 1;

    private static int ErrandPreference(InvestigatorState inv) => inv.DefId == "ibraheem" ? -3 : 0;

    // ---------- Activated Minors: free, so use them whenever they pay ----------

    private void UseMinorAbilities(InvestigatorState inv)
    {
        if (!Active(inv) || IsSpirit(inv) || HasPassiveMinor(inv.DefId))
        {
            return;
        }
        switch (inv.DefId)
        {
            case "aira":
                // Turn the one Involved Action she is going to take anyway into an Interact, so
                // the Evidence turn-in or objective step no longer costs her the whole turn.
                if (inv.FinalAction == FinalActionKind.None && WantsInvolvedAction(inv))
                {
                    _act.Try("aira-minor", () => _g.UseMinorAbility());
                }
                break;

            case "brielle":
                PlaceCans(inv);
                break;

            case "vincent":
                if (_g.Graph.Space(inv.Space).Kind == SpaceKind.PointOfInterest)
                {
                    _act.Try("vincent-scout", () => _g.UseMinorAbility());
                }
                break;
        }
    }

    /// <summary>Would this Investigator's plan this turn end in an Involved Action?</summary>
    private bool WantsInvolvedAction(InvestigatorState inv) =>
        inv.EvidenceCarried.Count > 0 ||
        S.Objective.TokenCarriers.Any(kv => kv.Value == inv.DefId) ||
        inv.Items.Contains("fuse") ||
        (S.Objective.SelectedEscapeCard != null && _threatLevel == 0);

    /// <summary>
    /// Brielle's Cans flip to Noise when the Adversary walks onto one — a tripwire across the
    /// approach lanes that costs nothing and tells the team which way it came from. Two a turn
    /// while the Adversary is about keeps the six tokens lasting.
    /// </summary>
    private void PlaceCans(InvestigatorState inv)
    {
        if (_threatLevel == 0)
        {
            return;
        }
        var spots = Nav.From(_g, inv.Space, 3).Keys
            .Where(sp => sp != inv.Space && !S.BoardTokens.Any(kv => kv.Value == sp && kv.Key.StartsWith("can:", StringComparison.Ordinal)))
            .OrderByDescending(Danger)
            .ThenBy(sp => sp, StringComparer.Ordinal)
            .Take(2)
            .ToList();
        if (spots.Count > 0)
        {
            _act.Try("brielle-cans", () => _g.UseMinorAbility(null, spots));
        }
    }

    /// <summary>Mitchell's Sweep: after the beam is down and whatever it caught is Revealed, it
    /// moves to a second position for the rest of the round — two cones for one Charge.</summary>
    private void SweepFlashlight(InvestigatorState inv)
    {
        if (inv.DefId != "mitchell" || !Active(inv))
        {
            return;
        }
        var placement = S.Flashlights.FirstOrDefault(f => f.InvestigatorId == inv.DefId);
        if (placement == null)
        {
            return;
        }
        double best = SecondBestFlashlightAngle(inv, placement.AngleRadians);
        _act.Try("mitchell-sweep", () => _g.UseMinorAbility(null,
            new List<string> { best.ToString("R", CultureInfo.InvariantCulture) }));
    }

    /// <summary>The best beam angle that is not (within a few degrees of) the one already
    /// placed — the same fine-angle search the first placement uses.</summary>
    private double SecondBestFlashlightAngle(InvestigatorState inv, double taken)
    {
        var (angle, score) = BestFlashlightAngle(inv, avoid: taken);
        return score > 0 ? angle : taken + Math.PI / 2;
    }

    // ---------- Majors: one token each, spent on purpose ----------

    private void ConsiderMajorAbility(InvestigatorState inv, Plan? plan)
    {
        if (!Active(inv) || IsSpirit(inv))
        {
            return;
        }
        // Dylan's return trip is free — his token was paid for when it was placed.
        if (inv.DefId == "dylan" && DylanShouldTeleport(inv, plan))
        {
            _act.Try("dylan-escape-artist-return", () => _g.UseMajorAbility());
            return;
        }
        bool forced = _g.HasRoundModifier(Game.ForcedMajorAbilityPrefix + inv.DefId);
        if (inv.MajorAbilityTokens < 1)
        {
            return;
        }
        if (!forced && !MajorIsWorthIt(inv, plan))
        {
            return;
        }
        var args = MajorArguments(inv, plan);
        if (args == null)
        {
            return;
        }
        _act.Try("major:" + inv.DefId, () => _g.UseMajorAbility(null, args));
    }

    /// <summary>
    /// A Major token is worth spending on one of three things: staying alive when the Adversary
    /// has the group cornered, buying the tempo that finishes an objective, or a piece of
    /// utility (an Event dodged, a Trade nobody else could make) that costs nothing to take.
    /// </summary>
    private bool MajorIsWorthIt(InvestigatorState inv, Plan? plan)
    {
        bool cornered = Danger(inv.Space) >= 3 ||
                        Alive.Any(o => o.Wounds.Count >= 3 && Danger(o.Space) >= 2);
        bool endgame = S.Objective.SelectedEscapeCard != null &&
                       S.Round >= _g.Db.Config.Rounds - 5;
        switch (inv.DefId)
        {
            case "lucy-belle":
            case "mitchell":
            case "aira":
                return cornered;
            case "asher":
            case "mada":
                return endgame || (cornered && inv.Wounds.Count >= 2);
            case "marci":
            case "dylan":
                return endgame;
            case "brielle":
                return EventIsPunishing();
            case "ibraheem":
                return HandOffTarget(inv) != null;
            case "vincent":
                return _threatLevel == 0 &&
                       inv.Items.Any(id => !id.Contains(':') && id != "fuse" && id != "the-hook" && id != "frayed-ropes");
            default:
                return false;
        }
    }

    /// <summary>Arguments for this Investigator's Major, or null when there is nothing to aim it at.</summary>
    private List<string>? MajorArguments(InvestigatorState inv, Plan? plan)
    {
        switch (inv.DefId)
        {
            case "aira":
            {
                // Name the two hottest spaces: anything on or beside them has to Reveal, and a
                // Revealed Adversary may not Attack for the rest of the round.
                var hot = HotSpaces(inv, 2);
                return hot.Count == 2 ? hot : null;
            }
            case "lucy-belle":
            {
                // Two Barricades adjacent to herself: Adversary-only walls that cost two Break
                // Door actions each to clear. Put them where the threat is coming from.
                var walls = Nav.Neighbors(_g, inv.Space)
                    .Where(n => !Alive.Any(o => o.Space == n))
                    .OrderByDescending(Danger).ThenBy(n => n, StringComparer.Ordinal)
                    .Take(2).ToList();
                return walls.Count == 2 ? walls : null;
            }
            case "mitchell":
            {
                var scared = Alive.OrderByDescending(o => o.Wounds.Count)
                    .ThenByDescending(o => Danger(o.Space))
                    .ThenBy(o => o.DefId, StringComparer.Ordinal).First();
                return new List<string> { scared.DefId };
            }
            case "marci":
            {
                // Walk up to 2 stragglers 2 spaces each — the cheapest way to finish a gather.
                var target = plan?.Space ?? inv.Space;
                var dist = Nav.From(_g, target);
                var args = new List<string>();
                foreach (var stray in Alive.Where(o => o != inv && Nav.Hops(dist, o.Space) is > 0 and <= 6)
                             .OrderByDescending(o => Nav.Hops(dist, o.Space))
                             .ThenBy(o => o.DefId, StringComparer.Ordinal)
                             .Take(2))
                {
                    string? step = StepToward(stray, target, 2);
                    if (step != null)
                    {
                        args.Add(stray.DefId);
                        args.Add(step);
                    }
                }
                return args.Count >= 2 ? args : null;
            }
            case "dylan":
            {
                // Drop the Escape Artist token on the space the team has to come back to.
                string? anchor = ObjectiveAnchor();
                return anchor == null ? null : new List<string> { anchor };
            }
            case "ibraheem":
                return new List<string>(); // extends his Trade range to 5 for the round
            case "asher":
            case "brielle":
            case "mada":
            case "vincent":
                return new List<string>();
            default:
                return null;
        }
    }

    /// <summary>Two different spaces most likely to be hiding the Adversary right now.</summary>
    private List<string> HotSpaces(InvestigatorState inv, int count)
    {
        var candidates = new List<string>(S.Adversary.ShadowTokens.Values.Where(_g.Graph.HasSpace));
        if (candidates.Count < count)
        {
            candidates.AddRange(Nav.From(_g, inv.Space, 4).Keys.OrderByDescending(Danger));
        }
        return candidates.Distinct().Take(count).ToList();
    }

    /// <summary>A space up to <paramref name="steps"/> closer to the target, for Marci's Major.</summary>
    private string? StepToward(InvestigatorState mover, string target, int steps)
    {
        var dist = Nav.From(_g, target);
        string current = mover.Space;
        for (int i = 0; i < steps; i++)
        {
            int here = Nav.Hops(dist, current);
            string? next = Nav.Neighbors(_g, current)
                .Where(n => Nav.Hops(dist, n) < here && !Alive.Any(o => o != mover && o.Space == n))
                .OrderBy(n => Nav.Hops(dist, n)).ThenBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next == null)
            {
                break;
            }
            current = next;
        }
        return current == mover.Space ? null : current;
    }

    /// <summary>The space the whole team eventually has to reach for the selected Escape card.</summary>
    private string? ObjectiveAnchor()
    {
        foreach (string token in new[] { "locked-escape", "truck", "altar", "saw" })
        {
            if (S.Objective.Tokens.TryGetValue(token, out string? space))
            {
                return space;
            }
        }
        return null;
    }

    private bool DylanShouldTeleport(InvestigatorState inv, Plan? plan)
    {
        string? token = _g.BoardTokenSpace("escape-artist:" + inv.DefId);
        if (token == null || plan == null)
        {
            return false;
        }
        var dist = Nav.From(_g, plan.Space);
        return Nav.Hops(dist, token) + 4 < Nav.Hops(dist, inv.Space);
    }

    /// <summary>Ibraheem's Major buys a Trade at 5 spaces: worth a token to put Evidence in the
    /// hands of somebody already standing on the Computer.</summary>
    private InvestigatorState? HandOffTarget(InvestigatorState inv)
    {
        if (inv.EvidenceCarried.Count == 0)
        {
            return null;
        }
        var reach = Nav.From(_g, inv.Space, 5);
        // Either somebody already parked on the Computer / Booth, or this round's designated
        // runner: both turn a two-round walk into a Trade.
        return Alive.FirstOrDefault(o => o != inv && !IsSpirit(o) && CanTakeInvolved(o) &&
                                         reach.ContainsKey(o.Space) &&
                                         (_g.Graph.Space(o.Space).Kind == _turnInKind || o.DefId == _runner));
    }

    /// <summary>After Ibraheem's Major lands, actually make the Trade it paid for.</summary>
    private void UseExtendedTrade(InvestigatorState inv)
    {
        var target = HandOffTarget(inv);
        if (target == null || _g.RoundModifier(Game.TradeRangePrefix + inv.DefId) < 2)
        {
            return;
        }
        foreach (string zone in inv.EvidenceCarried.ToList())
        {
            string z = zone;
            string to = target.DefId;
            _act.Try("extended-trade", () => _g.TradeEvidence(to, z));
        }
    }

    /// <summary>
    /// A face-up Fear Wound makes the Major Ability compulsory before the turn may end. The
    /// card's own text says "if you do not have a Major Ability token (or if you are unable to
    /// use it), there is no effect", but the engine only checks the token — so if the Ability's
    /// own preconditions cannot be met (Mada without 5 Stamina, Vincent without an Item card,
    /// Dylan with nowhere to anchor) the turn cannot be ended at all. Try every argument shape
    /// that could satisfy it before giving up.
    /// </summary>
    private void SatisfyForcedMajor(InvestigatorState inv)
    {
        if (!Active(inv) || IsSpirit(inv) || inv.MajorAbilityTokens < 1 ||
            !_g.HasRoundModifier(Game.ForcedMajorAbilityPrefix + inv.DefId))
        {
            return;
        }
        // Mada's Major costs 5 Stamina outright; top him up if anything can.
        if (inv.DefId == "mada" && inv.Stamina < 5 && inv.Items.Contains("energy-bar"))
        {
            _act.Try("energy-bar", () => _g.UseItem("energy-bar"));
        }
        foreach (var args in ForcedMajorAttempts(inv))
        {
            var attempt = args;
            if (_act.Try("forced-major:" + inv.DefId, () => _g.UseMajorAbility(null, attempt)))
            {
                return;
            }
        }
    }

    private IEnumerable<List<string>> ForcedMajorAttempts(InvestigatorState inv)
    {
        var planned = MajorArguments(inv, null);
        if (planned != null)
        {
            yield return planned;
        }
        yield return new List<string>();
        var neighbours = Nav.Neighbors(_g, inv.Space);
        switch (inv.DefId)
        {
            case "dylan":
                // A space "adjacent to yourself", never yourself: inv.Space itself is never a
                // legal Escape Artist target (DylanPlaceEscapeArtist requires adjacency), so
                // that always failed and left Fear permanently unsatisfied for him.
                if (neighbours.Count > 0)
                {
                    yield return new List<string> { neighbours[0] };
                }
                break;
            case "mitchell":
                yield return new List<string> { inv.DefId };
                break;
            case "aira":
            {
                var two = neighbours.Take(2).ToList();
                if (two.Count == 2)
                {
                    yield return two;
                }
                var any = _g.Graph.Def.Spaces.Take(2).Select(sp => sp.Id).ToList();
                yield return any;
                break;
            }
            case "lucy-belle":
            {
                var walls = neighbours.Where(n => !Alive.Any(o => o.Space == n)).Take(2).ToList();
                if (walls.Count == 2)
                {
                    yield return walls;
                }
                break;
            }
            case "marci":
            {
                var args = new List<string>();
                foreach (var other in Alive.Where(o => o != inv && !IsSpirit(o)).Take(2))
                {
                    string? step = Nav.Neighbors(_g, other.Space)
                        .FirstOrDefault(n => !Alive.Any(o => o.Space == n));
                    if (step != null)
                    {
                        args.Add(other.DefId);
                        args.Add(step);
                    }
                }
                if (args.Count >= 2)
                {
                    yield return args;
                }
                break;
            }
        }
    }

    /// <summary>Is this round's Event actually costing the team anything worth a token?</summary>
    private bool EventIsPunishing()
    {
        if (S.CurrentEvent == null || _g.HasRoundModifier(Game.EventIgnoredKey))
        {
            return false;
        }
        string[] hurts =
        {
            Game.MpPenaltyKey, Game.SprintStaminaSurchargeKey, Game.SprintWoundIconShiftKey,
            Game.SprintD6WoundThresholdKey, Game.FlashlightChargeSurchargeKey,
            Game.FlashlightCenterLineOnlyKey, Game.PoiPickupForbiddenKey,
            Game.ChargeActionForbiddenKey, Game.NoRestStaminaKey, Game.MoveStaminaCostKey,
        };
        return hurts.Any(key => _g.RoundModifier(key) > 0);
    }
}
