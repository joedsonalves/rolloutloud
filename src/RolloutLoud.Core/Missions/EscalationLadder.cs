namespace RolloutLoud.Core.Missions;

/// <summary>
/// What "try the possible and the impossible" means in practice.
/// </summary>
/// <remarks>
/// An agent left to its own devices does not run out of ideas — it runs out of *kinds* of ideas,
/// and then generates variations of the last one forever. The ladder names the kinds. When the
/// ledger stops producing new information, the tier goes up and the briefing changes shape, which
/// is a different instruction than "try harder".
///
/// Tier 3 is the one that earns its keep: handing the same mission and the same failed ledger to
/// a different CLI regularly finds what the first could not, because the failure was in the model's
/// habits rather than in the target.
/// </remarks>
public static class EscalationLadder
{
    public const int MaxTier = 4;

    public static string NameOf(int tier) => tier switch
    {
        0 => "Direct",
        1 => "Tooling variation",
        2 => "Composition",
        3 => "Cross-agent relay",
        _ => "Operator consult",
    };

    /// <summary>
    /// The instruction injected into the briefing at each tier. Written as an order rather than a
    /// hint: at tier 2 the agent has already ignored two rounds of suggestion.
    /// </summary>
    public static string InstructionFor(int tier) => tier switch
    {
        0 =>
            "Work the objective the direct way. State your hypothesis before each attempt.",

        1 =>
            "The direct approach is exhausted. Change tools, not parameters. If you used one scanner, " +
            "use a different one; if you used a library, drop to the protocol underneath it. Re-running " +
            "the same tool with new flags counts as the same attempt and will be rejected.",

        2 =>
            "Single tools are exhausted. Chain them. Compose a result from one step into the input of " +
            "another, look for the objective in the seam between two components rather than inside " +
            "either, and consider states the target only reaches under load, out of order, or partway " +
            "through a transaction.",

        3 =>
            "This agent's repertoire is spent. The mission is being relayed to a different CLI with your " +
            "full ledger attached. Before you hand off, write the one paragraph you would want to read " +
            "if you were picking this up cold: what you now believe about the target, and which of your " +
            "assumptions you no longer trust.",

        _ =>
            "All tiers are exhausted. Stop and produce an operator brief: what was tried by kind, what " +
            "each ruled out, the single most promising unexplored direction, and precisely what you " +
            "would need — access, tool, or information — to pursue it.",
    };

    /// <summary>
    /// Whether the run has stopped learning. Measured as novelty of the recent window, not as
    /// failure count: fifty failures that each rule something out is progress, and five repeats
    /// of one idea is not.
    /// </summary>
    public static bool ShouldEscalate(IReadOnlyList<Attempt> recent, int plateauThreshold)
    {
        // Declarations are intentions, not results. Counting them would escalate on the strength
        // of work that has not finished yet, and the shove would land while the agent was still
        // mid-idea.
        var settled = recent.Where(a => a.Outcome != AttemptOutcome.Declared).ToList();

        if (settled.Count < plateauThreshold)
        {
            return false;
        }

        var window = settled.TakeLast(plateauThreshold).ToList();

        // Nothing new to say about the target.
        var informative = window.Count(a =>
            a.Outcome is AttemptOutcome.Failed && !string.IsNullOrWhiteSpace(a.Observation));

        if (informative == 0)
        {
            return true;
        }

        // Or: the ideas themselves have collapsed onto one shape.
        var distinct = window.Select(a => a.Signature).Distinct().Count();
        return distinct <= window.Count / 2;
    }
}
