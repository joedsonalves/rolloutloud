namespace RolloutLoud.Core.Missions;

public enum GateKind
{
    /// <summary>No machine check. The run ends when the operator says so, and only then.</summary>
    OperatorJudged,

    /// <summary>A command whose exit code decides. Zero means the objective is met.</summary>
    Command,

    /// <summary>A file must exist and match a pattern — an artifact the agent had to produce.</summary>
    ArtifactMatch,
}

/// <summary>
/// The condition that ends a mission, evaluated by RolloutLoud rather than claimed by the agent.
/// </summary>
/// <remarks>
/// ⚠️ The re-verification is not redundancy, it is the feature. An agent that has been grinding
/// for two hours will produce a confident summary of a critical it did not find, and that summary
/// reads exactly like a real one. So a satisfied gate is run a second time, from a clean process,
/// before the mission is marked <see cref="MissionState.Achieved"/>. A gate that passes once and
/// fails once is a failed attempt with a note attached, not a win.
/// </remarks>
public sealed record SuccessGate
{
    public static SuccessGate OperatorJudged { get; } = new() { Kind = GateKind.OperatorJudged };

    public GateKind Kind { get; init; }

    /// <summary>Shell command for <see cref="GateKind.Command"/>.</summary>
    public string? Command { get; init; }

    /// <summary>Path for <see cref="GateKind.ArtifactMatch"/>, relative to the repository root.</summary>
    public string? ArtifactPath { get; init; }

    /// <summary>Regex the artifact must contain. Absent means existence is enough.</summary>
    public string? ArtifactPattern { get; init; }

    /// <summary>
    /// How the operator described the finish line in words. Shown to the agent even when a
    /// machine gate exists, because the gate says <em>whether</em> and this says <em>what</em>.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>Re-run the gate from a clean process before accepting it. Off only for cheap idempotent gates.</summary>
    public bool RequireReverification { get; init; } = true;

    public bool IsMachineCheckable => Kind is GateKind.Command or GateKind.ArtifactMatch;
}

/// <summary>Outcome of asking the gate. Deliberately three-valued.</summary>
public sealed record GateVerdict
{
    public required bool Satisfied { get; init; }

    /// <summary>True when the first evaluation passed and the re-run did not. The dangerous case.</summary>
    public bool Contradicted { get; init; }

    public required string Detail { get; init; }

    public int? ExitCode { get; init; }

    public static GateVerdict NotSatisfied(string detail) => new() { Satisfied = false, Detail = detail };
}
