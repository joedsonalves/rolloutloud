namespace RolloutLoud.Core.Missions;

public enum MissionState
{
    Draft,
    Running,
    Paused,

    /// <summary>The success gate was satisfied AND independently re-verified.</summary>
    Achieved,

    /// <summary>A stop condition fired first. Not a failure of the agent — a budget doing its job.</summary>
    Exhausted,

    Aborted,
}

/// <summary>
/// What the operator typed, turned into something a loop can test.
/// </summary>
/// <remarks>
/// The whole design rests on one distinction. "Attack until you get a critical" fails today
/// because the agent decides for itself when it is done, and an agent that is tired is very
/// good at deciding it is done. So the mission carries a <see cref="SuccessGate"/>: a command
/// whose exit code, not the agent's prose, ends the run. The agent cannot declare victory. It
/// can only produce evidence and ask the gate.
/// </remarks>
public sealed record Mission
{
    public required string Id { get; init; }

    /// <summary>The operator's sentence, verbatim. Goes into every briefing unedited.</summary>
    public required string Objective { get; init; }

    /// <summary>Agent the mission is currently assigned to.</summary>
    public required string AgentId { get; init; }

    public MissionState State { get; init; } = MissionState.Draft;

    public SuccessGate Gate { get; init; } = SuccessGate.OperatorJudged;

    /// <summary>
    /// The boundary the run must not cross. For a pentest this is the engagement scope, and it is
    /// enforced on every command the agent routes through the bridge — not advice in a prompt.
    /// </summary>
    public MissionScope Scope { get; init; } = MissionScope.Unrestricted;

    public StopConditions Stop { get; init; } = new();

    /// <summary>
    /// Whether whoever supervises this run is denied its raw material. See <see cref="FourthWall"/>.
    /// </summary>
    /// <remarks>
    /// A property of the mission rather than of a session, because the bridge cannot tell a
    /// supervising caller from a working one — both hold the same token and both may name the same
    /// agent. Making it a mission setting means there is one rule with no way to get the raw
    /// material by asking differently, which is the only version of this that is worth anything.
    ///
    /// The cost is real and worth stating: the working agent also stops seeing the argv echoed back
    /// in its ledger. It keeps what stops it repeating a <em>kind</em> of idea, and exact repeats
    /// were never held off by that echo — <c>Admit</c> blocks them by fingerprint.
    /// </remarks>
    public bool FourthWall { get; init; }

    /// <summary>
    /// The one path behind the wall the supervisor is meant to read, relative to the repository.
    /// </summary>
    /// <remarks>
    /// The window in the wall, and the thing that makes this mode usable rather than blind. The
    /// supervisor reads the report draft and says what is missing; that is reviewing. Reading the
    /// scan output that produced it is not.
    ///
    /// Named on the mission so both sides agree what it is before the work starts, rather than the
    /// supervisor discovering at review time that the deliverable is somewhere it did not look.
    /// </remarks>
    public string? Deliverable { get; init; }

    public OffloadSettings Offload { get; init; } = new();

    /// <summary>Escalation tier reached so far. See <see cref="EscalationLadder"/>.</summary>
    public int EscalationTier { get; init; }

    /// <summary>
    /// Ledger size when the tier last moved, so the next escalation waits for a fresh window.
    /// </summary>
    /// <remarks>
    /// Without this the ladder runs away. The plateau test looks at the last N attempts, and
    /// immediately after an escalation those N are still the same uninformative ones that caused
    /// it — so the very next attempt escalates again, and a run climbs from tier 0 to the top in
    /// three attempts without ever having tried a tier. The agent has to be given the window it
    /// was just told to use.
    /// </remarks>
    public int TierChangedAtAttempt { get; init; }

    /// <summary>Agents that have already worked this mission, oldest first.</summary>
    /// <remarks>
    /// Kept so the relay never rotates back to a model whose habits already got stuck — and,
    /// because the ledger forbids its own spent attempts, would arrive with fewer moves than it
    /// had the first time.
    /// </remarks>
    public IReadOnlyList<string> RelayHistory { get; init; } = [];

    /// <summary>
    /// What the outgoing agent wanted the next one to know.
    /// </summary>
    /// <remarks>
    /// The ledger says what was tried; this says what the previous agent came to BELIEVE, and
    /// which of its own assumptions it stopped trusting. Those are the two things a ledger cannot
    /// carry, and they are what someone picking the problem up cold would ask for first.
    /// </remarks>
    public string? HandoffNote { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Why the mission left <see cref="MissionState.Running"/>. Empty while it runs.</summary>
    public string? Resolution { get; init; }

    public bool IsTerminal => State is MissionState.Achieved or MissionState.Exhausted or MissionState.Aborted;

    /// <summary>
    /// A new mission id: a sortable timestamp plus eight random characters.
    /// </summary>
    /// <remarks>
    /// The suffix is not decoration. The timestamp alone has second resolution, and missions are
    /// keyed by id in a dictionary — so two created in the same second collided and the second
    /// silently REPLACED the first. That is not hypothetical: it happened the first time an agent
    /// opened two missions in one script, and the symptom was a mission that simply was not in the
    /// list, with no error anywhere.
    ///
    /// The timestamp stays first so ids still sort chronologically and read as a date.
    ///
    /// Eight characters rather than four, and the extra four are not superstition: at four hex
    /// characters the birthday bound puts two hundred ids in one second at roughly a 26% chance
    /// of a collision, which the test caught on its first run. Eight brings that below one in a
    /// million and costs four bytes.
    /// </remarks>
    public static string NewId() =>
        "m-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// The brakes. "Relentless" without these is just an unbounded spend, and the first long night
/// proved it: a loop with no wall clock does not stop when you go to sleep.
/// </summary>
public sealed record StopConditions
{
    public int MaxAttempts { get; init; } = 200;

    public TimeSpan MaxWallClock { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Dollars this mission may spend before it stops. Null means no money cap.
    /// </summary>
    /// <remarks>
    /// The cap the other two do not cover. Attempts count moves and the clock counts minutes, and a
    /// six-hour run with offload on can make a hundred cheap attempts or twenty expensive ones —
    /// only one of those is a bill the operator would have agreed to in advance.
    ///
    /// ⚠️ Null rather than a default figure, and that is not laziness. Any number picked here would
    /// be wrong for somebody, and a cap the operator did not choose is one they will not believe
    /// when it fires — they will raise it without reading it, which is worse than not having one.
    /// </remarks>
    public decimal? MaxSpendUsd { get; init; }

    /// <summary>
    /// Consecutive attempts that produced no new information before the ladder is forced upward.
    /// Not a stop — a shove. Repeating a failed idea is the default behaviour we are correcting.
    /// </summary>
    public int PlateauBeforeEscalation { get; init; } = 5;

    /// <summary>Escalation tiers exhausted with no progress ends the run.</summary>
    public int MaxEscalationTier { get; init; } = EscalationLadder.MaxTier;
}
