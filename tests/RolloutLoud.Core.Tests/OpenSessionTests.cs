using System.Diagnostics;
using System.Runtime.InteropServices;
using RolloutLoud.Core.Execution;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// One CLI window per role, and only the ones RolloutLoud opened itself get closed.
/// </summary>
/// <remarks>
/// The run that prompted this left a screen full of terminals. The supervisor wake-up fires on a
/// symptom — a question open with nobody answering — and its only brake was a fifteen-minute floor
/// between wakes. A supervisor that could not answer left the question open, which kept the trigger
/// true, which opened another window, for as long as the run lasted.
///
/// A floor in minutes was never the missing piece. <em>Is one already open</em> was, and answering
/// it needs a handle that outlives the launch — which is why <c>start</c> had to go first.
/// </remarks>
public sealed class OpenSessionTests : IDisposable
{
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A test process that got away is not worth failing a run over.
            }

            process.Dispose();
        }
    }

    /// <summary>A process that stays up long enough to be found alive, and dies on its own after.</summary>
    private Process Sleeper()
    {
        var info = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;

        var process = Process.Start(info)!;
        _started.Add(process);
        return process;
    }

    [Fact]
    public void Nothing_open_means_nothing_to_close()
    {
        var sessions = new OpenSessions();

        Assert.False(sessions.IsLive("worker"));
        Assert.Null(sessions.OriginOf("worker"));
        Assert.Null(sessions.Retire("worker"));
    }

    [Fact]
    public void A_window_RolloutLoud_opened_is_closed_when_its_turn_is_up()
    {
        var sessions = new OpenSessions();
        var process = Sleeper();

        sessions.Register("supervisor", process, SessionOrigin.Watchdog);
        Assert.True(sessions.IsLive("supervisor"));

        var said = sessions.Retire("supervisor");

        Assert.NotNull(said);
        Assert.Contains("Closed", said, StringComparison.Ordinal);
        Assert.True(process.WaitForExit(10_000), "the previous window was still running");
        Assert.False(sessions.IsLive("supervisor"));
    }

    [Fact]
    public void A_window_the_operator_opened_is_left_standing()
    {
        // ⚠️ The operator may be typing in it. A tool that kills the terminal under someone's hands
        // has done something worse than leaving a window around, so this one is told it has been
        // replaced rather than closed. The role is freed either way — the replacement takes it.
        var sessions = new OpenSessions();
        var process = Sleeper();

        sessions.Register("worker", process, SessionOrigin.Operator);
        Assert.Equal(SessionOrigin.Operator, sessions.OriginOf("worker"));

        var said = sessions.Retire("worker");

        Assert.NotNull(said);
        Assert.Contains("still open", said, StringComparison.Ordinal);
        Assert.False(process.HasExited);
        Assert.False(sessions.IsLive("worker"));
    }

    [Fact]
    public void A_window_that_was_closed_by_hand_does_not_hold_the_role()
    {
        // The operator closing a window themselves is the ordinary case, and it must not leave the
        // role occupied — that would be the opposite failure to the pile: no supervisor ever again,
        // because a dead handle says one is already open.
        var sessions = new OpenSessions();
        var process = Sleeper();

        sessions.Register("supervisor", process, SessionOrigin.Watchdog);
        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(10_000));

        Assert.False(sessions.IsLive("supervisor"));
        Assert.Null(sessions.OriginOf("supervisor"));
        Assert.Null(sessions.Retire("supervisor"));
    }

    [Fact]
    public void Registering_again_replaces_what_the_role_points_at()
    {
        var sessions = new OpenSessions();
        var first = Sleeper();
        var second = Sleeper();

        sessions.Register("worker", first, SessionOrigin.Watchdog);
        sessions.Register("worker", second, SessionOrigin.Watchdog);

        sessions.Retire("worker");

        Assert.True(second.WaitForExit(10_000), "the newest window should be the one retired");
        Assert.False(first.HasExited);
    }
}
