using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RolloutLoud.Core.Buttons;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Offload;

namespace RolloutLoud.Core.Bridge;

/// <summary>
/// The loopback HTTP endpoint the CLIs talk to.
/// </summary>
/// <remarks>
/// HTTP rather than a pipe or a socket protocol for one reason that outweighs the rest: every
/// one of these agents can already run <c>curl</c>, and none of them needs a client library or a
/// permission grant to do it. The integration cost for a new CLI is a paragraph in its
/// instruction file.
///
/// Two things it is careful about:
///
/// 1. **It binds 127.0.0.1 only, and still requires a token.** The loopback bind keeps it off the
///    network; the token keeps it away from every other process on the machine, which on a
///    developer box is not a small population. The token is compared in constant time — not
///    because a timing attack on localhost is likely, but because writing the sloppy comparison
///    once is how it ends up copied somewhere it matters.
///
/// 2. **Agent-supplied text is data.** Observations and button titles arrive from a model that
///    has been reading target output all evening, and target output is attacker-controlled. It is
///    stored and displayed as text, never interpreted, and never executed: a command only ever
///    runs through the allowlist path or an operator's click.
/// </remarks>
public sealed class BridgeServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly RolloutHost _host;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _serialize = new(1, 1);
    private readonly Offload.SubagentRunner _subagents;
    private Task? _loop;

    public BridgeServer(RolloutHost host, int port = 0)
    {
        _host = host;
        Port = port == 0 ? FindFreePort() : port;
        Token = GenerateToken();
        Endpoint = $"http://127.0.0.1:{Port}";
        _listener.Prefixes.Add(Endpoint + "/");

        _subagents = new Offload.SubagentRunner(host, host.Paths);
        _subagents.Logged += message => Logged?.Invoke(message);
    }

    public int Port { get; }

    public string Token { get; }

    public string Endpoint { get; }

    public event Action<string>? Logged;

    public void Start()
    {
        _listener.Start();
        _host.BridgeEndpoint = Endpoint;
        _host.BridgeToken = Token;
        WriteHandshake();
        _loop = Task.Run(AcceptLoopAsync);
        Logged?.Invoke($"Bridge listening on {Endpoint}");
    }

    /// <summary>
    /// Publishes the endpoint and token to <c>.rolloutloud/bridge.json</c>.
    /// </summary>
    /// <remarks>
    /// This is how an agent that was not launched from a button still finds the bridge: it reads
    /// a file in the repository it is already sitting in. The environment variables set by
    /// <see cref="RolloutHost.LaunchAgent"/> cover the launched case; this covers every other one,
    /// including the operator opening a terminal by hand.
    ///
    /// The file holds a live credential, which is why <c>.rolloutloud/</c> is in .gitignore.
    /// </remarks>
    private void WriteHandshake()
    {
        var handshake = new BridgeHandshake
        {
            Endpoint = Endpoint,
            Token = Token,
            RepositoryRoot = _host.Paths.RepositoryRoot,
            Elevated = _host.Elevation.IsElevated,
            ActiveMissionId = _host.ActiveMissionId,
            ProcessId = Environment.ProcessId,
        };

        Directory.CreateDirectory(_host.Paths.StateRoot);
        File.WriteAllText(_host.Paths.BridgeHandshakeFile, JsonSerializer.Serialize(handshake, Json));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleSafelyAsync(context));
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An unhandled fault here would drop the agent's request with no response, and the
            // agent would sit waiting on it. Always answer, even to say what broke.
            Logged?.Invoke($"Bridge error on {context.Request.RawUrl}: {ex.Message}");
            TryWriteError(context, HttpStatusCode.InternalServerError, ex.Message, null);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
            {
                // Client hung up first.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (path is "/v1/health" or "/health")
        {
            await WriteAsync(context, HttpStatusCode.OK, new
            {
                ok = true,
                repositoryRoot = _host.Paths.RepositoryRoot,
                elevated = _host.Elevation.IsElevated,
                activeMission = _host.ActiveMissionId,
            }).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(request))
        {
            await WriteAsync(context, HttpStatusCode.Unauthorized, new ErrorResponse
            {
                Error = "Missing or invalid token.",
                Hint = $"Send header {BridgeContracts.TokenHeader}, value from .rolloutloud/bridge.json " +
                       "or the ROLLOUTLOUD_TOKEN environment variable.",
            }).ConfigureAwait(false);
            return;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = request.HttpMethod;

        if (segments is ["v1", "resume"] && method == "POST")
        {
            await ResumeAsync(context).ConfigureAwait(false);
            return;
        }

        if (segments is ["v1", "identity"] && method == "GET")
        {
            await IdentityAsync(context).ConfigureAwait(false);
            return;
        }

        if (segments is ["v1", "shutdown"] && method == "POST")
        {
            await ShutdownAsync(context).ConfigureAwait(false);
            return;
        }

        // /v1/buttons ...
        if (segments is ["v1", "buttons"])
        {
            if (method == "POST")
            {
                await CreateButtonAsync(context).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, HttpStatusCode.OK, _host.Buttons).ConfigureAwait(false);
            return;
        }

        if (segments is ["v1", "buttons", var buttonId, "invoke"] && method == "POST")
        {
            await InvokeButtonAsync(context, buttonId).ConfigureAwait(false);
            return;
        }

        if (segments is ["v1", "buttons", var readId] && method == "GET")
        {
            var button = _host.FindButton(readId);
            if (button is null)
            {
                await WriteAsync(context, HttpStatusCode.NotFound, new ErrorResponse { Error = "No such button." })
                    .ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, HttpStatusCode.OK, Describe(button)).ConfigureAwait(false);
            return;
        }

        // /v1/missions/proposals — matched before the generic /v1/missions/{id} route below, which
        // would otherwise read "proposals" as a mission id and answer 404 with a hint about missions.
        if (segments is ["v1", "missions", "proposals"])
        {
            if (method == "POST")
            {
                await ProposeMissionAsync(context).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, HttpStatusCode.OK, _host.Proposals.Select(Describe)).ConfigureAwait(false);
            return;
        }

        if (segments is ["v1", "missions", "proposals", var proposalId] && method == "GET")
        {
            var proposal = _host.FindProposal(proposalId);
            if (proposal is null)
            {
                await WriteAsync(context, HttpStatusCode.NotFound, new ErrorResponse
                {
                    Error = "No such proposal.",
                    Hint = "Proposals live in memory only; if RolloutLoud restarted, propose again.",
                }).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, HttpStatusCode.OK, Describe(proposal)).ConfigureAwait(false);
            return;
        }

        // /v1/missions ...
        if (segments is ["v1", "missions"])
        {
            if (method == "POST")
            {
                await CreateMissionAsync(context).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, HttpStatusCode.OK, _host.Missions.Select(m => m.Mission)).ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 3 && segments is ["v1", "missions", ..])
        {
            var missionId = segments[2] == "active" ? null : segments[2];
            var engine = _host.FindMission(missionId);
            if (engine is null)
            {
                await WriteAsync(context, HttpStatusCode.NotFound, new ErrorResponse
                {
                    Error = "No such mission, and no active mission to fall back to.",
                    Hint = "Create one in the RolloutLoud window, or use /v1/missions to list them.",
                }).ConfigureAwait(false);
                return;
            }

            var tail = segments.Length > 3 ? segments[3] : string.Empty;
            switch (tail, method)
            {
                case ("", "GET"):
                    await WriteAsync(context, HttpStatusCode.OK, engine.Mission).ConfigureAwait(false);
                    return;

                case ("briefing", "GET"):
                    await WriteBriefingAsync(context, engine, request).ConfigureAwait(false);
                    return;

                case ("admit", "POST"):
                    await AdmitAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("attempts", "POST"):
                    await RecordAttemptAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("attempts", "GET"):
                    await QueryLedgerAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("gate", "POST"):
                    await EvaluateGateAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("continue", "GET"):
                    await ContinueAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("relay", "POST"):
                    await RelayAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("context", "GET"):
                    await ContextAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("spend", "GET"):
                    await SpendAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("wall", "GET"):
                    await WallAsync(context, engine).ConfigureAwait(false);
                    return;

                case ("subagent", "POST"):
                    await SubagentAsync(context, engine).ConfigureAwait(false);
                    return;
            }
        }

        await WriteAsync(context, HttpStatusCode.NotFound, new ErrorResponse
        {
            Error = $"Unknown route {method} {path}.",
            Hint = "See docs/BRIDGE.md for the endpoint list.",
        }).ConfigureAwait(false);
    }

    // ---- handlers -------------------------------------------------------------------------

    private async Task WriteBriefingAsync(HttpListenerContext context, MissionEngine engine, HttpListenerRequest request)
    {
        var task = request.QueryString["task"];
        var briefing = string.IsNullOrWhiteSpace(task)
            ? BriefingComposer.ForMainSession(engine.Mission, engine.Ledger, _host.HasAttachedIdentity)
            : BriefingComposer.ForSubagent(engine.Mission, engine.Ledger, task);

        await WriteAsync(context, HttpStatusCode.OK, new BriefingResponse
        {
            MissionId = engine.Mission.Id,
            Objective = engine.Mission.Objective,
            Briefing = briefing,
            Tier = engine.Mission.EscalationTier,
            OffloadActive = engine.Mission.Offload.Trigger != OffloadTrigger.Off,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a mission from the bridge, so the whole flow can start from a CLI prompt rather than
    /// from the window: "install ROLLOUTLOUD and keep at X until Y" becomes one POST.
    /// </summary>
    /// <summary>
    /// An agent handing the operator a mission it wrote.
    /// </summary>
    /// <remarks>
    /// The route the operator asked for: they type a sentence into a CLI, and the agent — better
    /// at turning "make the flakiness stop" into a testable objective and a gate than a person
    /// typing quickly — composes the whole thing.
    ///
    /// <b>It answers 202, never 201.</b> Nothing was created. Composing a mission means composing
    /// its success gate, and an agent that writes its own finish line has taken back the one
    /// decision this product exists to take away from it. So the reply is a receipt and a place to
    /// poll, the window shows the operator what the gate actually tests, and the mission exists
    /// only once they say so.
    /// </remarks>
    private async Task ProposeMissionAsync(HttpListenerContext context)
    {
        var body = await ReadAsync<ProposalRequest>(context).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Objective))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "'objective' is required.",
                Hint = "Say the outcome you want, not the steps to it. Add 'gateCommand' for what proves it.",
            }).ConfigureAwait(false);
            return;
        }

        var proposedBy = body.ProposedBy ?? body.Agent ?? Agents.AgentCatalog.Claude;
        var agentId = body.Agent ?? proposedBy;

        if (_host.FindAgent(agentId) is null)
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = $"Unknown agent '{agentId}'.",
                Hint = "Known: " + string.Join(", ", _host.Agents.Select(a => a.Id)),
            }).ConfigureAwait(false);
            return;
        }

        var proposal = _host.Propose(new MissionProposal
        {
            Id = MissionProposal.NewId(),
            Objective = body.Objective.Trim(),
            ProposedBy = proposedBy,
            AgentId = agentId,
            GateCommand = body.GateCommand,
            GateDescription = body.GateDescription,
            Scope = body.Scope ?? [],
            ScopeExclusions = body.ScopeExclusions ?? [],
            Authorization = body.Authorization,
            MaxAttempts = body.MaxAttempts,
            MaxHours = body.MaxHours,
            MaxSpendUsd = body.MaxSpendUsd,
            Offload = body.Offload,
            Rationale = body.Rationale,
            Review = new GateReview { Findings = [], Headline = string.Empty },
        });

        // The arrival itself is announced by whoever is watching RolloutHost.ProposalArrived, in
        // the operator's language. Only the gate findings are logged here — they are the part
        // nothing else says, and the part the operator has to see before clicking Start.
        foreach (var finding in proposal.Review.Findings)
        {
            Logged?.Invoke($"⚠ Gate: {finding.Detail}");
        }

        await WriteAsync(context, HttpStatusCode.Accepted, Describe(proposal)).ConfigureAwait(false);
    }

    /// <summary>
    /// What a proposal looks like to the agent that made it.
    /// </summary>
    /// <remarks>
    /// The critique goes back to the agent as well as to the operator, and that is the useful half:
    /// an agent told "this passes as soon as a file exists" will re-propose with a real check
    /// before the operator has finished reading the first one. Telling only the operator would make
    /// the tool the reviewer of a draft it could have improved for free.
    ///
    /// The briefing rides along on acceptance for the same reason <c>resume</c> returns one: the
    /// agent asked to start work, and what it needs next is what it would have asked for in its
    /// very next call.
    /// </remarks>
    private object Describe(MissionProposal proposal)
    {
        var engine = proposal.MissionId is null ? null : _host.FindMission(proposal.MissionId);

        return new
        {
            id = proposal.Id,
            state = proposal.State.ToString().ToLowerInvariant(),
            objective = proposal.Objective,
            proposedBy = proposal.ProposedBy,
            agent = proposal.AgentId,
            gateCommand = proposal.GateCommand,
            gateDescription = proposal.GateDescription,
            scope = proposal.Scope,
            authorization = proposal.Authorization,
            rationale = proposal.Rationale,
            createdAt = proposal.CreatedAt,
            decidedAt = proposal.DecidedAt,
            missionId = proposal.MissionId,
            decision = proposal.Decision,
            gateReview = new
            {
                headline = proposal.Review.Headline,
                serious = proposal.Review.HasSeriousFinding,
                findings = proposal.Review.Findings.Select(f => new
                {
                    weakness = f.Weakness.ToString(),
                    concern = f.Concern.ToString(),
                    detail = f.Detail,
                    fragment = f.Fragment,
                }),
            },
            warning = proposal.NeedsAuthorization
                ? "Targets are declared but no authorisation is recorded. The operator will see this."
                : null,
            next = proposal.State switch
            {
                ProposalState.Pending =>
                    "Waiting for the operator. Poll GET /v1/missions/proposals/" + proposal.Id +
                    " — and if the gate review above found something, fix it and propose again " +
                    "rather than waiting to be told.",
                ProposalState.Accepted =>
                    "Started. The briefing below is your mission; work it through the bridge as usual.",
                ProposalState.Rejected =>
                    "Turned down. Read 'decision', change what it names, and propose again.",
                _ => "Replaced by a newer proposal from you. Follow that one instead.",
            },
            briefing = engine is null
                ? null
                : BriefingComposer.ForMainSession(engine.Mission, engine.Ledger, _host.HasAttachedIdentity),
        };
    }

    private async Task CreateMissionAsync(HttpListenerContext context)
    {
        var body = await ReadAsync<MissionRequest>(context).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Objective))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "'objective' is required.",
                Hint = "Say the outcome you want, not the steps to it.",
            }).ConfigureAwait(false);
            return;
        }

        var agentId = body.Agent ?? Agents.AgentCatalog.Claude;
        if (_host.FindAgent(agentId) is null)
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = $"Unknown agent '{agentId}'.",
                Hint = "Known: " + string.Join(", ", _host.Agents.Select(a => a.Id)),
            }).ConfigureAwait(false);
            return;
        }

        var scope = body.Scope is { Count: > 0 }
            ? new MissionScope
            {
                Targets = body.Scope,
                Exclusions = body.ScopeExclusions ?? [],
                Authorization = body.Authorization,
            }
            : MissionScope.Unrestricted;

        // ⚠️ Refused, not warned. Everywhere else a declared target with no recorded authorisation
        // is amber and the run opens anyway — the operator is watching the traffic and can catch
        // drift themselves. Behind the Fourth Wall nobody is watching the traffic, by design. The
        // written record is the only thing left that makes the run attributable afterwards, so it
        // stops being optional at exactly the point it starts carrying the weight alone.
        if (body.FourthWall == true && scope.NeedsAuthorization)
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "A Fourth Wall mission with declared targets needs an authorisation on record.",
                Hint =
                    "Pass 'authorization' naming who approved reaching these targets and under what " +
                    "reference. Elsewhere this is a warning; here it is required, because nobody is " +
                    "reading the raw traffic and the record is what makes the run attributable later.",
            }).ConfigureAwait(false);
            return;
        }

        var gate = string.IsNullOrWhiteSpace(body.GateCommand)
            ? SuccessGate.OperatorJudged with { Description = body.GateDescription }
            : new SuccessGate
            {
                Kind = GateKind.Command,
                Command = body.GateCommand,
                Description = body.GateDescription,
            };

        var mission = new Mission
        {
            Id = Mission.NewId(),
            Objective = body.Objective.Trim(),
            AgentId = agentId,
            Gate = gate,
            Scope = scope,
            FourthWall = body.FourthWall == true,
            Deliverable = body.Deliverable,
            Stop = new StopConditions
            {
                MaxAttempts = body.MaxAttempts is > 0 ? body.MaxAttempts.Value : 200,
                MaxWallClock = TimeSpan.FromHours(body.MaxHours is > 0 ? body.MaxHours.Value : 6),
                MaxSpendUsd = body.MaxSpendUsd is > 0m ? body.MaxSpendUsd : null,
            },
            Offload = new OffloadSettings
            {
                Trigger = body.Offload?.ToLowerInvariant() switch
                {
                    "always" => OffloadTrigger.Always,
                    "threshold" => OffloadTrigger.ContextThreshold,
                    _ => OffloadTrigger.Off,
                },
                TokenThreshold = body.TokenThreshold is > 1000 ? body.TokenThreshold.Value : 120_000,
            },
        };

        var engine = _host.CreateMission(mission);
        engine.Start();

        // Re-publish the handshake: it advertises the active mission, and an agent that read it
        // before this point would otherwise keep pointing at the previous one.
        WriteHandshake();

        Logged?.Invoke($"Mission {mission.Id} opened from the bridge: {mission.Objective}");

        if (scope.NeedsAuthorization)
        {
            Logged?.Invoke("⚠ Mission declares targets with no authorisation recorded.");
        }

        // The critique runs here too, though this route starts the mission without asking. It is
        // the older path and agents already use it, so leaving it uncritiqued would mean the check
        // that matters most is the one an agent can skip by using the call it already knows. It
        // cannot stop anything here — the mission is running — so it goes where the operator will
        // actually come across it, which is the activity log.
        var review = GateCritique.Review(gate);

        foreach (var finding in review.Findings)
        {
            Logged?.Invoke($"⚠ Gate on {mission.Id}: {finding.Detail}");
        }

        await WriteAsync(context, HttpStatusCode.Created, new
        {
            mission = engine.Mission,
            warning = scope.NeedsAuthorization
                ? "Targets are declared but no authorisation is recorded. Fill in 'authorization' before running this against anything live."
                : null,
            gateReview = new
            {
                headline = review.Headline,
                serious = review.HasSeriousFinding,
                findings = review.Findings.Select(f => new
                {
                    weakness = f.Weakness.ToString(),
                    concern = f.Concern.ToString(),
                    detail = f.Detail,
                    fragment = f.Fragment,
                }),
            },
            briefing = BriefingComposer.ForMainSession(engine.Mission, engine.Ledger, _host.HasAttachedIdentity),
        }).ConfigureAwait(false);
    }

    private async Task AdmitAsync(HttpListenerContext context, MissionEngine engine)
    {
        var body = await ReadAsync<AdmitRequest>(context).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Command) || string.IsNullOrWhiteSpace(body.Hypothesis))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "Both 'hypothesis' and 'command' are required.",
                Hint = "The hypothesis is not paperwork: it is what makes the ledger able to tell " +
                       "you which kinds of idea are already dead.",
            }).ConfigureAwait(false);
            return;
        }

        await _serialize.WaitAsync().ConfigureAwait(false);
        AttemptAdmission admission;
        try
        {
            admission = engine.Admit(body.Agent ?? engine.Mission.AgentId, body.Hypothesis, body.Command);
        }
        finally
        {
            _serialize.Release();
        }

        await WriteAsync(context, HttpStatusCode.OK, new AdmitResponse
        {
            Admitted = admission.Admitted,
            Reason = admission.Reason,
            Outcome = admission.Admitted ? null : admission.Outcome.ToString(),
            Tier = engine.Mission.EscalationTier,
            TierInstruction = EscalationLadder.InstructionFor(engine.Mission.EscalationTier),
        }).ConfigureAwait(false);
    }

    private async Task RecordAttemptAsync(HttpListenerContext context, MissionEngine engine)
    {
        var body = await ReadAsync<AttemptRequest>(context).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Command) || string.IsNullOrWhiteSpace(body.Hypothesis))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "Both 'hypothesis' and 'command' are required.",
            }).ConfigureAwait(false);
            return;
        }

        var attemptId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        string? artifactDirectory = null;

        if (!string.IsNullOrWhiteSpace(body.Output))
        {
            // Output goes to a run folder, not into the ledger. A megabyte of scanner output in
            // the ledger is a megabyte in every future briefing.
            artifactDirectory = _host.Paths.RunDirectory(attemptId);
            Directory.CreateDirectory(artifactDirectory);
            await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "output.txt"), body.Output)
                .ConfigureAwait(false);
        }

        var attempt = new Attempt
        {
            Id = attemptId,
            MissionId = engine.Mission.Id,
            AgentId = body.Agent ?? engine.Mission.AgentId,
            Hypothesis = body.Hypothesis,
            Command = body.Command,
            Outcome = ParseOutcome(body.Outcome),
            Observation = body.Learned,
            ExitCode = body.ExitCode,
            Tier = engine.Mission.EscalationTier,
            ArtifactDirectory = artifactDirectory,
        };

        await _serialize.WaitAsync().ConfigureAwait(false);
        ContinuationDecision decision;
        try
        {
            engine.Record(attempt);
            decision = engine.ShouldContinue();
        }
        finally
        {
            _serialize.Release();
        }

        await WriteAsync(context, HttpStatusCode.OK, new AttemptResponse
        {
            AttemptId = attempt.Id,
            TotalAttempts = engine.Ledger.Count,
            Tier = engine.Mission.EscalationTier,
            MayStop = !decision.Continue,
            Directive = decision.Continue
                ? decision.Reason
                : "Stop and report: " + decision.Reason,
        }).ConfigureAwait(false);
    }

    private async Task EvaluateGateAsync(HttpListenerContext context, MissionEngine engine)
    {
        var verdict = await engine.EvaluateGateAsync(_shutdown.Token).ConfigureAwait(false);

        await WriteAsync(context, HttpStatusCode.OK, new GateResponse
        {
            Satisfied = verdict.Satisfied,
            Contradicted = verdict.Contradicted,
            Detail = verdict.Detail,
            State = engine.Mission.State.ToString(),
        }).ConfigureAwait(false);
    }

    private async Task ContinueAsync(HttpListenerContext context, MissionEngine engine)
    {
        var decision = engine.ShouldContinue();
        var progress = ProgressMeter.Assess(engine.Ledger.Attempts);

        await WriteAsync(context, HttpStatusCode.OK, new ContinueResponse
        {
            Continue = decision.Continue,
            Directive = decision.Reason,
            State = engine.Mission.State.ToString(),
            Tier = engine.Mission.EscalationTier,
            Attempts = engine.Ledger.Count,
            ProgressTrend = progress.Trend.ToString().ToLowerInvariant(),
            ProgressVerdict = progress.Verdict,
        }).ConfigureAwait(false);
    }

    private async Task RelayAsync(HttpListenerContext context, MissionEngine engine)
    {
        var body = await ReadAsync<Dictionary<string, string>>(context).ConfigureAwait(false);
        if (body is null || !body.TryGetValue("agent", out var agentId) || string.IsNullOrWhiteSpace(agentId))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "Provide {\"agent\": \"codex\"}.",
            }).ConfigureAwait(false);
            return;
        }

        if (_host.FindAgent(agentId) is null)
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = $"Unknown agent '{agentId}'.",
                Hint = "Known: " + string.Join(", ", _host.Agents.Select(a => a.Id)),
            }).ConfigureAwait(false);
            return;
        }

        engine.RelayTo(agentId);
        await WriteAsync(context, HttpStatusCode.OK, engine.Mission).ConfigureAwait(false);
    }

    /// <summary>
    /// The only endpoint that can end RolloutLoud, and the one most worth refusing.
    /// </summary>
    /// <remarks>
    /// An agent asking to shut the tool down is asking to end its own supervision, and an agent
    /// that has been grinding for hours has every incentive to believe it is finished. So nothing
    /// in the request body is an input to the decision: the verdict comes from MissionState, which
    /// only a twice-passed gate can set to Achieved.
    ///
    /// 409 rather than 403 on a refusal — the agent is not forbidden, the state is simply not what
    /// it thinks. The reason names the actual state so it can tell "not done" from "not allowed".
    /// </remarks>
    private async Task ShutdownAsync(HttpListenerContext context)
    {
        var body = await ReadAsync<ShutdownRequest>(context).ConfigureAwait(false);
        var engine = _host.FindMission(body?.MissionId);

        var decision = _host.RequestShutdown(body?.MissionId, body?.Agent, body?.Reason);

        Logged?.Invoke(decision.Allowed
            ? $"Shutdown allowed: {decision.Reason}"
            : $"Shutdown refused: {decision.Reason}");

        var payload = new ShutdownResponse
        {
            Verdict = decision.Verdict switch
            {
                ShutdownVerdict.AllowedUnattended => "allowedUnattended",
                ShutdownVerdict.Allowed => "allowed",
                _ => "refused",
            },
            Closing = decision.Verdict == ShutdownVerdict.AllowedUnattended,
            Reason = decision.Reason,
            MissionState = engine?.Mission.State.ToString(),
        };

        await WriteAsync(
            context,
            decision.Allowed ? HttpStatusCode.OK : HttpStatusCode.Conflict,
            payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands over the operator's attached details for one named site.
    /// </summary>
    /// <remarks>
    /// GET with a required <c>?site=</c>, and the site is not a formality: it is what the audit
    /// line records, and the record is the reason the operator was willing to lend anything.
    ///
    /// 404 when nothing is attached, because that is the honest shape — there is no identity here,
    /// rather than one you are not allowed. The body says so in words an agent can act on: do not
    /// create accounts, do not invent an address, note it and carry on.
    /// </remarks>
    private async Task IdentityAsync(HttpListenerContext context)
    {
        var site = context.Request.QueryString["site"];
        var agent = context.Request.QueryString["agent"];

        var disclosure = _host.DiscloseIdentity(site, agent);

        if (_host.LastIdentityAccess is { } line)
        {
            // Loud on purpose. This is the one endpoint that returns the operator's real details,
            // and they should see it happen rather than find it in a log afterwards.
            Logged?.Invoke("IDENTITY " + line);
        }

        await WriteAsync(
            context,
            disclosure.Granted ? HttpStatusCode.OK : HttpStatusCode.NotFound,
            new
            {
                granted = disclosure.Granted,
                reason = disclosure.Reason,
                fields = disclosure.Granted ? disclosure.Fields : null,
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one step in a fresh process and returns a few lines rather than a transcript.
    /// </summary>
    /// <remarks>
    /// The division of labour is the feature. RolloutLoud has no model and cannot decide what to
    /// try next; the caller supplies the task, because that is where the judgement lives. What
    /// happens here is everything around the decision — a clean process, the mission and ledger
    /// composed into a short briefing, the transcript on disk, the verdict parsed and filed, and
    /// a compact answer coming back.
    ///
    /// The response deliberately carries the verdict and not the output. Returning the transcript
    /// would move the context cost rather than remove it, which is the whole reason for the
    /// endpoint.
    /// </remarks>
    private async Task SubagentAsync(HttpListenerContext context, MissionEngine engine)
    {
        var body = await ReadAsync<SubagentRequest>(context).ConfigureAwait(false);

        if (body is null || string.IsNullOrWhiteSpace(body.Task))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "'task' is required.",
                Hint = "One step, in a sentence — not the objective. The subagent gets the mission " +
                       "and the ledger from here; what it needs from you is what to do next.",
            }).ConfigureAwait(false);
            return;
        }

        var result = await _subagents
            .RunAsync(engine, body.Task, body.Agent, _shutdown.Token)
            .ConfigureAwait(false);

        if (!result.Dispatched)
        {
            // 429 when it was refused for load, 409 when it was refused for the request itself.
            // The caller should retry the first and never the second, and one status for both
            // would leave it guessing.
            await WriteAsync(
                context,
                result.Throttled ? HttpStatusCode.TooManyRequests : HttpStatusCode.Conflict,
                new SubagentResponse
                {
                    Dispatched = false,
                    Verdict = result.Detail,
                    Throttled = result.Throttled,
                }).ConfigureAwait(false);
            return;
        }

        var decision = engine.ShouldContinue();

        await WriteAsync(context, HttpStatusCode.OK, new SubagentResponse
        {
            Dispatched = true,
            Verdict = result.Detail,
            Outcome = result.Verdict?.Outcome,
            Learned = result.Verdict?.Learned,
            Next = result.Verdict?.Next,
            WellFormed = result.Verdict?.WellFormed ?? false,
            AttemptId = result.AttemptId,
            Transcript = result.TranscriptPath,
            Agent = result.AgentId,
            TotalAttempts = engine.Ledger.Count,
            MayStop = !decision.Continue,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers "how expensive have I become, and should I be offloading yet".
    /// </summary>
    /// <remarks>
    /// The point of the endpoint is that the agent does not answer that question itself. It used
    /// to: the briefing said "once your context passes ~120,000 tokens, offload", which is
    /// self-assessment, which is the one thing this product takes away from an agent. Now
    /// RolloutLoud reads the CLI's own transcript where it can, falls back to counting what it
    /// sent where it cannot, and says which of the two the number is.
    /// </remarks>
    private async Task ContextAsync(HttpListenerContext context, MissionEngine engine)
    {
        var decision = _host.OffloadNow(engine.Mission);

        await WriteAsync(context, HttpStatusCode.OK, new ContextResponse
        {
            Tokens = decision.Reading.Tokens,
            Source = decision.Reading.Source.ToString().ToLowerInvariant(),
            Detail = decision.Reading.Detail,
            OffloadNow = decision.Offload,
            Reason = decision.Reason,
            Threshold = engine.Mission.Offload.TokenThreshold,
        }).ConfigureAwait(false);
    }


    /// <summary>
    /// What this mission has cost, and how close that is to its cap.
    /// </summary>
    /// <remarks>
    /// Exposed to the agent as well as the operator on purpose. An agent that knows it has spent
    /// eight of ten dollars can choose the cheap experiment next, and that is a better decision than
    /// the one it makes after being stopped mid-thought by a cap it could not see coming.
    ///
    /// It is a reading, never a lever: nothing here lets the agent raise its own budget.
    /// </remarks>
    private async Task SpendAsync(HttpListenerContext context, MissionEngine engine)
    {
        var verdict = _host.Spend.Evaluate(engine.Mission, _host.Paths.RepositoryRoot);
        var reading = verdict.Reading;
        var cap = engine.Mission.Stop.MaxSpendUsd;

        await WriteAsync(context, HttpStatusCode.OK, new
        {
            usd = reading.Usd,
            source = reading.Source.ToString().ToLowerInvariant(),
            detail = reading.Detail,
            capUsd = cap,
            remainingUsd = cap is null || !reading.HasNumber ? null : (decimal?)(cap.Value - reading.Usd),
            overBudget = verdict.OverBudget,
            unpricedTokens = reading.UnpricedTokens,
            byModel = reading.ByModel.Select(m => new
            {
                model = m.Model,
                usd = m.Usd,
                inputTokens = m.InputTokens,
                outputTokens = m.OutputTokens,
                cacheWriteTokens = m.CacheWriteTokens,
                cacheReadTokens = m.CacheReadTokens,
            }),
            note = cap is null
                ? "No money cap on this mission. The attempt and wall-clock caps still apply."
                : reading.HasNumber
                    ? "Spend it on the experiment most likely to move the gate, not the cheapest one."
                    : "Nothing can read this agent's token counts, so the figure is what RolloutLoud sent.",
        }).ConfigureAwait(false);
    }
    /// <summary>
    /// Picks up a mission that was left running when the window closed.
    /// </summary>
    /// <remarks>
    /// The ledger and the mission have always survived a restart; what did not was any way to get
    /// back to them. Closing the window four hours into a six-hour run meant starting over — not
    /// because the record was gone, but because nothing put it back in front of an agent.
    ///
    /// The briefing comes back in the response so a resumed agent needs no second call: it asked
    /// to resume, and what it needs is the thing it would have asked for next.
    /// </remarks>
    private async Task ResumeAsync(HttpListenerContext context)
    {
        var body = await ReadAsync<ResumeRequest>(context).ConfigureAwait(false);

        var engine = body?.MissionId is { } id
            ? _host.FindMission(id)
            : _host.Interrupted.FirstOrDefault() ?? _host.FindMission(null);

        if (engine is null)
        {
            await WriteAsync(context, HttpStatusCode.NotFound, new ResumeResponse
            {
                Resumed = false,
                Reason = _host.Missions.Count == 0
                    ? "There are no missions in this repository to resume."
                    : "No mission was left running. Name one with missionId, or open a new mission.",
            }).ConfigureAwait(false);
            return;
        }

        if (engine.Mission.IsTerminal)
        {
            // Refused rather than reopened. A mission that reached its gate, ran out of budget or
            // was aborted is finished, and quietly restarting it would undo a decision somebody
            // made — including the gate's.
            await WriteAsync(context, HttpStatusCode.Conflict, new ResumeResponse
            {
                Resumed = false,
                MissionId = engine.Mission.Id,
                Reason =
                    $"That mission is {engine.Mission.State} and finished: {engine.Mission.Resolution}. " +
                    "Open a new one rather than reopening a decision.",
            }).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(body?.Agent) && body.Agent != engine.Mission.AgentId)
        {
            if (_host.FindAgent(body.Agent) is null)
            {
                await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
                {
                    Error = $"Unknown agent '{body.Agent}'.",
                    Hint = "Known: " + string.Join(", ", _host.Agents.Select(a => a.Id)),
                }).ConfigureAwait(false);
                return;
            }

            engine.RelayTo(body.Agent, engine.Mission.HandoffNote);
        }

        engine.Resume();

        // ⚠️ Resuming has to make it the ACTIVE mission, and forgetting this makes the whole
        // command useless in a way that reads as a different bug. `resume` answered
        // `resumed: true` with the mission id, and then the agent's very next call — `attempt`,
        // `gate`, `continue`, none of which name a mission — got "no such mission, and no active
        // mission to fall back to". So the agent believes it resumed, and every following call
        // says the mission does not exist.
        //
        // Third time this exact shape has appeared here: a mission is brought into the host by
        // something other than the operator clicking, and nothing selects it. See the note on
        // missions opened through the bridge not appearing selected in the window.
        _host.SetActiveMission(engine.Mission.Id);
        WriteHandshake();

        var openButtons = _host.Buttons.Count(b => b.IsOpen && b.MissionId == engine.Mission.Id);

        Logged?.Invoke(
            $"Resumed {engine.Mission.Id} on {engine.Mission.AgentId} with {engine.Ledger.Count} " +
            $"attempt(s) of history" +
            (openButtons > 0 ? $" and {openButtons} button(s) still waiting." : "."));

        await WriteAsync(context, HttpStatusCode.OK, new ResumeResponse
        {
            Resumed = true,
            Reason =
                $"Picked up where it left off: tier {engine.Mission.EscalationTier} " +
                $"({EscalationLadder.NameOf(engine.Mission.EscalationTier)}), " +
                $"{engine.Ledger.Count} attempt(s) already ruled out." +
                (openButtons > 0
                    ? $" {openButtons} fluid button(s) were still waiting when the window closed."
                    : string.Empty),
            MissionId = engine.Mission.Id,
            Objective = engine.Mission.Objective,
            Agent = engine.Mission.AgentId,
            Tier = engine.Mission.EscalationTier,
            Attempts = engine.Ledger.Count,
            OpenButtons = openButtons,
            Briefing = BriefingComposer.ForMainSession(
                engine.Mission, engine.Ledger, _host.HasAttachedIdentity),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a question about what has been tried, without handing over the whole ledger.
    /// </summary>
    /// <remarks>
    /// This route used to return every attempt in full. The briefing caps its summary at forty
    /// entries precisely so a long run cannot flood a context — and then the only way to ask about
    /// an older one was a call that imported all of them, which undoes offload in a single request.
    ///
    /// Filters are cheap, the page is small, the ceiling is hard, and the answer says how many
    /// matched so the caller narrows rather than pages blindly through something it pays to read.
    /// </remarks>
    private async Task QueryLedgerAsync(HttpListenerContext context, MissionEngine engine)
    {
        var q = context.Request.QueryString;
        var wall = engine.Mission.FourthWall;

        // Refused rather than quietly ignored. Silently downgrading full=true would let a caller
        // believe it had the argv and act on its absence as if it were an empty command — and the
        // whole point of this mode is that the boundary is legible, not that it is invisible.
        if (wall && string.Equals(q["full"], "true", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(context, HttpStatusCode.Forbidden, new
            {
                error = "Fourth Wall: --full is refused on this mission.",
                hint = FourthWall.FullRefused,
                deliverable = engine.Mission.Deliverable,
                withheldSoFar = _host.Wall.For(engine.Mission.Id),
            }).ConfigureAwait(false);
            return;
        }

        var query = new LedgerQuery
        {
            Outcome = q["outcome"],
            Agent = q["agent"],
            Tier = int.TryParse(q["tier"], out var tier) ? tier : null,
            Contains = q["contains"],
            Since = DateTimeOffset.TryParse(q["since"], out var since) ? since : null,
            Limit = int.TryParse(q["limit"], out var limit) ? limit : LedgerQueryResult.DefaultLimit,
            Offset = int.TryParse(q["offset"], out var offset) ? offset : 0,
            Full = string.Equals(q["full"], "true", StringComparison.OrdinalIgnoreCase),
        };

        var result = LedgerQueryRunner.Run(engine.Ledger.Attempts, query);

        if (wall)
        {
            // Redacted even without full=true, because the entry shape is the same either way and
            // a mode that only holds when asked politely is not a mode.
            result = result with
            {
                Entries = [.. result.Entries.Select(FourthWall.Redact)],
                Guidance = result.Guidance + " Fourth Wall: the argv, exit codes and artifact " +
                           "folders are withheld on this mission.",
            };

            _host.Wall.Record(engine.Mission.Id, result.Entries.Count * FourthWall.FieldsPerEntry);
        }

        await WriteAsync(context, HttpStatusCode.OK, result).ConfigureAwait(false);
    }

    /// <summary>
    /// What this mission is keeping from whoever is steering it, and how much of it.
    /// </summary>
    /// <remarks>
    /// The counterweight to the guard-rail caveat. A supervisor working behind the wall needs to
    /// know the <em>shape</em> of what it cannot see, or it will mistake absence for evidence — and
    /// the operator's question about this mode is always "how much did it not see?". A wall whose
    /// height nobody can state is one people quietly stop believing in.
    /// </remarks>
    private async Task WallAsync(HttpListenerContext context, MissionEngine engine)
    {
        var mission = engine.Mission;

        await WriteAsync(context, HttpStatusCode.OK, new
        {
            fourthWall = mission.FourthWall,
            deliverable = mission.Deliverable,
            withheldFields = _host.Wall.For(mission.Id),
            withheld = mission.FourthWall
                ? new[] { "attempt command lines", "exit codes", "artifact folders", "fluid button output" }
                : [],
            note = mission.FourthWall
                ? "You get the hypothesis, what each attempt ruled out, the gate and the deliverable. " +
                  "Everything else is the raw material this mode keeps out of your context. Ask the " +
                  "agent rather than going around it — and if you do go around it, say so, because " +
                  "this is a guard rail on the bridge and not a sandbox on the disk."
                : "This mission is not in Fourth Wall mode. Nothing is being withheld.",
        }).ConfigureAwait(false);
    }

    private async Task CreateButtonAsync(HttpListenerContext context)
    {
        var body = await ReadAsync<ButtonRequest>(context).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Command) || string.IsNullOrWhiteSpace(body.Title))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ErrorResponse
            {
                Error = "Both 'title' and 'command' are required.",
            }).ConfigureAwait(false);
            return;
        }

        var button = _host.CreateButton(new FluidButton
        {
            Id = "btn-" + Guid.NewGuid().ToString("N")[..8],
            Title = body.Title,
            Command = body.Command,
            Rationale = body.Rationale,
            WorkingDirectory = body.WorkingDirectory,
            RequestedBy = body.Agent,
            MissionId = body.MissionId ?? _host.ActiveMissionId,
            RequiresElevation = body.RequiresElevation,
            Detached = body.Detached,
        });

        Logged?.Invoke($"Button requested by {body.Agent ?? "an agent"}: {button.Title}");
        await WriteAsync(context, HttpStatusCode.Created, Describe(button)).ConfigureAwait(false);
    }

    private async Task InvokeButtonAsync(HttpListenerContext context, string buttonId)
    {
        try
        {
            var button = await _host.InvokeButtonAsync(buttonId, byOperator: false, _shutdown.Token)
                .ConfigureAwait(false);
            await WriteAsync(context, HttpStatusCode.OK, Describe(button)).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            await WriteAsync(context, HttpStatusCode.NotFound, new ErrorResponse { Error = ex.Message })
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 403 rather than 401: the token was fine, the command was not blessed. The agent
            // needs to tell those apart so it waits for a human instead of retrying with a token.
            await WriteAsync(context, HttpStatusCode.Forbidden, new ErrorResponse
            {
                Error = ex.Message,
                Hint = "The button exists and is visible to the operator. Ask them to click it.",
            }).ConfigureAwait(false);
        }
    }

    private ButtonResponse Describe(FluidButton button)
    {
        var auto = button.Disposition == ButtonDisposition.AutoInvokable;
        var needsElevation = button.RequiresElevation && !_host.Elevation.IsElevated;

        var guidance = needsElevation
            ? "RolloutLoud is NOT elevated, so this command will run without administrative rights " +
              "and may fail. Ask the operator to relaunch RolloutLoud elevated."
            : auto
                ? $"On the allowlist. Run it yourself: POST {Endpoint}/v1/buttons/{button.Id}/invoke"
                : "Not on the allowlist — the operator has to click this one. Do not wait on it if " +
                  "nobody is at the machine; carry on with what you can do without it.";

        // A button's output excerpt is raw command output — the most direct raw material there is,
        // and on a pentest run it is target-controlled text. Behind the wall it is withheld and the
        // exit code goes with it, because "it exited 1" plus a hypothesis is enough to know the
        // attempt failed, and the difference between exit 1 and exit 7 only means something to
        // whoever can read the output that explains it.
        var wall = button.MissionId is { } id && _host.FindMission(id)?.Mission.FourthWall == true;

        if (wall)
        {
            _host.Wall.Record(button.MissionId!, 2);
        }

        return new ButtonResponse
        {
            Id = button.Id,
            Status = button.Status.ToString(),
            AutoInvokable = auto,
            Guidance = wall
                ? guidance + " Fourth Wall: this button's output and exit code are withheld — the " +
                  "status says whether it ran, and the agent has the rest."
                : guidance,
            ExitCode = wall ? null : button.ExitCode,
            Output = wall ? null : button.OutputExcerpt,
        };
    }

    // ---- plumbing -------------------------------------------------------------------------

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var supplied = request.Headers[BridgeContracts.TokenHeader];
        if (string.IsNullOrEmpty(supplied))
        {
            var authorization = request.Headers["Authorization"];
            if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                supplied = authorization["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(Token));
    }

    private static AttemptOutcome ParseOutcome(string? outcome) => outcome?.ToLowerInvariant() switch
    {
        "succeeded" or "success" or "ok" => AttemptOutcome.Succeeded,
        "blocked" => AttemptOutcome.BlockedByScope,
        "errored" or "error" => AttemptOutcome.Errored,
        _ => AttemptOutcome.Failed,
    };

    private static async Task<T?> ReadAsync<T>(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, HttpStatusCode status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static void TryWriteError(HttpListenerContext context, HttpStatusCode status, string error, string? hint)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new ErrorResponse { Error = error, Hint = hint }, Json));
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.OutputStream.Write(bytes);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or IOException)
        {
            // The response was already committed or the client left. Nothing further to do.
        }
    }

    private static string GenerateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        // The handshake advertises a live token; leaving it behind would point the next agent at
        // a dead port with a credential that is no longer scoped to anything.
        try
        {
            if (File.Exists(_host.Paths.BridgeHandshakeFile))
            {
                File.Delete(_host.Paths.BridgeHandshakeFile);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }

        _shutdown.Dispose();
        _serialize.Dispose();
    }
}
