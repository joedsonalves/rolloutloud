using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Watchdog;

public sealed record WakeSettings
{
    /// <summary>
    /// How long a question may sit unanswered before a supervisor is opened for it.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. The operator is usually there, and opening a session to answer a
    /// question they were about to answer themselves is both a waste and an irritation. Long enough
    /// that a human at the machine always wins the race; short enough that a run at 3am is not
    /// steering blind until morning.
    /// </remarks>
    public TimeSpan UnansweredFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the deliverable may go unreviewed after the agent last wrote to it.</summary>
    /// <remarks>
    /// Longer than the question timer, because an unread draft costs the run nothing while it is
    /// still being written — and reviewing a paragraph the agent is halfway through is noise.
    /// </remarks>
    public TimeSpan UnreviewedFor { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Never open two supervisors within this of each other, whatever the triggers say.</summary>
    /// <remarks>
    /// The money brake for this loop, and it is not optional. Every trigger here can stay true after
    /// a supervisor has looked and decided there was nothing to add — an unanswered question the
    /// supervisor deliberately left open still reads as unanswered — so without a floor the wake-up
    /// would spawn a session a minute for the rest of the run.
    /// </remarks>
    public TimeSpan NotMoreOftenThan { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Why a supervisor is being opened, or that none is needed.</summary>
public sealed record WakeDecision
{
    public static WakeDecision No { get; } = new() { Wake = false, Reason = string.Empty };

    public required bool Wake { get; init; }

    public required string Reason { get; init; }

    public static WakeDecision Yes(string reason) => new() { Wake = true, Reason = reason };
}

/// <summary>
/// Decides when a run has nobody watching it and needs one.
/// </summary>
/// <remarks>
/// The watchdog supervises the <em>worker</em>: it restarts an agent that stopped and waits out a
/// quota window. Nothing supervised the <em>supervisor</em> — so when that session ran out, the
/// agent's questions piled up unanswered and the run carried on with no one reading the deliverable.
///
/// <b>The trigger is a symptom, not a state.</b> "Is the supervisor idle?" is guesswork and would be
/// wrong in both directions; "has a question sat unanswered for ten minutes" is a fact already on
/// disk. Same reasoning as the give-up detector being grammatical rather than semantic: ask a
/// question that the record can answer.
///
/// ⚠️ <b>A woken supervisor may answer only where the operator delegated.</b> That is not a second
/// consent mechanism — it is the one they already give per mission, which says a supervising session
/// may act for them here. Without it, the woken session reads, reviews and drafts, and the question
/// stays open for the operator. The alternative was a model answering a model with no human anywhere
/// in the chain, on a run that could last all night.
/// </remarks>
public static class SupervisorWatch
{
    public static WakeDecision Assess(
        Mission mission,
        WakeSettings settings,
        DateTimeOffset now,
        DateTimeOffset? lastWoken,
        DateTimeOffset? deliverableWrittenAt)
    {
        if (mission.IsTerminal || mission.State != MissionState.Running)
        {
            return WakeDecision.No;
        }

        // The floor comes first, so a trigger that stays true after a supervisor has already looked
        // cannot spawn a session a minute for the rest of the run.
        if (lastWoken is { } last && now - last < settings.NotMoreOftenThan)
        {
            return WakeDecision.No;
        }

        var stale = mission.Questions
            .Where(q => q.IsOpen && now - q.At >= settings.UnansweredFor)
            .OrderBy(q => q.At)
            .FirstOrDefault();

        if (stale is not null)
        {
            return WakeDecision.Yes(
                $"a question has been open for {Describe(now - stale.At)} with nobody answering: " +
                $"\"{stale.Question}\"");
        }

        if (deliverableWrittenAt is { } written &&
            now - written >= settings.UnreviewedFor &&
            LastReview(mission) is var reviewed &&
            (reviewed is null || reviewed < written))
        {
            return WakeDecision.Yes(
                $"the deliverable was last written {Describe(now - written)} ago and nobody has " +
                "reviewed it since");
        }

        return WakeDecision.No;
    }

    private static DateTimeOffset? LastReview(Mission mission) =>
        mission.Reviews.Count == 0 ? null : mission.Reviews.Max(r => r.At);

    /// <summary>Whole minutes or hours, because "00:11:43.2" in a reason line reads as machine noise.</summary>
    private static string Describe(TimeSpan span) =>
        span < TimeSpan.FromHours(1)
            ? $"{(int)span.TotalMinutes} minute(s)"
            : $"{span.TotalHours:0.#} hour(s)";
}
