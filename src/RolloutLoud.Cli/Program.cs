using System.Diagnostics;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Cli;

/// <summary>
/// <c>rollout</c> — the command an agent is told to run, and the operator's way in.
/// </summary>
/// <remarks>
/// This exists because of the flow the operator described: you type "install the ROLLOUTLOUD repo"
/// into whichever CLI you happen to be in, and the CLI has to be able to finish the job on its
/// own — build the tool, open the window, and hand you the box to type the objective into. Every
/// step of that has to be one shell command with no interactive prompt, because the thing running
/// it is a model in a terminal, not a person.
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var paths = RolloutPaths.Discover(Directory.GetCurrentDirectory());
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();

        return command switch
        {
            "install" => await InstallAsync(paths, rest).ConfigureAwait(false),
            "open" => Open(paths, rest),
            "status" => await StatusAsync(paths).ConfigureAwait(false),
            "mission" => await MissionAsync(paths, rest).ConfigureAwait(false),
            "briefing" => await BriefingAsync(paths, rest).ConfigureAwait(false),
            "admit" => await AdmitAsync(paths, rest).ConfigureAwait(false),
            "attempt" => await AttemptAsync(paths, rest).ConfigureAwait(false),
            "continue" => await SimpleGetAsync(paths, "/v1/missions/active/continue").ConfigureAwait(false),
            "gate" => await SimplePostAsync(paths, "/v1/missions/active/gate").ConfigureAwait(false),
            "button" => await ButtonAsync(paths, rest).ConfigureAwait(false),
            "invoke" => await InvokeAsync(paths, rest).ConfigureAwait(false),
            "help" or "--help" or "-h" => Help(),
            _ => Unknown(command),
        };
    }

    // ---- install ---------------------------------------------------------------------------

    /// <summary>
    /// Builds RolloutLoud and opens it anchored to the current repository.
    /// </summary>
    /// <remarks>
    /// Anchored to <c>Directory.GetCurrentDirectory()</c>, which is the rule the operator set:
    /// wherever you run this from is where the elevated CLIs will open. Running the install from
    /// the wrong folder is therefore a real mistake with a quiet consequence, so the path is
    /// printed before anything else happens.
    /// </remarks>
    private static async Task<int> InstallAsync(RolloutPaths paths, string[] args)
    {
        Console.WriteLine($"Anchoring to {paths.RepositoryRoot}");
        Console.WriteLine("Every elevated CLI and every fluid button will run from there.");
        Console.WriteLine();

        var solution = Path.Combine(paths.RepositoryRoot, "RolloutLoud.slnx");
        if (!File.Exists(solution))
        {
            Console.Error.WriteLine(
                $"No RolloutLoud.slnx in {paths.RepositoryRoot}. Run this from the repository root, " +
                "or clone it first: git clone https://github.com/joedsonalves/rolloutloud");
            return 1;
        }

        Console.WriteLine("Building…");
        var build = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "build", solution, "-c", "Release", "--nologo", "-v", "q" },
            WorkingDirectory = paths.RepositoryRoot,
            UseShellExecute = false,
        });

        if (build is null)
        {
            Console.Error.WriteLine("Could not start dotnet. Is the .NET 10 SDK installed?");
            return 1;
        }

        await build.WaitForExitAsync().ConfigureAwait(false);
        if (build.ExitCode != 0)
        {
            Console.Error.WriteLine($"Build failed with exit code {build.ExitCode}.");
            return build.ExitCode;
        }

        Console.WriteLine("Built. Opening the window…");
        return args.Contains("--no-open") ? 0 : Open(paths, []);
    }

    private static int Open(RolloutPaths paths, string[] args)
    {
        var executable = FindApp(paths);
        if (executable is null)
        {
            Console.Error.WriteLine("RolloutLoud is not built yet. Run 'rollout install' first.");
            return 1;
        }

        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = paths.RepositoryRoot,
            UseShellExecute = true,
        };

        info.ArgumentList.Add("--repo");
        info.ArgumentList.Add(paths.RepositoryRoot);

        // `rollout open --elevated` asks the OS up front, for the operator who already knows this
        // session needs privilege and would rather answer the prompt now than mid-run.
        if (args.Contains("--elevated") && OperatingSystem.IsWindows())
        {
            info.Verb = "runas";
        }

        try
        {
            Process.Start(info);
            Console.WriteLine($"Opened RolloutLoud on {paths.RepositoryRoot}");
            return 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine("Could not start RolloutLoud: " + ex.Message);
            return 1;
        }
    }

    private static string? FindApp(RolloutPaths paths)
    {
        var name = OperatingSystem.IsWindows() ? "RolloutLoud.exe" : "RolloutLoud";

        foreach (var configuration in (string[])["Release", "Debug"])
        {
            var candidate = Path.Combine(
                paths.RepositoryRoot, "src", "RolloutLoud.App", "bin", configuration, "net10.0", name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // ---- bridge commands -------------------------------------------------------------------

    private static async Task<int> StatusAsync(RolloutPaths paths)
    {
        var client = BridgeClient.Discover(paths);
        if (client is null)
        {
            Console.WriteLine($"No RolloutLoud running for {paths.RepositoryRoot}.");
            Console.WriteLine("Start one with 'rollout open'.");
            return 1;
        }

        using (client)
        {
            Console.WriteLine(await client.GetAsync("/v1/health").ConfigureAwait(false));
            Console.WriteLine(await client.GetAsync("/v1/missions").ConfigureAwait(false));
        }

        return 0;
    }

    private static async Task<int> MissionAsync(RolloutPaths paths, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: rollout mission \"<objective>\" [--agent claude] [--gate \"<cmd>\"] " +
                                    "[--scope a,b] [--auth \"<who authorised it>\"] [--offload always|threshold] " +
                                    "[--max-attempts N] [--max-hours N]");
            return 1;
        }

        var payload = new Dictionary<string, object?>
        {
            ["objective"] = args[0],
            ["agent"] = Option(args, "--agent") ?? "claude",
            ["gateCommand"] = Option(args, "--gate"),
            ["gateDescription"] = Option(args, "--gate-description"),
            ["authorization"] = Option(args, "--auth"),
            ["offload"] = Option(args, "--offload") ?? "off",
        };

        var scope = Option(args, "--scope");
        if (!string.IsNullOrWhiteSpace(scope))
        {
            payload["scope"] = scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (int.TryParse(Option(args, "--max-attempts"), out var attempts))
        {
            payload["maxAttempts"] = attempts;
        }

        if (double.TryParse(Option(args, "--max-hours"), out var hours))
        {
            payload["maxHours"] = hours;
        }

        return await SendAsync(paths, client => client.PostAsync("/v1/missions", payload)).ConfigureAwait(false);
    }

    private static async Task<int> BriefingAsync(RolloutPaths paths, string[] args)
    {
        var task = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        var route = "/v1/missions/active/briefing" + (task is null ? "" : "?task=" + Uri.EscapeDataString(task));
        return await SimpleGetAsync(paths, route).ConfigureAwait(false);
    }

    private static async Task<int> AdmitAsync(RolloutPaths paths, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: rollout admit \"<hypothesis>\" \"<command>\" [--agent <id>]");
            return 1;
        }

        return await SendAsync(paths, client => client.PostAsync("/v1/missions/active/admit", new
        {
            hypothesis = args[0],
            command = args[1],
            agent = Option(args, "--agent"),
        })).ConfigureAwait(false);
    }

    private static async Task<int> AttemptAsync(RolloutPaths paths, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: rollout attempt \"<hypothesis>\" \"<command>\" [--outcome failed|succeeded|blocked|errored] " +
                "[--learned \"<what this rules out>\"] [--exit N] [--agent <id>]");
            return 1;
        }

        var payload = new Dictionary<string, object?>
        {
            ["hypothesis"] = args[0],
            ["command"] = args[1],
            ["outcome"] = Option(args, "--outcome") ?? "failed",
            ["learned"] = Option(args, "--learned"),
            ["agent"] = Option(args, "--agent"),
        };

        if (int.TryParse(Option(args, "--exit"), out var exitCode))
        {
            payload["exitCode"] = exitCode;
        }

        return await SendAsync(paths, client => client.PostAsync("/v1/missions/active/attempts", payload))
            .ConfigureAwait(false);
    }

    private static async Task<int> ButtonAsync(RolloutPaths paths, string[] args)
    {
        var title = Option(args, "--title");
        var command = Option(args, "--command");

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine(
                "Usage: rollout button --title \"<label>\" --command \"<command line>\" " +
                "[--why \"<rationale>\"] [--elevated] [--detached] [--agent <id>]");
            return 1;
        }

        return await SendAsync(paths, client => client.PostAsync("/v1/buttons", new
        {
            title,
            command,
            rationale = Option(args, "--why"),
            agent = Option(args, "--agent"),
            requiresElevation = args.Contains("--elevated"),
            detached = args.Contains("--detached"),
        })).ConfigureAwait(false);
    }

    private static async Task<int> InvokeAsync(RolloutPaths paths, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: rollout invoke <button-id>");
            return 1;
        }

        return await SimplePostAsync(paths, $"/v1/buttons/{args[0]}/invoke").ConfigureAwait(false);
    }

    // ---- plumbing --------------------------------------------------------------------------

    private static Task<int> SimpleGetAsync(RolloutPaths paths, string route) =>
        SendAsync(paths, client => client.GetAsync(route));

    private static Task<int> SimplePostAsync(RolloutPaths paths, string route) =>
        SendAsync(paths, client => client.PostAsync(route));

    private static async Task<int> SendAsync(RolloutPaths paths, Func<BridgeClient, Task<string>> call)
    {
        var client = BridgeClient.Discover(paths);
        if (client is null)
        {
            Console.Error.WriteLine(
                $"No RolloutLoud running for {paths.RepositoryRoot}. Start one with 'rollout open'.");
            return 1;
        }

        using (client)
        {
            try
            {
                Console.WriteLine(await call(client).ConfigureAwait(false));
                return 0;
            }
            catch (HttpRequestException ex)
            {
                // Almost always a stale handshake: the window was closed and the file outlived it.
                Console.Error.WriteLine(
                    $"Could not reach RolloutLoud at {client.Endpoint}: {ex.Message}\n" +
                    "The window may have been closed. Try 'rollout open'.");
                return 1;
            }
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Help();
        return 1;
    }

    private static int Help()
    {
        Console.WriteLine("""
            rollout — drive RolloutLoud from a terminal.

            Setup
              rollout install [--no-open]      Build RolloutLoud and open it on this repository.
              rollout open [--elevated]        Open the window anchored here.
              rollout status                   Health of the running instance, and its missions.

            Missions
              rollout mission "<objective>" [--agent claude] [--gate "<command>"]
                                             [--scope a,b] [--auth "<who authorised it>"]
                                             [--offload always|threshold]
                                             [--max-attempts N] [--max-hours N]
              rollout briefing ["<subagent task>"]   The briefing; with a task, the subagent form.
              rollout admit "<hypothesis>" "<command>"
                                             Ask before running. Rejects repeats and out-of-scope.
              rollout attempt "<hypothesis>" "<command>" [--outcome ...] [--learned "..."]
              rollout continue                 Whether you may stop. Almost always: no.
              rollout gate                     Ask the success gate. The only way a mission ends.

            Fluid buttons
              rollout button --title "<label>" --command "<cmd>" [--why "..."] [--elevated] [--detached]
              rollout invoke <button-id>       Run it yourself, if the allowlist permits.

            The repository you run this from is the anchor: elevated CLIs and buttons open there.
            """);

        return 0;
    }
}
