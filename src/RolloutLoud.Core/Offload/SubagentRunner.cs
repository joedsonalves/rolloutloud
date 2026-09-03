using RolloutLoud.Core.Agents;
using RolloutLoud.Core.Execution;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Core.Offload;

public sealed record SubagentResult
{
    public required bool Dispatched { get; init; }

    /// <summary>Why not, when <see cref="Dispatched"/> is false.</summary>
    public required string Detail { get; init; }

    /// <summary>True when it was refused for load rather than for anything about the task.</summary>
    /// <remarks>
    /// Separated so the caller can tell "send this again in a minute" from "this will never work",
    /// which are the same 409 otherwise and lead to opposite correct behaviours.
    /// </remarks>
    public bool Throttled { get; init; }

    public SubagentVerdict? Verdict { get; init; }

    /// <summary>Ledger entry id, when the round produced one.</summary>
    public string? AttemptId { get; init; }

    /// <summary>Where the full transcript went. Deliberately not its contents.</summary>
    public string? TranscriptPath { get; init; }

    public string? AgentId { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Runs one step of a mission in a fresh CLI process, so the main session never sees the transcript.
/// </summary>
/// <remarks>
/// This is the half of subagent offload that was missing. RolloutLoud already composed the
/// briefing; the main agent still had to dispatch it, which meant the subagent's entire output
/// landed in the main agent's context — the exact cost offload exists to avoid. Twenty kilobytes
/// of scanner output does not become cheaper because a subagent produced it.
///
/// **The division of labour matters and is worth stating.** RolloutLoud has no model. It cannot
/// decide what to try next, and it does not try to: the *task* comes from the main agent, which is
/// where the judgement lives. What RolloutLoud contributes is everything around that decision —
/// the mission, the ledger, the scope and duplicate checks, a clean process, the transcript on
/// disk, and a five-line answer coming back.
///
/// So the main agent spends its window on deciding, and pays a couple of hundred bytes per action
/// instead of twenty thousand.
/// </remarks>
public sealed class SubagentRunner
{
    private readonly RolloutHost _host;
    private readonly RolloutPaths _paths;
    private readonly SemaphoreSlim _concurrency;
    private int _inFlight;
    private int _waiting;

    public SubagentRunner(RolloutHost host, RolloutPaths paths, int maxConcurrent = 4)
    {
        _host = host;
        _paths = paths;

        // Capped because a main agent given an endpoint will happily start ten rounds at once, and
        // ten CLI processes on one machine is slower than four as well as more expensive.
        MaxConcurrent = Math.Max(1, maxConcurrent);
        _concurrency = new SemaphoreSlim(MaxConcurrent);
    }

    public int MaxConcurrent { get; }

    /// <summary>
    /// How long a round will queue for a slot before it is refused.
    /// </summary>
    /// <remarks>
    /// The cap used to be enforced by an unbounded wait, which turned a burst of ten into eight
    /// requests hanging until the caller's own HTTP timeout — and the agent then saw a timeout,
    /// which reads as "RolloutLoud is broken" rather than "you sent too many at once". A bounded
    /// wait and a 429 says the true thing, and says it in time to be acted on.
    /// </remarks>
    public TimeSpan MaxQueueWait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Rounds running right now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Rounds queued for a slot right now.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    public event Action<string>? Logged;

    public async Task<SubagentResult> RunAsync(
        MissionEngine mission,
        string task,
        string? agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return new SubagentResult
            {
                Dispatched = false,
                Detail = "Name the step you want run. A subagent gets one task, not the objective.",
            };
        }

        var agent = _host.FindAgent(agentId ?? mission.Mission.AgentId);

        if (agent is null)
        {
            return new SubagentResult
            {
                Dispatched = false,
                Detail = $"Unknown agent '{agentId ?? mission.Mission.AgentId}'. Known: " +
                         string.Join(", ", _host.Agents.Select(a => a.Id)),
            };
        }

        if (!AgentAvailability.CanBeRelayedTo(agent))
        {
            return new SubagentResult
            {
                Dispatched = false,
                Detail = $"{agent.DisplayName} is either not installed or has no one-shot prompt " +
                         "argument, so it cannot be run headlessly. Pick another, or add " +
                         "PromptArguments in agents.json.",
            };
        }

        var decision = mission.ShouldContinue();
        if (!decision.Continue)
        {
            // Refused rather than run: a mission that is over should not keep spending on rounds,
            // and the main agent asking for one has probably not noticed that it ended.
            return new SubagentResult
            {
                Dispatched = false,
                Detail = "This mission is not running: " + decision.Reason,
            };
        }

        Interlocked.Increment(ref _waiting);
        bool admitted;

        try
        {
            admitted = await _concurrency
                .WaitAsync(MaxQueueWait, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        if (!admitted)
        {
            // Refused rather than queued forever. The caller learns it is over-sending while it can
            // still do something about it, instead of collecting a timeout with no explanation.
            return new SubagentResult
            {
                Dispatched = false,
                Detail =
                    $"Too many subagents at once: {MaxConcurrent} are already running and this one " +
                    $"waited {MaxQueueWait:g} for a slot. Send fewer in parallel — they queue behind " +
                    "each other anyway, and nothing was spent on this request.",
                Throttled = true,
            };
        }

        Interlocked.Increment(ref _inFlight);

        try
        {
            return await DispatchAsync(mission, agent, task, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _concurrency.Release();
        }
    }

    private async Task<SubagentResult> DispatchAsync(
        MissionEngine mission,
        AgentDescriptor agent,
        string task,
        CancellationToken cancellationToken)
    {
        var briefing = BriefingComposer.ForSubagent(mission.Mission, mission.Ledger, task);

        var arguments = agent.PromptArguments
            .Select(a => a.Replace("{prompt}", briefing, StringComparison.Ordinal))
            .ToList();

        Logged?.Invoke($"Subagent ({agent.Id}): {Truncate(task, 90)}");

        var started = DateTimeOffset.UtcNow;

        var run = await ProcessLauncher.RunAsync(
            new LaunchRequest
            {
                Executable = agent.Executable,
                Arguments = arguments,
                WorkingDirectory = _paths.RepositoryRoot,
                Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ROLLOUTLOUD_BRIDGE"] = _host.BridgeEndpoint ?? string.Empty,
                    ["ROLLOUTLOUD_TOKEN"] = _host.BridgeToken ?? string.Empty,
                    ["ROLLOUTLOUD_MISSION"] = mission.Mission.Id,
                    ["ROLLOUTLOUD_AGENT"] = agent.Id,
                    ["ROLLOUTLOUD_SUBAGENT"] = "1",
                },
            },
            mission.Mission.Offload.SubagentTimeout,
            cancellationToken).ConfigureAwait(false);

        var elapsed = DateTimeOffset.UtcNow - started;
        var verdict = VerdictParser.Parse(run.StandardOutput);

        var attemptId = NewAttemptId();
        var transcript = await PersistAsync(attemptId, task, briefing, run, cancellationToken)
            .ConfigureAwait(false);

        // A timeout is not a failed idea. Recording it as one would put a hypothesis in the ledger
        // as ruled out when nothing ruled it out, and the next agent would believe it.
        if (run.TimedOut)
        {
            verdict = verdict with
            {
                Outcome = "errored",
                Learned = $"The subagent passed {mission.Mission.Offload.SubagentTimeout:g} and was " +
                          "abandoned. Says nothing about whether the idea works.",
            };
        }

        var attempt = new Attempt
        {
            Id = attemptId,
            MissionId = mission.Mission.Id,
            AgentId = agent.Id,
            Hypothesis = verdict.Hypothesis,
            Command = string.IsNullOrWhiteSpace(verdict.Command) ? task : verdict.Command,
            Outcome = ParseOutcome(verdict.Outcome),
            Observation = verdict.Learned,
            ExitCode = run.TimedOut ? null : run.ExitCode,
            Tier = mission.Mission.EscalationTier,
            ArtifactDirectory = Path.GetDirectoryName(transcript),
        };

        mission.Record(attempt);

        if (!verdict.WellFormed)
        {
            Logged?.Invoke(
                $"Subagent ({agent.Id}) did not use the answer format; its reply was salvaged as prose. " +
                "Full transcript in " + (transcript ?? "the run folder") + ".");
        }

        return new SubagentResult
        {
            Dispatched = true,
            Detail = verdict.Compact,
            Verdict = verdict,
            AttemptId = attemptId,
            TranscriptPath = transcript,
            AgentId = agent.Id,
            Duration = elapsed,
        };
    }

    /// <summary>
    /// Writes the round to disk: the task, the briefing it was given, and everything it said.
    /// </summary>
    /// <remarks>
    /// The briefing is kept as well as the output, and that is not padding. When a subagent
    /// answers something baffling, the first question is always what it was actually asked — and
    /// reconstructing that later is impossible, because the ledger it was composed from has moved
    /// on since.
    /// </remarks>
    private async Task<string?> PersistAsync(
        string attemptId,
        string task,
        string briefing,
        CapturedRun run,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = _paths.RunDirectory(attemptId);
            Directory.CreateDirectory(directory);

            var transcript = Path.Combine(directory, "subagent.txt");

            await File.WriteAllTextAsync(Path.Combine(directory, "task.txt"), task, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "briefing.md"), briefing, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(transcript, run.StandardOutput, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(run.StandardError))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "stderr.txt"), run.StandardError, cancellationToken)
                    .ConfigureAwait(false);
            }

            return transcript;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a transcript is not worth losing the verdict over.
            return null;
        }
    }

    private static AttemptOutcome ParseOutcome(string outcome) => outcome switch
    {
        "succeeded" => AttemptOutcome.Succeeded,
        "blocked" => AttemptOutcome.BlockedByScope,
        "errored" => AttemptOutcome.Errored,
        _ => AttemptOutcome.Failed,
    };

    private static string NewAttemptId() =>
        "sub-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    private static string Truncate(string value, int max)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
