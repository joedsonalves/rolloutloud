namespace RolloutLoud.Core.Missions;

public enum OffloadTrigger
{
    /// <summary>Every action goes to a subagent, from the first one.</summary>
    Always,

    /// <summary>Only once the main window crosses <see cref="OffloadSettings.TokenThreshold"/>.</summary>
    ContextThreshold,

    Off,
}

/// <summary>
/// The switch for heavy work: each action executed by a subagent instead of by the main session.
/// </summary>
/// <remarks>
/// The problem it solves is arithmetic, not architecture. A session that has been grinding for
/// two hours carries the whole grind in its window, and every subsequent action re-reads all of
/// it — so the hundredth attempt costs many times the first while being no more informed, because
/// what actually matters from those two hours is a page of ledger.
///
/// Offload inverts that: the main session keeps the mission and the ledger and spends its window
/// on judgement, while each concrete action is handed to a fresh subagent that gets a briefing
/// sized in hundreds of tokens and returns a structured verdict. The expensive context stops
/// growing, and — the part that surprised me — the attempts get better, because a subagent with
/// no memory of forty failures does not inherit the tunnel vision that produced them.
/// </remarks>
public sealed record OffloadSettings
{
    public OffloadTrigger Trigger { get; init; } = OffloadTrigger.Off;

    /// <summary>
    /// Estimated tokens in the main window past which actions are offloaded. The default sits
    /// where cost per action starts climbing faster than the value it returns.
    /// </summary>
    public int TokenThreshold { get; init; } = 120_000;

    /// <summary>Attempts one subagent may make before reporting back. Small on purpose.</summary>
    public int AttemptsPerSubagent { get; init; } = 3;

    /// <summary>Ledger entries handed to a subagent. It needs the shape of what failed, not the history.</summary>
    public int LedgerEntriesInBriefing { get; init; } = 12;

    /// <summary>Subagent runs longer than this are abandoned and recorded as errored.</summary>
    public TimeSpan SubagentTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public bool ShouldOffload(int estimatedTokens) => Trigger switch
    {
        OffloadTrigger.Always => true,
        OffloadTrigger.ContextThreshold => estimatedTokens >= TokenThreshold,
        _ => false,
    };
}
