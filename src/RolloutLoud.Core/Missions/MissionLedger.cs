using RolloutLoud.Core.Safety;

namespace RolloutLoud.Core.Missions;

/// <summary>Why an attempt was refused before it ran.</summary>
public sealed record AttemptAdmission
{
    public required bool Admitted { get; init; }

    public required AttemptOutcome Outcome { get; init; }

    public required string Reason { get; init; }

    public static AttemptAdmission Ok { get; } =
        new() { Admitted = true, Outcome = AttemptOutcome.Failed, Reason = "Admitted." };
}

/// <summary>
/// Every attempt made against one mission, and the rules about what may be attempted next.
/// </summary>
/// <remarks>
/// This is the memory that makes the loop relentless instead of merely repetitive. The agent is
/// stateless across rounds by nature — a fresh subagent has never heard of the last forty
/// attempts — so the ledger, not the agent, is what remembers.
///
/// It is deliberately not thread-safe by itself; the bridge serialises writes behind one lock,
/// because two agents on the same mission racing to append is a real case (cross-agent relay)
/// and getting a duplicate past the check would defeat the point.
/// </remarks>
public sealed class MissionLedger
{
    private readonly List<Attempt> _attempts = [];
    private readonly HashSet<string> _signatures = new(StringComparer.Ordinal);

    public MissionLedger(string missionId, IEnumerable<Attempt>? existing = null)
    {
        MissionId = missionId;
        foreach (var attempt in existing ?? [])
        {
            _attempts.Add(attempt);
            _signatures.Add(attempt.Signature);
        }
    }

    public string MissionId { get; }

    public IReadOnlyList<Attempt> Attempts => _attempts;

    public int Count => _attempts.Count;

    public int FailureCount => _attempts.Count(a => a.Outcome is AttemptOutcome.Failed);

    /// <summary>
    /// How long a declared-but-unreported attempt holds its claim on an idea.
    /// </summary>
    /// <remarks>
    /// Without an expiry, an agent that crashes between declaring and reporting locks that
    /// command out of the mission permanently — and the failure looks like the duplicate check
    /// being wrong, which is the kind of thing that gets the check disabled rather than fixed.
    /// Half an hour is longer than any single attempt should take and short enough that a crash
    /// costs one stale entry, not the idea itself.
    /// </remarks>
    public static TimeSpan DeclarationTimeout { get; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Decides whether an attempt may proceed. Runs before the command, so a duplicate or an
    /// out-of-scope command costs nothing but the round trip.
    /// </summary>
    public AttemptAdmission Admit(string command, MissionScope scope)
    {
        var decision = scope.Evaluate(command);
        if (!decision.InScope)
        {
            return new AttemptAdmission
            {
                Admitted = false,
                Outcome = AttemptOutcome.BlockedByScope,
                Reason = decision.Reason,
            };
        }

        var signature = Attempt.Fingerprint(command);
        if (!_signatures.Contains(signature))
        {
            return AttemptAdmission.Ok;
        }

        // Look past earlier refusals of this same idea. A rejection is bookkeeping, not a result,
        // and quoting one back reads as "this concluded: it was a duplicate" — which tells the
        // agent nothing about the target and makes the ledger look like it is arguing with itself.
        var previous =
            _attempts.LastOrDefault(a =>
                a.Signature == signature &&
                a.Outcome is not (AttemptOutcome.Duplicate or AttemptOutcome.BlockedByScope))
            ?? _attempts.LastOrDefault(a => a.Signature == signature);

        // A claim that was never reported on has expired: the agent holding it is gone. Let the
        // idea go back on the table rather than burying it because of somebody else's crash.
        if (previous is { Outcome: AttemptOutcome.Declared } &&
            DateTimeOffset.UtcNow - previous.At > DeclarationTimeout)
        {
            Expire(previous);
            return AttemptAdmission.Ok;
        }

        return new AttemptAdmission
        {
            Admitted = false,
            Outcome = AttemptOutcome.Duplicate,
            Reason = previous?.Outcome switch
            {
                AttemptOutcome.Declared =>
                    "Another agent declared this exact attempt at " + previous.At.ToString("u") +
                    " and has not reported back yet. Do something else rather than duplicating it.",
                null =>
                    "This is the same attempt as one already made. Change the kind of approach, not its parameters.",
                _ =>
                    $"This is the same attempt as one already made ({previous.At:u}), which concluded: " +
                    (previous.Observation?.TrimEnd('.') ?? "no observation recorded") +
                    ". Change the kind of approach, not its parameters.",
            },
        };
    }

    /// <summary>
    /// Files a result, replacing this agent's own declaration of the same command rather than
    /// appending beside it — otherwise every attempt would appear twice, once as an intention
    /// and once as an outcome, and the briefing would read as double the history.
    /// </summary>
    public void Record(Attempt attempt)
    {
        var declaredIndex = _attempts.FindLastIndex(a =>
            a.Outcome == AttemptOutcome.Declared && a.Signature == attempt.Signature);

        if (declaredIndex >= 0)
        {
            _attempts[declaredIndex] = attempt with { At = _attempts[declaredIndex].At };
        }
        else
        {
            _attempts.Add(attempt);
        }

        _signatures.Add(attempt.Signature);
    }

    /// <summary>Marks a stale declaration as never reported, so the ledger says what happened.</summary>
    private void Expire(Attempt declaration)
    {
        var index = _attempts.IndexOf(declaration);
        if (index < 0)
        {
            return;
        }

        _attempts[index] = declaration with
        {
            Outcome = AttemptOutcome.Errored,
            Observation =
                "Declared but never reported on — the agent holding it stopped. Says nothing about " +
                "whether the idea works.",
        };

        _signatures.Remove(declaration.Signature);
    }

    /// <summary>
    /// The ledger as the next agent should read it: ruled-out theories, newest last, capped.
    /// </summary>
    /// <remarks>
    /// The cap is the entire reason this method exists rather than the caller formatting
    /// <see cref="Attempts"/>. Two hundred attempts pasted into a briefing is the context blowup
    /// the offload mode exists to avoid — so the briefing gets the shape of what failed, not the
    /// transcript. Full detail stays in the run folders, addressable by id.
    /// </remarks>
    public string Summarize(int maxEntries = 40)
    {
        if (_attempts.Count == 0)
        {
            return "No attempts yet. You are first.";
        }

        var lines = new List<string>
        {
            $"{_attempts.Count} attempt(s) so far. Do not repeat any of these — change the kind of approach.",
            string.Empty,
        };

        var shown = _attempts.TakeLast(maxEntries).ToList();
        if (shown.Count < _attempts.Count)
        {
            lines.Add(
                $"[{_attempts.Count - shown.Count} earlier attempt(s) omitted. Ask for a specific one " +
                "rather than all of them: GET /v1/missions/active/attempts?contains=<text> — also " +
                "filterable by outcome, agent, tier and since.]");
            lines.Add(string.Empty);
        }

        foreach (var attempt in shown)
        {
            var label = attempt.Outcome == AttemptOutcome.Declared ? "IN FLIGHT" : attempt.Outcome.ToString();
            lines.Add($"- [T{attempt.Tier} {label}] {attempt.Hypothesis}");
            lines.Add($"    ran: {Truncate(attempt.Command, 200)}");
            if (!string.IsNullOrWhiteSpace(attempt.Observation))
            {
                // Defanged, not fenced per entry. An observation is written by an agent that has
                // been reading target output, so it can carry text nobody vouched for — but
                // wrapping forty of them in forty fences produces a document where the warning is
                // wallpaper. The whole ledger block is fenced once by the briefing; what happens
                // here is breaking any marker that would close that fence early.
                lines.Add($"    learned: {Truncate(UntrustedText.Defang(attempt.Observation), 300)}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
