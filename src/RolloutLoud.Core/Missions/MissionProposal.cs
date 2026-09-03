namespace RolloutLoud.Core.Missions;

public enum ProposalState
{
    /// <summary>On the operator's desk. The agent is waiting.</summary>
    Pending,

    /// <summary>Turned into a running mission. <see cref="MissionProposal.MissionId"/> says which.</summary>
    Accepted,

    Rejected,

    /// <summary>Superseded by a newer proposal from the same agent, or the window went away.</summary>
    Withdrawn,
}

/// <summary>
/// A mission an agent wrote and an operator has not yet agreed to.
/// </summary>
/// <remarks>
/// This is the answer to a flow the operator asked for: they open a CLI, tell the agent what they
/// want in a sentence, and the agent — which is better at writing a testable objective than a
/// person typing quickly — composes the whole mission and hands it to RolloutLoud.
///
/// <b>What that costs, and why it stops here rather than starting.</b> Composing the mission means
/// composing the <see cref="SuccessGate"/>, and a gate the agent wrote for itself is not a gate.
/// The single rule under this product is that the agent never decides on its own that it is done;
/// letting it author its own finish line hands that decision straight back, in a form that compiles,
/// passes its own re-verification, and reads exactly like the real thing. So a proposal is a draft:
/// the agent writes it, <see cref="GateCritique"/> says what is weak about it, and the operator
/// starts it or throws it away.
///
/// <b>The approval is what makes the objective the operator's.</b> Until then this text came from a
/// model and is shown as data — quoted in the window, never acted on. Accepting is the operator
/// adopting it as their own sentence, which is exactly why the accept path builds a normal
/// <see cref="Mission"/> through the normal route and nothing downstream needs to know a proposal
/// was ever involved.
///
/// Deliberately not persisted. A proposal is a question waiting for an answer that is being typed
/// right now; surviving a restart would mean the operator returns to a stale draft whose agent
/// stopped waiting hours ago, and accepting it would launch a mission nobody is there to work.
/// </remarks>
public sealed record MissionProposal
{
    public required string Id { get; init; }

    /// <summary>The objective, as the agent wrote it. Data until the operator accepts it.</summary>
    public required string Objective { get; init; }

    /// <summary>Which CLI proposed this.</summary>
    public required string ProposedBy { get; init; }

    /// <summary>Which CLI would work it. Usually the same one.</summary>
    public required string AgentId { get; init; }

    public string? GateCommand { get; init; }

    public string? GateDescription { get; init; }

    public IReadOnlyList<string> Scope { get; init; } = [];

    public IReadOnlyList<string> ScopeExclusions { get; init; } = [];

    public string? Authorization { get; init; }

    public int? MaxAttempts { get; init; }

    public double? MaxHours { get; init; }

    public string? Offload { get; init; }

    /// <summary>
    /// Why the agent chose this gate and this scope, in its own words.
    /// </summary>
    /// <remarks>
    /// The field that makes the review quick. "Is this gate right?" is hard to answer cold and easy
    /// to answer once you know what the agent thought it was testing — and when the reasoning is
    /// wrong, it is usually wrong in a way that is obvious to read and invisible in the command.
    ///
    /// It never reaches a briefing. It is written for the operator, and once the mission starts it
    /// has done its job.
    /// </remarks>
    public string? Rationale { get; init; }

    /// <summary>What RolloutLoud found wrong with the gate, computed when the proposal arrived.</summary>
    public required GateReview Review { get; init; }

    public ProposalState State { get; init; } = ProposalState.Pending;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>Set once accepted.</summary>
    public string? MissionId { get; init; }

    /// <summary>Why the operator said no, if they said why. Goes back to the agent verbatim.</summary>
    public string? Decision { get; init; }

    public bool IsPending => State == ProposalState.Pending;

    /// <summary>
    /// True when targets are named and nobody is recorded as having authorised reaching them.
    /// </summary>
    public bool NeedsAuthorization => Scope.Count > 0 && string.IsNullOrWhiteSpace(Authorization);

    public static string NewId() =>
        "p-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The gate this proposal would install, built once and used both to critique and to run.
    /// </summary>
    /// <remarks>
    /// One expression rather than two, because the alternative is a critique of a gate that is not
    /// quite the gate that ends up running — and the whole value of the critique is that the
    /// operator looked at the real finish line.
    /// </remarks>
    public SuccessGate Gate => string.IsNullOrWhiteSpace(GateCommand)
        ? SuccessGate.OperatorJudged with { Description = GateDescription }
        : new SuccessGate
        {
            Kind = GateKind.Command,
            Command = GateCommand,
            Description = GateDescription,
        };

    /// <summary>
    /// Turns an accepted proposal into a mission.
    /// </summary>
    /// <remarks>
    /// Every default here is the same one <c>POST /v1/missions</c> applies, and that is not
    /// duplication to be tidied away later — it is the point. Acceptance produces an ordinary
    /// mission by the ordinary route, so no invariant downstream has to know this one came from a
    /// proposal, and no code path exists that a proposal can reach and a mission cannot.
    /// </remarks>
    public Mission ToMission() => new()
    {
        Id = Mission.NewId(),
        Objective = Objective.Trim(),
        AgentId = AgentId,
        Gate = Gate,
        Scope = Scope.Count > 0
            ? new MissionScope
            {
                Targets = Scope,
                Exclusions = ScopeExclusions,
                Authorization = Authorization,
            }
            : MissionScope.Unrestricted,
        Stop = new StopConditions
        {
            MaxAttempts = MaxAttempts is > 0 ? MaxAttempts.Value : 200,
            MaxWallClock = TimeSpan.FromHours(MaxHours is > 0 ? MaxHours.Value : 6),
        },
        Offload = new OffloadSettings
        {
            Trigger = Offload?.ToLowerInvariant() switch
            {
                "always" => OffloadTrigger.Always,
                "threshold" => OffloadTrigger.ContextThreshold,
                _ => OffloadTrigger.Off,
            },
        },
    };
}
