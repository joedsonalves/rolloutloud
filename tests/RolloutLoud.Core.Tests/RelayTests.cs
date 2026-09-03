using RolloutLoud.Core.Agents;
using RolloutLoud.Core.Missions;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// Tier 3 of the ladder: hand the same objective and the same ledger to a different model,
/// because the failure was in one model's habits rather than in the problem.
/// </summary>
public class RelayTests
{
    private static AgentDescriptor Agent(string id, bool promptable = true) => new()
    {
        Id = id,
        DisplayName = id,
        Executable = id,
        InstructionFile = id + ".md",
        PromptArguments = promptable ? ["-p", "{prompt}"] : [],
    };

    private static IReadOnlyList<AgentDescriptor> Four =>
        [Agent("claude"), Agent("codex"), Agent("hermes"), Agent("openclaw")];

    private static Mission On(string agentId, params string[] history) => new()
    {
        Id = "m1",
        Objective = "make the suite pass",
        AgentId = agentId,
        State = MissionState.Running,
        RelayHistory = history,
    };

    [Fact]
    public void The_next_agent_is_one_that_has_not_worked_this_mission()
    {
        var choice = RelayPlanner.ChooseNext(On("claude"), Four, _ => true);

        Assert.True(choice.CanRelay);
        Assert.NotEqual("claude", choice.AgentId);
    }

    [Fact]
    public void An_agent_already_in_the_history_is_never_chosen_again()
    {
        // Rotating back produces the same habits that got stuck — and, because the ledger forbids
        // its own spent attempts, it would arrive with fewer moves than it had the first time.
        var choice = RelayPlanner.ChooseNext(On("hermes", "claude", "codex"), Four, _ => true);

        Assert.Equal("openclaw", choice.AgentId);
    }

    [Fact]
    public void An_agent_that_is_not_installed_is_not_a_candidate()
    {
        // The relay fires unattended. Handing the mission to a missing CLI would end the run with
        // a launch error at the rung most likely to have found the answer.
        var choice = RelayPlanner.ChooseNext(
            On("claude"), Four, a => a.Id == "openclaw");

        Assert.Equal("openclaw", choice.AgentId);
    }

    [Fact]
    public void An_agent_with_no_prompt_argument_cannot_be_relayed_to()
    {
        // Installed but not drivable headlessly: its launch button works, but there is no way to
        // hand it a prompt and read a result, so it cannot be supervised.
        var agents = new[] { Agent("claude"), Agent("codex", promptable: false) };

        var choice = RelayPlanner.ChooseNext(On("claude"), agents, AgentAvailability.CanBeRelayedTo);

        Assert.False(choice.CanRelay);
    }

    [Fact]
    public void With_nobody_left_the_reason_says_who_has_already_tried()
    {
        var choice = RelayPlanner.ChooseNext(
            On("openclaw", "claude", "codex", "hermes"), Four, _ => true);

        Assert.False(choice.CanRelay);
        Assert.Contains("claude", choice.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openclaw", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void With_only_one_agent_configured_there_is_nowhere_to_relay()
    {
        var choice = RelayPlanner.ChooseNext(On("claude"), [Agent("claude")], _ => true);

        Assert.False(choice.CanRelay);
    }

    [Fact]
    public void Relaying_records_the_outgoing_agent_and_drops_the_tier()
    {
        // The tier drops to 1 not as a reset of progress — the ledger still forbids every spent
        // attempt — but because the tier-3 instruction is "hand this off", and an agent that has
        // just arrived being told to hand off would relay again immediately.
        var paths = new Core.Workspace.RolloutPaths(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var store = new MissionStore(paths);

        var mission = On("claude") with { EscalationTier = 3, Gate = SuccessGate.OperatorJudged };
        var engine = new MissionEngine(mission, new MissionLedger("m1"), store, paths);

        try
        {
            engine.RelayTo("codex", "I no longer trust the fixture ordering.");

            Assert.Equal("codex", engine.Mission.AgentId);
            Assert.Contains("claude", engine.Mission.RelayHistory);
            Assert.Equal(1, engine.Mission.EscalationTier);
            Assert.Contains("fixture ordering", engine.Mission.HandoffNote!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(paths.StateRoot, recursive: true);
        }
    }

    [Fact]
    public void The_briefing_tells_the_new_agent_it_was_handed_the_mission()
    {
        var mission = On("codex", "claude") with
        {
            HandoffNote = "The failure is not in the fixtures; I stopped trusting the CI image.",
        };

        var briefing = Core.Offload.BriefingComposer.ForMainSession(mission, new MissionLedger("m1"));

        Assert.Contains("already been worked by", briefing, StringComparison.Ordinal);
        Assert.Contains("claude", briefing, StringComparison.Ordinal);
        Assert.Contains("stopped trusting the CI image", briefing, StringComparison.Ordinal);

        // And it is framed as opinion, because the previous agent got stuck holding it.
        Assert.Contains("one agent's opinion", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mission_that_was_never_relayed_says_nothing_about_a_handoff()
    {
        var briefing = Core.Offload.BriefingComposer.ForMainSession(On("claude"), new MissionLedger("m1"));

        Assert.DoesNotContain("already been worked by", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void Availability_rejects_an_executable_that_is_not_on_the_path()
    {
        Assert.False(AgentAvailability.IsInstalled("definitely-not-a-real-cli-xyzzy"));
        Assert.False(AgentAvailability.IsInstalled(""));
    }

    [Fact]
    public void Availability_finds_something_that_is_on_the_path()
    {
        // dotnet is here by definition: this test is running under it.
        Assert.True(AgentAvailability.IsInstalled("dotnet"));
    }
}
