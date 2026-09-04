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
    /// <summary>Dollars this mission may spend before it stops. Absent means no money cap.</summary>
    public decimal? MaxSpendUsd { get; init; }

    /// <summary>Deny whoever supervises this run its raw material. See <see cref="Missions.FourthWall"/>.</summary>
    public bool? FourthWall { get; init; }

    /// <summary>The one path behind that wall the supervisor is meant to read.</summary>
    public string? Deliverable { get; init; }

    /// <summary>
    /// Where the agent actually works, when that is not RolloutLoud's anchor.
    /// </summary>
    /// <remarks>
    /// Naming one does not open anything: crossing out of the anchor writes into another repository
    /// and starts a process there, so it produces a button and waits for the operator's click.
    /// </remarks>
    public string? WorkingDirectory { get; init; }

    /// <summary>Open the agent with its approval prompts off, when the operator clicks.</summary>
    public bool? Elevated { get; init; }
}

/// <summary>
/// A mission the agent composed, offered to the operator rather than started.
/// </summary>
/// <remarks>
/// Every field of <see cref="MissionRequest"/>, plus <see cref="Rationale"/>. The overlap is
/// deliberate: an agent that has composed a proposal and had it rejected should be able to fix one
/// field and re-propose, not translate between two vocabularies.
/// </remarks>
public sealed record ProposalRequest
{
    public required string Objective { get; init; }

    /// <summary>Which CLI would work it. Defaults to the one proposing.</summary>
    public string? Agent { get; init; }

    /// <summary>Which CLI is asking. Shown to the operator, so they know who wrote this.</summary>
    public string? ProposedBy { get; init; }

    public string? GateCommand { get; init; }

    public string? GateDescription { get; init; }

    public IReadOnlyList<string>? Scope { get; init; }

    public IReadOnlyList<string>? ScopeExclusions { get; init; }

    public string? Authorization { get; init; }

    public string? Offload { get; init; }

    public int? MaxAttempts { get; init; }

    public double? MaxHours { get; init; }
    /// <summary>Dollars this mission may spend before it stops. Absent means no money cap.</summary>
    public decimal? MaxSpendUsd { get; init; }

    /// <summary>Deny whoever supervises this run its raw material. See <see cref="Missions.FourthWall"/>.</summary>
    public bool? FourthWall { get; init; }

    /// <summary>The one path behind that wall the supervisor is meant to read.</summary>
    public string? Deliverable { get; init; }

    /// <summary>
    /// Where the agent actually works, when that is not RolloutLoud's anchor.
    /// </summary>
    /// <remarks>
    /// Naming one does not open anything: crossing out of the anchor writes into another repository
    /// and starts a process there, so it produces a button and waits for the operator's click.
    /// </remarks>
    public string? WorkingDirectory { get; init; }

    /// <summary>Open the agent with its approval prompts off, when the operator clicks.</summary>
    public bool? Elevated { get; init; }

    /// <summary>Why this gate and this scope. The field that makes the operator's review quick.</summary>
    public string? Rationale { get; init; }
}

/// <summary>
/// The supervisor saying what the deliverable still needs.
/// </summary>
/// <remarks>
/// The bridge's other direction. Everything else here carries what the agent did; this carries what
/// somebody reading the result wants next.
/// </remarks>
public sealed record ReviewRequest
{
    /// <summary>Who is reviewing. A label for the record.</summary>
    public string? From { get; init; }

    /// <summary>What needs to change, in a sentence the agent can act on.</summary>
    public required string Note { get; init; }

    /// <summary>The specific gaps. A list survives being skimmed; a paragraph does not.</summary>
    public IReadOnlyList<string>? Missing { get; init; }

    /// <summary>Deal with it before the next attempt. Never a stop — that is the gate's job.</summary>
    public bool? Blocking { get; init; }
}

/// <summary>
/// The agent bounding its own run, once it has learned where the boundary is.
/// </summary>
/// <remarks>
/// Only ever narrows. A boundary that can be widened after the fact is a note the run edits when it
/// becomes inconvenient, which is the drift the scope exists to stop.
/// </remarks>
public sealed record ScopeRequest
{
    /// <summary>Hosts, domains or CIDR blocks the run may touch from here.</summary>
    public required IReadOnlyList<string> Targets { get; init; }

    /// <summary>Carve-outs. These only ever accumulate.</summary>
    public IReadOnlyList<string>? Exclusions { get; init; }

    /// <summary>What permits reaching them: the programme, its policy URL, the engagement reference.</summary>
    public string? Authorization { get; init; }
}

/// <summary>Asking for a launch button on a mission that already exists.</summary>
public sealed record LaunchRequestBody
{
    /// <summary>Which CLI to open. Defaults to the one the mission is assigned to.</summary>
    public string? Agent { get; init; }

    /// <summary>Open it with approval prompts off.</summary>
    public bool? Elevated { get; init; }
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

    /// <summary>improving | steady | degrading | stalled | unknown</summary>
    /// <remarks>
    /// Carried on the answer to "may I stop" rather than given its own endpoint, because this is
    /// the moment the agent is already deciding what to do next — and "keep going, but what you
    /// are learning is costing several times what it did" is the same answer with the useful half
    /// attached.
    /// </remarks>
    public string? ProgressTrend { get; init; }

    public string? ProgressVerdict { get; init; }
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

/// <summary>Picking a mission back up after the window was closed.</summary>
public sealed record ResumeRequest
{
    /// <summary>Which mission. Omitted means the most recently interrupted one.</summary>
    public string? MissionId { get; init; }

    /// <summary>Hand it to a different CLI on the way back in. Defaults to the one it was on.</summary>
    public string? Agent { get; init; }
}

public sealed record ResumeResponse
{
    public required bool Resumed { get; init; }

    public required string Reason { get; init; }

    public string? MissionId { get; init; }

    public string? Objective { get; init; }

    public string? Agent { get; init; }

    public int Tier { get; init; }

    public int Attempts { get; init; }

    /// <summary>Buttons that were still waiting when the window closed.</summary>
    public int OpenButtons { get; init; }

    /// <summary>The briefing to work from, so a resumed agent needs no second call.</summary>
    public string? Briefing { get; init; }
}

/// <summary>How big the window has become, and what to do about it.</summary>
public sealed record ContextResponse
{
    public required int Tokens { get; init; }

    /// <summary>measured | estimated | unknown — the two are not interchangeable.</summary>
    public required string Source { get; init; }

    public required string Detail { get; init; }

    /// <summary>Whether concrete actions should be going to subagents right now.</summary>
    public required bool OffloadNow { get; init; }

    public required string Reason { get; init; }

    public int Threshold { get; init; }
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

    /// <summary>Refused for load, not for anything about the task. Worth retrying shortly.</summary>
    public bool Throttled { get; init; }

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
