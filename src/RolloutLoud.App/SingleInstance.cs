using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.App;

/// <summary>
/// Hands the repository over to the window that already owns it.
/// </summary>
/// <remarks>
/// Runs before Avalonia starts, because by the time a second window exists the damage is already
/// done — the second bridge has taken a new port and rewritten <c>bridge.json</c>, and every
/// agent the first instance launched is holding a dead token.
///
/// Bringing the existing window forward and exiting silently is what an operator expects from
/// launching an app that is already open. It also makes <c>rollout open</c> idempotent, which is
/// what lets an agent run it without first working out whether it needs to.
/// </remarks>
public static class SingleInstance
{
    /// <summary>
    /// True when another instance owns this repository — in which case this process must exit.
    /// </summary>
    public static bool HandOverIfRunning(RolloutPaths paths)
    {
        var found = RunningInstance.Detect(paths);
        if (found is null)
        {
            // A handshake left behind by a crash would otherwise block every future start.
            RunningInstance.ClearStale(paths);
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            TryFocus(found.Process.MainWindowHandle);
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void TryFocus(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Restore first: a minimised window ignores SetForegroundWindow, and the operator
            // would see nothing happen at all — which reads as "the app failed to start".
            if (NativeMethods.IsIconic(window))
            {
                NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
            }

            NativeMethods.SetForegroundWindow(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Focusing is a courtesy. Failing to exit would be the real problem, and we still do.
        }
    }

    /// <remarks>
    /// DllImport rather than LibraryImport: the source generator for the latter emits unsafe code
    /// and would force AllowUnsafeBlocks on the whole UI project, which is a large door to open
    /// for three window-management calls that pass nothing but an IntPtr and an int.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        internal const int SwRestore = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
