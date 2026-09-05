using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Money;

/// <summary>
/// What a mission has cost so far, in dollars, for the operator to read.
/// </summary>
/// <remarks>
/// A figure, not a brake. It answers "what has this run cost" — it never ends a run, and
/// <see cref="StopConditions.MaxAttempts"/> is the only stop condition denominated in anything.
///
/// The number is read from the agent's own transcript where a probe can find one, and priced from
/// <c>pricing.json</c>. Where nothing can be read, <see cref="Estimate"/> prices what RolloutLoud
/// itself sent, which is a floor and says so — it cannot see what the agent read on its own.
/// </remarks>
public sealed class SpendMeter
{
    private readonly IReadOnlyList<ISpendProbe> _probes;
    private readonly Func<TokenPrices> _prices;

    public SpendMeter(Func<TokenPrices>? prices = null, IEnumerable<ISpendProbe>? probes = null)
    {
        _prices = prices ?? (() => TokenPrices.Default);
        _probes = [.. probes ?? [new ClaudeCodeSpendProbe(), new CodexSpendProbe()]];
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
}
