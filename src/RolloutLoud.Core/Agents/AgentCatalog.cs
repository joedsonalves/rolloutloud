using System.Text.Json;

namespace RolloutLoud.Core.Agents;

/// <summary>
/// The four CLIs RolloutLoud ships knowing about, and the loader that lets the operator correct it.
/// </summary>
/// <remarks>
/// Flags verified against the installed versions on 02/09/2026. They will rot — that is exactly
/// why <see cref="Load"/> exists. When a bypass flag stops working, fix
/// <c>.rolloutloud/agents.json</c> and reopen the app; do not wait for a release.
/// </remarks>
public static class AgentCatalog
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Hermes = "hermes";
    public const string OpenClaw = "openclaw";

    public static IReadOnlyList<AgentDescriptor> Defaults { get; } =
    [
        new AgentDescriptor
        {
            Id = Claude,
            DisplayName = "Claude Code",
            Executable = "claude",
            NormalArguments = [],
            ElevatedArguments = ["--dangerously-skip-permissions"],
            InstructionFile = "CLAUDE.md",
            PromptArguments = ["-p", "{prompt}"],
            Notes = "--dangerously-skip-permissions turns off every permission prompt.",
        },
        new AgentDescriptor
        {
            Id = Codex,
            DisplayName = "Codex CLI",
            Executable = "codex",
            NormalArguments = [],
            ElevatedArguments = ["--dangerously-bypass-approvals-and-sandbox"],
            InstructionFile = "AGENTS.md",
            PromptArguments = ["exec", "{prompt}"],
            Notes = "Skips approvals AND the sandbox. Codex calls this one extremely dangerous itself.",
        },
        new AgentDescriptor
        {
            Id = Hermes,
            DisplayName = "Hermes",
            Executable = "hermes",
            NormalArguments = ["chat"],
            ElevatedArguments = ["chat", "--yolo"],
            InstructionFile = "HERMES.md",
            PromptArguments = ["-z", "{prompt}"],
            Notes = "--yolo is a global flag; it goes before the subcommand as well as after.",
        },
        new AgentDescriptor
        {
            Id = OpenClaw,
            DisplayName = "OpenClaw",
            Executable = "openclaw",
            NormalArguments = ["tui"],
            ElevatedArguments = ["tui"],
            InstructionFile = "OPENCLAW.md",
            PromptArguments = ["agent", "--message", "{prompt}"],
            Notes =
                "OpenClaw has no single bypass flag: permission lives in `openclaw approvals` and " +
                "`openclaw exec-policy`, which are persisted host state rather than a launch argument. " +
                "The elevated button therefore only elevates the process — set the exec policy once, by hand.",
        },
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the operator's overrides, falling back to <see cref="Defaults"/> for anything absent.
    /// A malformed file returns the defaults rather than throwing: losing the launch buttons
    /// because of a stray comma would be a worse failure than ignoring the edit.
    /// </summary>
    public static IReadOnlyList<AgentDescriptor> Load(string agentsFile)
    {
        if (!File.Exists(agentsFile))
        {
            return Defaults;
        }

        try
        {
            var overrides = JsonSerializer.Deserialize<List<AgentDescriptor>>(
                File.ReadAllText(agentsFile), SerializerOptions);

            if (overrides is null or { Count: 0 })
            {
                return Defaults;
            }

            var byId = Defaults.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in overrides)
            {
                byId[descriptor.Id] = descriptor;
            }

            return [.. byId.Values];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Defaults;
        }
    }

    public static void WriteDefaults(string agentsFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(agentsFile)!);
        File.WriteAllText(agentsFile, JsonSerializer.Serialize(Defaults, SerializerOptions));
    }
}
