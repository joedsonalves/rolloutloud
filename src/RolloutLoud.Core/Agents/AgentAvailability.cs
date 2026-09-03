using System.Runtime.InteropServices;

namespace RolloutLoud.Core.Agents;

/// <summary>
/// Whether a CLI is actually on this machine.
/// </summary>
/// <remarks>
/// Needed because the relay picks the next agent on its own, unattended, and handing a mission to
/// a CLI that is not installed would end the run with a launch error rather than a handoff — at
/// tier 3, which is the rung most likely to actually find the answer.
///
/// Resolved by walking PATH rather than by trying to start the process: starting it is slow, has
/// side effects, and some of these CLIs take seconds to print a version.
/// </remarks>
public static class AgentAvailability
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the agent's executable resolves, and it can be driven headlessly.
    /// </summary>
    /// <remarks>
    /// An agent with no <see cref="AgentDescriptor.PromptArguments"/> is installed but cannot be
    /// supervised — there is no way to hand it a prompt and read a result — so it is not a relay
    /// candidate even though its launch button works fine.
    /// </remarks>
    public static bool CanBeRelayedTo(AgentDescriptor agent) =>
        agent.PromptArguments.Count > 0 && IsInstalled(agent.Executable);

    public static bool IsInstalled(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(executable, out var cached))
            {
                return cached;
            }
        }

        var found = Resolve(executable) is not null;

        lock (Gate)
        {
            Cache[executable] = found;
        }

        return found;
    }

    /// <summary>Forgets what it found, for when the operator installs something mid-session.</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    private static string? Resolve(string executable)
    {
        // An absolute or relative path is used as given.
        if (executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable) ? executable : null;
        }

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            // PATHEXT, because on Windows these CLIs are usually a .cmd or .ps1 shim rather than an
            // .exe — looking only for the bare name finds none of them.
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), executable + extension);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry. Skip it rather than failing the whole lookup.
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
