namespace StiflingDark.Bots;

/// <summary>
/// How the Investigator team plays AGAINST a particular Adversary. The three games are not
/// the same game: The Butcher wins on a single kill, so nobody is ever alone and the team
/// runs early; the Cult wins on the clock, so speed outranks tidiness; the Insatiable Horror
/// ambushes from range, so the defaults hold. Everything here is posture — the plan
/// machinery in InvestigatorTeam reads these knobs instead of hard-coding one temperament.
/// </summary>
public sealed class AdversaryPlaybook
{
    /// <summary>Pairs stick together from round one, not only after first contact.</summary>
    public bool BuddyAlways { get; private set; }

    /// <summary>Wounds subtracted from the flee thresholds: 1 means "run one Wound earlier".</summary>
    public int Caution { get; private set; }

    /// <summary>Rounds of retreat before getting back to work.</summary>
    public int FleeStreakMax { get; private set; } = 2;

    /// <summary>ShouldTurnInNow: keep collecting while the next token is within this many
    /// hops beyond the nearest turn-in feature; smaller banks sooner.</summary>
    public int TurnInDetourSlack { get; private set; } = 10;

    /// <summary>Farthest a Light Switch may be (movement cost) before the team sweeps the
    /// Zone with beams on approach instead of walking there to flip it.</summary>
    public int LightSwitchReach { get; private set; } = 5;

    /// <summary>Beam score per unswept space of a Zone whose Evidence is still hidden.</summary>
    public double EvidenceSearchWeight { get; private set; } = 8;

    /// <summary>Rounds before the limit at which optional errands (POIs, Medical Items)
    /// are dropped and everyone works Evidence.</summary>
    public int ClockCrunchRounds { get; private set; } = 5;

    public static AdversaryPlaybook For(string adversaryId) => adversaryId switch
    {
        "butcher" => new AdversaryPlaybook
        {
            BuddyAlways = true,
            Caution = 1,
            FleeStreakMax = 2,
            TurnInDetourSlack = 8,
        },
        "cult-of-hunlow" => new AdversaryPlaybook
        {
            FleeStreakMax = 1,
            TurnInDetourSlack = 4,
            LightSwitchReach = 4,
            ClockCrunchRounds = 7,
        },
        _ => new AdversaryPlaybook(),
    };
}
