namespace RolloutLoud.Core.Missions;

public enum ProgressTrend
{
    /// <summary>Too few settled attempts, or too few findings, to say anything honest.</summary>
    Unknown,

    /// <summary>Findings are getting cheaper. The run is warming up.</summary>
    Improving,

    /// <summary>Roughly what it was costing. Ordinary.</summary>
    Steady,

    /// <summary>Each finding costs materially more than it did. The run is grinding.</summary>
    Degrading,

    /// <summary>The recent stretch bought nothing at all, at whatever it cost.</summary>
    Stalled,
}

public enum CostUnit
{
    None,

    /// <summary>Context window size at each attempt. What the model actually re-reads per turn.</summary>
    Tokens,

    /// <summary>Wall clock. Used when no token reading is available.</summary>
    Seconds,
}

public sealed record ProgressReading
{
    public required ProgressTrend Trend { get; init; }

    public required CostUnit Unit { get; init; }

    /// <summary>Cost per finding over the recent half of the sample.</summary>
    public double RecentCostPerFinding { get; init; }

    /// <summary>Cost per finding over the earlier half — the run's own baseline.</summary>
    public double BaselineCostPerFinding { get; init; }

    public int RecentFindings { get; init; }

    public int SampleSize { get; init; }

    public required string Verdict { get; init; }

    public static ProgressReading NotEnoughData(string why) => new()
    {
        Trend = ProgressTrend.Unknown,
        Unit = CostUnit.None,
        Verdict = why,
    };

    /// <summary>How much worse the recent stretch is than the baseline. 1.0 means unchanged.</summary>
    public double Ratio =>
        BaselineCostPerFinding <= 0 ? 0 : RecentCostPerFinding / BaselineCostPerFinding;

    public bool ShouldEscalate => Trend is ProgressTrend.Degrading or ProgressTrend.Stalled;
}

/// <summary>
/// Whether a run is still buying information, and at what price.
/// </summary>
/// <remarks>
/// The escalation ladder already asks whether attempts are novel. This asks the harder and more
/// useful question: **is the cost of each new finding rising?**
///
/// Fifty failures that each rule something out is progress and should not be interrupted. Five
/// repetitions of one idea is not, and neither is a stretch where every attempt is technically
/// distinct but nothing is being learned — which a novelty check passes and this does not.
///
/// **Cost is the context window at the time of the attempt, not the tokens the attempt added.**
/// That is the part worth stating, because the obvious reading is the wrong one. With a cached
/// session the model re-reads its whole window every turn, so what a turn costs is proportional to
/// how big the window already is — which is exactly the quantity that climbs with the hour and
/// the reason subagent offload exists at all. Measuring the delta instead would say a long, cheap
/// turn cost nothing.
///
/// **The comparison is against the run's own earlier half, never an absolute number.** Missions
/// differ by orders of magnitude in what a finding is worth, and any constant I picked would stop
/// good runs on one kind of work and never fire on another. A run that has doubled the price of
/// its own findings is saying something about itself that no threshold of mine could.
///
/// Falls back to wall-clock seconds where no token reading exists, and says so; with neither, it
/// declines to have an opinion rather than inventing one.
/// </remarks>
public static class ProgressMeter
{
    /// <summary>Settled attempts needed before this says anything at all.</summary>
    /// <remarks>
    /// Six, split into two halves of three. Below that a single lucky or unlucky attempt swings
    /// the ratio by a factor of three, and an escalation fired on that noise would interrupt a run
    /// that was going fine — which is worse than not escalating, because the tier instruction
    /// tells the agent to abandon an approach that was working.
    /// </remarks>
    public const int MinimumSample = 6;

    /// <summary>Recent cost per finding this many times the baseline reads as degrading.</summary>
    /// <remarks>
    /// Twice, deliberately loose. Cost per finding is naturally noisy — one hard question after
    /// three easy ones doubles it honestly — and a tight bound would turn ordinary variance into
    /// constant escalation. Two-fold sustained across three attempts is a trend rather than a
    /// bad afternoon.
    /// </remarks>
    public const double DegradingRatio = 2.0;

    public static ProgressReading Assess(IReadOnlyList<Attempt> attempts)
    {
        // Declared attempts have not happened yet, and refusals never reached a model — they cost
        // a round trip, not a turn. Neither belongs in a measure of what the run is spending.
        var settled = attempts
            .Where(a => a.Outcome is AttemptOutcome.Failed or AttemptOutcome.Succeeded or AttemptOutcome.Errored)
            .ToList();

        if (settled.Count < MinimumSample)
        {
            return ProgressReading.NotEnoughData(
                $"{settled.Count} settled attempt(s); {MinimumSample} are needed before cost per " +
                "finding means anything.");
        }

        var unit = ChooseUnit(settled);
        if (unit == CostUnit.None)
        {
            return ProgressReading.NotEnoughData(
                "No context reading and no timing on these attempts, so there is no cost to divide.");
        }

        var half = settled.Count / 2;
        var earlier = settled.Take(half).ToList();
        var recent = settled.Skip(half).ToList();

        var recentFindings = Findings(recent);
        var earlierFindings = Findings(earlier);

        var recentCost = Cost(recent, unit);
        var earlierCost = Cost(earlier, unit);

        if (recentFindings == 0)
        {
            return new ProgressReading
            {
                Trend = ProgressTrend.Stalled,
                Unit = unit,
                RecentCostPerFinding = recentCost,
                BaselineCostPerFinding = earlierFindings == 0 ? 0 : earlierCost / earlierFindings,
                RecentFindings = 0,
                SampleSize = settled.Count,
                Verdict =
                    $"The last {recent.Count} attempt(s) produced nothing that ruled anything out, " +
                    $"at a cost of {Describe(recentCost, unit)}. That is not a hard problem being " +
                    "worked, it is the same ground being covered.",
            };
        }

        if (earlierFindings == 0)
        {
            // The run started badly and has begun producing. Real, and the opposite of a problem.
            return new ProgressReading
            {
                Trend = ProgressTrend.Improving,
                Unit = unit,
                RecentCostPerFinding = recentCost / recentFindings,
                BaselineCostPerFinding = 0,
                RecentFindings = recentFindings,
                SampleSize = settled.Count,
                Verdict =
                    $"Nothing was being learned earlier and {recentFindings} finding(s) have come " +
                    "since. It is working now; leave it alone.",
            };
        }

        var recentPer = recentCost / recentFindings;
        var baselinePer = earlierCost / earlierFindings;
        var ratio = recentPer / baselinePer;

        var trend = ratio >= DegradingRatio ? ProgressTrend.Degrading
            : ratio <= 1 / DegradingRatio ? ProgressTrend.Improving
            : ProgressTrend.Steady;

        return new ProgressReading
        {
            Trend = trend,
            Unit = unit,
            RecentCostPerFinding = recentPer,
            BaselineCostPerFinding = baselinePer,
            RecentFindings = recentFindings,
            SampleSize = settled.Count,
            Verdict = trend switch
            {
                ProgressTrend.Degrading =>
                    $"Each finding now costs {Describe(recentPer, unit)}, against " +
                    $"{Describe(baselinePer, unit)} earlier — {ratio:0.#}× the price for the same " +
                    "kind of answer. The approach is running out even though attempts still differ.",

                ProgressTrend.Improving =>
                    $"Findings are getting cheaper: {Describe(recentPer, unit)} against " +
                    $"{Describe(baselinePer, unit)} earlier.",

                _ =>
                    $"Steady at about {Describe(recentPer, unit)} per finding, against " +
                    $"{Describe(baselinePer, unit)} earlier.",
            },
        };
    }

    /// <summary>
    /// An attempt that ruled something out.
    /// </summary>
    /// <remarks>
    /// A recorded observation is the whole test, and it is the right one: the ledger's value is
    /// the list of theories it has killed, so an attempt that added a line to that list bought
    /// something and one that did not, did not. A success counts whatever it wrote — reaching the
    /// objective is the finding.
    /// </remarks>
    private static int Findings(IEnumerable<Attempt> attempts) =>
        attempts.Count(a =>
            a.Outcome == AttemptOutcome.Succeeded ||
            (a.Outcome == AttemptOutcome.Failed && !string.IsNullOrWhiteSpace(a.Observation)));

    private static CostUnit ChooseUnit(IReadOnlyList<Attempt> attempts)
    {
        if (attempts.Any(a => a.ContextTokens is > 0))
        {
            return CostUnit.Tokens;
        }

        return attempts.Any(a => a.Duration > TimeSpan.Zero) ? CostUnit.Seconds : CostUnit.None;
    }

    private static double Cost(IEnumerable<Attempt> attempts, CostUnit unit) => unit switch
    {
        CostUnit.Tokens => attempts.Sum(a => (double)(a.ContextTokens ?? 0)),
        CostUnit.Seconds => attempts.Sum(a => a.Duration.TotalSeconds),
        _ => 0,
    };

    private static string Describe(double cost, CostUnit unit) => unit switch
    {
        CostUnit.Tokens => $"{cost:N0} tokens",
        CostUnit.Seconds => cost >= 120 ? $"{cost / 60:0.#} min" : $"{cost:0} s",
        _ => cost.ToString("0.#"),
    };
}
