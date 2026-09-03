using RolloutLoud.Core.Missions;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The operator's rule, in their words: *only if the task was actually completed — "I could not
/// do it" is not a completed task.* Every refusal below is a way an agent could otherwise talk
/// its way out of its own supervision.
/// </summary>
public class ShutdownGateTests
{
    private static Mission WithGate(MissionState state) => new()
    {
        Id = "m1",
        Objective = "make the suite pass",
        AgentId = "claude",
        State = state,
        Gate = new SuccessGate { Kind = GateKind.Command, Command = "dotnet test" },
    };

    [Fact]
    public void An_achieved_mission_may_close_the_window()
    {
        var mission = WithGate(MissionState.Achieved);

        var decision = ShutdownGate.Evaluate(mission, [mission], unattendedAllowed: false);

        Assert.True(decision.Allowed);
        Assert.Equal(ShutdownVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void Unattended_is_a_separate_permission_from_being_finished()
    {
        // The gate decides whether the WORK is done. This decides whether the operator wants the
        // window gone as a result. Two questions, and the second one is theirs.
        var mission = WithGate(MissionState.Achieved);

        Assert.Equal(
            ShutdownVerdict.Allowed,
            ShutdownGate.Evaluate(mission, [mission], unattendedAllowed: false).Verdict);

        Assert.Equal(
            ShutdownVerdict.AllowedUnattended,
            ShutdownGate.Evaluate(mission, [mission], unattendedAllowed: true).Verdict);
    }

    [Theory]
    [InlineData(MissionState.Running)]
    [InlineData(MissionState.Paused)]
    [InlineData(MissionState.Draft)]
    [InlineData(MissionState.Aborted)]
    public void Anything_short_of_achieved_is_refused(MissionState state)
    {
        var mission = WithGate(state);

        Assert.False(ShutdownGate.Evaluate(mission, [mission], unattendedAllowed: true).Allowed);
    }

    [Fact]
    public void Exhausted_is_refused_and_told_why_in_those_words()
    {
        // This is the important one. Exhausted is the state an agent reaches by running out of
        // budget, and it is exactly what "I could not do it" looks like from the outside. An
        // agent reading the refusal has to understand it is not a permission problem.
        var mission = WithGate(MissionState.Exhausted);

        var decision = ShutdownGate.Evaluate(mission, [mission], unattendedAllowed: true);

        Assert.False(decision.Allowed);
        Assert.Contains("Running out of budget is not completing the objective", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_judged_mission_cannot_be_closed_by_an_agent_at_all()
    {
        // With no machine gate there is nothing but the agent's own opinion, which is the one
        // input this whole mechanism refuses to take.
        var mission = new Mission
        {
            Id = "m1",
            Objective = "look into the flakiness",
            AgentId = "claude",
            State = MissionState.Achieved,
            Gate = SuccessGate.OperatorJudged,
        };

        var decision = ShutdownGate.Evaluate(mission, [mission], unattendedAllowed: true);

        Assert.False(decision.Allowed);
        Assert.Contains("only the operator", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_agent_finishing_does_not_close_a_window_another_is_working_in()
    {
        var mine = WithGate(MissionState.Achieved);
        var theirs = WithGate(MissionState.Running) with { Id = "m2", AgentId = "codex" };

        var decision = ShutdownGate.Evaluate(mine, [mine, theirs], unattendedAllowed: true);

        Assert.False(decision.Allowed);
        Assert.Contains("m2", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_mission_alongside_other_finished_ones_may_still_close()
    {
        var mine = WithGate(MissionState.Achieved);
        var done = WithGate(MissionState.Achieved) with { Id = "m2" };
        var spent = WithGate(MissionState.Exhausted) with { Id = "m3" };

        // Exhausted and Achieved are both finished — nobody is working in them.
        Assert.True(ShutdownGate.Evaluate(mine, [mine, done, spent], unattendedAllowed: false).Allowed);
    }

    [Fact]
    public void No_mission_at_all_is_refused()
    {
        var decision = ShutdownGate.Evaluate(null, [], unattendedAllowed: true);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Mission_ids_created_in_the_same_moment_do_not_collide()
    {
        // Ids were second-resolution and missions are keyed by id, so two opened in the same
        // second silently replaced one another — a mission that was simply not in the list, with
        // no error anywhere. Found by a script that created two in a row.
        var ids = Enumerable.Range(0, 200).Select(_ => Mission.NewId()).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("m-", id, StringComparison.Ordinal));
    }
}
