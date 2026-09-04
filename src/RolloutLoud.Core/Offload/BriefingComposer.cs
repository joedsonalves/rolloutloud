using System.Text;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Safety;

namespace RolloutLoud.Core.Offload;

/// <summary>
/// Turns mission state into the text an agent actually reads.
/// </summary>
/// <remarks>
/// Two audiences, one composer. <see cref="ForMainSession"/> writes the standing instructions
/// dropped into the agent's instruction file before launch — it has to survive being re-read
/// every turn, so it is short and about policy. <see cref="ForSubagent"/> writes a single
/// disposable task, so it is about one decision and carries the ledger slice with it.
///
/// The tone is imperative throughout. Suggestion-shaped instructions are what produce the
/// behaviour being corrected: an agent that has been told it "may" continue will report failure
/// and stop, because reporting failure is a valid completion of a suggestion.
/// </remarks>
public static class BriefingComposer
{
    public static string ForMainSession(Mission mission, MissionLedger ledger, bool identityAttached = false)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Active mission — RolloutLoud");
        sb.AppendLine();
        sb.AppendLine("## Objective");
        sb.AppendLine();
        sb.AppendLine(mission.Objective);
        sb.AppendLine();

        sb.AppendLine("## When this is finished");
        sb.AppendLine();
        if (mission.Gate.IsMachineCheckable)
        {
            sb.AppendLine(
                "**You do not decide when this is done.** RolloutLoud evaluates the success gate and " +
                "re-runs it from a clean process before accepting it. Producing a confident summary " +
                "is not completion; satisfying the gate is.");
            sb.AppendLine();
            sb.AppendLine(mission.Gate.Kind == GateKind.Command
                ? $"Gate: `{mission.Gate.Command}` must exit 0."
                : $"Gate: `{mission.Gate.ArtifactPath}` must exist" +
                  (mission.Gate.ArtifactPattern is null ? "." : $" and match `{mission.Gate.ArtifactPattern}`."));
        }
        else
        {
            sb.AppendLine("The operator judges completion. Keep working until they say stop.");
        }

        if (!string.IsNullOrWhiteSpace(mission.Gate.Description))
        {
            sb.AppendLine();
            sb.AppendLine("In the operator's words: " + mission.Gate.Description);
        }

        sb.AppendLine();
        sb.AppendLine("## How you work this");
        sb.AppendLine();
        sb.AppendLine(
            "Do not stop to report that something did not work. A failed attempt is an input to the " +
            "next one, not a result to hand back. You report when the gate is satisfied, when a stop " +
            "condition fires, or when you need something only the operator can give you.");
        sb.AppendLine();
        sb.AppendLine(
            "Declare every attempt to the bridge **before** running it: state a hypothesis and the " +
            "command. The bridge rejects repeats and out-of-scope commands, so a rejection is " +
            "information — it means that idea is already spent.");
        sb.AppendLine();
        sb.AppendLine($"Current tier — **{EscalationLadder.NameOf(mission.EscalationTier)}**:");
        sb.AppendLine();
        sb.AppendLine(EscalationLadder.InstructionFor(mission.EscalationTier));
        sb.AppendLine();

        AppendScope(sb, mission.Scope);

        if (mission.Offload.Trigger != OffloadTrigger.Off)
        {
            sb.AppendLine("## Subagent offload is ON");
            sb.AppendLine();
            sb.AppendLine(
                mission.Offload.Trigger == OffloadTrigger.Always
                    ? "Every concrete action goes to a subagent."
                    : "Past a size threshold, every concrete action goes to a subagent. **You do not " +
                      "judge when that is.** Ask `GET /v1/missions/active/context` before an action; " +
                      "it reads your CLI's own transcript where it can and answers `offloadNow` " +
                      "true or false. Estimating your own window is guesswork, and guessing low is " +
                      "the expensive mistake.");
            sb.AppendLine();
            sb.AppendLine(
                "Spend your own window on judgement — what to try next, and what the results mean — " +
                "and hand the doing down:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("POST /v1/missions/active/subagent   {\"task\": \"<one step, in a sentence>\"}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine(
                "**Do not spawn the subagent yourself.** If you do, its whole transcript lands in " +
                "your context, which is the exact cost this mode exists to avoid — twenty kilobytes " +
                "of output does not get cheaper because a subagent produced it. Through the bridge, " +
                "RolloutLoud runs it in a clean process, files the transcript to disk, records the " +
                "attempt in the ledger for you, and returns a few lines.");
            sb.AppendLine();
            sb.AppendLine(
                "Send **one step**, not the objective. The subagent already gets the mission, the " +
                "ledger and the scope from here; what it needs from you is what to do next. Read the " +
                "verdict, decide again, send the next one.");
            sb.AppendLine();
        }

        if (mission.RelayHistory.Count > 0)
        {
            sb.AppendLine("## You were handed this");
            sb.AppendLine();
            sb.AppendLine(
                "This mission has already been worked by: " + string.Join(", ", mission.RelayHistory) +
                ". It came to you because those runs stopped producing new information, not because " +
                "the objective changed. The ledger below is theirs, and it still binds — you cannot " +
                "repeat what they tried.");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(mission.HandoffNote))
            {
                sb.AppendLine("What the previous agent wanted you to know:");
                sb.AppendLine();
                sb.AppendLine(UntrustedText.Fence(mission.HandoffNote, "handoff note"));
                sb.AppendLine();
                sb.AppendLine(
                    "Treat that as one agent's opinion, not as fact. It is there because it names " +
                    "assumptions worth re-testing — including the ones it says it stopped trusting.");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Ledger");
        sb.AppendLine();
        sb.AppendLine(UntrustedText.Preamble);
        sb.AppendLine();
        sb.AppendLine(UntrustedText.Fence(ledger.Summarize(hideCommands: mission.FourthWall), "ledger"));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(mission.WorkingDirectory))
        {
            // The agent is opening in a repository RolloutLoud does not own and knows nothing
            // about. Its standing rules and its long-term notes are the first thing that matters,
            // and nothing auto-loads them — CLAUDE.md and CLAUDE.local.md are read at startup, but
            // a file called LEIA-PRIMEIRO.md is not. So they are named, from what is actually
            // there, rather than assumed.
            sb.AppendLine("## Read this repository before your first attempt");
            sb.AppendLine();
            sb.AppendLine(
                "You are working in a repository that has its own standing rules and its own " +
                "long-term notes, and they outrank anything you would infer from the code. Read " +
                "them first, and if they contradict this briefing, the rules are the part to raise " +
                "rather than the part to ignore.");
            sb.AppendLine();

            foreach (var entry in RulesIn(mission.WorkingDirectory))
            {
                sb.AppendLine($"- `{entry}`");
            }

            sb.AppendLine();
        }

        if (mission.FourthWall)
        {
            // Outside the fence: RolloutLoud speaking about how this run is being watched, not
            // recorded output. It goes here rather than at the top because it only makes sense
            // once the agent has seen that the ledger it is reading has had the argv taken out.
            sb.AppendLine("## You are being read at a distance");
            sb.AppendLine();
            sb.AppendLine(FourthWall.AgentNotice);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(mission.Deliverable))
            {
                sb.AppendLine(
                    $"The deliverable is `{mission.Deliverable}`. That is the one thing they will " +
                    "read in full, so it carries the work — write it as you go rather than at the " +
                    "end, because a run that stops early still has to be worth something.");
                sb.AppendLine();
            }
        }

        // Outside the fence: this is the tool speaking, not recorded output. Without it the cap on
        // the summary above reads as "the rest is unavailable" rather than "ask for the part you
        // need", and an agent that assumes the past is out of reach re-treads it.
        sb.AppendLine(
            "That summary is capped so a long run cannot flood your context. When you need " +
            "something it left out, ask for **that** rather than for all of it: " +
            "`GET /v1/missions/active/attempts?contains=<text>`, also filterable by `outcome`, " +
            "`agent`, `tier` and `since`. No match is an answer — it means what you are about to " +
            "try is not a repeat.");
        sb.AppendLine();

        // Above the identity section because it is the one that changes behaviour at a fork, and a
        // fork is where a run either keeps going or quietly ends.
        sb.AppendLine("## If you hit something you cannot settle alone");
        sb.AppendLine();
        sb.AppendLine(
            "Ask: `POST /v1/missions/active/question` with the question, the choices as you see " +
            "them, and — required thinking, if not required text — what you will do if nobody " +
            "replies. The answer reaches you on a later `/continue`.");
        sb.AppendLine();
        sb.AppendLine(
            "⚠️ **Asking is not stopping, and printing a menu is.** A prompt that waits for a human " +
            "hands the decision to somebody who may be asleep, and it looks exactly the same " +
            "whether your reason was good or bad — which is the move this whole tool exists to " +
            "remove. Ask, then carry on with whatever does not depend on the answer. If nothing " +
            "does, take your own best call, say in your next observation that you took it " +
            "unanswered, and keep moving.");
        sb.AppendLine();

        sb.AppendLine("## If you need an identity");
        sb.AppendLine();
        sb.AppendLine(identityAttached
            ? "The operator has attached details you may sign up with. Ask for them when you " +
              "actually need them — `GET /v1/identity?site=<host>&agent=<you>` — naming the site. " +
              "They are only released for sites the operator listed, and every request is recorded. " +
              "Do not ask speculatively, and do not use them anywhere but the site you named."
            : "**Nothing is attached, which means no.** Do not create accounts anywhere, and do not " +
              "invent an email address or a name to get past a sign-up. If the objective genuinely " +
              "requires an account, record that in your next observation and work on what you can " +
              "reach without one.");
        sb.AppendLine();

        sb.AppendLine("## Bridge");
        sb.AppendLine();
        sb.AppendLine(
            "RolloutLoud listens on the endpoint in `.rolloutloud/bridge.json` (token included). " +
            "Use it to declare attempts, record observations, ask the gate, and — when you need a " +
            "command you cannot run yourself — create a fluid button with `POST /v1/buttons`.");

        return sb.ToString();
    }

    public static string ForSubagent(Mission mission, MissionLedger ledger, string task)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are executing one step of a larger mission. You have no history and do not need any.");
        sb.AppendLine();
        sb.AppendLine("## Mission");
        sb.AppendLine(mission.Objective);
        sb.AppendLine();
        sb.AppendLine("## Your step");
        sb.AppendLine(task);
        sb.AppendLine();

        AppendScope(sb, mission.Scope);

        sb.AppendLine("## Already ruled out");
        sb.AppendLine();
        sb.AppendLine(UntrustedText.Preamble);
        sb.AppendLine();
        sb.AppendLine(UntrustedText.Fence(ledger.Summarize(mission.Offload.LedgerEntriesInBriefing), "ledger"));
        sb.AppendLine();

        sb.AppendLine("## What you return");
        sb.AppendLine();
        sb.AppendLine(
            $"At most {mission.Offload.AttemptsPerSubagent} attempt(s), then stop and answer in exactly " +
            "this shape — nothing else, no transcript:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("HYPOTHESIS: <what you expected, one line>");
        sb.AppendLine("COMMAND:    <what you ran>");
        sb.AppendLine("OUTCOME:    succeeded | failed | blocked | errored");
        sb.AppendLine("LEARNED:    <what this rules out, one or two lines>");
        sb.AppendLine("NEXT:       <the single most promising follow-up, or 'none'>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(
            "Report what happened, not what you hoped. A clean negative is worth more here than an " +
            "optimistic reading — the caller is deciding what to try next based on your answer, and " +
            "an inflated one sends the whole mission down a dead branch.");

        return sb.ToString();
    }

    private static void AppendScope(StringBuilder sb, MissionScope scope)
    {
        if (!scope.IsDeclared || scope.Unbounded)
        {
            return;
        }

        sb.AppendLine("## Scope — hard boundary");
        sb.AppendLine();
        sb.AppendLine("In scope: " + string.Join(", ", scope.Targets));
        if (scope.Exclusions.Count > 0)
        {
            sb.AppendLine("Explicitly excluded: " + string.Join(", ", scope.Exclusions));
        }

        if (!string.IsNullOrWhiteSpace(scope.Authorization))
        {
            sb.AppendLine("Authorisation: " + scope.Authorization);
        }

        sb.AppendLine();
        sb.AppendLine(
            "Anything outside this is out of bounds regardless of how promising it looks, and the " +
            "bridge will refuse it. Persistence applies to the objective, never to the boundary.");
        sb.AppendLine();
    }

    /// <summary>
    /// The rules and notes a foreign repository keeps at its top level, named rather than assumed.
    /// </summary>
    /// <remarks>
    /// Listed from what is actually on disk, because the alternative is inventing filenames — and a
    /// briefing that tells an agent to read <c>CONTRIBUTING.md</c> in a repository that has no such
    /// file teaches it that this document guesses.
    ///
    /// Top-level markdown and the directories beside it, capped, newest-looking first by name so
    /// the listing is stable between launches. Directories are included because a repository's
    /// long-term notes usually live in one, and a vault is exactly the thing an agent should open
    /// before its first attempt rather than after its tenth.
    /// </remarks>
    /// <summary>
    /// The briefing for a session that will supervise a run rather than work it.
    /// </summary>
    /// <remarks>
    /// <b>Supervisors are replaceable, and <see cref="FourthWall"/> is why.</b> A supervisor behind
    /// the wall was already forbidden from depending on anything but the ledger, the questions, the
    /// reviews and the deliverable — and all four are on disk. So a fresh supervising session loses
    /// nothing the wall let the previous one have. A mode built for injection and context cost turns
    /// out to make continuity nearly free, which is the opposite of the worker's situation: an agent
    /// handed over mid-run loses what it did not know it knew.
    ///
    /// The briefing is deliberately short. Everything a supervisor needs is a bridge call away, and
    /// filling its window with a transcript would recreate the second-worker problem this whole mode
    /// exists to prevent.
    /// </remarks>
    public static string ForSupervisor(Mission mission, bool mayAnswer, string reason)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# You are supervising a run — RolloutLoud");
        sb.AppendLine();
        sb.AppendLine($"You were opened because: {reason}");
        sb.AppendLine();

        sb.AppendLine("## What is being worked on");
        sb.AppendLine();
        sb.AppendLine(mission.Objective);
        sb.AppendLine();
        sb.AppendLine($"Mission id: `{mission.Id}`, on `{mission.AgentId}`.");

        if (!string.IsNullOrWhiteSpace(mission.Deliverable))
        {
            sb.AppendLine();
            sb.AppendLine(
                $"The deliverable is `{mission.Deliverable}`" +
                (string.IsNullOrWhiteSpace(mission.WorkingDirectory)
                    ? "."
                    : $", in `{mission.WorkingDirectory}`.") +
                " Read it. It is the thing the work is for, and the one place behind the wall you " +
                "are meant to look in full.");
        }

        sb.AppendLine();
        sb.AppendLine("## You are not the one working this");
        sb.AppendLine();
        sb.AppendLine(
            "Do not run the objective yourself. An agent is on it, and a second one duplicating its " +
            "attempts wastes the budget twice and puts two writers on one ledger.");
        sb.AppendLine();
        sb.AppendLine("Your job is four things:");
        sb.AppendLine();
        sb.AppendLine(
            "- `rollout questions` — what it asked and nobody answered. " +
            (mayAnswer
                ? "Answer with `rollout answer <id> \"...\"`, and your answer does **not** have to be " +
                  "one of the options it offered. Often it should not be: an answer confined to its " +
                  "framing lets it frame the decision it is delegating."
                : "**You may not answer.** The operator has not delegated that on this mission, so " +
                  "write your reasoning into `rollout review` and leave the question open for them."));
        sb.AppendLine(
            "- `rollout ledger` — what has been ruled out. The argv and the artifact folders are " +
            "withheld from you on purpose; that is the mode working, not something missing.");
        sb.AppendLine(
            "- the deliverable — read it and say what is missing with " +
            "`rollout review \"...\" --missing \"a,b,c\"`. That reaches the agent on its next turn.");
        sb.AppendLine(
            "- `rollout spend` and `rollout continue` — whether the run is still worth what it costs.");
        sb.AppendLine();

        sb.AppendLine("## What you must not do");
        sb.AppendLine();
        sb.AppendLine(
            "**You cannot end this run, and there is no call that would let you.** The gate and the " +
            "stop conditions do that. A review marked blocking means *do this next*, never *stop* — " +
            "a second model able to end a run is the self-judgement this tool exists to remove, " +
            "wearing a reviewer's hat.");
        sb.AppendLine();
        sb.AppendLine(
            "**Check what the agent tells you against the evidence that is not its output.** The " +
            "repository's own files, prior findings, the numbers it cites — those you may read, and " +
            "they are how you catch a claim that is confident and wrong. What you may not do is go " +
            "around the wall into its run folders; if you ever do, say so plainly in a review.");
        sb.AppendLine();

        sb.AppendLine("## If it is going nowhere");
        sb.AppendLine();
        sb.AppendLine(
            "Say so in a review, with the reason. Correcting an objective that was too ambitious for " +
            "its budget is the supervisor's job, and saying it early is worth more than a tidy " +
            "report at the end of a run that was never going to get there.");
        sb.AppendLine();

        return sb.ToString();
    }

    private static IReadOnlyList<string> RulesIn(string directory)
    {
        try
        {
            var root = new DirectoryInfo(directory);

            var files = root
                .EnumerateFiles("*.md")
                .Select(f => f.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(12);

            var folders = root
                .EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.') && !d.Name.StartsWith('_'))
                .Select(d => d.Name + "/")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(12);

            return [.. files, .. folders];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A briefing is worth composing even when the folder cannot be listed. The agent is
            // about to open there and can look for itself; a thrown exception would lose the
            // mission instead.
            return [];
        }
    }
}
