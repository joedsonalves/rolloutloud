using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Missions;

public enum AttemptOutcome
{
    /// <summary>
    /// Announced through the bridge but not yet reported on.
    /// </summary>
    /// <remarks>
    /// This state is what makes duplicate rejection actually work. Without it, an agent can
    /// declare the same command twice before either finishes and the ledger never notices,
    /// because nothing is written until a result arrives — which is the exact case that matters
    /// when two agents share one mission and both reach for the obvious idea first.
    /// </remarks>
    Declared,

    /// <summary>Ran, did not reach the objective. The ordinary case, and the useful one.</summary>
    Failed,

    /// <summary>Ran and the gate accepted it.</summary>
    Succeeded,

    /// <summary>Refused before running: outside <see cref="MissionScope"/>.</summary>
    BlockedByScope,

    /// <summary>Refused before running: the same idea, already tried.</summary>
    Duplicate,

    /// <summary>Crashed, timed out, or the tool was missing. Says nothing about the hypothesis.</summary>
    Errored,
}

/// <summary>
/// One thing the agent tried, with the reason it thought it would work.
/// </summary>
/// <remarks>
/// <see cref="Hypothesis"/> is required, and that is not bookkeeping. An agent forced to write
/// down why an approach should work before running it produces measurably fewer repeats — and
/// the ledger fed back to it becomes a list of ruled-out theories rather than a list of commands,
/// which is the difference between "don't run this again" and "this class of idea is dead".
/// </remarks>
public sealed record Attempt
{
    public required string Id { get; init; }

    public required string MissionId { get; init; }

    /// <summary>Which CLI ran it. Cross-agent relay needs to know who already failed at what.</summary>
    public required string AgentId { get; init; }

    /// <summary>Why the agent expected this to work. Required.</summary>
    public required string Hypothesis { get; init; }

    /// <summary>What was actually run.</summary>
    public required string Command { get; init; }

    public AttemptOutcome Outcome { get; init; } = AttemptOutcome.Failed;

    /// <summary>What was learned. The part that goes back into the next briefing.</summary>
    public string? Observation { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Escalation tier this attempt was made at.</summary>
    public int Tier { get; init; }

    /// <summary>Run folder holding stdout, stderr and any files. Keeps the ledger small.</summary>
    public string? ArtifactDirectory { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Size of the agent's context window when this was recorded.
    /// </summary>
    /// <remarks>
    /// The cost proxy for <see cref="ProgressMeter"/>, and the window rather than the delta on
    /// purpose: a cached session re-reads its whole context every turn, so what a turn costs is
    /// proportional to how big the window already was — not to how much this attempt added.
    ///
    /// Null when nothing could be read. The meter falls back to wall clock rather than treating
    /// an unknown as a zero, which would make an unmeasurable run look free.
    /// </remarks>
    public int? ContextTokens { get; init; }

    /// <summary>Stable fingerprint of the idea, used to reject repeats. See <see cref="Fingerprint"/>.</summary>
    public string Signature => Fingerprint(Command);

    /// <summary>
    /// Normalises a command down to its shape, so that changing a port, a timestamp or an output
    /// path does not disguise the same attempt as a new one.
    /// </summary>
    /// <remarks>
    /// Deliberately aggressive. A false "duplicate" costs one argued retry; a missed duplicate
    /// costs the loop its whole point, which is that it stops going in circles.
    /// </remarks>
    public static string Fingerprint(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\d+", "#", RegexOptions.None, TimeSpan.FromSeconds(1));
        normalized = Regex.Replace(normalized, @"[/\\][^\s]*[/\\]", "<path>", RegexOptions.None, TimeSpan.FromSeconds(1));
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
