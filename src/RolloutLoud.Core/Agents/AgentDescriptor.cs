namespace RolloutLoud.Core.Agents;

/// <summary>How much the agent is allowed to do without stopping to ask.</summary>
public enum LaunchMode
{
    /// <summary>The CLI's own default. It still asks before acting.</summary>
    Normal,

    /// <summary>
    /// Approval prompts off. This is the "elevated button" of the UI, and it is two separate
    /// things at once: the CLI's own bypass flag, and — when <see cref="AgentDescriptor.RequiresOsElevation"/>
    /// is set — an OS-elevated process. Confusing the two is how you end up with a CLI that
    /// never prompts but still cannot bind a privileged port.
    /// </summary>
    Elevated,
}

/// <summary>
/// One CLI RolloutLoud knows how to drive.
/// </summary>
/// <remarks>
/// Kept as data rather than a subclass per CLI on purpose: these four ship new flags constantly,
/// and a flag change should be a JSON edit by the operator, not a rebuild. <see cref="AgentCatalog"/>
/// carries the defaults; <c>.rolloutloud/agents.json</c> overrides them.
/// </remarks>
public sealed record AgentDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Executable name resolved through PATH, or an absolute path.</summary>
    public required string Executable { get; init; }

    /// <summary>
    /// Arguments for an interactive session RolloutLoud opens.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>These carry the CLI's bypass flag, same as <see cref="ElevatedArguments"/>.</b> Every
    /// session RolloutLoud opens is one the operator is not sitting in front of — that is what the
    /// tool is for — and a CLI that stops to ask permission in a window nobody is watching is a CLI
    /// that does nothing until somebody comes back. The consent moved to opening RolloutLoud; see
    /// the broker decision in CLAUDE.md.
    ///
    /// What still separates the two lists is <see cref="RequiresOsElevation"/>, which is a genuinely
    /// different question: the flag stops the prompts, and an elevated process gets the rights.
    /// Confusing them is how you get a CLI that never asks and still cannot bind a privileged port.
    /// </remarks>
    public IReadOnlyList<string> NormalArguments { get; init; } = [];

    /// <summary>Arguments that turn approval prompts off. The whole point of the elevated button.</summary>
    public IReadOnlyList<string> ElevatedArguments { get; init; } = [];

    /// <summary>
    /// One-shot invocation for a round with nobody watching. <c>{prompt}</c> is substituted.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PromptArguments"/> because the bypass flag does not go in the same
    /// place for every CLI — Codex wants it after <c>exec</c>, Hermes takes it globally before the
    /// subcommand — and a rule that guesses the position produces an argv the CLI rejects, which
    /// surfaces as a round that "ran" and returned nothing.
    ///
    /// ⚠️ <b>This is the list that was missing, and its absence was invisible.</b> Supervised and
    /// subagent rounds built their argv from <see cref="PromptArguments"/> alone, so the two
    /// mechanisms that exist specifically to work unattended were the two that could read but not
    /// write. A reconnaissance round comes back "succeeded" and well-formed whether the agent chose
    /// not to write or could not.
    ///
    /// Empty falls back to <see cref="PromptArguments"/>, so an operator's existing
    /// <c>agents.json</c> keeps working — with the old behaviour, which is the honest fallback:
    /// inventing a flag for a CLI we have no entry for would be worse than leaving it as it was.
    /// </remarks>
    public IReadOnlyList<string> HeadlessArguments { get; init; } = [];

    /// <summary>
    /// True when the CLI genuinely needs an OS-elevated process, not just its own bypass flag.
    /// Left false by default: most of the time the bypass flag is all that is wanted, and
    /// elevating a long-lived agent process is a real cost, not a free upgrade.
    /// </summary>
    public bool RequiresOsElevation { get; init; }

    /// <summary>
    /// File the agent reads for standing instructions, relative to the repository root. This is
    /// where the mission briefing and the subagent-offload policy get written before launch.
    /// </summary>
    public required string InstructionFile { get; init; }

    /// <summary>Argument template that hands the agent a one-shot prompt. <c>{prompt}</c> is substituted.</summary>
    public IReadOnlyList<string> PromptArguments { get; init; } = [];

    /// <summary>Documentation shown under the button, so the operator can see what will run.</summary>
    public string? Notes { get; init; }

    public IReadOnlyList<string> ArgumentsFor(LaunchMode mode) =>
        mode == LaunchMode.Elevated ? ElevatedArguments : NormalArguments;

    /// <summary>The argv for a headless round, with the prompt substituted in.</summary>
    public IReadOnlyList<string> HeadlessArgumentsFor(string prompt) =>
    [
        .. (HeadlessArguments.Count > 0 ? HeadlessArguments : PromptArguments)
            .Select(a => a.Replace("{prompt}", prompt, StringComparison.Ordinal)),
    ];

    /// <summary>
    /// The launch arguments plus an opening line, so the session starts working instead of waiting.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A briefing is not a start.</b> Writing the mission into the instruction file gets it
    /// loaded and gets it obeyed — but an interactive CLI opens at a prompt and sits there. Launch
    /// a mission and walk away, and you come back to an agent that has read everything, understood
    /// everything, and done nothing, with no error anywhere to explain it.
    ///
    /// This cost a real run: the agent was launched into the target repository, the mission block
    /// was written, the process was alive, the ledger stayed empty, and the operator was looking at
    /// a window where nothing happened.
    ///
    /// The opening line goes as a bare positional argument, which every one of these CLIs treats as
    /// "start interactively, with this as the first message". Deliberately <b>not</b>
    /// <see cref="PromptArguments"/>: those carry the one-shot print flags used for headless
    /// subagent rounds, and using them here would close the window as soon as the answer was
    /// printed — turning the operator's session into a batch job they cannot talk to.
    /// </remarks>
    public IReadOnlyList<string> ArgumentsFor(LaunchMode mode, string? opening) =>
        string.IsNullOrWhiteSpace(opening)
            ? ArgumentsFor(mode)
            : [.. ArgumentsFor(mode), opening];

    /// <summary>Human-readable command line, for the button tooltip and the ledger.</summary>
    public string CommandLineFor(LaunchMode mode)
    {
        var args = ArgumentsFor(mode);
        return args.Count == 0 ? Executable : Executable + " " + string.Join(' ', args);
    }
}
