using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Money;

/// <summary>What the money brake decided, and why.</summary>
public sealed record BudgetVerdict
{
    public required bool OverBudget { get; init; }

    /// <summary>Empty when the run is inside its budget or has none.</summary>
    public required string Reason { get; init; }

    public required SpendReading Reading { get; init; }

    public static BudgetVerdict Within(SpendReading reading) =>
        new() { OverBudget = false, Reason = string.Empty, Reading = reading };
}

/// <summary>
/// What a mission has cost, and whether that is past what the operator agreed to.
/// </summary>
/// <remarks>
/// The cap the other two do not cover. <see cref="StopConditions.MaxAttempts"/> counts moves and
/// <see cref="StopConditions.MaxWallClock"/> counts minutes, and neither tracks money — a six-hour
/// run with offload on can make a hundred cheap attempts or twenty expensive ones, and only one of
/// those is a bill the operator would have agreed to in advance.
///
/// <b>Estimated spend stops the run too, and that is the decision worth defending.</b> The context
/// meter takes the opposite line — no reading, no offload — because acting on a guess there makes
/// every action worse for the rest of the session. Money is not symmetric like that. Failing open
/// spends real money that cannot be got back; failing closed costs one <c>rollout resume</c>, which
/// already exists and returns the briefing with it. So the brake fires on whatever number it has,
/// and the stop reason says plainly which kind it was, so an operator who thinks the estimate is
/// wrong knows to raise the cap rather than to distrust the tool.
/// </remarks>
public sealed class SpendMeter
{
    private readonly IReadOnlyList<ISpendProbe> _probes;
    private readonly Func<TokenPrices> _prices;

    public SpendMeter(Func<TokenPrices>? prices = null, IEnumerable<ISpendProbe>? probes = null)
    {
        _prices = prices ?? (() => TokenPrices.Default);
        _probes = [.. probes ?? [new ClaudeCodeSpendProbe()]];
    }

    /// <summary>
    /// Rough dollars for tokens RolloutLoud knows it sent, when nothing can be read.
    /// </summary>
    /// <remarks>
    /// Deliberately priced as fresh input at the dearest rate on the list. An estimate that is
    /// going to be wrong should be wrong in the direction that makes the operator look, not in the
    /// one that lets a cap sail past unnoticed — and this estimate already omits output and every
    /// token the agent read that did not come through RolloutLoud, so it is a floor before the rate
    /// is even applied.
    /// </remarks>
    public SpendReading Estimate(int tokens)
    {
        if (tokens <= 0)
        {
            return SpendReading.Unknown;
        }

        var dearest = _prices().All.DefaultIfEmpty(TokenPrices.Defaults[0]).Max(p => p.InputPerMillion);

        return new SpendReading
        {
            Usd = tokens * dearest / 1_000_000m,
            Source = SpendSource.Estimated,
            Detail =
                $"{tokens:N0} tokens RolloutLoud sent, priced as fresh input at the dearest rate " +
                "on the list. A floor, not a bill: it cannot see what the agent read on its own.",
        };
    }

    /// <summary>What one agent has spent on this repository since a moment.</summary>
    public SpendReading Read(string agentId, string repositoryRoot, DateTimeOffset? since = null)
    {
        var prices = _prices();

        foreach (var probe in _probes.Where(p => p.AgentId is null || p.AgentId == agentId))
        {
            if (probe.TryRead(repositoryRoot, prices, since) is { } measured)
            {
                return measured;
            }
        }

        return SpendReading.Unknown;
    }

    /// <summary>
    /// Whether the mission has spent what it was allowed to.
    /// </summary>
    /// <remarks>
    /// Measured from <see cref="Mission.StartedAt"/> rather than from now-minus-something: the
    /// budget is for this mission, and turns burned before it opened belong to whatever the
    /// operator was doing beforehand. Charging those to the mission would fire the cap on a run
    /// that had not made a single attempt yet.
    /// </remarks>
    public BudgetVerdict Evaluate(Mission mission, string repositoryRoot, int? estimatedTokens = null)
    {
        if (mission.Stop.MaxSpendUsd is not { } cap || cap <= 0m)
        {
            return BudgetVerdict.Within(SpendReading.Unknown);
        }

        var reading = Read(mission.AgentId, repositoryRoot, mission.StartedAt);

        if (!reading.HasNumber && estimatedTokens is > 0)
        {
            reading = Estimate(estimatedTokens.Value);
        }

        if (!reading.HasNumber || reading.Usd < cap)
        {
            return BudgetVerdict.Within(reading);
        }

        return new BudgetVerdict
        {
            OverBudget = true,
            Reading = reading,
            Reason = reading.IsMeasured
                ? $"Spend cap reached: {Money(reading.Usd)} of {Money(cap)}, measured from the agent's own " +
                  "transcript. Raise --max-spend and 'rollout resume' if the work is worth more."
                : $"Spend cap reached on an ESTIMATE: ~{Money(reading.Usd)} of {Money(cap)}. Nothing could " +
                  "read this agent's real token counts, so this is RolloutLoud pricing what it sent " +
                  "and it may be well out. Raise --max-spend and 'rollout resume' if you think it is high.",
        };
    }

    /// <summary>
    /// Dollars, with enough decimals that a small figure is not rounded into a lie.
    /// </summary>
    /// <remarks>
    /// ⚠️ Two decimals is right for money and wrong for this message. A cap of $0.001 formatted as
    /// N2 reads "$0.00", so the stop says the run was cut off at a budget of nothing — which sends
    /// the operator looking for a bug in the brake instead of at the figure they typed.
    /// </remarks>
    private static string Money(decimal amount) =>
        amount != 0m && Math.Abs(amount) < 0.01m ? $"${amount:0.####}" : $"${amount:N2}";
}
