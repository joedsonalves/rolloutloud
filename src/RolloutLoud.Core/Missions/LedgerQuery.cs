using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Missions;

public sealed record LedgerQuery
{
    /// <summary>failed | succeeded | blocked | duplicate | errored | declared. Null means any.</summary>
    public string? Outcome { get; init; }

    /// <summary>Only attempts made by one agent — useful after a relay.</summary>
    public string? Agent { get; init; }

    /// <summary>Only attempts made at one escalation tier.</summary>
    public int? Tier { get; init; }

    /// <summary>Substring, matched against the hypothesis, the command and the observation.</summary>
    public string? Contains { get; init; }

    /// <summary>Only attempts after this moment.</summary>
    public DateTimeOffset? Since { get; init; }

    public int Limit { get; init; } = LedgerQueryResult.DefaultLimit;

    public int Offset { get; init; }

    /// <summary>Include the command, artifact folder and exit code. Off by default.</summary>
    public bool Full { get; init; }
}

/// <summary>One attempt, trimmed to what a question about the past usually needs.</summary>
public sealed record LedgerEntry
{
    public required string Id { get; init; }

    public required string Outcome { get; init; }

    public required string Hypothesis { get; init; }

    public string? Learned { get; init; }

    public required int Tier { get; init; }

    public required string Agent { get; init; }

    public required DateTimeOffset At { get; init; }

    // ---- only when Full is asked for -------------------------------------------------------

    public string? Command { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Where the output went. A path, never the output.</summary>
    public string? Artifacts { get; init; }
}

public sealed record LedgerQueryResult
{
    /// <summary>What one call returns when nothing is asked for.</summary>
    public const int DefaultLimit = 20;

    /// <summary>The most one call can ever return, whatever is asked for.</summary>
    /// <remarks>
    /// A hard cap rather than a default, because the failure it prevents is not an accident. An
    /// agent that can fetch the whole ledger will, and a two-hundred-attempt ledger pasted into a
    /// context is exactly the cost subagent offload exists to avoid — a single call would undo the
    /// mode the operator switched on.
    /// </remarks>
    public const int MaxLimit = 50;

    public required IReadOnlyList<LedgerEntry> Entries { get; init; }

    /// <summary>How many attempts matched, before the limit.</summary>
    public required int Matched { get; init; }

    /// <summary>Everything in the ledger, matching or not.</summary>
    public required int Total { get; init; }

    public required int Offset { get; init; }

    /// <summary>Says what to do next, and it is usually "narrow it" rather than "page on".</summary>
    public required string Guidance { get; init; }
}

/// <summary>
/// Answers questions about what has already been tried, without handing over the whole ledger.
/// </summary>
/// <remarks>
/// The briefing caps its summary at forty entries so a long run cannot flood it, which left an
/// agent that genuinely needed an older attempt with nowhere to go — the point of A8. But the
/// endpoint that existed made the opposite mistake: <c>GET /attempts</c> returned every attempt in
/// full, so the only way to ask about the past was to import all of it.
///
/// **Both halves matter.** Making the question askable without making the dump easy is the design,
/// and the operator asked for exactly that: *asking for everything should be uncomfortable, or the
/// agent will ask for everything every time.*
///
/// So: filters are cheap, the default page is small, the ceiling is hard at fifty, and the answer
/// says how many matched so the agent knows to narrow rather than page blindly through a ledger
/// it is paying to read. Commands and artifact paths are omitted unless asked for, because "what
/// has been ruled out" almost never needs the exact argv.
/// </remarks>
public static class LedgerQueryRunner
{
    public static LedgerQueryResult Run(IReadOnlyList<Attempt> attempts, LedgerQuery query)
    {
        var matching = attempts.AsEnumerable();

        if (query.Outcome is { Length: > 0 } outcome)
        {
            matching = matching.Where(a =>
                a.Outcome.ToString().Equals(Normalize(outcome), StringComparison.OrdinalIgnoreCase));
        }

        if (query.Agent is { Length: > 0 } agent)
        {
            matching = matching.Where(a => a.AgentId.Equals(agent, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Tier is { } tier)
        {
            matching = matching.Where(a => a.Tier == tier);
        }

        if (query.Since is { } since)
        {
            matching = matching.Where(a => a.At >= since);
        }

        if (query.Contains is { Length: > 0 } needle)
        {
            matching = matching.Where(a =>
                Mentions(a.Hypothesis, needle) ||
                Mentions(a.Command, needle) ||
                Mentions(a.Observation, needle));
        }

        var matched = matching.ToList();
        var limit = Math.Clamp(query.Limit, 1, LedgerQueryResult.MaxLimit);
        var offset = Math.Max(0, query.Offset);

        // Newest first. A question about the past is almost always about the recent past, and
        // paging from the beginning of a two-hundred-attempt ledger to reach it would cost several
        // calls to arrive at what the first one should have returned.
        var page = matched
            .AsEnumerable()
            .Reverse()
            .Skip(offset)
            .Take(limit)
            .Select(a => Describe(a, query.Full))
            .ToList();

        return new LedgerQueryResult
        {
            Entries = page,
            Matched = matched.Count,
            Total = attempts.Count,
            Offset = offset,
            Guidance = Guide(matched.Count, page.Count, offset, query),
        };
    }

    private static string Guide(int matched, int returned, int offset, LedgerQuery query)
    {
        var shown = offset + returned;

        if (matched == 0)
        {
            // "Nothing matched your filter" and "nothing has been tried at all" are different
            // answers, and telling an agent the ledger is empty when forty attempts precede it
            // would be the worst lie this endpoint could tell.
            return HasFilter(query)
                ? "Nothing matched. That is an answer: no attempt like this has been made, so it is " +
                  "not a repeat."
                : "The ledger is empty. You are first.";
        }

        if (shown >= matched)
        {
            return $"All {matched} matching attempt(s), newest first.";
        }

        var remaining = matched - shown;

        return
            $"{returned} of {matched} matching, newest first; {remaining} older one(s) not shown. " +
            "Narrow with outcome, agent, tier or contains rather than paging — reading the whole " +
            "ledger costs you the context that offload exists to protect.";
    }

    private static bool HasFilter(LedgerQuery query) =>
        query.Outcome is { Length: > 0 } ||
        query.Agent is { Length: > 0 } ||
        query.Tier is not null ||
        query.Since is not null ||
        query.Contains is { Length: > 0 };

    private static LedgerEntry Describe(Attempt attempt, bool full) => new()
    {
        Id = attempt.Id,
        Outcome = attempt.Outcome.ToString(),
        Hypothesis = attempt.Hypothesis,
        Learned = attempt.Observation,
        Tier = attempt.Tier,
        Agent = attempt.AgentId,
        At = attempt.At,
        Command = full ? attempt.Command : null,
        ExitCode = full ? attempt.ExitCode : null,
        Artifacts = full ? attempt.ArtifactDirectory : null,
    };

    private static string Normalize(string outcome) => outcome.ToLowerInvariant() switch
    {
        "blocked" => nameof(AttemptOutcome.BlockedByScope),
        "success" => nameof(AttemptOutcome.Succeeded),
        "error" => nameof(AttemptOutcome.Errored),
        _ => outcome,
    };

    private static bool Mentions(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        Regex.IsMatch(
            haystack,
            Regex.Escape(needle),
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
}
