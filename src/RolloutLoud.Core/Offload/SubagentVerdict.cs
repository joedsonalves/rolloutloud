using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Offload;

/// <summary>
/// What a subagent is asked to return, parsed back out of whatever it actually wrote.
/// </summary>
public sealed record SubagentVerdict
{
    public required string Hypothesis { get; init; }

    public required string Command { get; init; }

    /// <summary>succeeded | failed | blocked | errored</summary>
    public required string Outcome { get; init; }

    /// <summary>What the attempt rules out. The line that earns the round.</summary>
    public required string Learned { get; init; }

    /// <summary>The single most promising follow-up, or empty.</summary>
    public string? Next { get; init; }

    /// <summary>False when the block was missing and this was salvaged from prose.</summary>
    public required bool WellFormed { get; init; }

    /// <summary>One line for the main agent's context. Everything else stays on disk.</summary>
    public string Compact =>
        $"[{Outcome}] {Hypothesis} — {Learned}" +
        (string.IsNullOrWhiteSpace(Next) ? string.Empty : $" Next: {Next}");
}

/// <summary>
/// Reads a subagent's answer back.
/// </summary>
/// <remarks>
/// **Deliberately forgiving, and that is the whole design.** A subagent is asked for five labelled
/// lines and returns them perhaps most of the time; the rest of the time it wraps them in prose,
/// puts them in a code fence, uses a different case, or writes a paragraph and forgets the labels
/// entirely.
///
/// Refusing to parse those would be the wrong trade twice over. The round has already been paid
/// for — the model ran, the command ran — so discarding the answer over its formatting throws away
/// the money and the information. And a parser that fails on a fifth of rounds turns the barren
/// counter into a formatting detector: three prose answers in a row would stop a mission that was
/// working fine.
///
/// So: pull out what is labelled, salvage the rest as <see cref="SubagentVerdict.Learned"/>, and
/// mark it <see cref="SubagentVerdict.WellFormed"/> false so the operator can see how often the
/// format is being ignored. The full transcript is on disk either way.
/// </remarks>
public static class VerdictParser
{
    public static SubagentVerdict Parse(string? output)
    {
        var text = (output ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return new SubagentVerdict
            {
                Hypothesis = "(the subagent returned nothing)",
                Command = string.Empty,
                Outcome = "errored",
                Learned = "The subagent produced no output at all. Says nothing about the idea.",
                WellFormed = false,
            };
        }

        var hypothesis = Field(text, "HYPOTHESIS");
        var command = Field(text, "COMMAND");
        var outcome = Field(text, "OUTCOME");
        var learned = Field(text, "LEARNED");
        var next = Field(text, "NEXT");

        var wellFormed = hypothesis is not null && learned is not null;

        return new SubagentVerdict
        {
            Hypothesis = hypothesis ?? "(no hypothesis stated)",
            Command = command ?? string.Empty,
            Outcome = Normalize(outcome),
            Learned = learned ?? Salvage(text),
            Next = string.Equals(next, "none", StringComparison.OrdinalIgnoreCase) ? null : next,
            WellFormed = wellFormed,
        };
    }

    /// <summary>
    /// A labelled line, whatever it is wrapped in.
    /// </summary>
    /// <remarks>
    /// Multiline so the label can appear anywhere rather than only at the start, and the value
    /// runs to the end of its line — subagents put these inside code fences and bullet lists, and
    /// anchoring to the start of input would find none of them.
    /// </remarks>
    private static string? Field(string text, string label)
    {
        var match = Regex.Match(
            text,
            @"^\s*[>*\-\s]*" + label + @"\s*:\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));

        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim().Trim('`', '*', '"');
        return value.Length == 0 || value.StartsWith('<') ? null : value;
    }

    /// <summary>
    /// Turns a prose answer into something the ledger can hold.
    /// </summary>
    /// <remarks>
    /// The tail rather than the head: a subagent that writes prose puts its conclusion at the end,
    /// and the opening is usually a restatement of the task it was given — which the ledger
    /// already knows.
    /// </remarks>
    private static string Salvage(string text)
    {
        var lines = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("```", StringComparison.Ordinal))
            .ToList();

        var tail = string.Join(" ", lines.TakeLast(4));

        var salvaged = tail.Length <= 500 ? tail : tail[^500..];
        return "(unformatted answer) " + salvaged;
    }

    private static string Normalize(string? outcome) => outcome?.ToLowerInvariant() switch
    {
        not null and var o when o.Contains("succee", StringComparison.Ordinal) => "succeeded",
        not null and var o when o.Contains("success", StringComparison.Ordinal) => "succeeded",
        not null and var o when o.Contains("block", StringComparison.Ordinal) => "blocked",
        not null and var o when o.Contains("error", StringComparison.Ordinal) => "errored",

        // Unstated reads as failed, because that is the honest default: a subagent that did not
        // say it succeeded did not succeed, and recording an unclear round as a win is the one
        // mistake the whole product is built to prevent.
        _ => "failed",
    };
}
