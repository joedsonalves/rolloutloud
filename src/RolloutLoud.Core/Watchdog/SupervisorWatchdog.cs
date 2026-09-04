using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Watchdog;

/// <summary>
/// Watches for runs that have nobody supervising them, and opens somebody.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="AgentSupervisor"/>, which watches the worker. That one restarts an
/// agent that stopped and waits out a quota window; nothing watched the <em>supervisor</em>, so when
/// that session ended the agent's questions piled up unanswered and the run carried on unread.
///
/// <b>It only ever looks at missions the operator has delegated.</b> That is the consent, and it is
/// the one they already give per mission — a delegation says "a supervising session may act for me
/// here", which is exactly the statement that makes opening one on their behalf legitimate. A
/// mission with no delegation is one where the operator is at the keyboard, and opening a session to
/// stand in for somebody who is present would be both a waste and an intrusion.
///
/// ⚠️ <b>This spends money without being asked</b>, which is why the floor between wake-ups is not
/// optional and why the trigger is a fact rather than a mood. Every condition here can stay true
/// after a supervisor has looked and decided there was nothing to add — a question it deliberately
/// left open still reads as open — so without the floor it would open a session a minute for the
/// rest of the night.
/// </remarks>
public sealed class SupervisorWatchdog : IAsyncDisposable
{
    private readonly RolloutHost _host;
    private readonly TimeSpan _tick;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public SupervisorWatchdog(RolloutHost host, TimeSpan? tick = null)
    {
        _host = host;
        _tick = tick ?? TimeSpan.FromMinutes(1);
    }

    public WakeSettings Settings { get; set; } = new();

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Nothing to report.
        }

        _cancellation.Dispose();
        _cancellation = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_tick, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await CheckConsentAsync(cancellationToken).ConfigureAwait(false);

            foreach (var engine in _host.Missions)
            {
                // The delegation is the consent, and it is checked every pass rather than
                // remembered: withdrawing it has to stop this on the next tick, which is the
                // operator's only lever once they have walked away.
                if (_host.Deputies.For(engine.Mission.Id) is null)
                {
                    continue;
                }

                var decision = SupervisorWatch.Assess(
                    engine.Mission,
                    Settings,
                    DateTimeOffset.UtcNow,
                    _host.LastSupervisorWake,
                    DeliverableWrittenAt(engine.Mission));

                if (decision.Wake)
                {
                    _host.WakeSupervisor(engine, decision.Reason);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Notices when the run is editing the machinery that decides who may do what.
    /// </summary>
    /// <remarks>
    /// Detected rather than self-reported: asking the session to declare this would be asking the
    /// thing being watched to raise its own hand. And written to a file as well as the log, because
    /// the operator chose to be warned rather than asked, and the premise of the mission they set is
    /// that they are not reading the log while it happens.
    ///
    /// Only announced when the set of touched files <em>changes</em>. A supervisor working on the
    /// consent code for an hour would otherwise produce sixty identical warnings, which is how a
    /// warning stops being read.
    /// </remarks>
    private async Task CheckConsentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await Execution.ProcessLauncher.RunShellAsync(
                "git status --porcelain",
                _host.Paths.RepositoryRoot,
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);

            var touched = Consent.ConsentWatch.Touched(
                Consent.ConsentWatch.PathsIn(status.StandardOutput));

            var signature = string.Join("|", touched);

            if (touched.Count == 0 || signature == _lastConsentSignature)
            {
                _lastConsentSignature = signature;
                return;
            }

            _lastConsentSignature = signature;

            var line = Consent.ConsentWatch.Describe(touched, "this run");
            _host.Announce("⚠ " + line);

            var record = Path.Combine(_host.Paths.StateRoot, "consent-changes.log");
            await File.AppendAllTextAsync(record, line + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not a git repository, git not installed, a locked index mid-commit. None of those is
            // worth stopping the watchdog over, and none of them is evidence of anything.
        }
    }

    private string _lastConsentSignature = string.Empty;

    /// <summary>
    /// When the deliverable was last written, or null when there is nothing to look at.
    /// </summary>
    /// <remarks>
    /// Resolved against the mission's working directory, because a run working in another
    /// repository names its deliverable relative to that one — checking the anchor would report
    /// "never written" for a file being edited every few minutes.
    /// </remarks>
    private static DateTimeOffset? DeliverableWrittenAt(Mission mission)
    {
        if (string.IsNullOrWhiteSpace(mission.Deliverable))
        {
            return null;
        }

        try
        {
            var root = string.IsNullOrWhiteSpace(mission.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : mission.WorkingDirectory;

            var path = Path.IsPathRooted(mission.Deliverable)
                ? mission.Deliverable
                : Path.Combine(root, mission.Deliverable);

            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
