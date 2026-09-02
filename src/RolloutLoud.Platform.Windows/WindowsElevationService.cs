using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using RolloutLoud.Core.Elevation;

namespace RolloutLoud.Platform.Windows;

/// <summary>
/// UAC, used the way it is meant to be used.
/// </summary>
/// <remarks>
/// Elevation on Windows is a property of the process token, fixed at creation. A running
/// medium-integrity process cannot promote itself, and it cannot create a high-integrity child
/// without the consent prompt — that is the design of UAC, not a gap in it. So the only honest
/// path to "the agent's button runs elevated" is to restart RolloutLoud through the prompt and
/// have the operator approve it once, which is what <see cref="RelaunchElevatedAsync"/> does.
///
/// ⚠️ There is a second consequence of running elevated, and it bites in the opposite direction:
/// UIPI blocks drag-and-drop and several window messages from lower-integrity processes into an
/// elevated one. Dragging a file from Explorer into an elevated RolloutLoud window silently does
/// nothing. Worth remembering before adding a drop target.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsElevationService : IElevationService
{
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool CanElevate => true;

    public string PromptDescription =>
        "Windows will show a User Account Control prompt. RolloutLoud restarts with administrative " +
        "rights, and every CLI and fluid button it starts afterwards inherits them without asking again.";

    public Task<bool> RelaunchElevatedAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return Task.FromResult(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = repositoryRoot,

            // UseShellExecute is mandatory here: the "runas" verb is a shell concept, and with
            // UseShellExecute false the flag is ignored and the child starts unelevated — the
            // failure mode that looks like it worked until a privileged command fails much later.
            UseShellExecute = true,
            Verb = "runas",
        };

        startInfo.ArgumentList.Add("--repo");
        startInfo.ArgumentList.Add(repositoryRoot);

        try
        {
            var process = Process.Start(startInfo);
            return Task.FromResult(process is not null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the operator said no. An answer, not a fault.
            return Task.FromResult(false);
        }
    }
}
