using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Missions;

public enum GateWeakness
{
    /// <summary>No machine check at all. Nothing but the operator can ever end this run.</summary>
    NoMachineCheck,

    /// <summary>The command exits 0 whatever happens. A finish line that is already crossed.</summary>
    CannotFail,

    /// <summary>Satisfied by the agent producing a file. Claiming victory, with extra steps.</summary>
    SelfCertifying,

    /// <summary>Reads RolloutLoud's own records — which the agent writes. Circular.</summary>
    Circular,

    /// <summary>The gate asks a model. That is the judgement the gate exists to replace.</summary>
    JudgedByAModel,
}

public enum GateConcern
{
    /// <summary>Worth the operator's eye, not necessarily wrong.</summary>
    WorthKnowing,

    /// <summary>The gate does not do the job a gate exists to do.</summary>
    Serious,
}

public sealed record GateFinding
{
    public required GateWeakness Weakness { get; init; }

    public required GateConcern Concern { get; init; }

    /// <summary>What is wrong, addressed to the operator reading the proposal.</summary>
    public required string Detail { get; init; }

    /// <summary>The fragment of the command that prompted it. Empty when the whole thing did.</summary>
    public string Fragment { get; init; } = string.Empty;
}

public sealed record GateReview
{
    public required IReadOnlyList<GateFinding> Findings { get; init; }

    public bool HasSeriousFinding => Findings.Any(f => f.Concern == GateConcern.Serious);

    /// <summary>One line for the activity log and the CLI. Says the worst thing first.</summary>
    public required string Headline { get; init; }
}

/// <summary>
/// Reads a proposed success gate and says what is weak about it — to the operator, never to the
/// agent that wrote it.
/// </summary>
/// <remarks>
/// This exists because of one consequence of letting an agent compose a mission. The whole product
/// rests on <b>the gate deciding rather than the agent</b>, and a gate the agent wrote for itself
/// is not a gate — it is the agent's own opinion of done, wearing a command's clothes. That failure
/// compiles, runs, passes its re-verification, and lies, which is the class of bug this project
/// keeps finding by running things rather than by reading diffs.
///
/// The dangerous shapes are not exotic:
///
/// <list type="bullet">
/// <item><c>dotnet test || true</c> — reads as rigorous and can never fail.</item>
/// <item><c>test -f REPORT.md</c> — passes the moment the agent writes a file it was going to
/// write anyway.</item>
/// <item><c>grep -q CRITICAL findings.json</c> — the same thing with a coat of diligence on.</item>
/// <item>Anything pointed at <c>.rolloutloud/</c> — the agent authors those records.</item>
/// </list>
///
/// <b>It marks, it never refuses.</b> Same call as the injection guard, for the same reason: a
/// gate that looks self-certifying may be exactly right — a scanner really does write its output
/// to a file — and the tool does not know which. What it can do is make sure the operator's eye
/// lands on the gate before it becomes the finish line, which is the whole point of asking them.
///
/// The counterpart matters as much: when there is nothing to say, it says nothing. A checker that
/// warns about every gate is one the operator learns to click past, and then it is worse than
/// absent, because they believe something is watching.
/// </remarks>
public static class GateCritique
{
    /// <summary>Commands whose exit code is zero no matter what happened before them.</summary>
    private static readonly string[] AlwaysSucceed =
        ["true", ":", "exit 0", "echo", "printf", "cmd /c exit 0", "cmd /c exit /b 0"];

    /// <summary>Verbs that ask "is this file there?" rather than "is the objective met?".</summary>
    private static readonly string[] ExistenceVerbs =
        ["test", "[", "[[", "test-path", "ls", "dir", "cat", "type", "get-content", "stat", "file"];

    /// <summary>Verbs that read a file looking for a word in it.</summary>
    private static readonly string[] SearchVerbs =
        ["grep", "egrep", "fgrep", "rg", "ripgrep", "findstr", "select-string", "jq"];

    public static GateReview Review(SuccessGate gate)
    {
        var findings = new List<GateFinding>();

        if (!gate.IsMachineCheckable)
        {
            findings.Add(new GateFinding
            {
                Weakness = GateWeakness.NoMachineCheck,
                Concern = GateConcern.Serious,
                Detail =
                    "There is no gate command, so nothing can ever mark this Achieved except you. " +
                    "That is a fine choice when the finish line genuinely needs a human eye — but " +
                    "it means the run has no automatic end, and the agent will keep going until a " +
                    "stop condition fires.",
            });

            return Compose(findings);
        }

        if (gate.Kind == GateKind.ArtifactMatch)
        {
            findings.Add(new GateFinding
            {
                Weakness = GateWeakness.SelfCertifying,
                Concern = GateConcern.WorthKnowing,
                Detail =
                    "This gate is satisfied by a file appearing with the right text in it, and the " +
                    "agent is what writes files. It holds only if something the agent does not " +
                    "control produces that file — a test runner, a scanner. If the agent writes it " +
                    "by hand, this is the agent marking its own work.",
                Fragment = gate.ArtifactPath ?? string.Empty,
            });

            return Compose(findings);
        }

        var command = gate.Command ?? string.Empty;
        var segments = Split(command);

        Neutralised(command, segments, findings);
        SelfCertifying(segments, findings);
        Circular(command, findings);
        AsksAModel(segments, findings);

        return Compose(findings);
    }

    /// <summary>
    /// The gate that reads as rigorous and cannot fail.
    /// </summary>
    /// <remarks>
    /// Position is the whole tell, and getting it wrong in either direction is a bug.
    ///
    /// <c>dotnet test || true</c> and <c>dotnet test; echo done</c> both exit 0 whatever the test
    /// did, because the shell reports the <em>last</em> segment. But <c>echo probing && curl …</c>
    /// is fine — an always-true command that is not last decides nothing. So only the final
    /// segment is judged, plus any <c>||</c> fallback, which by construction runs exactly when the
    /// real check failed.
    /// </remarks>
    private static void Neutralised(string command, IReadOnlyList<Segment> segments, List<GateFinding> findings)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var last = segments[^1];

        if (!IsAlwaysTrue(last.Text))
        {
            return;
        }

        // A lone `echo …` as the entire gate is a different sentence from `dotnet test || true`:
        // one is a placeholder nobody replaced, the other looks like a real check and is not.
        var alone = segments.Count == 1;

        findings.Add(new GateFinding
        {
            Weakness = GateWeakness.CannotFail,
            Concern = GateConcern.Serious,
            Detail = alone
                ? "This command exits 0 whatever happens, so the gate is satisfied the instant it " +
                  "is asked. The mission would be Achieved before any work was done."
                : $"The gate ends in '{last.Text}', which always exits 0 — and the shell reports " +
                  "the last command. Whatever the real check found, this gate passes. It reads " +
                  "rigorous and cannot fail.",
            Fragment = alone ? command : last.Text,
        });
    }

    private static void SelfCertifying(IReadOnlyList<Segment> segments, List<GateFinding> findings)
    {
        foreach (var segment in segments)
        {
            var verb = Verb(segment.Text);

            if (ExistenceVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new GateFinding
                {
                    Weakness = GateWeakness.SelfCertifying,
                    Concern = GateConcern.Serious,
                    Detail =
                        "This passes as soon as a file is there, and writing files is the one thing " +
                        "the agent can always do. It checks that a report exists, not that the " +
                        "objective was met. Prefer a command that re-derives the result — a test, a " +
                        "build, the scanner run again.",
                    Fragment = segment.Text,
                });

                continue;
            }

            // A search reading a file is the same trap dressed up. A search reading a PIPE is not:
            // there the text came from whatever ran upstream a moment ago, which is exactly the
            // "re-derive it" shape wanted. Flagging that would train the operator to click past
            // the warning that matters.
            if (SearchVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase) && !segment.FedByAPipe)
            {
                findings.Add(new GateFinding
                {
                    Weakness = GateWeakness.SelfCertifying,
                    Concern = GateConcern.WorthKnowing,
                    Detail =
                        "This looks for text in a file rather than re-deriving the result. It holds " +
                        "only if that file comes from something the agent does not control. If the " +
                        "agent writes it, the gate is asking the agent whether it succeeded.",
                    Fragment = segment.Text,
                });
            }
        }
    }

    /// <summary>
    /// A gate pointed at RolloutLoud's own records.
    /// </summary>
    /// <remarks>
    /// The ledger, the run folders and the mission files are all authored by the agent through the
    /// bridge. A gate that greps them asks the agent to confirm its own report — the exact loop the
    /// gate was introduced to break, closed with more steps in it.
    /// </remarks>
    private static void Circular(string command, List<GateFinding> findings)
    {
        var match = Regex.Match(
            command,
            @"[""'\s=]?(\.rolloutloud[\\/][^\s""']*|runs[\\/][^\s""']*)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (!match.Success)
        {
            return;
        }

        findings.Add(new GateFinding
        {
            Weakness = GateWeakness.Circular,
            Concern = GateConcern.Serious,
            Detail =
                "This reads RolloutLoud's own records, and the agent is what writes them — every " +
                "ledger entry and every run folder came from the agent through the bridge. The gate " +
                "would be asking the agent to confirm its own report.",
            Fragment = match.Groups[1].Value,
        });
    }

    private static void AsksAModel(IReadOnlyList<Segment> segments, List<GateFinding> findings)
    {
        foreach (var segment in segments)
        {
            var verb = Verb(segment.Text);

            if (!Agents.AgentCatalog.Defaults.Any(a =>
                    verb.Equals(a.Executable, StringComparison.OrdinalIgnoreCase) ||
                    verb.Equals(a.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            findings.Add(new GateFinding
            {
                Weakness = GateWeakness.JudgedByAModel,
                Concern = GateConcern.Serious,
                Detail =
                    "The gate runs an agent CLI, so the verdict comes from a model reading the work " +
                    "and deciding whether it is good. That is the judgement this gate exists to " +
                    "replace, and re-running it from a clean process buys nothing when the thing " +
                    "being re-run is another opinion.",
                Fragment = segment.Text,
            });
        }
    }

    private static GateReview Compose(List<GateFinding> findings)
    {
        var headline = findings.Count == 0
            ? "Nothing to flag: the gate re-derives the result rather than reading back the agent's word for it."
            : findings.OrderByDescending(f => f.Concern).First().Detail;

        return new GateReview { Findings = findings, Headline = headline };
    }

    /// <summary>One command in a chain, and whether something upstream is feeding it.</summary>
    private readonly record struct Segment(string Text, bool FedByAPipe);

    /// <summary>
    /// Breaks a command line into the commands the shell would run in turn.
    /// </summary>
    /// <remarks>
    /// Not a shell parser and not trying to be. It splits on the operators and remembers whether
    /// the separator before each part was a pipe, because that one bit is what tells a file-reading
    /// grep apart from a pipeline-reading one. Quoting is not honoured — a separator inside quotes
    /// splits a segment that should have stayed whole — and the cost of that is a warning the
    /// operator reads and dismisses, which is the right direction to be wrong in.
    /// </remarks>
    private static List<Segment> Split(string command)
    {
        var segments = new List<Segment>();
        var parts = Regex.Split(command, @"(\|\||&&|;|\||\n)", RegexOptions.None, TimeSpan.FromSeconds(1));
        var piped = false;

        foreach (var part in parts)
        {
            var text = part.Trim();

            if (text is "||" or "&&" or ";" or "|")
            {
                piped = text == "|";
                continue;
            }

            if (text.Length > 0)
            {
                segments.Add(new Segment(text, piped));
                piped = false;
            }
        }

        return segments;
    }

    private static bool IsAlwaysTrue(string segment)
    {
        var text = segment.Trim().TrimEnd(';').Trim();

        return AlwaysSucceed.Any(t =>
            text.Equals(t, StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith(t + " ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The program a segment runs, with the noise that hides it stripped.
    /// </summary>
    /// <remarks>
    /// ⚠️ Without stripping <c>VAR=1</c>, <c>sudo</c> and <c>cmd /c</c>, the obvious version reads
    /// the verb of <c>sudo test -f out.txt</c> as "sudo" and finds nothing wrong with a gate that
    /// is the textbook self-certifying case. A checker that misses the plain form of the thing it
    /// checks for is worse than none: the operator now believes the gate was looked at.
    /// </remarks>
    private static string Verb(string segment)
    {
        var words = segment.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];

            if (word.Contains('=', StringComparison.Ordinal) && !word.StartsWith('-'))
            {
                continue;
            }

            if (word is "sudo" or "doas" or "env" or "!")
            {
                continue;
            }

            if ((word.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                 word.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                 word.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
                 word.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
                 word.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
                 word.Equals("powershell", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < words.Length && words[i + 1].StartsWith(['-', '/']))
            {
                i++;
                continue;
            }

            // Reduce a path to the program: /usr/bin/test and C:\bin\grep.exe are the same verbs.
            var name = word.Split(['/', '\\']).Last();

            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name;
        }

        return string.Empty;
    }
}
