using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Watchdog;

public enum HandoverReason
{
    None,

    /// <summary>Findings are costing several times what they did. The strong signal.</summary>
    Degrading,

    /// <summary>The window passed the ceiling. The floor under the strong signal.</summary>
    Ceiling,
}

public sealed record HandoverDecision
{
    public static HandoverDecision No { get; } = new() { Reason = HandoverReason.None, Detail = string.Empty };

    public required HandoverReason Reason { get; init; }

    public required string Detail { get; init; }

    public bool HandOver => Reason != HandoverReason.None;
}

public sealed record HandoverSettings
{
    /// <summary>
    /// Window size at which a session is replaced whatever the progress meter says.
    /// </summary>
    /// <remarks>
    /// A floor, not a criterion. The operator set it at 200,000 — deliberately low, and low is safe
    /// on the supervising side because that handover is nearly lossless. On the working side it
    /// costs a briefing and a re-read each time, so if sessions churn without getting anywhere this
    /// is the number to raise, and it is one field.
    ///
    /// ⚠️ Measured against the WINDOW, not against spend. The window is what makes the next turn
    /// expensive; cumulative spend is what the run has already cost and says nothing about whether
    /// carrying on is dear.
    /// </remarks>
    public int WindowCeiling { get; init; } = 200_000;

    /// <summary>Settled attempts before the progress meter is allowed an opinion.</summary>
    /// <remarks>
    /// The meter has its own floor for this; repeating it here would be two numbers to keep in
    /// step. This exists so a run with almost no history cannot be handed over on a trend computed
    /// from three data points.
    /// </remarks>
    public int MinimumAttempts { get; init; } = 8;
}

/// <summary>
/// Decides when a session should hand over to a fresh one rather than carry on.
/// </summary>
/// <remarks>
/// The operator's idea, and it generalises a decision this project already made and validated: the
/// relay collects its handover note <em>before</em> the switch, while the agent that has the context
/// still exists. Same arithmetic as offload, too — a session at 600,000 tokens costs multiples per
/// turn of one at 50,000, and a handover resets that.
///
/// <b>The strong trigger is cost per finding, not a token count.</b> That correction is the vault
/// looking back at us: the offload threshold once used a number the operator guessed, and the fix
/// was to measure. <see cref="ProgressMeter"/> already measures what a finding costs and can say
/// "degrading" — and a session whose findings have doubled in price is precisely the one worth
/// replacing, whatever its window happens to read. The ceiling is the backstop for the run where
/// degradation never fires because nothing is being found at all.
///
/// ⚠️ <b>The two roles are not symmetric, and this class does not pretend otherwise.</b> A
/// supervisor's handover is nearly lossless — <see cref="FourthWall"/> already forbade it from
/// depending on anything that is not written down. A worker's handover loses what it did not know
/// it knew, and no note fixes that. Whether ledger plus note gets a fresh worker back to where the
/// old one was is an open question with a number nobody has measured yet.
/// </remarks>
public static class HandoverWatch
{
    public static HandoverDecision Assess(
        IReadOnlyList<Attempt> attempts,
        int? window,
        HandoverSettings settings)
    {
        // Cost per finding first, because it is the signal that means something. A session can be
        // small and useless or large and productive, and only this tells them apart.
        if (attempts.Count >= settings.MinimumAttempts)
        {
            var progress = ProgressMeter.Assess(attempts);

            if (progress.Trend == ProgressTrend.Degrading)
            {
                return new HandoverDecision
                {
                    Reason = HandoverReason.Degrading,
                    Detail =
                        "findings are costing several times what they did earlier in this run — " +
                        progress.Verdict,
                };
            }
        }

        if (window is { } tokens && tokens >= settings.WindowCeiling)
        {
            return new HandoverDecision
            {
                Reason = HandoverReason.Ceiling,
                Detail =
                    $"the window has reached {tokens:N0} tokens, past the {settings.WindowCeiling:N0} " +
                    "ceiling — every turn from here re-reads all of it",
            };
        }

        return HandoverDecision.No;
    }

    /// <summary>
    /// What the outgoing session is asked to write before it goes.
    /// </summary>
    /// <remarks>
    /// Asked <b>while it is still healthy</b>, which is the whole reason this fires on a ceiling
    /// rather than on a session dying. An agent that has run out has nothing left to think with; one
    /// at the ceiling can still say what it came to believe.
    ///
    /// The questions are the ones a ledger cannot answer. What was tried is already recorded; what
    /// the agent came to <em>believe</em>, and which of its own assumptions it stopped trusting, are
    /// only in its head — and are the first two things somebody picking this up cold would ask.
    /// </remarks>
    public const string HandoverPrompt =
        "You are close to the point where a fresh session is cheaper than this one, so write your " +
        "handover now while you can still think clearly: `rollout handover \"<what you came to " +
        "believe>\" --dropped \"<assumptions you stopped trusting>\" --next \"<the most promising " +
        "thing you had not got to>\"`.\n\n" +
        "The ledger already says what you tried, so do not repeat it. Say the things it cannot " +
        "carry. Then carry on — you are not finished, and RolloutLoud will open your replacement " +
        "when it makes sense.";

    /// <summary>
    /// What the outgoing session is told on the turn it is actually replaced.
    /// </summary>
    /// <remarks>
    /// Said plainly, because the alternative is a window that simply stops answering. The sentence
    /// above used to be the only one there was, and it promised a replacement that nothing opened —
    /// so a session wrote its handover, was told to carry on, and carried on for the rest of the
    /// run in the same expensive window.
    /// </remarks>
    public const string ReplacedPrompt =
        "Your handover is recorded and RolloutLoud is opening your replacement now. **Stop here.** " +
        "Do not start another attempt — a fresh session is picking this up with your note, the " +
        "ledger and the mission block, and two sessions on one ledger is exactly what this swap " +
        "exists to avoid.\n\n" +
        "This window is finished. Nothing you type into it after this is part of the run.";
}
