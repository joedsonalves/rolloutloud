namespace RolloutLoud.Core.Context;

public enum ContextSource
{
    /// <summary>Nothing could be read and nothing was sent by us. No number at all.</summary>
    Unknown,

    /// <summary>Counted from characters. Rough, and labelled as such wherever it is shown.</summary>
    Estimated,

    /// <summary>Read out of the CLI's own transcript, which records what the API charged.</summary>
    Measured,
}

/// <summary>
/// How big an agent's window has become.
/// </summary>
/// <remarks>
/// <see cref="Source"/> is carried alongside the number rather than dropped, because the two are
/// not interchangeable. A measured 950,000 is a fact from the CLI's own record of what the API
/// counted; an estimated 950,000 is characters divided by four and could be out by a third. An
/// operator deciding whether to switch offload on deserves to know which one they are looking at,
/// and so does an agent deciding whether to believe it.
/// </remarks>
public sealed record ContextReading
{
    public required int Tokens { get; init; }

    public required ContextSource Source { get; init; }

    /// <summary>Where the number came from, in words. Shown in the window.</summary>
    public required string Detail { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public static ContextReading Unknown { get; } = new()
    {
        Tokens = 0,
        Source = ContextSource.Unknown,
        Detail = "No transcript to read and nothing sent through RolloutLoud yet.",
    };

    public bool IsMeasured => Source == ContextSource.Measured;

    public bool HasNumber => Source != ContextSource.Unknown;

    public string Summary => Source switch
    {
        ContextSource.Measured => $"{Tokens:N0} tokens (measured) — {Detail}",
        ContextSource.Estimated => $"~{Tokens:N0} tokens (estimated) — {Detail}",
        _ => Detail,
    };
}

/// <summary>Reads, or estimates, the size of one agent's context.</summary>
public interface IContextProbe
{
    /// <summary>Agent id this probe knows how to read, or null for any.</summary>
    string? AgentId { get; }

    ContextReading? TryRead(string repositoryRoot);
}
