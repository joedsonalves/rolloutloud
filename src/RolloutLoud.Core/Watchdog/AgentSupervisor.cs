using System.Diagnostics;
using RolloutLoud.Core.Agents;
using RolloutLoud.Core.Execution;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Offload;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Core.Watchdog;

public sealed record WatchdogSettings
{
    /// <summary>A round longer than this is abandoned and restarted.</summary>
    public TimeSpan RoundTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Rounds allowed to pass without a single new ledger entry before supervision stops.
    /// </summary>
    /// <remarks>
    /// This is the money brake, and it is not optional. Everything else here restarts an agent
    /// that stopped; without this, an agent that is broken — bad credentials, a missing tool, a
    /// prompt it cannot parse — gets restarted forever, producing nothing and billing for every
    /// round. Three barren rounds is enough to tell the difference between a hard problem and a
    /// broken setup.
    /// </remarks>
    public int MaxBarrenRounds { get; init; } = 3;

    /// <summary>Total rounds, as a last-resort ceiling independent of the mission's own budget.</summary>
    public int MaxRounds { get; init; } = 100;

    /// <summary>
    /// Wait out a spent token allowance and carry on, rather than treating it as a dead end.
    /// </summary>
    /// <remarks>
    /// On by default. A session running out of allowance mid-run is ordinary — these are hourly
    /// or multi-hour windows and a six-hour mission will cross one — and the alternative is
    /// abandoning a run that was going fine because the clock ran out rather than the ideas.
    /// </remarks>
    public bool WaitOutQuotaLimits { get; init; } = true;

    /// <summary>Longest single quota wait. Beyond this the run stops rather than sleeping all day.</summary>
    public TimeSpan MaxQuotaWait { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Hand the mission to a different CLI when the ladder reaches tier 3.
    /// </summary>
    /// <remarks>
    /// On by default, because it is the rung with the best return and it is useless if it needs
    /// somebody awake to trigger it — the whole point of a tier-3 escalation is that it happens at
    /// 3am, when the current agent has run out of habits and nobody is watching.
    /// </remarks>
    public bool RelayBetweenAgents { get; init; } = true;

    /// <summary>How long the outgoing agent gets to write its handoff note.</summary>
    public TimeSpan HandoffTimeout { get; init; } = TimeSpan.FromMinutes(4);
}

public sealed record WatchdogEvent(DateTimeOffset At, string Kind, string Message);

/// <summary>
/// Runs the agent itself, and refuses to accept it stopping early.
/// </summary>
/// <remarks>
/// Everything before this feature was passive: the mission, the ledger and the gate all waited
/// for an agent to come and ask. That works only while the agent keeps asking — and the entire
/// failure this product was built for is the agent that stops asking, writes "let me know if
/// you'd like me to try another approach", and exits. Nothing was watching for that.
///
/// So supervision runs the agent headless, one round at a time, through its one-shot prompt
/// argument. Between rounds it decides:
///
/// - Did the gate pass? Stop, achieved.
/// - Did a stop condition fire? Stop, exhausted — the budget doing its job.
/// - Did the agent hand the decision back to a human? **Restart it** with the ledger and the
///   current tier, which is a different instruction than the one it just gave up on.
/// - Did it exit having learned nothing, three rounds running? Stop and say so, because that is
///   a broken setup rather than a hard problem.
///
/// ⚠️ Supervised rounds are headless: the operator cannot type into them. That is the trade for
/// being able to restart the process at all, and it is why the launch buttons still exist beside
/// this. Use the buttons to work with an agent; use this to leave one working.
/// </remarks>
public sealed class AgentSupervisor : IAsyncDisposable
{
    private readonly RolloutHost _host;
    private readonly RolloutPaths _paths;
    private readonly List<WatchdogEvent> _events = [];
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public AgentSupervisor(RolloutHost host, RolloutPaths paths)
    {
        _host = host;
        _paths = paths;
    }

    public WatchdogSettings Settings { get; set; } = new();

    public bool IsRunning => _loop is { IsCompleted: false };

    public int Round { get; private set; }

    public string? AgentId { get; private set; }

    public IReadOnlyList<WatchdogEvent> Events => _events;

    public event Action<WatchdogEvent>? Logged;

    public void Start(MissionEngine mission)
    {
        if (IsRunning)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        Round = 0;
        _loop = Task.Run(() => RunAsync(mission, _cancellation.Token));
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: this is how a stop request reaches an in-flight round.
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _loop = null;
        Log("stopped", "Supervision stopped by the operator.");
    }

    private async Task RunAsync(MissionEngine mission, CancellationToken cancellationToken)
    {
        var agent = _host.FindAgent(mission.Mission.AgentId);
        if (agent is null)
        {
            Log("error", $"Unknown agent '{mission.Mission.AgentId}'. Nothing to supervise.");
            return;
        }

        if (agent.PromptArguments.Count == 0)
        {
            Log("error",
                $"{agent.DisplayName} has no one-shot prompt argument configured, so it cannot be " +
                "supervised. Add PromptArguments to .rolloutloud/agents.json, or use the launch button.");
            return;
        }

        AgentId = agent.Id;
        var barrenRounds = 0;

        Log("started",
            $"Supervising {agent.DisplayName}. It will be restarted whenever it stops before the gate is satisfied.");

        while (!cancellationToken.IsCancellationRequested && Round < Settings.MaxRounds)
        {
            var decision = mission.ShouldContinue();
            if (!decision.Continue)
            {
                Log("finished", decision.Reason);
                return;
            }

            // Tier 3 is "hand it to somebody else", so it is acted on here rather than described
            // in a briefing the current agent would have to act on itself. A successful relay
            // restarts the loop with the new agent; nobody to hand to moves the ladder to tier 4
            // instead of spinning on a rung it cannot climb.
            if (Settings.RelayBetweenAgents &&
                mission.Mission.EscalationTier == 3 &&
                mission.Mission.State == MissionState.Running)
            {
                var relayed = await TryRelayAsync(mission, agent, cancellationToken).ConfigureAwait(false);
                if (relayed is not null)
                {
                    agent = relayed;
                    AgentId = agent.Id;
                    barrenRounds = 0;
                    continue;
                }
            }

            Round++;
            var ledgerBefore = mission.Ledger.Count;

            var prompt = Round == 1
                ? BriefingComposer.ForMainSession(mission.Mission, mission.Ledger, _host.HasAttachedIdentity)
                : BriefingComposer.ForMainSession(mission.Mission, mission.Ledger, _host.HasAttachedIdentity)
                  + Environment.NewLine + Environment.NewLine + Continuation(mission);

            Log("round", $"Round {Round} — tier {mission.Mission.EscalationTier} " +
                         $"({EscalationLadder.NameOf(mission.Mission.EscalationTier)}), " +
                         $"{mission.Ledger.Count} attempt(s) on the ledger.");

            CapturedRun run;
            try
            {
                run = await RunRoundAsync(agent, prompt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log("error", $"Round {Round} could not start: {ex.Message}");
                return;
            }

            await PersistRoundAsync(run, cancellationToken).ConfigureAwait(false);

            var learned = mission.Ledger.Count - ledgerBefore;
            var transcript = run.StandardOutput + run.StandardError;

            // Out of allowance is not out of ideas, and the two look identical from here: the
            // agent stopped mid-work and the round produced nothing. Handled before the barren
            // counter so a crossed usage window cannot be mistaken for a broken setup — three of
            // those in a row would otherwise end a run that was going perfectly well.
            var quota = QuotaDetector.Inspect(transcript);
            if (quota.Exhausted && Settings.WaitOutQuotaLimits)
            {
                var wait = QuotaDetector.WaitFor(quota, DateTimeOffset.Now);

                if (wait > Settings.MaxQuotaWait)
                {
                    Log("quota",
                        $"The session is out of allowance and the window does not reopen for {wait:g}, " +
                        $"which is past the {Settings.MaxQuotaWait:g} ceiling. Stopping — resume when it is back.");
                    return;
                }

                Log("quota",
                    $"Out of allowance (\"{quota.Phrase}\"). " +
                    (quota.ResetsAt is { } at
                        ? $"Waiting until {at:HH:mm} plus a minute, {Describe(wait)} from now."
                        : $"No reset time given, so waiting {Describe(wait)} and trying again.") +
                    " This round does not count as barren; the mission is intact and the ledger is kept.");

                try
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Log("quota", "Allowance window should be open. Continuing.");
                Round--;  // the wall was not an attempt
                continue;
            }

            barrenRounds = learned > 0 ? 0 : barrenRounds + 1;

            // Ask the gate before deciding anything else. If the round actually succeeded, the
            // agent's closing sentence does not matter.
            if (mission.Mission.Gate.IsMachineCheckable)
            {
                var verdict = await mission.EvaluateGateAsync(cancellationToken).ConfigureAwait(false);
                if (verdict.Satisfied)
                {
                    Log("achieved", "Gate satisfied and re-verified. " + verdict.Detail);
                    return;
                }

                if (verdict.Contradicted)
                {
                    Log("contradicted", verdict.Detail);
                }
            }

            var signal = GiveUpDetector.Inspect(transcript);

            if (signal.ShouldRestart)
            {
                Log("gave-up",
                    $"The agent handed the decision back (\"{signal.Phrase}\") and is being restarted. " +
                    $"Context: …{signal.Excerpt}…");
            }
            else if (run.TimedOut)
            {
                Log("timeout", $"Round {Round} passed {Settings.RoundTimeout:g} and was abandoned. Restarting.");
            }
            else
            {
                Log("exited",
                    $"Round {Round} ended with exit code {run.ExitCode} and the gate unsatisfied. " +
                    $"{learned} new ledger entr{(learned == 1 ? "y" : "ies")}. Restarting.");
            }

            if (barrenRounds >= Settings.MaxBarrenRounds)
            {
                Log("barren",
                    $"{barrenRounds} rounds produced no ledger entries at all. That is a broken setup " +
                    "rather than a hard problem — stopping instead of burning more. Check that the agent " +
                    "can reach the bridge, and that its credentials and tools are working.");
                return;
            }
        }

        if (Round >= Settings.MaxRounds)
        {
            Log("exhausted", $"Round ceiling reached ({Settings.MaxRounds}).");
        }
    }

    /// <summary>
    /// Collects a handoff note from the outgoing agent, then moves the mission.
    /// </summary>
    /// <remarks>
    /// The note is asked for BEFORE the relay, while the agent that has the context still exists.
    /// The ledger records what was tried; only the agent that tried it can say what it came to
    /// believe and which of its own assumptions it stopped trusting, and those are the two things
    /// somebody picking the problem up cold would ask for first.
    ///
    /// A failed or empty note is not a reason to skip the relay. The handoff is the valuable part;
    /// the note is a bonus, and an agent too stuck to write one is exactly the agent that should
    /// be handing over.
    /// </remarks>
    private async Task<AgentDescriptor?> TryRelayAsync(
        MissionEngine mission,
        AgentDescriptor current,
        CancellationToken cancellationToken)
    {
        var choice = RelayPlanner.ChooseNext(mission.Mission, _host.Agents);

        if (!choice.CanRelay)
        {
            Log("relay-blocked", choice.Reason + " Moving to the operator-consult tier instead.");
            mission.ForceTier(EscalationLadder.MaxTier);
            return null;
        }

        var next = _host.FindAgent(choice.AgentId!);
        if (next is null)
        {
            return null;
        }

        Log("relay", choice.Reason);

        var note = await AskForHandoffAsync(mission, current, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(note))
        {
            Log("relay", "No handoff note came back. Relaying anyway — the ledger goes either way.");
        }

        mission.RelayTo(next.Id, note);
        return next;
    }

    private async Task<string?> AskForHandoffAsync(
        MissionEngine mission,
        AgentDescriptor current,
        CancellationToken cancellationToken)
    {
        var prompt =
            "You are handing this mission to a different agent, and this paragraph is the only " +
            "thing you get to tell them. The ledger goes with it, so do not summarise what you " +
            "tried — they can read that." + Environment.NewLine + Environment.NewLine +
            "Write one paragraph and nothing else:" + Environment.NewLine +
            "  - what you now BELIEVE about this problem that is not obvious from the attempts;" +
            Environment.NewLine +
            "  - which of your own assumptions you stopped trusting, and what made you stop." +
            Environment.NewLine + Environment.NewLine +
            "Objective: " + mission.Mission.Objective + Environment.NewLine + Environment.NewLine +
            mission.Ledger.Summarize(20);

        try
        {
            var run = await RunRoundAsync(current, prompt, cancellationToken, Settings.HandoffTimeout)
                .ConfigureAwait(false);

            var note = run.StandardOutput.Trim();

            // Capped: an agent asked for a paragraph sometimes returns a transcript, and this text
            // goes into every future briefing for the rest of the mission.
            return note.Length <= 1500 ? note : note[..1500] + "…";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log("relay", "Could not collect a handoff note: " + ex.Message);
            return null;
        }
    }

    private async Task<CapturedRun> RunRoundAsync(
        AgentDescriptor agent,
        string prompt,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        // Headless, so the bypass flag is part of the argv rather than a mode. There is no
        // terminal here for anyone to approve anything in: a supervised round that stops at a
        // permission prompt burns its whole timeout and comes back empty, which reads exactly like
        // an agent that had nothing to say.
        var arguments = agent.HeadlessArgumentsFor(prompt);

        return await ProcessLauncher.RunAsync(
            new LaunchRequest
            {
                Executable = agent.Executable,
                Arguments = arguments,
                WorkingDirectory = _paths.RepositoryRoot,
                Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ROLLOUTLOUD_BRIDGE"] = _host.BridgeEndpoint ?? string.Empty,
                    ["ROLLOUTLOUD_TOKEN"] = _host.BridgeToken ?? string.Empty,
                    ["ROLLOUTLOUD_MISSION"] = _host.ActiveMissionId ?? string.Empty,
                    ["ROLLOUTLOUD_AGENT"] = agent.Id,
                    ["ROLLOUTLOUD_SUPERVISED"] = "1",
                },
            },
            timeout ?? Settings.RoundTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the round's transcript on disk rather than in memory or in a briefing.
    /// </summary>
    /// <remarks>
    /// A supervised night is a hundred rounds of full agent output. Holding that in the process
    /// is a leak, and putting any of it into the next briefing is the context blowup that offload
    /// exists to prevent. The ledger keeps the conclusions; this keeps the evidence.
    /// </remarks>
    private async Task PersistRoundAsync(CapturedRun run, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(_paths.RunsDirectory, $"watchdog-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-r{Round}");
            Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "stdout.txt"), run.StandardOutput, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(run.StandardError))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "stderr.txt"), run.StandardError, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a transcript is not worth ending a supervised run over.
        }
    }

    /// <summary>
    /// What the agent is told when it is restarted after stopping.
    /// </summary>
    /// <remarks>
    /// Written as a correction rather than a nudge. The agent just concluded it was finished, so
    /// "please continue" is answering a decision with a suggestion — and a suggestion is exactly
    /// what it will decline again.
    /// </remarks>
    private static string Continuation(MissionEngine mission) =>
        "## You were restarted" + Environment.NewLine + Environment.NewLine +
        "You stopped before the gate was satisfied. That is not how this mission ends — the gate " +
        "decides, and it has not. Reporting that something did not work is an input to your next " +
        "attempt, not a result to hand back, and there is nobody waiting to answer a question." +
        Environment.NewLine + Environment.NewLine +
        "Read the ledger above: those approaches are spent. Do not repeat one, and do not ask " +
        "permission to continue. Take the tier instruction, pick an approach of a different kind, " +
        "declare it to the bridge, and run it." +
        Environment.NewLine + Environment.NewLine +
        $"Current tier — **{EscalationLadder.NameOf(mission.Mission.EscalationTier)}**: " +
        EscalationLadder.InstructionFor(mission.Mission.EscalationTier);

    /// <summary>A duration a person reads at a glance, without a format string full of escapes.</summary>
    private static string Describe(TimeSpan wait) =>
        wait.TotalMinutes < 1 ? $"{wait.TotalSeconds:0}s"
        : wait.TotalHours < 1 ? $"{wait.TotalMinutes:0} min"
        : $"{wait.TotalHours:0.#} h";

    private void Log(string kind, string message)
    {
        var entry = new WatchdogEvent(DateTimeOffset.UtcNow, kind, message);
        _events.Add(entry);
        Logged?.Invoke(entry);
        Debug.WriteLine($"[watchdog:{kind}] {message}");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
