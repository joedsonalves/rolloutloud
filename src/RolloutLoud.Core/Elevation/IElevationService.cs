namespace RolloutLoud.Core.Elevation;

/// <summary>
/// Whether this process holds administrative rights, and how to get them.
/// </summary>
/// <remarks>
/// The design decision behind this interface is worth stating, because it is the one that makes
/// the fluid buttons work at all.
///
/// RolloutLoud does not try to defeat the OS prompt. It **becomes the broker**: the operator
/// elevates RolloutLoud once, consenting once, and from then on every child process it starts
/// inherits that token with no further prompt. So an agent that cannot run a privileged command
/// itself posts a button, and the button runs elevated — the agent may even invoke it itself,
/// which is what the operator asked for, without anything having bypassed the prompt. One
/// consent, recorded, at a moment the operator chose.
///
/// ⚠️ A non-elevated RolloutLoud cannot start an elevated child silently on any supported OS,
/// and pretending otherwise is how a tool ends up shipping a UAC bypass. When elevation is
/// needed and absent, the answer is <see cref="RelaunchElevatedAsync"/> — restart the whole app
/// through the OS prompt — never a workaround.
/// </remarks>
public interface IElevationService
{
    /// <summary>True when this process can already start elevated children without prompting.</summary>
    bool IsElevated { get; }

    /// <summary>Whether the platform can escalate at all. False on a locked-down or unknown OS.</summary>
    bool CanElevate { get; }

    /// <summary>What the operator will be shown by the OS, so the warning dialog can be honest.</summary>
    string PromptDescription { get; }

    /// <summary>
    /// Restarts RolloutLoud through the OS elevation prompt, preserving the repository anchor.
    /// Returns false when the operator declines — a decline is an answer, not an error, and the
    /// app carries on unelevated.
    /// </summary>
    Task<bool> RelaunchElevatedAsync(string repositoryRoot, CancellationToken cancellationToken = default);
}
