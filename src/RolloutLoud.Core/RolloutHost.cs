using RolloutLoud.Core.Agents;
using RolloutLoud.Core.Buttons;
using RolloutLoud.Core.Elevation;
using RolloutLoud.Core.Execution;
using RolloutLoud.Core.Missions;
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
    private ButtonAllowlist _allowlist = ButtonAllowlist.Empty;
    private DateTime _allowlistStamp = DateTime.MinValue;

    public RolloutHost(RolloutPaths paths, IElevationService elevation)
    {
        Paths = paths;
        Elevation = elevation;
        Paths.EnsureCreated();

        Store = new MissionStore(paths);
        Agents = AgentCatalog.Load(paths.AgentsFile);

        foreach (var record in Store.LoadAll())
        {
            var ledger = new MissionLedger(record.Mission.Id, record.Attempts);
            _engines[record.Mission.Id] = new MissionEngine(record.Mission, ledger, Store, paths);
        }
    }

    public RolloutPaths Paths { get; }

    public IElevationService Elevation { get; }

    public MissionStore Store { get; }

    public IReadOnlyList<AgentDescriptor> Agents { get; private set; }

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
            _engines[mission.Id] = engine;
            ActiveMissionId = mission.Id;
        }

        engine.Changed += _ => StateChanged?.Invoke();
        StateChanged?.Invoke();
        return engine;
    }

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

    public void ReloadConfiguration()
    {
        Agents = AgentCatalog.Load(Paths.AgentsFile);
        StateChanged?.Invoke();
    }

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
            var briefing = Offload.BriefingComposer.ForMainSession(mission.Mission, mission.Ledger);
            var target = Path.Combine(Paths.RepositoryRoot, agent.InstructionFile);
            WriteBriefingSection(target, briefing);
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
