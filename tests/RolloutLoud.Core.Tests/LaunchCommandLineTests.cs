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

        return new ProcessStartInfoView(
            info.FileName, info.Arguments, info.ArgumentList.Count, info.WorkingDirectory);
    }

    private readonly record struct ProcessStartInfoView(
        string FileName, string Arguments, int ListCount, string WorkingDirectory);

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

        var built = Build(Spaced);

        // It travels as a field now instead of as `start /D "<path>"`, which is the argument whose
        // quoting broke. Verbatim, spaces and all — nothing quotes or escapes it on the way.
        Assert.Equal(Spaced, built.WorkingDirectory);

        // The exact shape of the old bug, named so a future rewrite that reintroduces it fails
        // here: ArgumentList escaping rewrites every quote with a backslash cmd cannot read.
        Assert.DoesNotContain("\\\"", built.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_is_this_process_rather_than_a_grandchild_of_it()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // ⚠️ The regression guard for the whole reason `start` was dropped. `cmd /c start ... cmd
        // /k <cli>` returns a process that exits the instant the window exists, so the window is a
        // GRANDCHILD and the handle RolloutLoud keeps can never close it. That is what left a
        // supervisor window on screen every fifteen minutes for an afternoon.
        //
        // `cmd /k` launched directly IS the window, and Kill(entireProcessTree: true) reaches it.
        var arguments = Build(Spaced).Arguments;

        Assert.StartsWith("/k ", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("start ", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_gets_a_title_so_two_of_them_can_be_told_apart()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var info = ProcessLauncher.BuildTerminalStartInfo(new LaunchRequest
        {
            Executable = "claude",
            WorkingDirectory = Spaced,
            InTerminal = true,
            WindowTitle = "RolloutLoud supervisor",
        });

        Assert.Contains("title RolloutLoud supervisor & ", info.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_cannot_smuggle_anything_into_the_command_line()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // The title is the one part of this line that could ever carry text from outside — a role
        // today, a mission objective the day somebody finds that useful. cmd has no escaping worth
        // trusting, so it is stripped to letters, digits, spaces and dashes rather than quoted.
        var info = ProcessLauncher.BuildTerminalStartInfo(new LaunchRequest
        {
            Executable = "claude",
            WorkingDirectory = Spaced,
            InTerminal = true,
            WindowTitle = "pwned & del /q /s | echo hi",
        });

        Assert.StartsWith("/k title pwned", info.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("&&", info.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("del /q", info.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("|", info.Arguments, StringComparison.Ordinal);
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
        Assert.Equal(@"C:\src\repo", Build(@"C:\src\repo").WorkingDirectory);
    }

    [Fact]
    public void A_launched_mission_carries_an_opening_line_so_the_session_starts()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // ⚠️ A briefing is not a start. Writing the mission into the instruction file gets it
        // loaded and obeyed — and an interactive CLI still opens at a prompt and sits there. This
        // cost a real run: the agent was launched into the target repository, the mission block was
        // written, the process was alive, the ledger stayed empty, and the operator was watching a
        // window where nothing happened. There is no error anywhere to explain that.
        var agent = RolloutLoud.Core.Agents.AgentCatalog.Defaults[0];

        var info = ProcessLauncher.BuildTerminalStartInfo(new LaunchRequest
        {
            Executable = agent.Executable,
            Arguments = agent.ArgumentsFor(
                RolloutLoud.Core.Agents.LaunchMode.Elevated,
                "Start the mission in CLAUDE.local.md now. Mission id: m-1."),
            WorkingDirectory = Spaced,
            InTerminal = true,
        });

        Assert.Contains("Start the mission in CLAUDE.local.md now", info.Arguments, StringComparison.Ordinal);
        Assert.Contains("--dangerously-skip-permissions", info.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_with_no_mission_says_nothing()
    {
        // A launch button with no mission is the operator opening a terminal. Putting words in it
        // would be presumptuous, and they would arrive as a message nobody meant to send.
        var agent = RolloutLoud.Core.Agents.AgentCatalog.Defaults[0];

        Assert.Equal(
            agent.ArgumentsFor(RolloutLoud.Core.Agents.LaunchMode.Normal),
            agent.ArgumentsFor(RolloutLoud.Core.Agents.LaunchMode.Normal, null));

        Assert.Equal(
            agent.ArgumentsFor(RolloutLoud.Core.Agents.LaunchMode.Normal),
            agent.ArgumentsFor(RolloutLoud.Core.Agents.LaunchMode.Normal, "   "));
    }

    [Fact]
    public void The_opening_line_is_one_argument_even_though_it_has_spaces()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // It is a sentence, so it must arrive quoted or the CLI reads its first word as the prompt
        // and the rest as flags it does not know — which fails loudly for some and silently for
        // others.
        var agent = RolloutLoud.Core.Agents.AgentCatalog.Defaults[0];

        var info = ProcessLauncher.BuildTerminalStartInfo(new LaunchRequest
        {
            Executable = agent.Executable,
            Arguments = agent.ArgumentsFor(RolloutLoud.Core.Agents.LaunchMode.Normal, "two words"),
            WorkingDirectory = Spaced,
            InTerminal = true,
        });

        Assert.Contains("\"two words\"", info.Arguments, StringComparison.Ordinal);
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
