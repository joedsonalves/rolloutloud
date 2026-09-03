namespace RolloutLoud.Core.Buttons;

public enum ButtonDisposition
{
    /// <summary>Waits for the operator. The safe default for anything unrecognised.</summary>
    NeedsOperator,

    /// <summary>Matched the allowlist; the agent may invoke it itself.</summary>
    AutoInvokable,
}

public enum ButtonStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Dismissed,
}

/// <summary>
/// A command an agent needs run but cannot run itself, surfaced in the UI as a button.
/// </summary>
/// <remarks>
/// The motivating case is small and completely real: Hermes needs Chrome listening on port 9222
/// and cannot start it. Today that ends the run — the agent says what it needs and waits for a
/// human who is asleep. With a fluid button the agent posts the command, RolloutLoud (already
/// elevated) runs it, and the run continues.
///
/// The design tension is that this is, by construction, an agent asking for arbitrary code
/// execution at whatever privilege RolloutLoud holds. Hence <see cref="Disposition"/>: an agent
/// may auto-invoke only what the operator has already blessed by pattern in
/// <see cref="ButtonAllowlist"/>. Everything else lights up and waits for a click. The allowlist
/// is the consent, granted in advance and in writing, and it is the reason auto-invocation is not
/// simply a hole.
/// </remarks>
public sealed record FluidButton
{
    public required string Id { get; init; }

    /// <summary>Label on the button. Written by the agent, so it is displayed as text, never as markup.</summary>
    public required string Title { get; init; }

    /// <summary>Why the agent needs it. Shown under the title so the operator can judge before clicking.</summary>
    public string? Rationale { get; init; }

    /// <summary>The command line to run, verbatim and visible before it runs.</summary>
    public required string Command { get; init; }

    /// <summary>Working directory. Defaults to the repository root when absent.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Which agent asked. Attribution matters when three of them are running.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Mission the request belongs to, when there is one.</summary>
    public string? MissionId { get; init; }

    public ButtonDisposition Disposition { get; init; } = ButtonDisposition.NeedsOperator;

    public ButtonStatus Status { get; init; } = ButtonStatus.Pending;

    /// <summary>True when the command needs the elevated token, not merely a process.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Fire-and-forget: do not hold the caller waiting for a long-lived process like a browser.</summary>
    public bool Detached { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? InvokedAt { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>First lines of output, for the UI. The full capture lives in the run folder.</summary>
    public string? OutputExcerpt { get; init; }

    public bool IsOpen => Status is ButtonStatus.Pending or ButtonStatus.Running;
}
