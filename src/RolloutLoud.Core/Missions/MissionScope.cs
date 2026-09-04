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

    /// <summary>
    /// Whether this scope already covers a target somebody wants to add.
    /// </summary>
    /// <remarks>
    /// Used to decide whether a later declaration is a narrowing or a widening. An unbounded scope
    /// covers everything, so the first declaration on one is always a narrowing — which is the case
    /// that matters: a run whose boundary is discovered rather than known up front starts with no
    /// boundary at all, and the first thing it learns can only make that smaller.
    /// </remarks>
    public bool Covers(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (Exclusions.Any(e => Mentions(target, e)))
        {
            return false;
        }

        return Unbounded || Targets.Count == 0 || Targets.Any(t => Mentions(target, t));
    }

    /// <summary>
    /// Replaces this scope with one that must be no wider.
    /// </summary>
    /// <remarks>
    /// <b>Narrowing only, and that rule is the whole feature.</b> A boundary that can be widened
    /// after the fact is not a boundary — it is a note the run edits when it becomes inconvenient,
    /// which is precisely the drift the scope exists to stop. At attempt forty, "let me just look
    /// at the host next door" has to fail against what was declared at attempt one.
    ///
    /// Exclusions only ever accumulate, for the same reason: a carve-out somebody made is a
    /// decision, and dropping it later would undo it silently.
    ///
    /// ⚠️ Authorisation is required here even though it is only a warning at creation. A scope
    /// declared mid-run is one nobody reviewed beforehand, so the written record of who allowed it
    /// is the only thing that makes the run attributable afterwards.
    /// </remarks>
    public ScopeNarrowing Narrow(
        IReadOnlyList<string> targets,
        IReadOnlyList<string> exclusions,
        string? authorization)
    {
        var wanted = targets.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();

        if (wanted.Count == 0)
        {
            return ScopeNarrowing.Refused(
                "Name at least one target. An empty declaration would leave the run unbounded, " +
                "which is the state this call exists to leave.");
        }

        if (string.IsNullOrWhiteSpace(authorization))
        {
            return ScopeNarrowing.Refused(
                "Name what authorises reaching these targets — the programme, the policy URL, the " +
                "engagement reference. A boundary declared mid-run was reviewed by nobody " +
                "beforehand, so the record is what makes the run attributable afterwards.");
        }

        var outside = wanted.Where(t => !Covers(t)).ToList();

        if (outside.Count > 0)
        {
            return ScopeNarrowing.Refused(
                "These are outside the scope already in force, and a scope can only ever be " +
                "narrowed: " + string.Join(", ", outside) + ". In force: " +
                (Unbounded ? "unbounded" : string.Join(", ", Targets)) + ".");
        }

        return ScopeNarrowing.Accepted(new MissionScope
        {
            Unbounded = false,
            Targets = wanted,
            Exclusions = [.. Exclusions.Concat(exclusions.Where(e => !string.IsNullOrWhiteSpace(e))).Distinct(StringComparer.OrdinalIgnoreCase)],
            Authorization = authorization.Trim(),
        });
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

/// <summary>The answer to "may I bound this run to these targets?".</summary>
public sealed record ScopeNarrowing
{
    public required bool Allowed { get; init; }

    public required string Reason { get; init; }

    /// <summary>The scope to install. Null when refused.</summary>
    public MissionScope? Scope { get; init; }

    public static ScopeNarrowing Refused(string reason) =>
        new() { Allowed = false, Reason = reason };

    public static ScopeNarrowing Accepted(MissionScope scope) => new()
    {
        Allowed = true,
        Scope = scope,
        Reason =
            "Bounded to " + string.Join(", ", scope.Targets) +
            ". From here the bridge refuses any command that does not name one of these, and this " +
            "can only be narrowed further, never widened.",
    };
}

public sealed record ScopeDecision
{
    public static ScopeDecision Allowed { get; } = new() { InScope = true, Reason = "In scope." };

    public required bool InScope { get; init; }

    public required string Reason { get; init; }

    public static ScopeDecision Blocked(string reason) => new() { InScope = false, Reason = reason };
}
