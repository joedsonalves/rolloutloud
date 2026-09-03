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
    private Task? _loop;

    public BridgeServer(RolloutHost host, int port = 0)
    {
        _host = host;
        Port = port == 0 ? FindFreePort() : port;
        Token = GenerateToken();
        Endpoint = $"http://127.0.0.1:{Port}";
        _listener.Prefixes.Add(Endpoint + "/");
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
                    await WriteAsync(context, HttpStatusCode.OK, engine.Ledger.Attempts).ConfigureAwait(false);
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
            ? BriefingComposer.ForMainSession(engine.Mission, engine.Ledger)
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
            Stop = new StopConditions
            {
                MaxAttempts = body.MaxAttempts is > 0 ? body.MaxAttempts.Value : 200,
                MaxWallClock = TimeSpan.FromHours(body.MaxHours is > 0 ? body.MaxHours.Value : 6),
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

        await WriteAsync(context, HttpStatusCode.Created, new
        {
            mission = engine.Mission,
            warning = scope.NeedsAuthorization
                ? "Targets are declared but no authorisation is recorded. Fill in 'authorization' before running this against anything live."
                : null,
            briefing = BriefingComposer.ForMainSession(engine.Mission, engine.Ledger),
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

        await WriteAsync(context, HttpStatusCode.OK, new ContinueResponse
        {
            Continue = decision.Continue,
            Directive = decision.Reason,
            State = engine.Mission.State.ToString(),
            Tier = engine.Mission.EscalationTier,
            Attempts = engine.Ledger.Count,
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

        return new ButtonResponse
        {
            Id = button.Id,
            Status = button.Status.ToString(),
            AutoInvokable = auto,
            Guidance = guidance,
            ExitCode = button.ExitCode,
            Output = button.OutputExcerpt,
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
