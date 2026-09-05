using RolloutLoud.Core.Execution;
using RolloutLoud.Core.Safety;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Core.Missions;

/// <summary>Something worth showing the operator, and worth writing down.</summary>
public sealed record MissionEvent(DateTimeOffset At, string Kind, string Message);

/// <summary>
/// The loop that keeps a mission going, and the adjudicator that decides when it may stop.
/// </summary>
/// <remarks>
/// Everything here exists to move one judgement out of the agent and into the tool: <em>am I
/// done, and may I stop trying?</em> An agent answering that for itself is the failure the
/// operator described — it tries a thing, it does not work, and it comes back to report. So:
///
/// - <see cref="AdmitAsync"/> runs before every attempt, and rejects repeats and out-of-scope
///   commands, which is what stops the loop from being circular rather than merely long.
/// - <see cref="EvaluateGateAsync"/> is the only path to <see cref="MissionState.Achieved"/>, and
///   it re-runs a satisfied gate from a clean process before believing it.
/// - <see cref="ShouldContinue"/> answers whether the agent is allowed to stop. Almost always: no.
///
/// **It locks itself rather than trusting callers to.** It used to be single-threaded by
/// contract, with the bridge serialising the two routes it knew about. Then subagent execution
/// added a third path that writes the ledger, and a burst of ten finished at once — a data race on
/// the attempt list, and two failed saves colliding on one temp file.
///
/// A contract that says "call me from one thread" is only as good as the newest caller having read
/// it. This one enforces its own.
/// </remarks>
public sealed class MissionEngine
{
    private readonly MissionStore _store;
    private readonly RolloutPaths _paths;
    private readonly List<MissionEvent> _events = [];
    private readonly Lock _gate = new();

    public MissionEngine(Mission mission, MissionLedger ledger, MissionStore store, RolloutPaths paths)
    {
        Mission = mission;
        Ledger = ledger;
        _store = store;
        _paths = paths;
    }

    /// <summary>
    /// Supplies the context window size when an attempt is recorded, if anything can read it.
    /// </summary>
    /// <remarks>
    /// A callback rather than a dependency because Core must not know about the context meter's
    /// probes — those read another program's files, and the mission engine has to stay runnable in
    /// a test with nothing installed.
    /// </remarks>
    public Func<string, int?>? ReadContextTokens { get; set; }

    public Mission Mission { get; private set; }

    public MissionLedger Ledger { get; }

    public IReadOnlyList<MissionEvent> Events => _events;

    public event Action<MissionEngine>? Changed;

    /// <summary>
    /// Raised for every recorded event, with the event itself.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Changed"/>, which only says "something moved, redraw". Mission
    /// events were being appended to a list nobody read: an escalation, a contradicted gate, a
    /// scope block, a relay and an injection flag all happened silently. Those are the moments the
    /// operator most needs to see, and a redraw notification cannot carry them.
    /// </remarks>
    public event Action<MissionEvent>? EventLogged;

    public static MissionEngine Create(Mission mission, MissionStore store, RolloutPaths paths)
    {
        var record = store.Load(mission.Id);
        var ledger = new MissionLedger(mission.Id, record?.Attempts);
        return new MissionEngine(mission, ledger, store, paths);
    }

    public void Start()
    {
        Mission = Mission with { State = MissionState.Running, StartedAt = DateTimeOffset.UtcNow };
        Log("started", $"Mission running on {Mission.AgentId}: {Mission.Objective}");
        Persist();
    }

    public void Pause(string reason)
    {
        Mission = Mission with { State = MissionState.Paused };
        Log("paused", reason);
        Persist();
    }

    public void Resume()
    {
        Mission = Mission with { State = MissionState.Running };
        Log("resumed", "Operator resumed the mission.");
        Persist();
    }

    /// <summary>
    /// Bounds the run to targets that were not known when it opened.
    /// </summary>
    /// <remarks>
    /// A gap this product had until a real run walked into it. The scope was create-time only —
    /// fine when the operator knows the boundary in advance, useless when <em>finding</em> the
    /// boundary is part of the job. A run whose first task is "pick a programme and work inside its
    /// published scope" cannot name its targets on the command line that starts it, so it started
    /// with no boundary at all: the one guard that matters most on that kind of work, off.
    ///
    /// It can only ever narrow. See <see cref="MissionScope.Narrow"/> for why that is the whole
    /// feature rather than a detail — and note that this is the agent bounding itself, which stops
    /// drift and stops nothing else. It is a guard rail like every other scope call, and the value
    /// is that attempt forty is measured against what attempt one wrote down.
    /// </remarks>
    public ScopeNarrowing DeclareScope(
        IReadOnlyList<string> targets,
        IReadOnlyList<string> exclusions,
        string? authorization)
    {
        lock (_gate)
        {
            var narrowing = Mission.Scope.Narrow(targets, exclusions, authorization);

            if (!narrowing.Allowed)
            {
                Log("scope-refused", narrowing.Reason);
                return narrowing;
            }

            Mission = Mission with { Scope = narrowing.Scope! };
            Log("scope-declared", narrowing.Reason + " Authorised by: " + narrowing.Scope!.Authorization);
            Persist();
            return narrowing;
        }
    }

    /// <summary>
    /// Records a question the agent could not settle alone. Never changes the run's state.
    /// </summary>
    /// <remarks>
    /// Asking is not stopping, and that separation is the entire value: an agent that blocks on a
    /// question has handed the decision to somebody who may be asleep, which is the move this
    /// product exists to remove.
    /// </remarks>
    public AgentQuestion Ask(AgentQuestion question)
    {
        lock (_gate)
        {
            Mission = Mission with { Questions = [.. Mission.Questions, question] };
        }

        Log("question", $"{question.From}: {question.Question}");
        Persist();
        return question;
    }

    /// <summary>The supervisor answering. Returns null when there is no such open question.</summary>
    public AgentQuestion? Answer(string questionId, string answer, string? from)
    {
        lock (_gate)
        {
            var open = Mission.Questions.FirstOrDefault(q => q.Id == questionId && q.IsOpen);

            if (open is null)
            {
                return null;
            }

            var answered = open with
            {
                Answer = answer,
                AnsweredBy = from ?? "the supervisor",
                AnsweredAt = DateTimeOffset.UtcNow,
            };

            Mission = Mission with
            {
                Questions = [.. Mission.Questions.Select(q => q.Id == questionId ? answered : q)],
            };

            Log("answer", $"{answered.AnsweredBy}: {answer}");
            Persist();
            return answered;
        }
    }

    /// <summary>Hands over answers the agent has not collected, and marks them delivered.</summary>
    public IReadOnlyList<AgentQuestion> CollectAnswers()
    {
        lock (_gate)
        {
            var pending = Mission.Questions.Where(q => q.IsUndelivered).ToList();

            if (pending.Count == 0)
            {
                return [];
            }

            var now = DateTimeOffset.UtcNow;

            Mission = Mission with
            {
                Questions = [.. Mission.Questions.Select(q => q.IsUndelivered ? q with { DeliveredAt = now } : q)],
            };

            Persist();
            return pending;
        }
    }

    /// <summary>
    /// Records what the supervisor asked for after reading the deliverable.
    /// </summary>
    /// <remarks>
    /// Never touches <see cref="MissionState"/>, and that is the line worth holding. A supervisor is
    /// not a stop condition — the gate and the budgets are — and letting a second model end a run
    /// would put back the self-judgement this whole product exists to remove. A blocking note means
    /// "do this next", never "stop".
    /// </remarks>
    public SupervisorNote Review(SupervisorNote note)
    {
        lock (_gate)
        {
            Mission = Mission with { Reviews = [.. Mission.Reviews, note] };
        }

        Log("review", $"{note.From}: {note.Note}");
        Persist();
        return note;
    }

    /// <summary>
    /// Hands over every note the agent has not seen, and marks them delivered.
    /// </summary>
    /// <remarks>
    /// Delivered once, kept for ever. Repeating a note on every turn would make the briefing an
    /// echo chamber and teach the agent to skim past the section; dropping it would lose the record
    /// of how a run was steered, which behind the wall is the only trace of the steering there is.
    /// </remarks>
    public IReadOnlyList<SupervisorNote> CollectReviews()
    {
        lock (_gate)
        {
            var pending = Mission.Reviews.Where(r => r.IsPending).ToList();

            if (pending.Count == 0)
            {
                return [];
            }

            var now = DateTimeOffset.UtcNow;

            Mission = Mission with
            {
                Reviews = [.. Mission.Reviews.Select(r => r.IsPending ? r with { DeliveredAt = now } : r)],
            };

            Persist();
            return pending;
        }
    }

    public void Abort(string reason)
    {
        Mission = Mission with
        {
            State = MissionState.Aborted,
            EndedAt = DateTimeOffset.UtcNow,
            Resolution = reason,
        };

        Log("aborted", reason);
        Persist();
    }

    /// <summary>
    /// Moves the ladder straight to a tier, for when a rung turns out to be unclimbable.
    /// </summary>
    /// <remarks>
    /// The one caller is the relay finding nobody to hand to. Without it the run sits at tier 3 —
    /// an instruction to hand off that cannot be carried out — and every later escalation check
    /// finds it already at 3 and changes nothing, so the mission grinds at a rung with no
    /// instruction it can act on.
    /// </remarks>
    public void ForceTier(int tier)
    {
        Mission = Mission with
        {
            EscalationTier = Math.Clamp(tier, 0, EscalationLadder.MaxTier),
            TierChangedAtAttempt = Ledger.Count,
        };

        Log("escalated", $"Tier {Mission.EscalationTier} — {EscalationLadder.NameOf(Mission.EscalationTier)}.");
        Persist();
    }

    /// <summary>
    /// Reassigns the mission to another CLI, carrying the ledger and a handoff note. Tier 3.
    /// </summary>
    /// <remarks>
    /// The tier drops back to 1 on the way through, and that is not a reset of progress — the
    /// ledger still forbids every spent attempt, so nothing can be redone. It is because the tier
    /// instruction at 3 is "hand this off", and an agent that has just arrived being told to hand
    /// off would relay again immediately. It needs a rung it can actually work on, and the ladder
    /// climbs again from there if it gets stuck too.
    /// </remarks>
    public void RelayTo(string agentId, string? handoffNote = null)
    {
        var from = Mission.AgentId;

        Mission = Mission with
        {
            AgentId = agentId,
            RelayHistory = [.. Mission.RelayHistory, from],
            HandoffNote = string.IsNullOrWhiteSpace(handoffNote) ? Mission.HandoffNote : handoffNote,
            EscalationTier = 1,
            TierChangedAtAttempt = Ledger.Count,
        };

        Log("relay",
            $"Mission relayed from {from} to {agentId} with {Ledger.Count} attempt(s) of history" +
            (string.IsNullOrWhiteSpace(handoffNote) ? " and no handoff note." : " and a handoff note."));

        Persist();
    }

    /// <summary>
    /// Whether the agent may run this. Called before the command, so a rejection is cheap — and
    /// a rejection is itself recorded, because "that idea is spent" is information the next round
    /// needs and the agent will not otherwise retain.
    /// </summary>
    public AttemptAdmission Admit(string agentId, string hypothesis, string command)
    {
        lock (_gate)
        {
            return AdmitCore(agentId, hypothesis, command);
        }
    }

    private AttemptAdmission AdmitCore(string agentId, string hypothesis, string command)
    {
        var admission = Ledger.Admit(command, Mission.Scope);

        if (admission.Admitted)
        {
            // Write the declaration down immediately. The signature has to be reserved at the
            // moment the idea is claimed, not when its result arrives — otherwise two agents
            // (or one agent asking twice) both sail through the duplicate check, which is the
            // one thing this whole mechanism exists to prevent.
            Ledger.Record(new Attempt
            {
                Id = NewAttemptId(),
                MissionId = Mission.Id,
                AgentId = agentId,
                Hypothesis = hypothesis,
                Command = command,
                Outcome = AttemptOutcome.Declared,
                Tier = Mission.EscalationTier,
            });

            Persist();
            return admission;
        }

        Ledger.Record(new Attempt
        {
            Id = NewAttemptId(),
            MissionId = Mission.Id,
            AgentId = agentId,
            Hypothesis = hypothesis,
            Command = command,
            Outcome = admission.Outcome,
            Observation = admission.Reason,
            Tier = Mission.EscalationTier,
        });

        Log(admission.Outcome == AttemptOutcome.BlockedByScope ? "scope-block" : "duplicate", admission.Reason);
        Persist();
        return admission;
    }

    /// <summary>Records a finished attempt and lets the ladder react to it.</summary>
    public Attempt Record(Attempt attempt)
    {
        lock (_gate)
        {
            return RecordCore(attempt);
        }
    }

    private Attempt RecordCore(Attempt attempt)
    {
        // Stamped here rather than by the caller so every path — bridge, subagent, watchdog —
        // carries it without each of them having to remember.
        if (attempt.ContextTokens is null && ReadContextTokens is not null)
        {
            attempt = attempt with { ContextTokens = ReadContextTokens(attempt.AgentId) };
        }

        Ledger.Record(attempt);
        Log("attempt", $"[{attempt.Outcome}] {attempt.Hypothesis}");

        // Surfaced, never blocked. The observation is evidence, and refusing it would both lose a
        // real finding and hand an attacker a way to stop one being recorded — embed a trigger
        // phrase and the report never lands. What the operator gets is a note that something in
        // this run tried to talk to the agent rather than to them.
        var injection = UntrustedText.Inspect(attempt.Observation);
        if (injection.Found)
        {
            Log("injection",
                $"Instruction-shaped text was recorded into the ledger by {attempt.AgentId} " +
                $"({string.Join("; ", injection.Patterns)}). It is kept as evidence and fenced in " +
                $"every briefing, not obeyed. Context: …{injection.Excerpt}…");
        }

        var sinceTierChange = Ledger.Count - Mission.TierChangedAtAttempt;

        if (sinceTierChange >= Mission.Stop.PlateauBeforeEscalation &&
            Mission.EscalationTier < Mission.Stop.MaxEscalationTier)
        {
            // Two independent reasons to climb, because they catch different failures.
            //
            // Novelty catches the run that has collapsed onto one idea. Cost per finding catches
            // the one where every attempt is technically distinct and nothing is being learned —
            // which passes a novelty check and is exactly the expensive way to be stuck.
            var stale = EscalationLadder.ShouldEscalate(Ledger.Attempts, Mission.Stop.PlateauBeforeEscalation);
            var progress = ProgressMeter.Assess(Ledger.Attempts);

            if (stale || progress.ShouldEscalate)
            {
                var tier = Mission.EscalationTier + 1;
                Mission = Mission with { EscalationTier = tier, TierChangedAtAttempt = Ledger.Count };

                Log("escalated",
                    (stale
                        ? $"No new information in the last {Mission.Stop.PlateauBeforeEscalation} attempt(s). "
                        : progress.Verdict + " ") +
                    $"Tier {tier} — {EscalationLadder.NameOf(tier)}.");
            }
        }

        Persist();
        return attempt;
    }

    /// <summary>
    /// Asks the gate. This is the only way a mission reaches <see cref="MissionState.Achieved"/>;
    /// an agent's own report never is.
    /// </summary>
    public async Task<GateVerdict> EvaluateGateAsync(CancellationToken cancellationToken = default)
    {
        var verdict = await RunGateOnceAsync(cancellationToken).ConfigureAwait(false);
        if (!verdict.Satisfied)
        {
            return verdict;
        }

        if (Mission.Gate.RequireReverification)
        {
            var second = await RunGateOnceAsync(cancellationToken).ConfigureAwait(false);
            if (!second.Satisfied)
            {
                // The dangerous case, and the reason the second run exists. Do not resolve the
                // mission; hand the contradiction back as a failed attempt so the run continues
                // with the knowledge that the evidence did not hold up.
                var contradiction = new GateVerdict
                {
                    Satisfied = false,
                    Contradicted = true,
                    Detail =
                        "The gate passed once and failed on re-run, so the result is not reproducible. " +
                        "Treat it as not achieved and find out which of the two runs was lying. " +
                        "First: " + verdict.Detail + " Second: " + second.Detail,
                };

                RecordUnderLock(new Attempt
                {
                    Id = NewAttemptId(),
                    MissionId = Mission.Id,
                    AgentId = Mission.AgentId,
                    Hypothesis = "The success gate is satisfied.",
                    Command = DescribeGate(),
                    Outcome = AttemptOutcome.Failed,
                    Observation = contradiction.Detail,
                    Tier = Mission.EscalationTier,
                });

                Log("gate-contradicted", contradiction.Detail);
                return contradiction;
            }
        }

        Mission = Mission with
        {
            State = MissionState.Achieved,
            EndedAt = DateTimeOffset.UtcNow,
            Resolution = verdict.Detail,
        };

        Log("achieved", verdict.Detail);
        Persist();
        return verdict;
    }

    /// <summary>
    /// Whether the agent must keep going. The answer the relentless loop is built on: a failed
    /// attempt is never a reason to stop, only a stop condition is.
    /// </summary>
    public ContinuationDecision ShouldContinue()
    {
        if (Mission.IsTerminal)
        {
            return new ContinuationDecision(false, Mission.Resolution ?? "The mission is already resolved.");
        }

        if (Mission.State == MissionState.Paused)
        {
            return new ContinuationDecision(false, "The operator paused the mission.");
        }

        if (Ledger.Count >= Mission.Stop.MaxAttempts)
        {
            Exhaust($"Attempt cap reached ({Mission.Stop.MaxAttempts}).");
            return new ContinuationDecision(false, Mission.Resolution!);
        }

        return new ContinuationDecision(
            true,
            "Keep going. " + EscalationLadder.InstructionFor(Mission.EscalationTier));
    }

    /// <summary>
    /// Records from a path that is already inside the lock, or safely outside every other writer.
    /// </summary>
    /// <remarks>
    /// Split out because System.Threading.Lock is reentrant and relying on that quietly is how a
    /// later refactor that splits the lock introduces a race nobody is looking for.
    /// </remarks>
    private void RecordUnderLock(Attempt attempt)
    {
        lock (_gate)
        {
            RecordCore(attempt);
        }
    }

    private void Exhaust(string reason)
    {
        Mission = Mission with
        {
            State = MissionState.Exhausted,
            EndedAt = DateTimeOffset.UtcNow,
            Resolution = reason,
        };

        Log("exhausted", reason);
        Persist();
    }

    private async Task<GateVerdict> RunGateOnceAsync(CancellationToken cancellationToken)
    {
        switch (Mission.Gate.Kind)
        {
            case GateKind.Command when !string.IsNullOrWhiteSpace(Mission.Gate.Command):
            {
                var run = await ProcessLauncher
                    .RunShellAsync(Mission.Gate.Command, _paths.RepositoryRoot, TimeSpan.FromMinutes(5), cancellationToken)
                    .ConfigureAwait(false);

                return new GateVerdict
                {
                    Satisfied = run.ExitCode == 0,
                    ExitCode = run.ExitCode,
                    Detail = run.ExitCode == 0
                        ? $"Gate command exited 0. {Excerpt(run.StandardOutput)}"
                        : $"Gate command exited {run.ExitCode}. {Excerpt(run.StandardError + run.StandardOutput)}",
                };
            }

            case GateKind.ArtifactMatch when !string.IsNullOrWhiteSpace(Mission.Gate.ArtifactPath):
            {
                var path = Path.Combine(_paths.RepositoryRoot, Mission.Gate.ArtifactPath);
                if (!File.Exists(path))
                {
                    return GateVerdict.NotSatisfied($"'{Mission.Gate.ArtifactPath}' does not exist yet.");
                }

                if (string.IsNullOrWhiteSpace(Mission.Gate.ArtifactPattern))
                {
                    return new GateVerdict { Satisfied = true, Detail = $"'{Mission.Gate.ArtifactPath}' exists." };
                }

                var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var matched = System.Text.RegularExpressions.Regex.IsMatch(
                    content,
                    Mission.Gate.ArtifactPattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5));

                return new GateVerdict
                {
                    Satisfied = matched,
                    Detail = matched
                        ? $"'{Mission.Gate.ArtifactPath}' matches /{Mission.Gate.ArtifactPattern}/."
                        : $"'{Mission.Gate.ArtifactPath}' exists but does not match /{Mission.Gate.ArtifactPattern}/.",
                };
            }

            default:
                return GateVerdict.NotSatisfied(
                    "This mission has no machine-checkable gate; only the operator can end it.");
        }
    }

    private string DescribeGate() => Mission.Gate.Kind switch
    {
        GateKind.Command => Mission.Gate.Command ?? "(no command)",
        GateKind.ArtifactMatch => $"artifact:{Mission.Gate.ArtifactPath}",
        _ => "operator-judged",
    };

    private void Log(string kind, string message)
    {
        var entry = new MissionEvent(DateTimeOffset.UtcNow, kind, message);

        _events.Add(entry);
        EventLogged?.Invoke(entry);
        Changed?.Invoke(this);
    }

    private void Persist() => _store.Save(Mission, Ledger);

    private static string NewAttemptId() =>
        DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    private static string Excerpt(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "(no output)";
        }

        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}

public sealed record ContinuationDecision(bool Continue, string Reason);
