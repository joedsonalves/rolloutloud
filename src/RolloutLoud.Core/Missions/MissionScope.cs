using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Missions;

/// <summary>
/// The engagement boundary, enforced rather than requested.
/// </summary>
/// <remarks>
/// "Respecting the scope as asked" cannot live in a prompt. A prompt is a suggestion that
/// competes with two hours of frustration, and frustration wins. So the scope is matched against
/// every command the agent routes through the bridge, and a violation comes back as a refusal —
/// which is itself recorded as a failed attempt, with the reason, so the agent learns the edge
/// instead of hammering it.
///
/// ⚠️ This class is a guard rail, not a sandbox. It reads the command the agent *declared*.
/// An agent running unsupervised in an elevated terminal can always reach past it. The scope is
/// there to stop honest drift, and it is the reason <see cref="Authorization"/> is mandatory for
/// anything but <see cref="Unrestricted"/>.
/// </remarks>
public sealed record MissionScope
{
    public static MissionScope Unrestricted { get; } = new() { Unbounded = true };

    /// <summary>No boundary declared. Legitimate for a local refactor; never for a live target.</summary>
    public bool Unbounded { get; init; }

    /// <summary>Hosts, domains and CIDR blocks the run may touch. Wildcards with <c>*</c> allowed.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>Targets explicitly carved out, which win over <see cref="Targets"/>.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>
    /// Who authorised this and under what reference. Free text, but required whenever
    /// <see cref="Targets"/> is non-empty — the record exists so the run is attributable later.
    /// </summary>
    public string? Authorization { get; init; }

    public bool IsDeclared => Unbounded || Targets.Count > 0;

    public bool NeedsAuthorization => Targets.Count > 0 && string.IsNullOrWhiteSpace(Authorization);

    /// <summary>
    /// Decides whether a command may run. Exclusions are checked first so a carve-out inside a
    /// broad range cannot be re-included by a wider pattern.
    /// </summary>
    public ScopeDecision Evaluate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ScopeDecision.Blocked("Empty command.");
        }

        foreach (var exclusion in Exclusions)
        {
            if (Mentions(command, exclusion))
            {
                return ScopeDecision.Blocked($"'{exclusion}' is explicitly excluded from the scope.");
            }
        }

        if (Unbounded || Targets.Count == 0)
        {
            return ScopeDecision.Allowed;
        }

        foreach (var target in Targets)
        {
            if (Mentions(command, target))
            {
                return ScopeDecision.Allowed;
            }
        }

        return ScopeDecision.Blocked(
            "No in-scope target appears in the command. In scope: " + string.Join(", ", Targets));
    }

    private static bool Mentions(string command, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = @"\b" + Regex.Escape(pattern.Trim()).Replace(@"\*", @"[^\s]*", StringComparison.Ordinal);
        return Regex.IsMatch(command, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
}

public sealed record ScopeDecision
{
    public static ScopeDecision Allowed { get; } = new() { InScope = true, Reason = "In scope." };

    public required bool InScope { get; init; }

    public required string Reason { get; init; }

    public static ScopeDecision Blocked(string reason) => new() { InScope = false, Reason = reason };
}
