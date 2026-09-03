using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Context;

/// <summary>
/// How large an agent's window has become, and whether that is past the point of offloading.
/// </summary>
/// <remarks>
/// This exists because the threshold trigger did not work at all. <c>ShouldOffload</c> was written,
/// exposed in the window as "only once the window gets expensive", offered on the bridge as
/// <c>"offload": "threshold"</c> — and never called by anything. The briefing made it worse by
/// telling the agent *"once your context passes ~120,000 tokens, offload"*, which asks the agent to
/// judge its own cost. Self-assessment is the one thing this product exists to take away from it.
///
/// Two sources, and the reading always says which it used:
///
/// **Measured**, from the CLI's own transcript, where one exists. Claude Code records what the API
/// counted, so that is a fact rather than an approximation.
///
/// **Estimated**, from what RolloutLoud itself sent — every briefing composed, every subagent
/// prompt dispatched. Exact for supervised runs, since RolloutLoud wrote the whole prompt; rough
/// for interactive ones, where it only knows its own half of the conversation.
///
/// The estimate is characters over four. That is a rule of thumb, wrong by a fair margin on code
/// and on non-English text, and it is labelled an estimate everywhere it is shown for that reason.
/// A rough number that exists beats a precise one that nothing computes.
/// </remarks>
public sealed class ContextMeter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _charactersSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IContextProbe> _probes;

    public ContextMeter(IEnumerable<IContextProbe>? probes = null) =>
        _probes = [.. probes ?? [new ClaudeCodeProbe()]];

    /// <summary>
    /// Characters RolloutLoud has put into an agent's context, per agent.
    /// </summary>
    /// <remarks>
    /// Cumulative rather than per-round: a window grows across a session, and the question the
    /// threshold asks is how big it has become, not how big the last thing was.
    /// </remarks>
    public void RecordSent(string agentId, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _charactersSent[agentId] = _charactersSent.GetValueOrDefault(agentId) + text.Length;
        }
    }

    /// <summary>Forgets an agent's accumulated estimate — a fresh session starts empty.</summary>
    public void Reset(string agentId)
    {
        lock (_gate)
        {
            _charactersSent.Remove(agentId);
        }
    }

    public ContextReading Read(string agentId, string repositoryRoot)
    {
        foreach (var probe in _probes.Where(p => p.AgentId is null || p.AgentId == agentId))
        {
            var measured = probe.TryRead(repositoryRoot);
            if (measured is not null)
            {
                return measured;
            }
        }

        long characters;
        lock (_gate)
        {
            characters = _charactersSent.GetValueOrDefault(agentId);
        }

        if (characters == 0)
        {
            return ContextReading.Unknown;
        }

        return new ContextReading
        {
            Tokens = (int)Math.Min(int.MaxValue, characters / 4),
            Source = ContextSource.Estimated,
            Detail =
                "from what RolloutLoud has sent this agent — it cannot see anything you typed " +
                "directly, so the real window is at least this large",
        };
    }

    /// <summary>
    /// Whether actions should be going to subagents right now.
    /// </summary>
    /// <remarks>
    /// With no reading at all, the answer is no. Guessing "probably expensive by now" would send
    /// every action through a subagent from the first turn of a mission that had barely started,
    /// which is the opposite of what the threshold setting asks for — that is what
    /// <see cref="OffloadTrigger.Always"/> is for, and the operator chose otherwise.
    /// </remarks>
    public OffloadDecision ShouldOffload(Mission mission, string repositoryRoot)
    {
        switch (mission.Offload.Trigger)
        {
            case OffloadTrigger.Off:
                return new OffloadDecision(false, ContextReading.Unknown, "Offload is switched off for this mission.");

            case OffloadTrigger.Always:
                return new OffloadDecision(
                    true,
                    ContextReading.Unknown,
                    "Offload is set to always: every concrete action goes to a subagent.");
        }

        var reading = Read(mission.AgentId, repositoryRoot);

        if (!reading.HasNumber)
        {
            return new OffloadDecision(
                false,
                reading,
                "No reading yet, so not offloading. " + reading.Detail);
        }

        var over = reading.Tokens >= mission.Offload.TokenThreshold;

        return new OffloadDecision(
            over,
            reading,
            over
                ? $"{reading.Summary}. Past the {mission.Offload.TokenThreshold:N0} threshold — " +
                  "hand concrete actions to subagents from here."
                : $"{reading.Summary}. Under the {mission.Offload.TokenThreshold:N0} threshold — " +
                  "carry on directly.");
    }
}

public sealed record OffloadDecision(bool Offload, ContextReading Reading, string Reason);
