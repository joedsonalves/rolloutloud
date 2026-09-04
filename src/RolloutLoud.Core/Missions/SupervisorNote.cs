namespace RolloutLoud.Core.Missions;

/// <summary>
/// What the supervisor said after reading the deliverable.
/// </summary>
/// <remarks>
/// The channel that was missing, and without it <see cref="FourthWall"/> described a job nobody
/// could do. A supervisor behind the wall reads the deliverable and forms an opinion — "the
/// reproduction skips the step that actually triggers it", "there is no impact stated" — and until
/// now there was nowhere to put that. The ledger records what the <em>agent</em> tried; nothing
/// carried a sentence in the other direction.
///
/// <b>It arrives through the call the agent already makes.</b> Notes come back on
/// <c>GET /continue</c>, which the agent hits between attempts because it has to. A channel the
/// agent must remember to poll is a channel that goes unread on the run where it mattered.
///
/// <b>Delivered once, kept for ever.</b> Repeating a note every turn would make the agent's
/// briefing an echo chamber; dropping it would lose the record of how a run was steered. So each
/// note is handed over once and then stays on the mission as history.
///
/// ⚠️ <b>Every note is shown to the operator, and that is the point of writing it down.</b> Behind
/// the wall the supervisor is steering a run the operator cannot see the raw material of. If the
/// steering were also invisible, the operator would have delegated their eyes and their voice and
/// kept only the bill. The activity log is where that stays visible.
/// </remarks>
public sealed record SupervisorNote
{
    public required string Id { get; init; }

    /// <summary>Who wrote it. A label for the record, the same as elsewhere on this bridge.</summary>
    public required string From { get; init; }

    /// <summary>What the supervisor wants changed, in their own words.</summary>
    public required string Note { get; init; }

    /// <summary>
    /// The specific gaps, listed.
    /// </summary>
    /// <remarks>
    /// Separate from the prose because a list survives being skimmed and a paragraph does not — and
    /// an agent forty turns deep is skimming. It is also the part that can be read back later as
    /// "was this ever addressed?", which prose cannot.
    /// </remarks>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>
    /// Whether the agent should deal with this before carrying on.
    /// </summary>
    /// <remarks>
    /// Deliberately not a stop. A supervisor is not a stop condition — the gate and the budgets
    /// are, and giving a second model the power to end a run would put back exactly the
    /// self-judgement this product exists to remove. Blocking means "do this next", not "stop".
    /// </remarks>
    public bool Blocking { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the agent was handed this. Null until it collects it.</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    public bool IsPending => DeliveredAt is null;

    public static string NewId() => "n-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>How the note reads to the agent that receives it.</summary>
    public string ForAgent()
    {
        var lines = new List<string>
        {
            (Blocking ? "Deal with this before your next attempt: " : "From your supervisor: ") + Note.Trim(),
        };

        lines.AddRange(Missing.Select(m => "  - still missing: " + m.Trim()));

        return string.Join(Environment.NewLine, lines);
    }
}
