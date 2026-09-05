using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RolloutLoud.Core.Execution;

public sealed record LaunchRequest
{
    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public required string WorkingDirectory { get; init; }

    /// <summary>Open in the operator's terminal instead of capturing output. True for the CLI buttons.</summary>
    public bool InTerminal { get; init; }

    /// <summary>Console window title, so a screen with two of these open says which is which.</summary>
    public string? WindowTitle { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record CapturedRun
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    public required TimeSpan Duration { get; init; }

    public bool TimedOut { get; init; }
}

/// <summary>
/// Starts processes on behalf of the operator and of the agents.
/// </summary>
/// <remarks>
/// Children inherit this process's token, which is the entire mechanism behind the elevated
/// buttons: elevate RolloutLoud once and everything it starts is elevated, with no second prompt.
/// </remarks>
public static class ProcessLauncher
{
    /// <summary>
    /// Opens an interactive session in a terminal window. Used by the CLI launch buttons, where
    /// the operator has to be able to see and type.
    /// </summary>
    public static Process Launch(LaunchRequest request)
    {
        var startInfo = BuildTerminalStartInfo(request);
        startInfo.WorkingDirectory = request.WorkingDirectory;
        startInfo.UseShellExecute = false;

        foreach (var (key, value) in request.Environment)
        {
            startInfo.Environment[key] = value;
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException($"The OS did not start '{request.Executable}'.");
    }

    /// <summary>
    /// Runs a command to completion and captures it. Used for success gates and fluid buttons,
    /// where the output is evidence rather than something a person watches scroll past.
    /// </summary>
    public static async Task<CapturedRun> RunAsync(
        LaunchRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in request.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"The OS did not start '{request.Executable}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var readOut = PumpAsync(process.StandardOutput, stdout, cancellationToken);
        var readErr = PumpAsync(process.StandardError, stderr, cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }

        await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
        stopwatch.Stop();

        return new CapturedRun
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            Duration = stopwatch.Elapsed,
            TimedOut = timedOut,
        };
    }

    /// <summary>
    /// Runs a shell command line — the form fluid buttons and success gates arrive in, since an
    /// agent writes `start chrome.exe --remote-debugging-port=9222`, not an argv array.
    /// </summary>
    public static Task<CapturedRun> RunShellAsync(
        string commandLine,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var request = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new LaunchRequest
            {
                Executable = "cmd.exe",
                Arguments = ["/c", commandLine],
                WorkingDirectory = workingDirectory,
            }
            : new LaunchRequest
            {
                Executable = "/bin/sh",
                Arguments = ["-c", commandLine],
                WorkingDirectory = workingDirectory,
            };

        return RunAsync(request, timeout, cancellationToken);
    }

    /// <summary>
    /// Internal so a test can read the command line without starting a window.
    /// </summary>
    /// <remarks>
    /// The escaping bug this guards against could only ever be caught by inspecting the string:
    /// the code compiled, the launch "succeeded" from RolloutLoud's point of view — cmd.exe started
    /// fine — and the failure surfaced as a dialog from `start` naming the tail of the operator's
    /// own path. Nothing on this side of the process boundary knew anything had gone wrong.
    /// </remarks>
    internal static ProcessStartInfo BuildTerminalStartInfo(LaunchRequest request)
    {
        if (!request.InTerminal)
        {
            var direct = new ProcessStartInfo
            {
                FileName = request.Executable,
                WorkingDirectory = request.WorkingDirectory,
            };
            foreach (var argument in request.Arguments)
            {
                direct.ArgumentList.Add(argument);
            }

            return direct;
        }

        var commandLine = Quote(request.Executable) + " " + string.Join(' ', request.Arguments.Select(Quote));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // ⚠️ No `start`, and dropping it is the point of this shape.
            //
            // `cmd /c start "RolloutLoud" ... cmd /k <cli>` did give the agent its own window, but
            // the process it hands back is the `cmd /c start`, which exits the instant the window
            // exists. The window is a GRANDCHILD. So the handle RolloutLoud held could never close
            // what RolloutLoud had opened — and a supervisor woken every fifteen minutes against a
            // question nobody could answer left seventeen windows on screen in one afternoon.
            //
            // cmd.exe launched directly IS the window. RolloutLoud is a WinExe and has no console
            // of its own, so Windows allocates a fresh one for this child, and
            // Kill(entireProcessTree: true) closes it together with the CLI running inside it.
            // The working directory comes from the ProcessStartInfo instead of `start /D`, which
            // also retires the quoting that `/D` needed.
            //
            // ⚠️ Arguments, NOT ArgumentList, and this one cost a real failed launch. ArgumentList
            // escapes for the C runtime: an argument containing quotes comes out with every `"`
            // rewritten as `\"`. cmd.exe has no backslash escape, so it sees a stray backslash
            // glued to each quote. It only shows when a path contains a space, which is why it
            // survived — the command line was verified as a STRING and never run from a folder
            // like "MEU PROJETOS - PROGRAMAS". Setting Arguments passes the line through verbatim,
            // which is what a shell that does its own quoting needs.
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k " + Title(request.WindowTitle) + commandLine,

                // Set here rather than only in Launch, so the folder the window opens in is part
                // of what a test can read. `start /D` used to carry it, and that was the argument
                // whose quoting broke on a path with a space in it.
                WorkingDirectory = request.WorkingDirectory,
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var script =
                "tell application \"Terminal\" to do script \"cd " + Escape(request.WorkingDirectory) +
                " && " + Escape(commandLine) + "\"";
            var info = new ProcessStartInfo { FileName = "osascript" };
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add(script);
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("tell application \"Terminal\" to activate");
            return info;
        }

        var linux = new ProcessStartInfo { FileName = "x-terminal-emulator" };
        linux.ArgumentList.Add("-e");
        linux.ArgumentList.Add(commandLine);
        return linux;
    }

    /// <summary>
    /// A `title` command for the console window, so the operator can tell the windows apart.
    /// </summary>
    /// <remarks>
    /// Stripped to letters, digits, spaces and dashes before it goes anywhere near the command
    /// line. The title is the one part of this string that could carry text from outside — a role
    /// name today, a mission objective the day somebody finds that useful — and cmd has no
    /// escaping worth trusting for the rest.
    /// </remarks>
    private static string Title(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var safe = new string([.. title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-')]).Trim();

        return safe.Length == 0 ? string.Empty : "title " + safe + " & ";
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sink.Append(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever arrived before the cancel is still evidence; keep it.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Already gone between the timeout and the kill.
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
