using System.Diagnostics;
using System.Text.Json;
using RolloutLoud.Core.Bridge;
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
            "attach" => await AttachAsync(paths, rest).ConfigureAwait(false),
            "finish" => await FinishAsync(paths, rest).ConfigureAwait(false),
            "identity" => Identity(paths, rest),
            "subagent" => await SubagentAsync(paths, rest).ConfigureAwait(false),
            "resume" => await ResumeAsync(paths, rest).ConfigureAwait(false),
            "propose" => await ProposeAsync(paths, rest).ConfigureAwait(false),
            "ledger" => await LedgerAsync(paths, rest).ConfigureAwait(false),
            "spend" => await SimpleGetAsync(paths, "/v1/missions/active/spend").ConfigureAwait(false),
            "wall" => await SimpleGetAsync(paths, "/v1/missions/active/wall").ConfigureAwait(false),
            "review" => await ReviewAsync(paths, rest).ConfigureAwait(false),
            "scope" => await ScopeAsync(paths, rest).ConfigureAwait(false),
            "launch" => await SimplePostAsync(paths, "/v1/missions/active/launch").ConfigureAwait(false),
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

    /// <summary>
    /// The one command an agent runs to get working: find RolloutLoud, or start it, either way
    /// print the bridge details.
    /// </summary>
    /// <remarks>
    /// This exists because "is it installed, is it running, do I need to start it, has it finished
    /// starting" is four questions, and an agent asking them in a shell gets three of them wrong.
    /// One idempotent command answers all four and always ends with the same JSON on stdout.
    ///
    /// Safe to run repeatedly: the app hands the repository over to whichever instance already
    /// owns it, so a second call focuses that window rather than starting a rival that would
    /// overwrite the handshake and strand every agent holding the old token.
    /// </remarks>
    private static async Task<int> AttachAsync(RolloutPaths paths, string[] args)
    {
        // --quiet suppresses the handshake JSON, for callers that only wanted the side effect of
        // RolloutLoud being up. Without it, `rollout resume` printed two JSON documents and the
        // caller had to work out which one answered its question.
        var quiet = args.Contains("--quiet");

        var existing = RunningInstance.Detect(paths);
        if (existing is not null)
        {
            if (!quiet)
            {
                Console.WriteLine(Describe(existing.Handshake, started: false));
            }

            return await MaybeOpenMissionAsync(paths, args).ConfigureAwait(false);
        }

        if (args.Contains("--no-start"))
        {
            Console.Error.WriteLine($"No RolloutLoud running for {paths.RepositoryRoot}.");
            return 1;
        }

        var opened = Open(paths, args.Where(a => a != "--mission").ToArray(), quiet);
        if (opened != 0)
        {
            return opened;
        }

        // Poll for the handshake rather than sleeping a fixed amount: a cold start behind an
        // antivirus scan can take several seconds, and a fixed wait is either too short to work
        // or too long every other time.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(400).ConfigureAwait(false);

            var found = RunningInstance.Detect(paths, TimeSpan.FromSeconds(1));
            if (found is not null)
            {
                if (!quiet)
                {
                    Console.WriteLine(Describe(found.Handshake, started: true));
                }

                return await MaybeOpenMissionAsync(paths, args).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine(
            "RolloutLoud was started but did not publish .rolloutloud/bridge.json within 45s. " +
            "Check that the window actually opened.");
        return 1;
    }

    private static Task<int> MaybeOpenMissionAsync(RolloutPaths paths, string[] args)
    {
        var objective = Option(args, "--mission");
        return string.IsNullOrWhiteSpace(objective)
            ? Task.FromResult(0)
            : MissionAsync(paths, [objective, .. args]);
    }

    private static string Describe(BridgeHandshake handshake, bool started) =>
        JsonSerializer.Serialize(
            new
            {
                started,
                endpoint = handshake.Endpoint,
                token = handshake.Token,
                repositoryRoot = handshake.RepositoryRoot,
                elevated = handshake.Elevated,
                activeMissionId = handshake.ActiveMissionId,
            },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Asks to close RolloutLoud because the objective is met.
    /// </summary>
    /// <remarks>
    /// Refused unless the mission is Achieved — which only a twice-passed gate produces. Running
    /// out of ideas arrives here as Exhausted and is turned down with that named back.
    /// </remarks>
    private static async Task<int> FinishAsync(RolloutPaths paths, string[] args)
    {
        var client = BridgeClient.Discover(paths);
        if (client is null)
        {
            Console.Error.WriteLine($"No RolloutLoud running for {paths.RepositoryRoot}.");
            return 1;
        }

        using (client)
        {
            var body = await client.PostAsync("/v1/shutdown", new
            {
                missionId = Option(args, "--mission-id"),
                agent = Option(args, "--agent"),
                reason = Positional(args, "--mission-id", "--agent"),
            }).ConfigureAwait(false);

            Console.WriteLine(body);

            // Non-zero on a refusal so a script can branch on it without parsing the JSON.
            return body.Contains("\"verdict\": \"refused\"", StringComparison.Ordinal) ? 2 : 0;
        }
    }

    /// <summary>
    /// Writes the starter identity file, or says whether one is attached.
    /// </summary>
    /// <remarks>
    /// Only ever on an explicit <c>--template</c>, and it refuses to overwrite. Attaching an
    /// identity is the operator consenting to lend their details; a command that created the file
    /// as a side effect of being run would be making that decision for them.
    /// </remarks>
    private static int Identity(RolloutPaths paths, string[] args)
    {
        var file = paths.IdentityFile;

        if (!args.Contains("--template"))
        {
            if (File.Exists(file))
            {
                Console.WriteLine($"Attached: {file}");
                Console.WriteLine($"Delete it to withdraw. Access is recorded in {paths.IdentityAuditFile}.");
            }
            else
            {
                Console.WriteLine("Nothing attached. Agents are told not to create accounts.");
                Console.WriteLine($"To lend details:  rollout identity --template   then edit {file}");
            }

            return 0;
        }

        if (File.Exists(file))
        {
            Console.Error.WriteLine($"{file} already exists. Edit it rather than regenerating — " +
                                    "overwriting would silently drop whatever is in there now.");
            return 1;
        }

        AttachedIdentity.WriteTemplate(file);
        Console.WriteLine($"Wrote {file}");
        Console.WriteLine("Edit it, then agents can ask for it by site. It is plaintext on disk, and");
        Console.WriteLine("anything read from it becomes part of an agent's context. No passwords in there.");
        return 0;
    }

    private static int Open(RolloutPaths paths, string[] args, bool quiet = false)
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

            if (!quiet)
            {
                Console.WriteLine($"Opened RolloutLoud on {paths.RepositoryRoot}");
            }

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
                                    "[--max-attempts N] [--max-hours N] [--max-spend USD] " +
                                    "[--fourth-wall] [--deliverable <path>]");
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

        if (decimal.TryParse(SpendCap(args), out var spend))
        {
            payload["maxSpendUsd"] = spend;
        }

        if (args.Contains("--fourth-wall"))
        {
            payload["fourthWall"] = true;
        }

        if (Option(args, "--deliverable") is { Length: > 0 } deliverable)
        {
            payload["deliverable"] = deliverable;
        }

        if (Option(args, "--at") is { Length: > 0 } workdir)
        {
            payload["workingDirectory"] = workdir;
        }

        if (args.Contains("--elevated"))
        {
            payload["elevated"] = true;
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

    /// <summary>
    /// Hands one step to a fresh process and prints the verdict, not the transcript.
    /// </summary>
    /// <summary>
    /// Picks a mission back up after the window was closed.
    /// </summary>
    /// <remarks>
    /// Starts RolloutLoud first if it is not running, because the common case is exactly that: the
    /// operator closed everything, came back, and wants to carry on. Making them run two commands
    /// in the right order would be the tool asking them to remember its internals.
    /// </remarks>
    private static async Task<int> ResumeAsync(RolloutPaths paths, string[] args)
    {
        if (RunningInstance.Detect(paths) is null)
        {
            var attached = await AttachAsync(paths, ["--quiet", .. args]).ConfigureAwait(false);
            if (attached != 0)
            {
                return attached;
            }
        }

        return await SendAsync(paths, client => client.PostAsync("/v1/resume", new
        {
            missionId = Option(args, "--mission-id"),
            agent = Option(args, "--agent"),
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// Composes a mission for the operator to approve, and opens RolloutLoud if it is shut.
    /// </summary>
    /// <remarks>
    /// The flow this exists for: the operator is in a CLI, says what they want in a sentence, and
    /// asks the agent to set the mission up. The agent writes a sharper objective than a person
    /// types in a hurry, and it knows the repository — which test actually proves the thing.
    ///
    /// It starts the window first, for the same reason <c>resume</c> does: "open RolloutLoud and
    /// give it this objective" is one instruction from the operator, and turning it into two
    /// commands in the right order is the tool asking them to remember its internals.
    ///
    /// ⚠️ <b>Nothing is created by this command.</b> It waits by default, because an agent that
    /// fires and forgets leaves a draft on a desk nobody told the operator to look at, and then
    /// reports the mission as set up. Blocking makes the handoff visible in the terminal the
    /// operator is already looking at.
    /// </remarks>
    private static async Task<int> ProposeAsync(RolloutPaths paths, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Usage: rollout propose \"<objective>\" [--gate \"<command>\"] [--why \"<reasoning>\"]\n" +
                "                       [--agent <id>] [--scope a,b] [--auth \"<who authorised it>\"]\n" +
                "                       [--offload always|threshold] [--max-attempts N] [--max-hours N]\n" +
                "                       [--max-spend USD]\n" +
                "                       [--no-wait]\n\n" +
                "The gate is a command that must exit 0, and it should RE-DERIVE the result — a test,\n" +
                "a build, the scan run again. A gate that checks a file you wrote is you marking your\n" +
                "own work, and RolloutLoud will say so to the operator.");
            return 1;
        }

        if (RunningInstance.Detect(paths) is null)
        {
            var attached = await AttachAsync(paths, ["--quiet", .. args.Skip(1)]).ConfigureAwait(false);
            if (attached != 0)
            {
                return attached;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["objective"] = args[0],
            ["agent"] = Option(args, "--agent"),
            ["proposedBy"] = Option(args, "--agent") ?? "claude",
            ["gateCommand"] = Option(args, "--gate"),
            ["gateDescription"] = Option(args, "--gate-description"),
            ["authorization"] = Option(args, "--auth"),
            ["offload"] = Option(args, "--offload"),
            ["rationale"] = Option(args, "--why"),
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

        if (decimal.TryParse(SpendCap(args), out var spend))
        {
            payload["maxSpendUsd"] = spend;
        }

        var client = BridgeClient.Discover(paths);
        if (client is null)
        {
            Console.Error.WriteLine($"No RolloutLoud running for {paths.RepositoryRoot}.");
            return 1;
        }

        using (client)
        {
            string created;
            try
            {
                created = await client.PostAsync("/v1/missions/proposals", payload).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Could not reach RolloutLoud at {client.Endpoint}: {ex.Message}");
                return 1;
            }

            Console.WriteLine(created);

            var id = FieldOf(created, "id");
            if (args.Contains("--no-wait") || id is null)
            {
                return 0;
            }

            Console.Error.WriteLine(
                $"Waiting for the operator to start or discard {id}. " +
                "The proposal is on screen in the RolloutLoud window.");

            return await AwaitDecisionAsync(client, id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Blocks until the operator answers, then prints the answer.
    /// </summary>
    /// <remarks>
    /// No deadline. The operator may well be making coffee, and a proposal that times out would
    /// leave the agent reporting failure while the draft sits on screen waiting to be accepted —
    /// the worst of both, since the operator then starts a mission whose agent has already given
    /// up on it. Ctrl-C is the way out, and the proposal survives it.
    ///
    /// A rejected proposal exits 2 rather than 1: "the operator said no" and "the tool broke" are
    /// different outcomes, and an agent scripting this needs to tell them apart.
    /// </remarks>
    private static async Task<int> AwaitDecisionAsync(BridgeClient client, string id)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            string body;
            try
            {
                body = await client.GetAsync($"/v1/missions/proposals/{id}").ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                Console.Error.WriteLine("RolloutLoud went away while the proposal was waiting.");
                return 1;
            }

            switch (FieldOf(body, "state"))
            {
                case "pending":
                    continue;

                case "accepted":
                    Console.WriteLine(body);
                    return 0;

                default:
                    Console.WriteLine(body);
                    return 2;
            }
        }
    }

    /// <summary>
    /// Pulls one top-level string out of a JSON response.
    /// </summary>
    /// <remarks>
    /// Enough for two fields on a document this command just received from a server it started.
    /// Deliberately not a typed contract shared with Core: the CLI is a thin client, and every
    /// response shape it mirrors is one more thing that has to be changed in two places.
    /// </remarks>
    private static string? FieldOf(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Bounds the run to targets that were not known when the mission opened.
    /// </summary>
    /// <remarks>
    /// For the run whose first job is to find out where the boundary is. It only ever narrows, so
    /// there is no flag here that could widen one — if the work genuinely needs a target outside
    /// the scope in force, that is a new mission and the operator opens it.
    /// </remarks>
    private static async Task<int> ScopeAsync(RolloutPaths paths, string[] args)
    {
        var targets = Positional(args, "--auth", "--exclude");

        if (string.IsNullOrWhiteSpace(targets))
        {
            Console.Error.WriteLine(
                "Usage: rollout scope \"a.example.com,*.staging.example.com\" " +
                "--auth \"<what permits reaching them>\" [--exclude \"c,d\"]\n\n" +
                "Narrows the run to these and nothing else. It cannot be widened afterwards.");
            return 1;
        }

        var payload = new Dictionary<string, object?>
        {
            ["targets"] = targets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ["authorization"] = Option(args, "--auth"),
        };

        if (Option(args, "--exclude") is { Length: > 0 } exclusions)
        {
            payload["exclusions"] = exclusions.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return await SendAsync(
            paths,
            client => client.PostAsync("/v1/missions/active/scope", payload)).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the agent what the deliverable still needs, after reading it.
    /// </summary>
    /// <remarks>
    /// The supervisor's half of the bridge. Everything else this CLI does reports what an agent
    /// did; this is the one command that sends a sentence the other way.
    ///
    /// It cannot stop the run, and there is deliberately no flag that would. A supervisor is not a
    /// stop condition — the gate and the budgets are — and a second model with the power to end a
    /// run is the self-judgement this tool exists to remove, wearing a reviewer's hat.
    /// </remarks>
    private static async Task<int> ReviewAsync(RolloutPaths paths, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Usage: rollout review \"<what it still needs>\" [--missing \"a,b,c\"] " +
                "[--blocking] [--from <id>]\n\n" +
                "Read the deliverable first. The agent gets this on its next 'continue', once.");
            return 1;
        }

        var payload = new Dictionary<string, object?>
        {
            ["note"] = args[0],
            ["from"] = Option(args, "--from") ?? "claude",
            ["blocking"] = args.Contains("--blocking"),
        };

        if (Option(args, "--missing") is { Length: > 0 } missing)
        {
            payload["missing"] = missing.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return await SendAsync(
            paths,
            client => client.PostAsync("/v1/missions/active/review", payload)).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks what has already been tried, filtered.
    /// </summary>
    /// <remarks>
    /// There is deliberately no flag that fetches everything. The ceiling is enforced by the
    /// bridge, and offering a --all here would be handing back the thing the cap exists to prevent.
    /// </remarks>
    private static async Task<int> LedgerAsync(RolloutPaths paths, string[] args)
    {
        var parts = new List<string>();

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        Add("outcome", Option(args, "--outcome"));
        Add("agent", Option(args, "--agent"));
        Add("tier", Option(args, "--tier"));
        Add("contains", Option(args, "--contains") ?? Positional(
            args, "--outcome", "--agent", "--tier", "--contains", "--since", "--limit", "--offset"));
        Add("since", Option(args, "--since"));
        Add("limit", Option(args, "--limit"));
        Add("offset", Option(args, "--offset"));

        if (args.Contains("--full"))
        {
            parts.Add("full=true");
        }

        var route = "/v1/missions/active/attempts" + (parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty);

        return await SimpleGetAsync(paths, route).ConfigureAwait(false);
    }

    private static async Task<int> SubagentAsync(RolloutPaths paths, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Usage: rollout subagent \"<one step>\" [--agent <id>]");
            return 1;
        }

        return await SendAsync(paths, client => client.PostAsync("/v1/missions/active/subagent", new
        {
            task = args[0],
            agent = Option(args, "--agent"),
        })).ConfigureAwait(false);
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

    /// <summary>
    /// The first argument that is a word of its own, rather than a flag or a flag's value.
    /// </summary>
    /// <remarks>
    /// ⚠️ The obvious version — first argument not starting with <c>--</c> — is wrong, and quietly:
    /// <c>rollout ledger --limit 2</c> takes <c>2</c> as the text to search for, matches nothing,
    /// and reports "no attempt like this has been made". A confident, wrong answer to the one
    /// question this command exists to answer.
    ///
    /// The flags that take a value have to be named. Skipping the word after *any* flag would break
    /// the other half — <c>rollout ledger --full timeout</c> would swallow the search term, since
    /// <c>--full</c> is a bare switch.
    /// </remarks>
    private static string? Positional(string[] args, params string[] valueFlags)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return args[i];
            }

            if (valueFlags.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                i++;
            }
        }

        return null;
    }

    /// <summary>
    /// The money cap, with the punctuation an operator naturally types taken off.
    /// </summary>
    /// <remarks>
    /// ⚠️ Without this, <c>--max-spend $20</c> fails to parse and the mission runs with **no money
    /// brake at all**, silently, while the operator believes they set one. Failing loudly on a
    /// malformed cap would be acceptable; failing silently on a safety limit is not, so the two
    /// obvious spellings — a dollar sign and a thousands comma — have to work.
    /// </remarks>
    private static string? SpendCap(string[] args) =>
        Option(args, "--max-spend")?.Trim().TrimStart('$').Replace(",", string.Empty);

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
              rollout attach [--mission "<objective>"] [--no-start] [--elevated] [--quiet]
                                             Find it, or start it, and print the bridge details.
                                             Idempotent — safe to run every session.
              rollout open [--elevated]        Open the window anchored here.
              rollout status                   Health of the running instance, and its missions.

            Missions
              rollout mission "<objective>" [--agent claude] [--gate "<command>"]
                                             [--scope a,b] [--auth "<who authorised it>"]
                                             [--offload always|threshold]
                                             [--max-attempts N] [--max-hours N] [--max-spend USD]
                                             [--fourth-wall] [--deliverable <path>]
                                             [--at <folder>] [--elevated]
                                             --at points the agent at another repository: the
                                             mission block is written into ITS instruction file and
                                             the CLI opens there. That leaves RolloutLoud's anchor,
                                             so it produces a button and waits for the operator.
                                             --fourth-wall denies whoever supervises this run its
                                             raw material: no argv, no exit codes, no artifact
                                             folders, no button output. The deliverable is the one
                                             thing behind the wall they are meant to read. With
                                             declared targets it REQUIRES --auth.
              rollout briefing ["<subagent task>"]   The briefing; with a task, the subagent form.
              rollout admit "<hypothesis>" "<command>"
                                             Ask before running. Rejects repeats and out-of-scope.
              rollout attempt "<hypothesis>" "<command>" [--outcome ...] [--learned "..."]
              rollout subagent "<one step>" [--agent <id>]
                                             Run one step in a fresh process. Returns the verdict,
                                             not the transcript — the point is that the output
                                             never reaches your context.
              rollout resume [--mission-id <id>] [--agent <id>]
                                             Pick a mission back up after the window was closed.
                                             Starts RolloutLoud first if it is not running.
              rollout propose "<objective>" [--gate "<command>"] [--why "<reasoning>"]
                                             [--agent <id>] [--scope a,b] [--auth "..."]
                                             [--offload always|threshold] [--no-wait]
                                             Compose a mission and hand it to the operator to
                                             start. Opens RolloutLoud first if it is shut, then
                                             waits for their answer. Nothing runs until they say
                                             so — a gate you wrote for yourself is not a gate.
              rollout ledger ["<text>"] [--outcome ...] [--agent ...] [--tier N]
                                             [--since ...] [--limit N] [--full]
                                             What has already been tried. Filtered and paged —
                                             there is no way to fetch the lot, on purpose.
              rollout spend                    What this mission has cost so far, against its cap.
              rollout launch                   Ask for a launch button on the active mission.
                                             Opens nothing: the operator clicks it, or you do if
                                             they delegated it for this mission.
              rollout scope "a.example.com,*.staging.example.com" --auth "<what permits it>"
                                             Bound the run once you know where the boundary is.
                                             Narrows only — it can never be widened.
              rollout review "<what it still needs>" [--missing "a,b,c"] [--blocking]
                                             Read the deliverable, then say what is missing. The
                                             agent gets it on its next 'continue', once. It never
                                             stops the run — that is the gate's job, not yours.
              rollout wall                     What a Fourth Wall mission is withholding, and how
                                             much of it. Read this before mistaking absence for
                                             evidence.
              rollout continue                 Whether you may stop. Almost always: no.
              rollout gate                     Ask the success gate. The only way a mission ends.

            Identity
              rollout identity                 Is one attached?
              rollout identity --template      Write a starter file to lend details from.
                                             No file means agents are told not to create accounts.

            Finishing
              rollout finish "<what was achieved>" [--agent <id>]
                                             Ask to close RolloutLoud. Refused unless the mission
                                             is Achieved — running out of ideas is not finishing.

            Fluid buttons
              rollout button --title "<label>" --command "<cmd>" [--why "..."] [--elevated] [--detached]
              rollout invoke <button-id>       Run it yourself, if the allowlist permits.

            The repository you run this from is the anchor: elevated CLIs and buttons open there.
            """);

        return 0;
    }
}
