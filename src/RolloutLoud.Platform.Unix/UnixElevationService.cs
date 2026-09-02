using System.Diagnostics;
using RolloutLoud.Core.Elevation;

namespace RolloutLoud.Platform.Unix;

/// <summary>
/// The macOS and Linux half of the same idea: consent once, then broker.
/// </summary>
/// <remarks>
/// macOS goes through <c>osascript … with administrator privileges</c>, which raises the standard
/// authentication sheet — the same one an installer uses. Linux goes through <c>pkexec</c>, which
/// is present wherever polkit is, and absent on minimal systems; <see cref="CanElevate"/> reports
/// that honestly rather than failing at the moment the operator clicks.
///
/// ⚠️ Running a GUI as root on Unix is a heavier decision than on Windows. Files the elevated app
/// touches change owner, and a mission's run folders written as root are then unwritable by the
/// normal session — which shows up much later as a permission error nobody connects to the
/// elevation. Prefer leaving RolloutLoud unelevated here and elevating individual buttons, and
/// keep elevation for the cases that genuinely need it.
/// </remarks>
public sealed partial class UnixElevationService : IElevationService
{
    public bool IsElevated => Environment.GetEnvironmentVariable("USER") == "root" || GetEffectiveUserId() == 0;

    public bool CanElevate => OperatingSystem.IsMacOS() || File.Exists("/usr/bin/pkexec");

    public string PromptDescription => OperatingSystem.IsMacOS()
        ? "macOS will ask for your password. RolloutLoud restarts with administrative rights, and " +
          "everything it starts afterwards inherits them."
        : "polkit will ask for authentication via pkexec. RolloutLoud restarts with administrative " +
          "rights, and everything it starts afterwards inherits them.";

    public Task<bool> RelaunchElevatedAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !CanElevate)
        {
            return Task.FromResult(false);
        }

        try
        {
            ProcessStartInfo startInfo;

            if (OperatingSystem.IsMacOS())
            {
                var command = $"\\\"{executable}\\\" --repo \\\"{repositoryRoot}\\\"";
                startInfo = new ProcessStartInfo { FileName = "osascript" };
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add($"do shell script \"{command}\" with administrator privileges");
            }
            else
            {
                startInfo = new ProcessStartInfo { FileName = "pkexec" };
                startInfo.ArgumentList.Add(executable);
                startInfo.ArgumentList.Add("--repo");
                startInfo.ArgumentList.Add(repositoryRoot);
            }

            startInfo.UseShellExecute = false;
            startInfo.WorkingDirectory = repositoryRoot;

            var process = Process.Start(startInfo);
            return Task.FromResult(process is not null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Cancelled at the prompt, or the helper is not installed after all.
            return Task.FromResult(false);
        }
    }

    private static int GetEffectiveUserId()
    {
        try
        {
            return NativeMethods.geteuid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("libc", SetLastError = false)]
        internal static partial int geteuid();
    }
}
