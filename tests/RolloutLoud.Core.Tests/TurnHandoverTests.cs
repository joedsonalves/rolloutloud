using RolloutLoud.Core;
using RolloutLoud.Core.Elevation;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The swap the ceiling prompt promised and nothing performed.
/// </summary>
/// <remarks>
/// <c>ShouldHandOver</c> was consulted in exactly one place: to append "RolloutLoud will open your
/// replacement when it makes sense" to a <c>/continue</c> response. Nothing opened one. A session
/// wrote its handover, was told to carry on, and carried on for the rest of the run in the same
/// window it had just been told was too expensive.
///
/// <b>The note is the token.</b> A replacement needs a handover recorded since the last one. That is
/// what makes the swap fire once per ceiling rather than once per turn — a replaced session's window
/// reading resets, but the degrading-progress trigger does not — and it is also what stops a session
/// being closed before it has said what it learned.
/// </remarks>
public sealed class TurnHandoverTests : IDisposable
{
    private readonly RolloutPaths _paths;
    private readonly RolloutHost _host;

    public TurnHandoverTests()
    {
        _paths = new RolloutPaths(Path.Combine(Path.GetTempPath(), "rlturn-" + Guid.NewGuid().ToString("N")[..8]));
        _paths.EnsureCreated();
        _host = new RolloutHost(_paths, new NoElevation());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_paths.RepositoryRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a run over.
        }
    }

    private sealed class NoElevation : IElevationService
    {
        public bool IsElevated => false;

        public bool CanElevate => false;

        public string PromptDescription => "not in a test";

        public Task<bool> RelaunchElevatedAsync(string root, CancellationToken token = default) =>
            Task.FromResult(false);
    }

    private static Mission Mission() => new()
    {
        Id = "m1",
        Objective = "make the suite pass",
        AgentId = "claude",
        State = MissionState.Running,
    };

    private void Wrote(string role, string believes = "the runner is fine") =>
        _host.Brain.Record("m1", new Handover
        {
            Role = role,
            From = "claude",
            Believes = believes,
        });

    [Fact]
    public void With_no_handover_written_nothing_is_replaced()
    {
        // ⚠️ The half that protects the outgoing session. Closing a window before it has said what
        // it learned throws away the only thing the ledger cannot reconstruct, and the ceiling
        // prompt keeps asking until it does.
        Assert.False(_host.HandoverIsReady(Mission(), RolloutHost.WorkerRole));
        Assert.False(_host.SpendHandover("m1", RolloutHost.WorkerRole));
    }

    [Fact]
    public void A_written_handover_makes_the_swap_ready()
    {
        Wrote(RolloutHost.WorkerRole);

        Assert.True(_host.HandoverIsReady(Mission(), RolloutHost.WorkerRole));
    }

    [Fact]
    public void One_note_buys_one_replacement()
    {
        // ⚠️ Without this the run swaps every turn. The window reading resets when a session is
        // replaced, so the ceiling trigger goes quiet — but the degrading-progress trigger is
        // computed from the ledger, which the new session inherits, so it stays true.
        Wrote(RolloutHost.WorkerRole);

        Assert.True(_host.SpendHandover("m1", RolloutHost.WorkerRole));
        Assert.False(_host.SpendHandover("m1", RolloutHost.WorkerRole));
        Assert.False(_host.HandoverIsReady(Mission(), RolloutHost.WorkerRole));
    }

    [Fact]
    public void The_next_session_writing_its_own_note_buys_the_next_replacement()
    {
        Wrote(RolloutHost.WorkerRole, "first session");
        Assert.True(_host.SpendHandover("m1", RolloutHost.WorkerRole));

        Wrote(RolloutHost.WorkerRole, "second session");

        Assert.True(_host.HandoverIsReady(Mission(), RolloutHost.WorkerRole));
        Assert.True(_host.SpendHandover("m1", RolloutHost.WorkerRole));
    }

    [Fact]
    public void The_two_roles_spend_their_own_notes()
    {
        // A supervisor writing its handover must not close the worker's window, and the other way
        // round. They are separate sessions with separate ceilings.
        Wrote(RolloutHost.SupervisorRole);

        Assert.False(_host.HandoverIsReady(Mission(), RolloutHost.WorkerRole));
        Assert.True(_host.HandoverIsReady(Mission(), RolloutHost.SupervisorRole));

        Assert.True(_host.SpendHandover("m1", RolloutHost.SupervisorRole));
        Assert.False(_host.SpendHandover("m1", RolloutHost.WorkerRole));
    }

    [Fact]
    public void The_prompt_for_the_turn_it_happens_tells_the_session_to_stop()
    {
        // The two prompts are different instructions and the difference matters on the last turn.
        // "Write your handover, then carry on" spends a turn the session does not have; the run
        // that prompted all this had the first sentence and no second one at all.
        Assert.Contains("carry on", Watchdog.HandoverWatch.HandoverPrompt, StringComparison.Ordinal);
        Assert.Contains("Stop here", Watchdog.HandoverWatch.ReplacedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("carry on", Watchdog.HandoverWatch.ReplacedPrompt, StringComparison.Ordinal);
    }
}
