using System.Diagnostics;

namespace RolloutLoud.Core.Execution;

/// <summary>Who asked for a session to be opened.</summary>
public enum SessionOrigin
{
    /// <summary>The operator clicked a launch button. Theirs to close.</summary>
    Operator,

    /// <summary>RolloutLoud opened it on its own — a woken supervisor, or a turn handover.</summary>
    Watchdog,
}

/// <summary>
/// One CLI window per role, and the handle needed to close it.
/// </summary>
/// <remarks>
/// <b>The pile of windows this exists to stop.</b> The supervisor wake-up fires on a symptom — "a
/// question has been open for ten minutes with nobody answering" — and its only brake was a
/// fifteen-minute floor between wakes. A supervisor that could not answer, because it was out of
/// allowance or because it could not write, left the question open, which kept the trigger true.
/// One window every fifteen minutes for as long as the run lasted. The floor was never the missing
/// part: <em>is one already open</em> was.
///
/// ⚠️ <b>Only what RolloutLoud opened itself is closed.</b> A window the operator opened by clicking
/// is one they may be typing in, and a tool that kills the terminal under someone's hands has done
/// something worse than leaving a window around. It is told it has been replaced instead.
///
/// The handle is only worth holding because the launcher stopped going through <c>start</c>; see
/// <see cref="ProcessLauncher.BuildTerminalStartInfo"/>. Before that, what came back was a process
/// that had already exited and the window was a grandchild nobody could reach.
/// </remarks>
public sealed class OpenSessions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _byRole = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(Process Process, SessionOrigin Origin, DateTimeOffset At);

    /// <summary>Whether a session for this role is still on screen.</summary>
    public bool IsLive(string role)
    {
        lock (_gate)
        {
            return _byRole.TryGetValue(role, out var entry) && Alive(entry.Process);
        }
    }

    /// <summary>How the live session for this role was opened, or null when there is none.</summary>
    public SessionOrigin? OriginOf(string role)
    {
        lock (_gate)
        {
            return _byRole.TryGetValue(role, out var entry) && Alive(entry.Process) ? entry.Origin : null;
        }
    }

    public void Register(string role, Process process, SessionOrigin origin)
    {
        lock (_gate)
        {
            _byRole[role] = new Entry(process, origin, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Closes the session holding this role, when RolloutLoud is the one that opened it.
    /// </summary>
    /// <returns>What happened, for the log. Null when there was nothing there.</returns>
    public string? Retire(string role)
    {
        Process? doomed = null;

        lock (_gate)
        {
            if (!_byRole.TryGetValue(role, out var entry) || !Alive(entry.Process))
            {
                _byRole.Remove(role);
                return null;
            }

            if (entry.Origin == SessionOrigin.Operator)
            {
                // Left standing on purpose. The replacement takes the role; this one keeps its
                // scrollback and whatever the operator was in the middle of typing.
                _byRole.Remove(role);
                return $"Your own {role} window is still open — the replacement is the new one.";
            }

            doomed = entry.Process;
            _byRole.Remove(role);
        }

        // Killed outside the lock. It is the one call here that waits on the OS, and holding a
        // lock across it would stall every other caller for as long as the tree takes to die.
        try
        {
            doomed.Kill(entireProcessTree: true);
            return $"Closed the previous {role} window.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It went on its own between the check and the kill, or the OS refused. Either way the
            // role is free, which is what the caller asked about.
            return null;
        }
    }

    private static bool Alive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // No process was ever associated with the handle. Nothing is on screen.
            return false;
        }
    }
}
