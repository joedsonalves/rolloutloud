using System.Runtime.InteropServices;
using RolloutLoud.Core.Execution;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The launch button never worked from a folder whose path contains a space — which is the
/// operator's own anchor, <c>MEU PROJETOS - PROGRAMAS</c>. It survived because the command line was
/// verified as a string and never actually run from such a folder, and because nothing on this side
/// of the process boundary can tell: cmd.exe starts fine, and the failure arrives as a dialog from
/// <c>start</c> naming the tail of the path it could not find.
/// </summary>
public class LaunchCommandLineTests
{
    private static readonly string Spaced =
        @"C:\JOEDSON\CANAIS YT\MEU PROJETOS - PROGRAMAS\ROLLOUTLOUD\ROLLOUTLOUD";

    private static ProcessStartInfoView Build(string workingDirectory)
    {
        var info = ProcessLauncher.BuildTerminalStartInfo(new LaunchRequest
        {
            Executable = "claude",
            Arguments = ["--dangerously-skip-permissions"],
            WorkingDirectory = workingDirectory,
            InTerminal = true,
        });

        return new ProcessStartInfoView(info.FileName, info.Arguments, info.ArgumentList.Count);
    }

    private readonly record struct ProcessStartInfoView(string FileName, string Arguments, int ListCount);

    [Fact]
    public void The_windows_launch_uses_a_verbatim_command_line_not_an_argument_list()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // ⚠️ ArgumentList escapes for the C runtime: an argument containing quotes comes out with
        // every `"` rewritten as `\"`. cmd.exe has no backslash escape, so it sees a stray
        // backslash glued to each quote, the working directory arrives as `\C:\…\ROLLOUTLOUD\`,
        // and `start` cannot find it. A shell that does its own quoting needs the line verbatim.
        var built = Build(Spaced);

        Assert.Equal("cmd.exe", built.FileName);
        Assert.Equal(0, built.ListCount);
        Assert.NotEqual(string.Empty, built.Arguments);
    }

    [Fact]
    public void The_working_directory_survives_the_spaces_in_it()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var arguments = Build(Spaced).Arguments;

        // The whole path, inside real quotes, with no backslash in front of either of them.
        Assert.Contains($"/D \"{Spaced}\"", arguments, StringComparison.Ordinal);

        // The exact shape of the bug, named so a future rewrite that reintroduces it fails here.
        Assert.DoesNotContain("\\\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_still_gets_a_title_so_start_does_not_eat_the_command()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Leaving the title out makes `start` take the first quoted token as the window title
        // instead of running it — a silent no-op that looks like the button did nothing. Already
        // known, and worth pinning next to the quoting rule it interacts with.
        Assert.Contains("start \"RolloutLoud\"", Build(Spaced).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_and_its_flags_are_still_in_the_line()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var arguments = Build(Spaced).Arguments;

        Assert.Contains("claude", arguments, StringComparison.Ordinal);
        Assert.Contains("--dangerously-skip-permissions", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_without_spaces_is_built_the_same_way()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // The bug only showed with a space, which is exactly why it lasted. One code path for both
        // means the case that works cannot drift away from the case that did not.
        Assert.Contains(@"/D ""C:\src\repo""", Build(@"C:\src\repo").Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void A_captured_run_still_uses_the_argument_list()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // The non-terminal path starts the executable directly rather than through a shell, so
        // ArgumentList is right there — it is the shell in between that cannot read its escaping.
        var info = ProcessLauncher.BuildTerminalStartInfo(new LaunchRequest
        {
            Executable = "claude",
            Arguments = ["--version"],
            WorkingDirectory = Spaced,
            InTerminal = false,
        });

        Assert.Equal("claude", info.FileName);
        Assert.Equal(["--version"], info.ArgumentList);
    }
}
