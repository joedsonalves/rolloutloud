namespace RolloutLoud.Core.Bridge;

/// <summary>
/// The wire shapes an agent sees. Flat and boring on purpose — these are written by a model
/// composing JSON by hand in a shell, so every nested object is a chance to get it wrong.
/// </summary>
public static class BridgeContracts
{
    public const string TokenHeader = "X-RolloutLoud-Token";
}

/// <summary>Written to <c>.rolloutloud/bridge.json</c> so an agent can find the bridge unaided.</summary>
public sealed record BridgeHandshake
{
    public required string Endpoint { get; init; }

    public required string Token { get; init; }

    public required string RepositoryRoot { get; init; }

    public required bool Elevated { get; init; }

    public string? ActiveMissionId { get; init; }

    public required int ProcessId { get; init; }
}

/// <summary>
/// Opening a mission from outside the window — the `rollout mission` command, and the flow where
/// a CLI is told "install ROLLOUTLOUD and work on X" and sets the mission up for itself.
/// </summary>
public sealed record MissionRequest
{
    public required string Objective { get; init; }

    public string? Agent { get; init; }

    /// <summary>Shell command that must exit 0. Absent means only the operator can end the run.</summary>
    public string? GateCommand { get; init; }

    /// <summary>The finish line in words, shown to the agent alongside the machine gate.</summary>
    public string? GateDescription { get; init; }

    public IReadOnlyList<string>? Scope { get; init; }

    public IReadOnlyList<string>? ScopeExclusions { get; init; }

    public string? Authorization { get; init; }

    /// <summary>off | always | threshold</summary>
    public string Offload { get; init; } = "off";

    public int? TokenThreshold { get; init; }

    public int? MaxAttempts { get; init; }

    public double? MaxHours { get; init; }
}

/// <summary>Declaring an attempt before running it. The hypothesis is required, and that is the point.</summary>
public sealed record AdmitRequest
{
    public string? Agent { get; init; }

    public required string Hypothesis { get; init; }

    public required string Command { get; init; }
}

public sealed record AdmitResponse
{
    public required bool Admitted { get; init; }

    public required string Reason { get; init; }

    /// <summary>Set when refused, so the agent knows whether it hit the scope or its own history.</summary>
    public string? Outcome { get; init; }

    public required int Tier { get; init; }

    public required string TierInstruction { get; init; }
}

/// <summary>Reporting what an attempt actually did.</summary>
public sealed record AttemptRequest
{
    public string? Agent { get; init; }

    public required string Hypothesis { get; init; }

    public required string Command { get; init; }

    /// <summary>succeeded | failed | blocked | errored. Anything unrecognised is read as failed.</summary>
    public string Outcome { get; init; } = "failed";

    /// <summary>What this ruled out. The field that makes the ledger worth keeping.</summary>
    public string? Learned { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Raw output to file under the run folder rather than carry in the ledger.</summary>
    public string? Output { get; init; }
}

public sealed record AttemptResponse
{
    public required string AttemptId { get; init; }

    public required int TotalAttempts { get; init; }

    public required int Tier { get; init; }

    /// <summary>False almost always. An agent reading this is being told to keep working.</summary>
    public required bool MayStop { get; init; }

    public required string Directive { get; init; }
}

public sealed record ContinueResponse
{
    public required bool Continue { get; init; }

    public required string Directive { get; init; }

    public required string State { get; init; }

    public required int Tier { get; init; }

    public required int Attempts { get; init; }
}

public sealed record GateResponse
{
    public required bool Satisfied { get; init; }

    public required bool Contradicted { get; init; }

    public required string Detail { get; init; }

    public required string State { get; init; }
}

/// <summary>Asking for a command RolloutLoud can run and the agent cannot.</summary>
public sealed record ButtonRequest
{
    public required string Title { get; init; }

    public required string Command { get; init; }

    public string? Rationale { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? Agent { get; init; }

    public string? MissionId { get; init; }

    public bool RequiresElevation { get; init; }

    /// <summary>For long-lived processes — a browser, a listener — that must not block the caller.</summary>
    public bool Detached { get; init; }
}

public sealed record ButtonResponse
{
    public required string Id { get; init; }

    public required string Status { get; init; }

    /// <summary>
    /// True when the allowlist covers this command, meaning the agent may POST to invoke it
    /// itself. False means a human clicks — say so plainly rather than letting the agent wait
    /// on something that will never happen unattended.
    /// </summary>
    public required bool AutoInvokable { get; init; }

    public required string Guidance { get; init; }

    public int? ExitCode { get; init; }

    public string? Output { get; init; }
}

public sealed record BriefingResponse
{
    public required string MissionId { get; init; }

    public required string Objective { get; init; }

    public required string Briefing { get; init; }

    public required int Tier { get; init; }

    public required bool OffloadActive { get; init; }
}

/// <summary>The main agent handing one step down to a fresh process.</summary>
public sealed record SubagentRequest
{
    /// <summary>The single step to run. Not the objective — a subagent gets one thing.</summary>
    public required string Task { get; init; }

    /// <summary>Which CLI runs it. Defaults to the mission's own agent.</summary>
    public string? Agent { get; init; }
}

public sealed record SubagentResponse
{
    public required bool Dispatched { get; init; }

    /// <summary>
    /// The whole point: one line, not a transcript.
    /// </summary>
    /// <remarks>
    /// Keeping the subagent's output out of the caller's context is the entire reason this
    /// endpoint exists. Returning it here would move the cost rather than remove it.
    /// </remarks>
    public required string Verdict { get; init; }

    public string? Outcome { get; init; }

    public string? Learned { get; init; }

    public string? Next { get; init; }

    /// <summary>False when the subagent ignored the answer format and its reply was salvaged.</summary>
    public bool WellFormed { get; init; }

    public string? AttemptId { get; init; }

    /// <summary>Where the full transcript is, if you genuinely need it. You usually do not.</summary>
    public string? Transcript { get; init; }

    public string? Agent { get; init; }

    public int TotalAttempts { get; init; }

    public bool MayStop { get; init; }
}

/// <summary>An agent asking to close RolloutLoud because it believes the objective is met.</summary>
public sealed record ShutdownRequest
{
    public string? MissionId { get; init; }

    public string? Agent { get; init; }

    /// <summary>What was achieved, in the agent's words. Shown to the operator, never trusted.</summary>
    public string? Reason { get; init; }
}

public sealed record ShutdownResponse
{
    /// <summary>allowed | allowedUnattended | refused</summary>
    public required string Verdict { get; init; }

    public required bool Closing { get; init; }

    public required string Reason { get; init; }

    /// <summary>State the decision was made on. The agent's own opinion is not an input.</summary>
    public string? MissionState { get; init; }
}

public sealed record ErrorResponse
{
    public required string Error { get; init; }

    public string? Hint { get; init; }
}
