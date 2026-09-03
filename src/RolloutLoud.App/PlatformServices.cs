using RolloutLoud.Core.Elevation;

namespace RolloutLoud.App;

/// <summary>Picks the elevation implementation for the machine actually running.</summary>
public static class PlatformServices
{
    public static IElevationService CreateElevationService()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Platform.Windows.WindowsElevationService();
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return new Platform.Unix.UnixElevationService();
        }

        return new UnsupportedElevationService();
    }

    /// <summary>
    /// The honest answer on an OS we do not know how to escalate on.
    /// </summary>
    /// <remarks>
    /// Reports <see cref="CanElevate"/> false rather than throwing when asked, so the UI can grey
    /// the button out and say why. A tool that offers elevation it cannot deliver sends the
    /// operator hunting for a failure that was never going to work.
    /// </remarks>
    private sealed class UnsupportedElevationService : IElevationService
    {
        public bool IsElevated => false;

        public bool CanElevate => false;

        public string PromptDescription =>
            "RolloutLoud does not know how to request administrative rights on this platform. " +
            "Start it from an already-elevated shell if you need elevated buttons.";

        public Task<bool> RelaunchElevatedAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
