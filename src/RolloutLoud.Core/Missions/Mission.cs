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

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Why the mission left <see cref="MissionState.Running"/>. Empty while it runs.</summary>
    public string? Resolution { get; init; }

    public bool IsTerminal => State is MissionState.Achieved or MissionState.Exhausted or MissionState.Aborted;
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
    /// Consecutive attempts that produced no new information before the ladder is forced upward.
    /// Not a stop — a shove. Repeating a failed idea is the default behaviour we are correcting.
    /// </summary>
    public int PlateauBeforeEscalation { get; init; } = 5;

    /// <summary>Escalation tiers exhausted with no progress ends the run.</summary>
    public int MaxEscalationTier { get; init; } = EscalationLadder.MaxTier;
}
