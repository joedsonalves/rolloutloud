namespace RolloutLoud.Core.Missions;

/// <summary>
/// The agent asking the supervisor something, without stopping to wait for the answer.
/// </summary>
/// <remarks>
/// <b>This exists because the alternative is the failure the whole product was built against.</b> A
/// real run reached a fork it could not settle alone — three programmes, and the evidence for
/// choosing between them sat in files the agent had read. Having no channel, it did what a CLI
/// agent always does: it printed a menu and stopped. That is a hand-back. It is the same move as
/// "let me know if you'd like me to try another approach", wearing better manners, and it stops the
/// run just as dead — worse behind <see cref="FourthWall"/>, where the menu is visible only to the
/// operator and the supervisor is the one who should be answering.
///
/// So asking is explicitly <b>not</b> blocking. The agent records the question, carries on with
/// whatever does not depend on the answer, and collects it on its next <c>/continue</c> — the same
/// way <see cref="SupervisorNote"/> travels in the other direction. A question with nobody at the
/// other end costs the run nothing; a menu costs it everything.
///
/// ⚠️ <b>Options are a courtesy, never a fence.</b> The supervisor may answer with none of them, and
/// the agent has to accept that — an answer that had to be one of the agent's three choices would
/// let the agent frame the decision it claims to be delegating.
/// </remarks>
public sealed record AgentQuestion
{
    public required string Id { get; init; }

    /// <summary>Which agent is asking.</summary>
    public required string From { get; init; }

    /// <summary>The question, in a sentence somebody can answer without the raw material.</summary>
    public required string Question { get; init; }

    /// <summary>
    /// What the agent believes the choices are, and why it cannot settle it alone.
    /// </summary>
    /// <remarks>
    /// The reasoning matters more than the list. Behind the wall the supervisor cannot see what the
    /// agent saw, so a question with no grounds is one that can only be answered by guessing — and
    /// a guess from the supervisor is worse than a decision from the agent, which at least read the
    /// evidence.
    /// </remarks>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>What the agent will do if nobody answers. Required thinking, not required text.</summary>
    /// <remarks>
    /// Asked for because a run that cannot proceed without an answer has not asked a question, it
    /// has stopped — and naming the fallback out loud is what turns one into the other.
    /// </remarks>
    public string? IfUnanswered { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public string? Answer { get; init; }

    public string? AnsweredBy { get; init; }

    public DateTimeOffset? AnsweredAt { get; init; }

    /// <summary>When the agent collected the answer. Answers, like notes, are handed over once.</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    public bool IsOpen => Answer is null;

    public bool IsUndelivered => Answer is not null && DeliveredAt is null;

    public static string NewId() => "q-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>How an answered question reads to the agent that asked it.</summary>
    public string ForAgent() =>
        $"Answer to your question \"{Question.Trim()}\" — {AnsweredBy ?? "the supervisor"} says: " +
        (Answer ?? string.Empty).Trim();
}
