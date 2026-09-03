namespace RolloutLoud.Core.Money;

public enum SpendSource
{
    /// <summary>Nothing readable. No figure at all — never zero, which would read as "free".</summary>
    Unknown,

    /// <summary>Derived from what RolloutLoud sent. Rough, and labelled so everywhere.</summary>
    Estimated,

    /// <summary>Summed from the CLI's own transcript: the tokens the API actually counted.</summary>
    Measured,
}

/// <summary>
/// What a run has cost so far.
/// </summary>
/// <remarks>
/// <b>Spend is an integral; the context window is a level.</b> They come out of the same transcript
/// and they are not the same quantity, which is the mistake to avoid here. The window is the usage
/// block of the <em>last</em> turn — how much the model had to read this time. The bill is every
/// turn's usage added up, with each kind of token at its own rate, and output included because it
/// was charged for even though it never enters the window.
///
/// Reading the last block and calling it the cost would report a six-hour run as costing one turn.
/// Adding up window sizes would double-count every cached prefix. Both are wrong by orders of
/// magnitude, in opposite directions.
/// </remarks>
public sealed record SpendReading
{
    public required decimal Usd { get; init; }

    public required SpendSource Source { get; init; }

    /// <summary>Where the number came from, in words. Shown in the window.</summary>
    public required string Detail { get; init; }

    /// <summary>Tokens seen for models with no price. Reported, never guessed at.</summary>
    public long UnpricedTokens { get; init; }

    /// <summary>Per-model subtotals, dearest first. What the operator wants when the bill surprises.</summary>
    public IReadOnlyList<ModelSpend> ByModel { get; init; } = [];

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public static SpendReading Unknown { get; } = new()
    {
        Usd = 0m,
        Source = SpendSource.Unknown,
        Detail = "No transcript with token counts, so there is no spend figure for this agent.",
    };

    public bool HasNumber => Source != SpendSource.Unknown;

    public bool IsMeasured => Source == SpendSource.Measured;

    public string Summary => Source switch
    {
        SpendSource.Measured => $"${Usd:N2} (measured) — {Detail}" + Unpriced,
        SpendSource.Estimated => $"~${Usd:N2} (estimated) — {Detail}" + Unpriced,
        _ => Detail,
    };

    /// <summary>
    /// The tokens left out, said out loud.
    /// </summary>
    /// <remarks>
    /// A bill that quietly omits a model it had no price for is worse than one that admits the gap:
    /// the operator reads a small number, trusts it, and the cap never fires. This is why unpriced
    /// tokens are counted rather than dropped.
    /// </remarks>
    private string Unpriced => UnpricedTokens > 0
        ? $", plus {UnpricedTokens:N0} tokens from a model with no price in pricing.json"
        : string.Empty;
}

public sealed record ModelSpend
{
    public required string Model { get; init; }

    public required decimal Usd { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long TotalTokens => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;
}

/// <summary>Reads what one agent has spent. Mirrors <see cref="Context.IContextProbe"/>.</summary>
public interface ISpendProbe
{
    /// <summary>Agent id this probe can read, or null for any.</summary>
    string? AgentId { get; }

    SpendReading? TryRead(string repositoryRoot, TokenPrices prices, DateTimeOffset? since);
}
