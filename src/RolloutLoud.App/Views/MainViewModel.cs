using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using RolloutLoud.Core;
using RolloutLoud.Core.Agents;
using RolloutLoud.Core.Bridge;
using RolloutLoud.Core.Buttons;
using RolloutLoud.Core.Localization;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Watchdog;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.App.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(name);
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One CLI, with its two launch buttons.</summary>
public sealed class AgentLauncher(AgentDescriptor descriptor, MainViewModel owner) : Observable
{
    public AgentDescriptor Descriptor { get; } = descriptor;

    public string DisplayName => Descriptor.DisplayName;

    public string NormalCommandLine => Descriptor.CommandLineFor(LaunchMode.Normal);

    public string ElevatedCommandLine => Descriptor.CommandLineFor(LaunchMode.Elevated);

    /// <summary>
    /// Resolved rather than shown raw: the shipped notes are localisation keys, while a note an
    /// operator wrote into agents.json comes back exactly as they typed it.
    /// </summary>
    public string? Notes => Localizer.Current.Resolve(Descriptor.Notes);

    public RelayCommand LaunchNormal { get; } =
        new(_ => owner.Launch(descriptor, LaunchMode.Normal));

    public RelayCommand LaunchElevated { get; } =
        new(_ => owner.LaunchElevatedAsync(descriptor));
}

/// <summary>A fluid button as the window shows it.</summary>
public sealed class ButtonCard(FluidButton button, MainViewModel owner) : Observable
{
    private FluidButton _button = button;

    public FluidButton Model
    {
        get => _button;
        set
        {
            _button = value;
            Raise(nameof(Title));
            Raise(nameof(Command));
            Raise(nameof(Rationale));
            Raise(nameof(StatusLine));
            Raise(nameof(IsOpen));
        }
    }

    public string Id => _button.Id;

    public string Title => _button.Title;

    public string Command => _button.Command;

    public string Rationale => string.IsNullOrWhiteSpace(_button.Rationale)
        ? Localizer.Current.Format("buttons.requestedBy", _button.RequestedBy ?? Localizer.Current["buttons.anAgent"])
        : _button.Rationale!;

    public bool IsOpen => _button.IsOpen;

    public string StatusLine => _button.Status switch
    {
        ButtonStatus.Pending when _button.Disposition == ButtonDisposition.AutoInvokable =>
            Localizer.Current["buttons.pendingAuto"],
        ButtonStatus.Pending => Localizer.Current["buttons.pending"],
        ButtonStatus.Running => Localizer.Current["buttons.running"],
        ButtonStatus.Succeeded => Localizer.Current.Format("buttons.succeeded", Excerpt(_button.OutputExcerpt)),
        ButtonStatus.Failed => Localizer.Current.Format("buttons.failed", _button.ExitCode, Excerpt(_button.OutputExcerpt)),
        _ => Localizer.Current["buttons.dismissed"],
    };

    public RelayCommand Run { get; } = new(_ => owner.InvokeButtonAsync(button.Id));

    public RelayCommand Dismiss { get; } = new(_ => owner.DismissButton(button.Id));

    private static string Excerpt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
    }
}

/// <summary>One row in the open-missions list.</summary>
public sealed class MissionSummaryItem(RolloutLoud.Core.Missions.MissionEngine engine) : Observable
{
    public string Id => engine.Mission.Id;

    public string Label =>
        $"{Truncate(engine.Mission.Objective, 60)}  ·  {engine.Mission.AgentId}  ·  " +
        $"{engine.Mission.State}  ·  T{engine.Mission.EscalationTier}  ·  {engine.Ledger.Count}";

    public void Refresh() => Raise(nameof(Label));

    private static string Truncate(string value, int max)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}

public sealed class MainViewModel : Observable
{
    private readonly RolloutHost _host;
    private readonly BridgeServer _bridge;
    private readonly AgentSupervisor _supervisor;

    private string _objective = string.Empty;
    private string _gateCommand = string.Empty;
    private string _gateDescription = string.Empty;
    private bool _gateIsCommand;
    private string _scopeTargets = string.Empty;
    private string _scopeAuthorization = string.Empty;
    private bool _offloadEnabled;
    private bool _offloadAlways = true;
    private int _tokenThreshold = 120_000;
    private int _maxAttempts = 200;
    private double _maxHours = 6;
    private bool _watchdogEnabled = true;
    private bool _allowUnattendedShutdown;
    private MissionSummaryItem? _selectedMission;
    // Normalised to the two the toggle moves between. ROLLOUTLOUD_THEME=system still applies to
    // the window itself; it just is not a state the button can land on.
    private ThemeChoice _theme =
        UiPreferences.Load().Effective == ThemeChoice.Light ? ThemeChoice.Light : ThemeChoice.Dark;
    private double _watchdogRoundMinutes = 20;
    private bool _relayBetweenAgents = true;
    private string _selectedAgentId = AgentCatalog.Claude;
    private MissionEngine? _mission;

    public MainViewModel(RolloutHost host, BridgeServer bridge)
    {
        _host = host;
        _bridge = bridge;
        _supervisor = new AgentSupervisor(host, host.Paths);

        Agents = [.. host.Agents.Select(a => new AgentLauncher(a, this))];

        StartMission = new RelayCommand(_ => StartMissionAsync(), _ => !string.IsNullOrWhiteSpace(Objective));
        PauseMission = new RelayCommand(_ => TogglePause(), _ => _mission is not null);
        AbortMission = new RelayCommand(_ => AbortCurrentMission(), _ => _mission is not null);
        CheckGate = new RelayCommand(_ => CheckGateAsync(), _ => _mission is not null);
        ToggleSupervision = new RelayCommand(_ => ToggleSupervisionAsync(), _ => _mission is not null);
        Elevate = new RelayCommand(_ => ElevateAsync(), _ => !IsElevated && _host.Elevation.CanElevate);
        ToggleTheme = new RelayCommand(_ => FlipTheme());
        AttachIdentity = new RelayCommand(_ => AttachIdentityFile());
        TidyUp = new RelayCommand(_ => TidyWorkspace());
        RevealIdentity = new RelayCommand(_ => RevealIdentityFile(), _ => IdentityAttached);
        OpenAllowlist = new RelayCommand(_ => WriteStarterConfiguration());

        host.StateChanged += OnHostChanged;

        host.MissionEventLogged += e => Dispatcher.UIThread.Post(() =>
        {
            // Every kind, not a filtered subset. An escalation, a contradicted gate, a scope
            // block and an injection flag are all things the operator wants to see happen, and
            // deciding here which ones matter would be guessing on their behalf.
            Log(e.Kind switch
            {
                "injection" => "⚠ " + e.Message,
                "gate-contradicted" => "⚠ " + e.Message,
                "scope-block" => "⚠ " + e.Message,
                _ => $"[{e.Kind}] {e.Message}",
            });
        });
        bridge.Logged += message => Dispatcher.UIThread.Post(() => Log(message));
        RelayCommand.Failed += ex => Dispatcher.UIThread.Post(() => Log("Error: " + ex.Message));

        _supervisor.Logged += e => Dispatcher.UIThread.Post(() =>
        {
            Log($"[watchdog:{e.Kind}] {e.Message}");
            RefreshWatchdog();
        });

        Log($"Anchored to {host.Paths.RepositoryRoot}");

        if (host.LastHousekeeping is { } tidy)
        {
            Log(tidy.Summary);
        }
        Log($"Language: {Localizer.Current.Language}");
        Log(IsElevated
            ? "Running elevated. Fluid buttons and elevated CLIs will start without another prompt."
            : "Running unelevated. Elevated launches will offer to restart RolloutLoud first.");
    }

    public ObservableCollection<AgentLauncher> Agents { get; }

    public ObservableCollection<ButtonCard> Buttons { get; } = [];

    public ObservableCollection<string> Activity { get; } = [];

    public ObservableCollection<string> Ledger { get; } = [];

    /// <summary>
    /// Every mission open in this window.
    /// </summary>
    /// <remarks>
    /// Several agents work here at once — one mission each — so this is not decoration. Which one
    /// is selected decides what "active" resolves to for an agent that calls the bridge without
    /// naming a mission, which is most of them.
    /// </remarks>
    public ObservableCollection<MissionSummaryItem> OpenMissions { get; } = [];

    public MissionSummaryItem? SelectedMission
    {
        get => _selectedMission;
        set
        {
            Set(ref _selectedMission, value);
            if (value is null)
            {
                return;
            }

            _mission = _host.FindMission(value.Id);
            _host.SetActiveMission(value.Id);
            RefreshMission();
        }
    }

    /// <summary>
    /// Whether an agent may close the window without a click.
    /// </summary>
    /// <remarks>
    /// Off by default, and the split is deliberate: the gate decides whether the WORK is done, and
    /// this decides whether the operator wants the window gone as a result. Two different
    /// questions, and the second one is theirs.
    /// </remarks>
    public bool AllowUnattendedShutdown
    {
        get => _allowUnattendedShutdown;
        set
        {
            Set(ref _allowUnattendedShutdown, value);
            _host.AllowUnattendedShutdown = value;
        }
    }

    /// <summary>
    /// Sun or moon, one click, two states.
    /// </summary>
    /// <remarks>
    /// The glyph shows what clicking will GIVE you, not what you have — the usual convention for
    /// a theme toggle, and unambiguous here because there are only two states to move between.
    /// The tooltip says it in words as well, because a bare glyph is a guess for anybody who has
    /// not met the convention.
    ///
    /// "Follow the system" is deliberately not offered. It is a third state that has to be
    /// explained, and its value is mostly to somebody who changes their OS theme during the day.
    /// ROLLOUTLOUD_THEME=system still reaches it for a contrast check.
    /// </remarks>
    /// <summary>
    /// Vector geometry rather than a text glyph.
    /// </summary>
    /// <remarks>
    /// ☀ and ☽ were the obvious first choice and the moon was nearly invisible: the two codepoints
    /// come from different fonts in the fallback chain and are drawn at different weights, so the
    /// sun read as an icon and the moon as a hairline. Drawing both as paths makes them one
    /// family, and takes the font stack out of the question entirely.
    /// </remarks>
    public string ThemeGeometry => _theme == ThemeChoice.Dark ? SunGeometry : MoonGeometry;

    private const string SunGeometry =
        "M12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7Z " +
        "M11,1H13V4.5H11V1Z M11,19.5H13V23H11V19.5Z M1,11H4.5V13H1V11Z M19.5,11H23V13H19.5V11Z " +
        "M4.22,5.64L5.64,4.22L8.11,6.69L6.69,8.11L4.22,5.64Z " +
        "M15.89,17.31L17.31,15.89L19.78,18.36L18.36,19.78L15.89,17.31Z " +
        "M18.36,4.22L19.78,5.64L17.31,8.11L15.89,6.69L18.36,4.22Z " +
        "M5.64,19.78L4.22,18.36L6.69,15.89L8.11,17.31L5.64,19.78Z";

    private const string MoonGeometry =
        "M12.5,2A9.5,9.5 0 0,0 12.5,21C15,21 17.26,20.03 18.94,18.45C14.2,18.2 10.5,14.5 " +
        "10.5,10A8.5,8.5 0 0,1 15.4,2.28C14.47,2.1 13.5,2 12.5,2Z";

    public string ThemeTooltip =>
        Localizer.Current[_theme == ThemeChoice.Dark ? "theme.toLight" : "theme.toDark"];

    public RelayCommand ToggleTheme { get; }

    private Task FlipTheme()
    {
        _theme = _theme == ThemeChoice.Dark ? ThemeChoice.Light : ThemeChoice.Dark;
        AvaloniaApp.SetTheme(_theme);

        Raise(nameof(ThemeGeometry));
        Raise(nameof(ThemeTooltip));
        Log($"Theme: {_theme}. Remembered in {UiPreferences.FilePath}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The live context reading for the current mission's agent.
    /// </summary>
    /// <remarks>
    /// Shown next to the threshold because a threshold without a reading is a number with nothing
    /// to compare it against — which is exactly what it was before the meter existed.
    /// </remarks>
    public string ContextReadingSummary
    {
        get
        {
            if (_mission is null)
            {
                return Localizer.Current["offload.reading.none"];
            }

            var decision = _host.OffloadNow(_mission.Mission);
            return decision.Reading.HasNumber ? decision.Reason : Localizer.Current["offload.reading.none"];
        }
    }

    // ---- housekeeping -----------------------------------------------------------------------

    /// <summary>
    /// What .rolloutloud/ currently holds, and what the last tidy removed.
    /// </summary>
    /// <remarks>
    /// Visible rather than silent because the growth is invisible otherwise until somebody
    /// wonders why a folder has twelve thousand directories in it. A number on screen is also what
    /// makes the pruning trustworthy — an operator who can see it working does not have to take
    /// it on faith that it is not eating something they wanted.
    /// </remarks>
    public string DiskSummary =>
        _host.LastHousekeeping?.Summary ?? Localizer.Current["housekeeping.hint"];

    public RelayCommand TidyUp { get; }

    private Task TidyWorkspace()
    {
        var report = _host.Tidy();

        Log(report.DidAnything
            ? report.Summary
            : "Nothing to tidy. " + report.Summary);

        Raise(nameof(DiskSummary));
        return Task.CompletedTask;
    }

    // ---- attached identity ------------------------------------------------------------------

    public bool IdentityAttached => _host.HasAttachedIdentity;

    public string IdentityStatus =>
        Localizer.Current[IdentityAttached ? "identity.attached" : "identity.none"];

    /// <summary>
    /// Shown whenever a file is attached, not tucked behind a link.
    /// </summary>
    /// <remarks>
    /// The operator asked for this specifically, and they were right: the moment that matters is
    /// when somebody is about to put something in the file, not when they read the README six
    /// months ago. It names what does not belong there rather than saying "no secrets" — a
    /// blanket rule that gets ignored protects nothing, and a throwaway password for a disposable
    /// account is exactly what the feature is for.
    /// </remarks>
    public string IdentityWarning => Localizer.Current["identity.warning"];

    public RelayCommand AttachIdentity { get; }

    public RelayCommand RevealIdentity { get; }

    private Task AttachIdentityFile()
    {
        var file = _host.Paths.IdentityFile;

        if (File.Exists(file))
        {
            Log($"An identity file already exists at {file}. Edit it rather than replacing it.");
            return RevealIdentityFile();
        }

        AttachedIdentity.WriteTemplate(file);
        Log($"Wrote {file}. Edit it, then agents can ask for it by site.");
        Log("⚠ Anything in that file reaches the model provider. Throwaway test credentials only — " +
            "no payment details, no password you use anywhere real, no recovery codes.");

        RefreshIdentity();
        return RevealIdentityFile();
    }

    private Task RevealIdentityFile()
    {
        var file = _host.Paths.IdentityFile;
        if (!File.Exists(file))
        {
            return Task.CompletedTask;
        }

        try
        {
            // UseShellExecute so the OS opens it in whatever the operator edits JSON with, rather
            // than this app deciding on an editor.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = file, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"Could not open {file}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void RefreshIdentity()
    {
        Raise(nameof(IdentityAttached));
        Raise(nameof(IdentityStatus));
        RevealIdentity.RaiseCanExecuteChanged();
    }

    public string RepositoryRoot => _host.Paths.RepositoryRoot;

    public string BridgeHint => Localizer.Current.Format("app.bridge.hint", _bridge.Endpoint);

    public bool IsElevated => _host.Elevation.IsElevated;

    public string ElevationBadge => Localizer.Current[IsElevated ? "app.elevated" : "app.notElevated"];

    public string ElevationDetail => IsElevated
        ? Localizer.Current["app.elevated.detail"]
        : _host.Elevation.CanElevate
            ? Localizer.Current["app.notElevated.detail"]
            : _host.Elevation.PromptDescription;

    public IReadOnlyList<string> AgentIds => [.. _host.Agents.Select(a => a.Id)];

    public string SelectedAgentId
    {
        get => _selectedAgentId;
        set => Set(ref _selectedAgentId, value);
    }

    public string Objective
    {
        get => _objective;
        set
        {
            Set(ref _objective, value);
            StartMission.RaiseCanExecuteChanged();
        }
    }

    public bool GateIsCommand
    {
        get => _gateIsCommand;
        set => Set(ref _gateIsCommand, value);
    }

    public string GateCommand
    {
        get => _gateCommand;
        set => Set(ref _gateCommand, value);
    }

    public string GateDescription
    {
        get => _gateDescription;
        set => Set(ref _gateDescription, value);
    }

    public string ScopeTargets
    {
        get => _scopeTargets;
        set
        {
            Set(ref _scopeTargets, value);
            Raise(nameof(ScopeWarning));
        }
    }

    public string ScopeAuthorization
    {
        get => _scopeAuthorization;
        set
        {
            Set(ref _scopeAuthorization, value);
            Raise(nameof(ScopeWarning));
        }
    }

    /// <summary>
    /// Shown live under the scope box. A declared target with no authorisation on record is the
    /// state worth catching before the run rather than after it.
    /// </summary>
    public string ScopeWarning =>
        !string.IsNullOrWhiteSpace(ScopeTargets) && string.IsNullOrWhiteSpace(ScopeAuthorization)
            ? Localizer.Current["scope.warning"]
            : string.Empty;

    public bool OffloadEnabled
    {
        get => _offloadEnabled;
        set => Set(ref _offloadEnabled, value);
    }

    public bool OffloadAlways
    {
        get => _offloadAlways;
        set => Set(ref _offloadAlways, value);
    }

    public int TokenThreshold
    {
        get => _tokenThreshold;
        set => Set(ref _tokenThreshold, value);
    }

    public int MaxAttempts
    {
        get => _maxAttempts;
        set => Set(ref _maxAttempts, value);
    }

    public double MaxHours
    {
        get => _maxHours;
        set => Set(ref _maxHours, value);
    }

    public bool WatchdogEnabled
    {
        get => _watchdogEnabled;
        set => Set(ref _watchdogEnabled, value);
    }

    public double WatchdogRoundMinutes
    {
        get => _watchdogRoundMinutes;
        set => Set(ref _watchdogRoundMinutes, value);
    }

    /// <summary>
    /// Whether a stuck mission is handed to a different CLI on its own.
    /// </summary>
    /// <remarks>
    /// On by default, because the rung is useless if it needs somebody awake to trigger it — the
    /// whole point of a tier-3 escalation is that it happens at 3am when the current agent has run
    /// out of habits.
    /// </remarks>
    public bool RelayBetweenAgents
    {
        get => _relayBetweenAgents;
        set => Set(ref _relayBetweenAgents, value);
    }

    public string WatchdogStatus => _supervisor.IsRunning
        ? Localizer.Current.Format("watchdog.status.running", _supervisor.AgentId, _supervisor.Round)
        : Localizer.Current["watchdog.status.idle"];

    public string SuperviseLabel =>
        Localizer.Current[_supervisor.IsRunning ? "action.stopSupervision" : "action.supervise"];

    public string MissionSummary => _mission is null
        ? Localizer.Current["mission.none"]
        : Localizer.Current.Format(
            "mission.summary",
            _mission.Mission.State,
            _mission.Mission.EscalationTier,
            EscalationLadder.NameOf(_mission.Mission.EscalationTier),
            _mission.Ledger.Count,
            _mission.Mission.AgentId);

    /// <summary>Who has already worked the current mission, if anyone.</summary>
    public string RelayHistory =>
        _mission is { Mission.RelayHistory.Count: > 0 }
            ? Localizer.Current.Format("mission.relayed", string.Join(" → ", _mission.Mission.RelayHistory))
            : string.Empty;

    public string PauseLabel =>
        Localizer.Current[_mission?.Mission.State == MissionState.Paused ? "action.resume" : "action.pause"];

    public RelayCommand StartMission { get; }

    public RelayCommand PauseMission { get; }

    public RelayCommand AbortMission { get; }

    public RelayCommand CheckGate { get; }

    public RelayCommand ToggleSupervision { get; }

    public RelayCommand Elevate { get; }

    public RelayCommand OpenAllowlist { get; }

    // ---- actions ---------------------------------------------------------------------------

    internal void Launch(AgentDescriptor agent, LaunchMode mode)
    {
        _host.LaunchAgent(agent, mode, _mission);
        Log($"Launched {agent.DisplayName} ({mode.ToString().ToLowerInvariant()}) in {RepositoryRoot}");
    }

    /// <summary>
    /// The elevated launch, including the warning.
    /// </summary>
    /// <remarks>
    /// The order matters. If RolloutLoud is not elevated it offers to restart itself first,
    /// because a CLI started from an unelevated RolloutLoud is unelevated no matter which button
    /// was clicked — and an operator who clicked the red button and got an ordinary shell will
    /// not find that out until a privileged command fails an hour later.
    /// </remarks>
    internal async Task LaunchElevatedAsync(AgentDescriptor agent)
    {
        if (!IsElevated)
        {
            var choice = await ElevationPrompt
                .AskAsync(agent.DisplayName, _host.Elevation.PromptDescription)
                .ConfigureAwait(true);

            switch (choice)
            {
                case ElevationChoice.Elevate:
                    // Restarting replaces this process, so the launch is not resumed here. The
                    // operator clicks the button again in the elevated window — one deliberate
                    // click rather than an agent that appears out of a restart they may have
                    // forgotten they triggered.
                    await ElevateAsync().ConfigureAwait(true);
                    return;

                case ElevationChoice.Cancel:
                    Log($"{agent.DisplayName} launch cancelled.");
                    return;

                default:
                    Log($"{agent.DisplayName} launching with its bypass flag but no OS elevation, at your request.");
                    break;
            }
        }

        Launch(agent, LaunchMode.Elevated);
    }

    internal async Task ElevateAsync()
    {
        if (IsElevated)
        {
            return;
        }

        Log("Requesting elevation…");
        var started = await _host.Elevation.RelaunchElevatedAsync(RepositoryRoot).ConfigureAwait(true);

        if (!started)
        {
            Log("Elevation declined. Carrying on unelevated.");
            return;
        }

        // The elevated copy owns the bridge port and the handshake file from here on. Two
        // RolloutLouds fighting over both is worse than a moment of no window.
        Environment.Exit(0);
    }

    private Task StartMissionAsync()
    {
        var scope = string.IsNullOrWhiteSpace(ScopeTargets)
            ? MissionScope.Unrestricted
            : new MissionScope
            {
                Targets = [.. ScopeTargets.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                Authorization = string.IsNullOrWhiteSpace(ScopeAuthorization) ? null : ScopeAuthorization,
            };

        var gate = GateIsCommand && !string.IsNullOrWhiteSpace(GateCommand)
            ? new SuccessGate
            {
                Kind = GateKind.Command,
                Command = GateCommand,
                Description = string.IsNullOrWhiteSpace(GateDescription) ? null : GateDescription,
            }
            : SuccessGate.OperatorJudged with
            {
                Description = string.IsNullOrWhiteSpace(GateDescription) ? null : GateDescription,
            };

        var mission = new Mission
        {
            Id = Mission.NewId(),
            Objective = Objective.Trim(),
            AgentId = SelectedAgentId,
            Gate = gate,
            Scope = scope,
            Stop = new StopConditions
            {
                MaxAttempts = Math.Max(1, MaxAttempts),
                MaxWallClock = TimeSpan.FromHours(Math.Max(0.1, MaxHours)),
            },
            Offload = new OffloadSettings
            {
                Trigger = !OffloadEnabled
                    ? OffloadTrigger.Off
                    : OffloadAlways ? OffloadTrigger.Always : OffloadTrigger.ContextThreshold,
                TokenThreshold = Math.Max(1000, TokenThreshold),
            },
        };

        _mission = _host.CreateMission(mission);
        _mission.Start();
        SyncMissions();
        _selectedMission = OpenMissions.FirstOrDefault(m => m.Id == mission.Id);
        Raise(nameof(SelectedMission));

        Log($"Mission {mission.Id} started on {mission.AgentId}.");
        if (!gate.IsMachineCheckable)
        {
            Log("No machine-checkable gate: only you can end this mission. Agents will not stop on their own.");
        }

        if (scope.NeedsAuthorization)
        {
            Log("⚠ Targets declared with no authorisation recorded. Fill that in before you run this against anything live.");
        }

        RefreshMission();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts or stops supervision — RolloutLoud running the agent itself and restarting it when
    /// it stops before the gate is satisfied.
    /// </summary>
    private async Task ToggleSupervisionAsync()
    {
        if (_supervisor.IsRunning)
        {
            await _supervisor.StopAsync().ConfigureAwait(true);
            RefreshWatchdog();
            return;
        }

        if (_mission is null)
        {
            return;
        }

        if (!WatchdogEnabled)
        {
            Log("The watchdog is switched off, so a supervised run would launch the agent once and " +
                "let it stop. Turn it on first, or use a launch button.");
            return;
        }

        _supervisor.Settings = _supervisor.Settings with
        {
            RoundTimeout = TimeSpan.FromMinutes(Math.Max(1, WatchdogRoundMinutes)),
            RelayBetweenAgents = RelayBetweenAgents,
        };

        // Supervised rounds are headless — there is no terminal for the operator to type into.
        // That is the trade for being able to restart the process at all, and saying it here
        // beats the operator discovering it by looking for a window that never appears.
        Log("Supervised rounds run headless: no terminal window, output captured to .rolloutloud/runs/.");

        _supervisor.Start(_mission);
        RefreshWatchdog();
    }

    private Task TogglePause()
    {
        if (_mission is null)
        {
            return Task.CompletedTask;
        }

        if (_mission.Mission.State == MissionState.Paused)
        {
            _mission.Resume();
            Log("Mission resumed.");
        }
        else
        {
            _mission.Pause("Paused from the RolloutLoud window.");
            Log("Mission paused. Agents polling /continue will be told to stop.");
        }

        RefreshMission();
        return Task.CompletedTask;
    }

    private async Task AbortCurrentMission()
    {
        if (_supervisor.IsRunning)
        {
            await _supervisor.StopAsync().ConfigureAwait(true);
        }

        _mission?.Abort("Aborted from the RolloutLoud window.");
        Log("Mission aborted.");
        RefreshMission();
        RefreshWatchdog();
    }

    private async Task CheckGateAsync()
    {
        if (_mission is null)
        {
            return;
        }

        Log("Evaluating the success gate…");
        var verdict = await _mission.EvaluateGateAsync().ConfigureAwait(true);

        Log(verdict switch
        {
            { Contradicted: true } => "Gate passed once and failed on re-run — not reproducible. " + verdict.Detail,
            { Satisfied: true } => "Gate satisfied and re-verified. " + verdict.Detail,
            _ => "Gate not satisfied. " + verdict.Detail,
        });

        RefreshMission();
    }

    internal async Task InvokeButtonAsync(string buttonId)
    {
        var button = await _host.InvokeButtonAsync(buttonId, byOperator: true).ConfigureAwait(true);
        Log($"Ran '{button.Title}' → {button.Status}");
    }

    internal void DismissButton(string buttonId) => _host.DismissButton(buttonId);

    /// <summary>Writes the starter agents.json and allowlist.json so the formats are discoverable.</summary>
    private Task WriteStarterConfiguration()
    {
        if (!File.Exists(_host.Paths.AllowlistFile))
        {
            ButtonAllowlist.Write(_host.Paths.AllowlistFile, ButtonAllowlist.SuggestedPatterns);
        }

        if (!File.Exists(_host.Paths.AgentsFile))
        {
            AgentCatalog.WriteDefaults(_host.Paths.AgentsFile);
        }

        _host.ReloadConfiguration();
        Log($"Configuration written to {_host.Paths.StateRoot}. Edit allowlist.json to let agents self-invoke buttons.");
        return Task.CompletedTask;
    }

    // ---- refresh ---------------------------------------------------------------------------

    private void OnHostChanged() => Dispatcher.UIThread.Post(() =>
    {
        SyncButtons();
        SyncMissions();
        RefreshMission();
        RefreshIdentity();
        Raise(nameof(DiskSummary));
    });

    private void SyncMissions()
    {
        foreach (var engine in _host.Missions.OrderByDescending(m => m.Mission.CreatedAt))
        {
            var existing = OpenMissions.FirstOrDefault(m => m.Id == engine.Mission.Id);
            if (existing is null)
            {
                OpenMissions.Add(new MissionSummaryItem(engine));
            }
            else
            {
                existing.Refresh();
            }
        }

        // Follow the host's active mission when the window is not already on one.
        //
        // Without this, a mission opened through the bridge — which is the main flow, since that
        // is how an agent starts work — left the list populated but nothing selected, the summary
        // reading "no mission", and Pause/Abort/Check gate all disabled. The window looked like
        // nothing had happened while an agent was already working.
        if (_selectedMission is not null || _host.ActiveMissionId is not { } active)
        {
            return;
        }

        var item = OpenMissions.FirstOrDefault(m => m.Id == active);
        if (item is null)
        {
            return;
        }

        _selectedMission = item;
        _mission = _host.FindMission(active);
        Raise(nameof(SelectedMission));
    }

    /// <summary>Records why the window is about to close, so the last log line explains it.</summary>
    internal void NoteShutdown(string reason) => Log("Closing: " + reason);

    private void SyncButtons()
    {
        foreach (var button in _host.Buttons)
        {
            var existing = Buttons.FirstOrDefault(b => b.Id == button.Id);
            if (existing is null)
            {
                Buttons.Insert(0, new ButtonCard(button, this));
                Log($"New button from {button.RequestedBy ?? "an agent"}: {button.Title}");
            }
            else
            {
                existing.Model = button;
            }
        }
    }

    private void RefreshWatchdog()
    {
        Raise(nameof(WatchdogStatus));
        Raise(nameof(SuperviseLabel));
        RefreshMission();
    }

    private void RefreshMission()
    {
        Raise(nameof(MissionSummary));
        Raise(nameof(RelayHistory));
        Raise(nameof(ContextReadingSummary));
        Raise(nameof(PauseLabel));
        PauseMission.RaiseCanExecuteChanged();
        AbortMission.RaiseCanExecuteChanged();
        CheckGate.RaiseCanExecuteChanged();
        ToggleSupervision.RaiseCanExecuteChanged();

        Ledger.Clear();
        if (_mission is null)
        {
            return;
        }

        foreach (var attempt in _mission.Ledger.Attempts.TakeLast(60).Reverse())
        {
            Ledger.Add($"[{attempt.Outcome}] {attempt.Hypothesis} — {attempt.Observation ?? attempt.Command}");
        }
    }

    private void Log(string message)
    {
        Activity.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {message}");

        // The log is a window onto what is happening, not an archive. The archive is the ledger
        // and the run folders; an unbounded ObservableCollection here just leaks over six hours.
        while (Activity.Count > 300)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
    }
}
