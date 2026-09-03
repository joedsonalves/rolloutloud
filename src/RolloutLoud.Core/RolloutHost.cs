using RolloutLoud.Core.Agents;
using RolloutLoud.Core.Buttons;
using RolloutLoud.Core.Context;
using RolloutLoud.Core.Elevation;
using RolloutLoud.Core.Execution;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Money;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Core;

/// <summary>
/// The single object the UI and the bridge both talk to.
/// </summary>
/// <remarks>
/// Deliberately one shared instance behind one lock rather than a service graph. Two agents
/// working the same mission through the bridge while the operator clicks buttons in the window
/// is the ordinary case, not the stress case, and the invariants that matter — no duplicate
/// admitted twice, no button invoked twice, no ledger interleaved mid-write — are all invariants
/// across the whole state rather than within one component. A coarse lock states that honestly.
/// The critical sections are short; the long work (running a command) happens outside it.
/// </remarks>
public sealed class RolloutHost
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MissionEngine> _engines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FluidButton> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MissionProposal> _proposals = new(StringComparer.Ordinal);
    private ButtonAllowlist _allowlist = ButtonAllowlist.Empty;
    private DateTime _allowlistStamp = DateTime.MinValue;
    private IReadOnlyList<AgentDescriptor> _agents = AgentCatalog.Defaults;
    private DateTime _agentsStamp = DateTime.MinValue;
    private TokenPrices _prices = TokenPrices.Default;
    private DateTime _pricesStamp = DateTime.MinValue;

    public RolloutHost(RolloutPaths paths, IElevationService elevation)
    {
        Paths = paths;
        Elevation = elevation;
        Paths.EnsureCreated();

        Store = new MissionStore(paths);
        Buttons_ = new ButtonStore(paths);
        Housekeeping = new Housekeeper(paths);
        Context = new ContextMeter();
        Spend = new SpendMeter(() => Prices);

        foreach (var button in Buttons_.Load())
        {
            _buttons[button.Id] = button;
        }

        foreach (var record in Store.LoadAll())
        {
            var ledger = new MissionLedger(record.Mission.Id, record.Attempts);
            // ⚠️ Both hooks, and the second is easy to miss because this path uses an object
            // initialiser while CreateMission assigns them one by one. Wiring only the new-mission
            // path gives a money cap that works until RolloutLoud is restarted and then silently
            // does not — on exactly the long runs a spend cap exists for.
            var engine = new MissionEngine(record.Mission, ledger, Store, paths)
            {
                ReadContextTokens = TokensFor,
                ReadSpend = BudgetFor,
            };

            engine.EventLogged += e => MissionEventLogged?.Invoke(e);
            _engines[record.Mission.Id] = engine;
        }

        Interrupted =
        [
            .. _engines.Values.Where(e => e.Mission.State == MissionState.Running),
        ];

        // Tidy after loading, not before: the missions have to be known so the run folders that
        // still belong to an open one can be protected from the prune.
        if (Housekeeping.Policy.RunOnStartup)
        {
            LastHousekeeping = Tidy();
        }
    }

    public Housekeeper Housekeeping { get; }

    /// <summary>How large each agent's window has become. See <see cref="ContextMeter"/>.</summary>
    public ContextMeter Context { get; }

    /// <summary>Whether concrete actions should be going to subagents right now.</summary>
    public OffloadDecision OffloadNow(Mission mission) =>
        Context.ShouldOffload(mission, Paths.RepositoryRoot);

    /// <summary>The window size for an agent, or null when nothing can read it.</summary>
    private int? TokensFor(string agentId)
    {
        var reading = Context.Read(agentId, Paths.RepositoryRoot);
        return reading.HasNumber ? reading.Tokens : null;
    }

    /// <summary>What a mission has spent, and whether that is past its cap.</summary>
    public SpendMeter Spend { get; }

    /// <summary>The current price list, re-read whenever pricing.json changes.</summary>
    /// <remarks>
    /// Live for the same reason the allowlist and the agent catalogue are: the operator corrects a
    /// price precisely in the middle of a run — a cap fired at a figure that looked wrong — and
    /// making the edit wait for a restart means it does nothing at the one moment it was wanted.
    /// </remarks>
    public TokenPrices Prices
    {
        get
        {
            var stamp = File.Exists(Paths.PricingFile)
                ? File.GetLastWriteTimeUtc(Paths.PricingFile)
                : DateTime.MinValue;

            lock (_gate)
            {
                if (stamp != _pricesStamp)
                {
                    _prices = TokenPrices.Load(Paths.PricingFile);
                    _pricesStamp = stamp;
                }

                return _prices;
            }
        }
    }

    /// <summary>
    /// The money brake for one mission.
    /// </summary>
    /// <remarks>
    /// The estimate handed in as a fallback is the context reading, which is what RolloutLoud knows
    /// it sent. It is a floor and it is labelled one — but a floor that stops a run is better than
    /// a cap that silently never fires, which is what an unmeasurable agent would otherwise get.
    /// </remarks>
    private BudgetVerdict BudgetFor(Mission mission) =>
        Spend.Evaluate(mission, Paths.RepositoryRoot, TokensFor(mission.AgentId));

    /// <summary>What the last tidy found and removed. Shown in the window.</summary>
    public HousekeepingReport? LastHousekeeping { get; private set; }

    /// <summary>
    /// Prunes run folders and archives finished missions.
    /// </summary>
    /// <remarks>
    /// Run folders belonging to a mission that is still open are protected whatever their age.
    /// Deleting the evidence under a running mission would leave ledger entries pointing at
    /// nothing, which is worse than any amount of disk.
    /// </remarks>
    public HousekeepingReport Tidy()
    {
        var open = _engines.Values.Select(e => e.Mission).ToList();
        var report = Housekeeping.Tidy(Housekeeper.ProtectedRunsFor(open, Store));

        LastHousekeeping = report;
        StateChanged?.Invoke();
        return report;
    }

    public RolloutPaths Paths { get; }

    public IElevationService Elevation { get; }

    public MissionStore Store { get; }

    private ButtonStore Buttons_ { get; }

    /// <summary>
    /// Missions left Running when the process last died.
    /// </summary>
    /// <remarks>
    /// Not a timestamp check. One RolloutLoud owns a repository, so if this one is starting, no
    /// process is working any mission — every Running mission on disk was interrupted, by
    /// definition. Their state is deliberately not rewritten: the mission genuinely is unfinished,
    /// and marking it Paused or Aborted would be the tool inventing a decision the operator never
    /// made. It is surfaced instead, so it can be resumed rather than quietly forgotten.
    /// </remarks>
    public IReadOnlyList<MissionEngine> Interrupted { get; private set; } = [];

    /// <summary>
    /// The configured CLIs, re-read whenever agents.json changes.
    /// </summary>
    /// <remarks>
    /// Live for the same reason the allowlist is: the operator edits this file precisely in the
    /// middle of a session — a bypass flag stopped working, or a new CLI needs registering — and
    /// making the edit take effect only after a restart means it silently does nothing at the one
    /// moment it was wanted.
    ///
    /// It was startup-only until a test registered a new agent mid-run and got "unknown agent"
    /// back, which reads as a bug in the request rather than in the timing.
    /// </remarks>
    public IReadOnlyList<AgentDescriptor> Agents
    {
        get
        {
            var stamp = File.Exists(Paths.AgentsFile)
                ? File.GetLastWriteTimeUtc(Paths.AgentsFile)
                : DateTime.MinValue;

            lock (_gate)
            {
                if (stamp != _agentsStamp)
                {
                    _agents = AgentCatalog.Load(Paths.AgentsFile);
                    _agentsStamp = stamp;

                    // A newly registered CLI may well have just been installed too.
                    AgentAvailability.Forget();
                }

                return _agents;
            }
        }
    }

    /// <summary>
    /// The current allowlist, re-read whenever the file on disk has changed.
    /// </summary>
    /// <remarks>
    /// Re-reading rather than caching at startup is deliberate. The operator edits
    /// <c>allowlist.json</c> precisely in the middle of a run — an agent has just asked for
    /// something, they decide it should be automatic from now on, and they save the file. Making
    /// that take effect only after a restart means the change silently does nothing at the one
    /// moment it was wanted, and the operator concludes the allowlist is broken.
    ///
    /// Cheap enough to do on every lookup: one <c>stat</c>, and a parse only when the stamp moved.
    /// </remarks>
    public ButtonAllowlist Allowlist
    {
        get
        {
            var stamp = File.Exists(Paths.AllowlistFile)
                ? File.GetLastWriteTimeUtc(Paths.AllowlistFile)
                : DateTime.MinValue;

            lock (_gate)
            {
                if (stamp != _allowlistStamp)
                {
                    _allowlist = ButtonAllowlist.Load(Paths.AllowlistFile);
                    _allowlistStamp = stamp;
                }

                return _allowlist;
            }
        }
    }

    /// <summary>Mission an agent gets when it does not name one. The one the operator is watching.</summary>
    public string? ActiveMissionId { get; private set; }

    public event Action? StateChanged;

    /// <summary>Every mission event, from every mission, for the activity log.</summary>
    public event Action<MissionEvent>? MissionEventLogged;

    /// <summary>
    /// Raised when a shutdown request has passed <see cref="ShutdownGate"/> and the operator has
    /// allowed unattended closing. The App owns the actual exit — Core has no window to close.
    /// </summary>
    public event Action<string>? ShutdownApproved;

    /// <summary>
    /// Whether an agent may close the window without the operator clicking. Off by default: the
    /// gate decides whether the WORK is done, and this decides whether the operator wants the
    /// window gone as a result. Those are different questions and the second one is theirs.
    /// </summary>
    public bool AllowUnattendedShutdown { get; set; }

    /// <summary>
    /// An agent asking to close RolloutLoud because it believes the objective is met.
    /// </summary>
    /// <remarks>
    /// Never evaluated on what the agent claims — only on <see cref="MissionState"/>, which only a
    /// twice-passed gate can set to Achieved. "I could not do it" reaches this method as
    /// Exhausted or Running, and is refused with that named back at it.
    /// </remarks>
    public ShutdownDecision RequestShutdown(string? missionId, string? requestedBy, string? reason)
    {
        var engine = FindMission(missionId);
        var decision = ShutdownGate.Evaluate(
            engine?.Mission,
            [.. Missions.Select(m => m.Mission)],
            AllowUnattendedShutdown);

        if (!decision.Allowed)
        {
            return decision;
        }

        var who = requestedBy ?? engine?.Mission.AgentId ?? "an agent";

        if (decision.Verdict == ShutdownVerdict.AllowedUnattended)
        {
            ShutdownApproved?.Invoke($"{who}: {reason ?? decision.Reason}");
            return decision;
        }

        // A button rather than an exit. The work is done; whether the window goes is the
        // operator's call, and it is one click.
        CreateButton(new FluidButton
        {
            Id = "btn-shutdown-" + Guid.NewGuid().ToString("N")[..6],
            Title = "Close RolloutLoud — objective met",
            Command = ShutdownButtonCommand,
            Rationale =
                $"{who} finished: {reason ?? "the gate passed and was re-verified"}. " +
                "Nothing else is open.",
            RequestedBy = requestedBy,
            MissionId = engine?.Mission.Id,
        });

        return decision;
    }

    /// <summary>
    /// Sentinel command for the shutdown button.
    /// </summary>
    /// <remarks>
    /// Not a real command line. <see cref="InvokeButtonAsync"/> recognises it and raises
    /// <see cref="ShutdownApproved"/> instead of running a shell — because a button that closed
    /// the app by shelling out to taskkill would be both fragile and allowlist-bypassable.
    /// </remarks>
    public const string ShutdownButtonCommand = "rolloutloud:shutdown";

    /// <summary>Whether an identity has been lent at all. Never exposes the contents.</summary>
    public bool HasAttachedIdentity => AttachedIdentity.Load(Paths.IdentityFile) is { IsUsable: true };

    /// <summary>
    /// Hands an agent the attached identity for one named site, and writes down that it did.
    /// </summary>
    /// <remarks>
    /// Re-read from disk on every request rather than cached, so deleting the file withdraws the
    /// grant immediately — which is the operator's only lever once a run is going, and it has to
    /// work without a restart.
    ///
    /// The audit line is not bookkeeping. This is the one place the tool hands out the operator's
    /// real details, and "which agent asked for what, when" is the question they will have later.
    /// It is appended before the value is returned, so a crash mid-response still leaves the
    /// record.
    /// </remarks>
    public IdentityDisclosure DiscloseIdentity(string? site, string? requestedBy)
    {
        var identity = AttachedIdentity.Load(Paths.IdentityFile);

        if (identity is null || !identity.IsUsable)
        {
            return IdentityDisclosure.Refused(
                "No identity is attached, so the operator has not lent you one. Do not create " +
                "accounts, and do not use an address you invented. If the mission genuinely needs " +
                "one, say so in your next attempt's observation and carry on with what you can do.");
        }

        if (string.IsNullOrWhiteSpace(site))
        {
            return IdentityDisclosure.Refused(
                "Name the site you need it for, as ?site=example.com. The operator listed which " +
                "sites this identity may be used on, and the record of what it was used for is " +
                "the reason it was lent at all.");
        }

        if (!identity.AllowsSite(site))
        {
            AuditIdentity(site, requestedBy, granted: false);
            return IdentityDisclosure.Refused(
                $"'{site}' is not on the list of sites this identity may be used on: " +
                string.Join(", ", identity.AllowedSites) + ".");
        }

        AuditIdentity(site, requestedBy, granted: true);
        StateChanged?.Invoke();

        return new IdentityDisclosure
        {
            Granted = true,
            Reason = identity.Note ?? "Use these only for " + site + ".",
            Fields = identity.Fields,
        };
    }

    private void AuditIdentity(string site, string? requestedBy, bool granted)
    {
        var line =
            $"{DateTimeOffset.Now:u}  {(granted ? "GRANTED" : "REFUSED")}  " +
            $"site={site}  agent={requestedBy ?? "unknown"}  mission={ActiveMissionId ?? "-"}";

        LastIdentityAccess = line;

        try
        {
            Directory.CreateDirectory(Paths.StateRoot);
            File.AppendAllText(Paths.IdentityAuditFile, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // The in-memory line still reaches the activity log, which is what the operator sees.
        }
    }

    /// <summary>Most recent identity request, for the activity log.</summary>
    public string? LastIdentityAccess { get; private set; }

    public IReadOnlyList<MissionEngine> Missions
    {
        get { lock (_gate) { return [.. _engines.Values]; } }
    }

    public IReadOnlyList<FluidButton> Buttons
    {
        get { lock (_gate) { return [.. _buttons.Values.OrderByDescending(b => b.CreatedAt)]; } }
    }

    public AgentDescriptor? FindAgent(string id) =>
        Agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public MissionEngine? FindMission(string? missionId)
    {
        lock (_gate)
        {
            var id = missionId ?? ActiveMissionId;
            return id is not null && _engines.TryGetValue(id, out var engine) ? engine : null;
        }
    }

    public MissionEngine CreateMission(Mission mission)
    {
        MissionEngine engine;
        lock (_gate)
        {
            engine = MissionEngine.Create(mission, Store, Paths);
            engine.ReadContextTokens = TokensFor;
            engine.ReadSpend = BudgetFor;
            _engines[mission.Id] = engine;
            ActiveMissionId = mission.Id;
        }

        engine.Changed += _ => StateChanged?.Invoke();
        engine.EventLogged += e => MissionEventLogged?.Invoke(e);

        StateChanged?.Invoke();
        return engine;
    }

    /// <summary>Mission drafts an agent has written and the operator has not answered yet.</summary>
    public IReadOnlyList<MissionProposal> Proposals
    {
        get { lock (_gate) { return [.. _proposals.Values.OrderByDescending(p => p.CreatedAt)]; } }
    }

    /// <summary>The one the window should be showing. Oldest pending, so a queue drains in order.</summary>
    public MissionProposal? PendingProposal
    {
        get
        {
            lock (_gate)
            {
                return _proposals.Values
                    .Where(p => p.IsPending)
                    .OrderBy(p => p.CreatedAt)
                    .FirstOrDefault();
            }
        }
    }

    public MissionProposal? FindProposal(string id)
    {
        lock (_gate) { return _proposals.GetValueOrDefault(id); }
    }

    /// <summary>
    /// Takes a mission an agent composed and puts it in front of the operator.
    /// </summary>
    /// <remarks>
    /// It deliberately does not start anything. See <see cref="MissionProposal"/> for why: the
    /// agent authoring its own success gate is the agent deciding it is done, one step removed.
    ///
    /// A new proposal withdraws that agent's previous pending one rather than queueing behind it.
    /// An agent revises — it proposes, reads the critique, proposes better — and leaving both on
    /// the desk would make the operator choose between two drafts of the same idea, where the only
    /// answer that could be right is the newer one.
    /// </remarks>
    public MissionProposal Propose(MissionProposal proposal)
    {
        var reviewed = proposal with { Review = GateCritique.Review(proposal.Gate) };

        lock (_gate)
        {
            foreach (var stale in _proposals.Values
                         .Where(p => p.IsPending && p.ProposedBy.Equals(reviewed.ProposedBy, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _proposals[stale.Id] = stale with
                {
                    State = ProposalState.Withdrawn,
                    DecidedAt = DateTimeOffset.UtcNow,
                    Decision = "Replaced by a newer proposal from the same agent.",
                };
            }

            _proposals[reviewed.Id] = reviewed;
        }

        ProposalArrived?.Invoke(reviewed);
        StateChanged?.Invoke();
        return reviewed;
    }

    /// <summary>
    /// The operator saying yes. Builds an ordinary mission and starts it.
    /// </summary>
    /// <param name="edited">
    /// The proposal as the operator left it after editing. Null accepts it as written.
    /// </param>
    /// <remarks>
    /// ⚠️ The proposal leaves Pending <em>before</em> the mission is built, not after. A double
    /// click on Start is one event away from two calls, and the tidy-looking version — check
    /// pending, create, then mark accepted — lets both through and opens the same mission twice.
    /// Two engines, two ledgers, one objective, and the second silently becomes the active one.
    /// </remarks>
    public MissionEngine? AcceptProposal(string id, MissionProposal? edited = null)
    {
        MissionProposal proposal;

        lock (_gate)
        {
            if (_proposals.GetValueOrDefault(id) is not { IsPending: true } pending)
            {
                return null;
            }

            proposal = edited is null ? pending : edited with { Id = pending.Id, CreatedAt = pending.CreatedAt };

            _proposals[id] = proposal with
            {
                State = ProposalState.Accepted,
                DecidedAt = DateTimeOffset.UtcNow,
            };
        }

        var engine = CreateMission(proposal.ToMission());
        engine.Start();

        lock (_gate)
        {
            _proposals[id] = _proposals[id] with { MissionId = engine.Mission.Id };
        }

        StateChanged?.Invoke();
        return engine;
    }

    /// <summary>The operator saying no. The reason goes back to the agent verbatim.</summary>
    public MissionProposal? RejectProposal(string id, string? reason)
    {
        lock (_gate)
        {
            if (_proposals.GetValueOrDefault(id) is not { IsPending: true } pending)
            {
                return null;
            }

            var rejected = pending with
            {
                State = ProposalState.Rejected,
                DecidedAt = DateTimeOffset.UtcNow,
                Decision = string.IsNullOrWhiteSpace(reason)
                    ? "The operator discarded this proposal without giving a reason."
                    : reason,
            };

            _proposals[id] = rejected;
            StateChanged?.Invoke();
            return rejected;
        }
    }

    /// <summary>Raised when an agent proposes a mission, so the window can come forward.</summary>
    public event Action<MissionProposal>? ProposalArrived;

    public void SetActiveMission(string missionId)
    {
        lock (_gate)
        {
            if (_engines.ContainsKey(missionId))
            {
                ActiveMissionId = missionId;
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>Nudges the UI after the starter files are written. Both tables re-read themselves.</summary>
    public void ReloadConfiguration() => StateChanged?.Invoke();

    /// <summary>
    /// Writes the mission briefing into the agent's instruction file and opens its terminal.
    /// </summary>
    /// <remarks>
    /// The briefing is written before launch rather than typed after, because an instruction the
    /// agent read at startup outranks one pasted into a conversation forty turns deep — by then
    /// the standing rules have been diluted by everything since. This is also why the file is
    /// rewritten on every launch instead of appended to: a stale mission left in CLAUDE.md is an
    /// agent quietly working last week's objective.
    /// </remarks>
    public void LaunchAgent(AgentDescriptor agent, LaunchMode mode, MissionEngine? mission)
    {
        if (mission is not null)
        {
            var briefing = Offload.BriefingComposer.ForMainSession(mission.Mission, mission.Ledger, HasAttachedIdentity);
            var target = Path.Combine(Paths.RepositoryRoot, agent.InstructionFile);
            WriteBriefingSection(target, briefing);

            // A launch is a fresh session, so the running estimate starts over rather than
            // carrying the previous one's total into a window that no longer holds it.
            Context.Reset(agent.Id);
            Context.RecordSent(agent.Id, briefing);
        }

        ProcessLauncher.Launch(new LaunchRequest
        {
            Executable = agent.Executable,
            Arguments = agent.ArgumentsFor(mode),
            WorkingDirectory = Paths.RepositoryRoot,
            InTerminal = true,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ROLLOUTLOUD_BRIDGE"] = BridgeEndpoint ?? string.Empty,
                ["ROLLOUTLOUD_TOKEN"] = BridgeToken ?? string.Empty,
                ["ROLLOUTLOUD_MISSION"] = mission?.Mission.Id ?? string.Empty,
                ["ROLLOUTLOUD_AGENT"] = agent.Id,
            },
        });
    }

    public string? BridgeEndpoint { get; internal set; }

    public string? BridgeToken { get; internal set; }

    // ---- Fluid buttons -------------------------------------------------------------------

    public FluidButton CreateButton(FluidButton button)
    {
        var resolved = button with { Disposition = Allowlist.DispositionFor(button.Command) };

        // The stored disposition is only what the UI shows; InvokeButtonAsync re-checks against
        // the live allowlist, so an edit between creation and invocation is honoured.
        lock (_gate)
        {
            _buttons[resolved.Id] = resolved;
        }

        PersistButtons();
        StateChanged?.Invoke();
        return resolved;
    }

    /// <summary>
    /// Runs a button's command. Callable by the operator's click and — when the allowlist covers
    /// it — by the agent itself, which is the whole point of the fluid-button mechanism.
    /// </summary>
    public async Task<FluidButton> InvokeButtonAsync(
        string buttonId,
        bool byOperator,
        CancellationToken cancellationToken = default)
    {
        // Read outside the lock. The getter takes the same lock to refresh itself, and while
        // System.Threading.Lock is reentrant, a nested acquisition that only works because of
        // reentrancy is the kind of thing that stops working when someone splits the lock later.
        var allowlist = Allowlist;

        FluidButton button;
        lock (_gate)
        {
            if (!_buttons.TryGetValue(buttonId, out var found))
            {
                throw new KeyNotFoundException($"No button '{buttonId}'.");
            }

            // Checked against the allowlist as it stands NOW, not as it stood when the button was
            // created. A pattern the operator added a minute ago has to apply to the request that
            // prompted them to add it, and one they removed has to stop applying immediately.
            if (!byOperator && !allowlist.Allows(found.Command))
            {
                throw new UnauthorizedAccessException(
                    "This command is not on the allowlist, so only the operator can run it. " +
                    "Add a pattern to .rolloutloud/allowlist.json if it should be automatic.");
            }

            if (found.Status == ButtonStatus.Running)
            {
                return found;
            }

            button = found with { Status = ButtonStatus.Running, InvokedAt = DateTimeOffset.UtcNow };
            _buttons[buttonId] = button;
        }

        StateChanged?.Invoke();

        // The shutdown button is a sentinel, not a command line: recognised here so it never
        // reaches a shell, and so it can never be reached through the allowlist path either.
        if (button.Command == ShutdownButtonCommand)
        {
            if (!byOperator)
            {
                throw new UnauthorizedAccessException(
                    "The shutdown button is only ever clicked by the operator. Your request already " +
                    "passed the gate — the window closes when they say so.");
            }

            ShutdownApproved?.Invoke("Operator clicked the shutdown button.");
            return button with { Status = ButtonStatus.Succeeded, OutputExcerpt = "Closing." };
        }

        // Outside the lock: this can take minutes, and holding the lock would stall the UI and
        // every other agent on the bridge.
        try
        {
            var workingDirectory = button.WorkingDirectory ?? Paths.RepositoryRoot;

            if (button.Detached)
            {
                ProcessLauncher.Launch(new LaunchRequest
                {
                    Executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                    Arguments = OperatingSystem.IsWindows()
                        ? ["/c", button.Command]
                        : ["-c", button.Command],
                    WorkingDirectory = workingDirectory,
                });

                button = button with
                {
                    Status = ButtonStatus.Succeeded,
                    OutputExcerpt = "Started detached; no exit code is collected for a long-lived process.",
                };
            }
            else
            {
                var run = await ProcessLauncher
                    .RunShellAsync(button.Command, workingDirectory, TimeSpan.FromMinutes(10), cancellationToken)
                    .ConfigureAwait(false);

                button = button with
                {
                    Status = run.ExitCode == 0 ? ButtonStatus.Succeeded : ButtonStatus.Failed,
                    ExitCode = run.ExitCode,
                    OutputExcerpt = Excerpt(run.StandardOutput + run.StandardError),
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            button = button with
            {
                Status = ButtonStatus.Failed,
                OutputExcerpt = ex.Message,
            };
        }

        lock (_gate)
        {
            _buttons[button.Id] = button;
        }

        PersistButtons();
        StateChanged?.Invoke();
        return button;
    }

    public void DismissButton(string buttonId)
    {
        lock (_gate)
        {
            if (_buttons.TryGetValue(buttonId, out var button))
            {
                _buttons[buttonId] = button with { Status = ButtonStatus.Dismissed };
            }
        }

        PersistButtons();
        StateChanged?.Invoke();
    }

    public FluidButton? FindButton(string buttonId)
    {
        lock (_gate)
        {
            return _buttons.TryGetValue(buttonId, out var button) ? button : null;
        }
    }

    /// <summary>
    /// Replaces the RolloutLoud-managed block in an instruction file, leaving the operator's own
    /// content alone. Anything between the markers belongs to the tool and is overwritten; a file
    /// without them keeps everything it had and gains the block at the end.
    /// </summary>
    private static void WriteBriefingSection(string file, string briefing)
    {
        const string begin = "<!-- ROLLOUTLOUD:BEGIN -->";
        const string end = "<!-- ROLLOUTLOUD:END -->";

        var block = begin + Environment.NewLine + briefing.TrimEnd() + Environment.NewLine + end;
        var existing = File.Exists(file) ? File.ReadAllText(file) : string.Empty;

        var startIndex = existing.IndexOf(begin, StringComparison.Ordinal);
        var endIndex = existing.IndexOf(end, StringComparison.Ordinal);

        string updated;
        if (startIndex >= 0 && endIndex > startIndex)
        {
            updated = existing[..startIndex] + block + existing[(endIndex + end.Length)..];
        }
        else if (existing.Length == 0)
        {
            updated = block + Environment.NewLine;
        }
        else
        {
            updated = existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, updated);
    }

    /// <summary>Writes the open buttons out. Called on every change to one.</summary>
    private void PersistButtons()
    {
        List<FluidButton> snapshot;
        lock (_gate)
        {
            snapshot = [.. _buttons.Values];
        }

        Buttons_.Save(snapshot);
    }

    private static string Excerpt(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "(no output)";
        }

        return trimmed.Length <= 2000 ? trimmed : trimmed[..2000] + "…";
    }
}
