using RolloutLoud.Core.Agents;

namespace RolloutLoud.Core.Missions;

public sealed record RelayChoice(string? AgentId, string Reason)
{
    public bool CanRelay => AgentId is not null;
}

/// <summary>
/// Decides which CLI a stuck mission goes to next.
/// </summary>
/// <remarks>
/// Tier 3 of the ladder, and the rung with the best return. Handing the same objective and the
/// same ledger to a different model regularly finds what the first could not — because the
/// failure was in that model's habits rather than in the problem. The ledger comes along, so the
/// new agent cannot redo the spent ideas even if it wants to; what it brings is a different set
/// of instincts about what to try instead.
///
/// Two rules, and both matter more than they look:
///
/// **An agent that has already worked this mission is not a candidate.** Rotating back to it
/// would produce the same habits that got stuck, and — because the ledger forbids its own spent
/// attempts — it would arrive with fewer moves than it had the first time.
///
/// **An agent that is not installed is not a candidate.** The relay fires unattended, and handing
/// the mission to a missing CLI ends the run with a launch error at the exact rung most likely to
/// have found the answer.
///
/// When nobody is left, <see cref="RelayChoice.CanRelay"/> is false and the caller goes to tier 4
/// — stop and brief the operator — rather than spinning on a rung it cannot climb.
/// </remarks>
public static class RelayPlanner
{
    public static RelayChoice ChooseNext(
        Mission mission,
        IReadOnlyList<AgentDescriptor> agents,
        Func<AgentDescriptor, bool>? isAvailable = null)
    {
        var available = isAvailable ?? AgentAvailability.CanBeRelayedTo;

        var worked = new HashSet<string>(mission.RelayHistory, StringComparer.OrdinalIgnoreCase)
        {
            mission.AgentId,
        };

        var candidates = agents
            .Where(a => !worked.Contains(a.Id))
            .ToList();

        if (candidates.Count == 0)
        {
            return new RelayChoice(
                null,
                $"Every configured agent has already worked this mission ({string.Join(", ", worked)}). " +
                "There is nobody left with a different set of habits to bring.");
        }

        var installed = candidates.Where(a => available(a)).ToList();

        if (installed.Count == 0)
        {
            return new RelayChoice(
                null,
                "The agents that have not worked this mission (" +
                string.Join(", ", candidates.Select(c => c.Id)) +
                ") are either not installed or have no one-shot prompt argument configured, so they " +
                "cannot be driven headlessly. Install one, or add PromptArguments in agents.json.");
        }

        var next = installed[0];
        return new RelayChoice(
            next.Id,
            $"Handing the mission to {next.DisplayName}. It has not worked this one, and it brings " +
            "a different set of instincts to a ledger that already rules out what has been tried.");
    }
}
