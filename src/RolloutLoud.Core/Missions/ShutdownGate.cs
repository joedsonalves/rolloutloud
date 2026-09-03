namespace RolloutLoud.Core.Missions;

public enum ShutdownVerdict
{
    /// <summary>The mission is genuinely finished. Closing is allowed.</summary>
    Allowed,

    /// <summary>Allowed, and the operator has opted in to it happening without a click.</summary>
    AllowedUnattended,

    /// <summary>Refused. The reason says what is still true that should not be.</summary>
    Refused,
}

public sealed record ShutdownDecision(ShutdownVerdict Verdict, string Reason)
{
    public bool Allowed => Verdict is ShutdownVerdict.Allowed or ShutdownVerdict.AllowedUnattended;
}

/// <summary>
/// Decides whether an agent may close RolloutLoud.
/// </summary>
/// <remarks>
/// The operator's rule, in their words: *only if the task was actually completed — "I could not
/// do it" is not a completed task.*
///
/// That is the same rule as the rest of the product, applied to one more decision. An agent
/// asking to shut the tool down is asking to end its own supervision, and an agent that has been
/// grinding for hours has every incentive to believe it is finished. So the request is not
/// evaluated on what the agent says; it is evaluated on <see cref="MissionState"/>, which only
/// <see cref="MissionEngine.EvaluateGateAsync"/> can set to <see cref="MissionState.Achieved"/>
/// — and which requires the gate to pass twice, from clean processes.
///
/// Three refusals matter, and each is a different mistake:
///
/// - **Not achieved.** Exhausted, aborted, still running — all mean the objective was not met.
///   Exhausted is the loudest: it is the state an agent reaches by running out of budget, and it
///   is exactly what "I could not do it" looks like from the outside.
/// - **No machine gate.** A mission the operator judges cannot be closed by an agent at all,
///   because nothing but the operator can say it is done.
/// - **Another mission is still running.** One agent finishing does not entitle it to close a
///   window another agent is working in.
/// </remarks>
public static class ShutdownGate
{
    public static ShutdownDecision Evaluate(
        Mission? mission,
        IReadOnlyList<Mission> allMissions,
        bool unattendedAllowed)
    {
        if (mission is null)
        {
            return new ShutdownDecision(
                ShutdownVerdict.Refused,
                "There is no mission to have finished. RolloutLoud only closes on a completed one.");
        }

        if (!mission.Gate.IsMachineCheckable)
        {
            return new ShutdownDecision(
                ShutdownVerdict.Refused,
                "This mission has no machine-checkable gate, so only the operator can say it is done. " +
                "Report your result and leave the window open.");
        }

        if (mission.State != MissionState.Achieved)
        {
            var detail = mission.State switch
            {
                MissionState.Exhausted =>
                    "the mission is Exhausted — a stop condition fired before the gate was satisfied. " +
                    "Running out of budget is not completing the objective",
                MissionState.Aborted => "the mission was aborted",
                MissionState.Paused => "the mission is paused",
                _ => "the mission is still running and the gate has not been satisfied",
            };

            return new ShutdownDecision(
                ShutdownVerdict.Refused,
                $"Refused: {detail}. RolloutLoud closes when the objective is met, not when you are " +
                "finished trying. If you believe it is met, POST to the gate and let it decide.");
        }

        var othersRunning = allMissions
            .Where(m => m.Id != mission.Id && m.State is MissionState.Running or MissionState.Paused)
            .Select(m => m.Id)
            .ToList();

        if (othersRunning.Count > 0)
        {
            return new ShutdownDecision(
                ShutdownVerdict.Refused,
                $"Your mission is achieved, but {othersRunning.Count} other mission(s) are still open " +
                $"({string.Join(", ", othersRunning)}). Another agent is working in this window.");
        }

        return unattendedAllowed
            ? new ShutdownDecision(
                ShutdownVerdict.AllowedUnattended,
                $"Mission {mission.Id} is achieved and re-verified, nothing else is open, and the " +
                "operator allowed unattended shutdown. Closing.")
            : new ShutdownDecision(
                ShutdownVerdict.Allowed,
                $"Mission {mission.Id} is achieved and re-verified. A button to close RolloutLoud is " +
                "waiting for the operator — unattended shutdown is switched off.");
    }
}
