using System.Text;
using RolloutLoud.Core.Missions;

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
                    : $"Once your context passes ~{mission.Offload.TokenThreshold:N0} tokens, every concrete action goes to a subagent.");
            sb.AppendLine();
            sb.AppendLine(
                "Spend your own window on judgement — what to try next, and what the results mean. " +
                "Hand execution down with a briefing built from `GET /v1/missions/{id}/briefing`, and " +
                $"give each subagent at most {mission.Offload.AttemptsPerSubagent} attempt(s). Do not " +
                "paste transcripts into your own context: read the verdict, record the observation, decide again.");
            sb.AppendLine();
        }

        sb.AppendLine("## Ledger");
        sb.AppendLine();
        sb.AppendLine(ledger.Summarize());
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
        sb.AppendLine(ledger.Summarize(mission.Offload.LedgerEntriesInBriefing));
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
}
