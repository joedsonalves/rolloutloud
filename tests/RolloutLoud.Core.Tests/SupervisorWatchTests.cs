using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Offload;
using RolloutLoud.Core.Watchdog;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The watchdog supervises the worker. Nothing supervised the supervisor — so when that session ran
/// out of allowance the agent's questions piled up unanswered and the run carried on with nobody
/// reading the deliverable.
/// </summary>
public class SupervisorWatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static Mission Running(params AgentQuestion[] questions) => new()
    {
        Id = "m1",
        Objective = "work something",
        AgentId = "claude",
        State = MissionState.Running,
        Deliverable = "docs/PLAN.md",
        Questions = questions,
    };

    private static AgentQuestion Open(int minutesAgo) => new()
    {
        Id = AgentQuestion.NewId(),
        From = "claude",
        Question = "which programme?",
        At = Now.AddMinutes(-minutesAgo),
    };

    private static WakeDecision Assess(
        Mission mission,
        DateTimeOffset? lastWoken = null,
        DateTimeOffset? written = null,
        bool oneIsAlreadyOpen = false) =>
        SupervisorWatch.Assess(mission, new WakeSettings(), Now, lastWoken, written, oneIsAlreadyOpen);

    // ---- the trigger is a fact, not a mood ----------------------------------------------------

    [Fact]
    public void A_question_left_open_long_enough_wakes_one()
    {
        // "Is the supervisor idle" is guesswork and would be wrong in both directions. "Has a
        // question sat unanswered for ten minutes" is already on disk. Same reasoning as the
        // give-up detector being grammatical rather than semantic.
        var decision = Assess(Running(Open(minutesAgo: 15)));

        Assert.True(decision.Wake);
        Assert.Contains("which programme?", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_question_asked_a_moment_ago_does_not()
    {
        // The operator is usually there. Opening a session to answer something they were about to
        // answer themselves is both a waste and an irritation.
        Assert.False(Assess(Running(Open(minutesAgo: 2))).Wake);
    }

    [Fact]
    public void An_answered_question_does_not()
    {
        var answered = Open(minutesAgo: 60) with { Answer = "visa", AnsweredAt = Now.AddMinutes(-50) };

        Assert.False(Assess(Running(answered)).Wake);
    }

    [Fact]
    public void A_deliverable_nobody_has_read_wakes_one()
    {
        Assert.True(Assess(Running(), written: Now.AddMinutes(-45)).Wake);
    }

    [Fact]
    public void A_deliverable_already_reviewed_since_it_changed_does_not()
    {
        var mission = Running() with
        {
            Reviews =
            [
                new SupervisorNote
                {
                    Id = "n1",
                    From = "claude",
                    Note = "read it",
                    At = Now.AddMinutes(-10),
                },
            ],
        };

        Assert.False(Assess(mission, written: Now.AddMinutes(-45)).Wake);
    }

    [Fact]
    public void A_deliverable_written_again_after_the_last_review_wakes_one()
    {
        var mission = Running() with
        {
            Reviews =
            [
                new SupervisorNote
                {
                    Id = "n1",
                    From = "claude",
                    Note = "read it",
                    At = Now.AddMinutes(-90),
                },
            ],
        };

        Assert.True(Assess(mission, written: Now.AddMinutes(-45)).Wake);
    }

    // ---- the money brake -----------------------------------------------------------------------

    [Fact]
    public void It_will_not_open_two_within_the_floor()
    {
        // ⚠️ Not optional. Every trigger here stays true after a supervisor has looked and decided
        // there was nothing to add — a question it deliberately left open still reads as open — so
        // without the floor this would open a session a minute for the rest of the night.
        Assert.False(Assess(Running(Open(minutesAgo: 120)), lastWoken: Now.AddMinutes(-5)).Wake);
    }

    [Fact]
    public void It_will_once_the_floor_has_passed()
    {
        Assert.True(Assess(Running(Open(minutesAgo: 120)), lastWoken: Now.AddMinutes(-20)).Wake);
    }

    // ---- runs that are not running --------------------------------------------------------------

    [Theory]
    [InlineData(MissionState.Achieved)]
    [InlineData(MissionState.Exhausted)]
    [InlineData(MissionState.Aborted)]
    [InlineData(MissionState.Paused)]
    public void A_run_that_is_not_going_needs_nobody_watching_it(MissionState state)
    {
        var mission = Running(Open(minutesAgo: 120)) with { State = state };

        Assert.False(Assess(mission).Wake);
    }

    // ---- what the woken session is told ---------------------------------------------------------

    [Fact]
    public void With_a_delegation_it_is_told_it_may_answer()
    {
        var briefing = BriefingComposer.ForSupervisor(Running(), mayAnswer: true, "a question is open");

        Assert.Contains("rollout answer", briefing, StringComparison.Ordinal);
        Assert.Contains("does **not** have to be one of the options", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_one_it_is_told_it_may_not()
    {
        // The operator's delegation is the whole authority here. Without it the woken session reads,
        // reviews and drafts — a model answering a model with no human anywhere is the thing that
        // needed a boundary.
        var briefing = BriefingComposer.ForSupervisor(Running(), mayAnswer: false, "a question is open");

        Assert.Contains("You may not answer", briefing, StringComparison.Ordinal);
        Assert.Contains("leave the question open", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_told_not_to_work_the_objective()
    {
        // Two writers on one ledger is worse than none, and a supervisor that starts hunting is the
        // second-worker failure this whole mode exists to prevent.
        var briefing = BriefingComposer.ForSupervisor(Running(), mayAnswer: true, "a question is open");

        Assert.Contains("Do not run the objective yourself", briefing, StringComparison.Ordinal);
        Assert.Contains("two writers on one ledger", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_told_it_cannot_end_the_run()
    {
        var briefing = BriefingComposer.ForSupervisor(Running(), mayAnswer: true, "a question is open");

        Assert.Contains("You cannot end this run", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_told_why_it_was_opened()
    {
        // A session that does not know why it exists starts by asking, which is the one thing
        // nobody is there to answer.
        Assert.Contains(
            "a question has been open",
            BriefingComposer.ForSupervisor(Running(), true, "a question has been open for 15 minutes"),
            StringComparison.Ordinal);
    }

    // ---- one supervisor, not a screen full of them --------------------------------------------

    [Fact]
    public void A_supervisor_already_on_screen_stops_a_second_one()
    {
        // ⚠️ The regression this pair exists for. Every trigger here is a symptom, and a symptom
        // stays true until somebody acts on it: a question reads as unanswered whether nobody has
        // looked at it or somebody is reading it this second. A supervisor that could not answer —
        // out of allowance, or launched with no permission to write — kept this true and earned a
        // fresh window every fifteen minutes for an afternoon.
        var mission = Running(Open(minutesAgo: 240));

        Assert.True(Assess(mission).Wake);
        Assert.False(Assess(mission, oneIsAlreadyOpen: true).Wake);
    }

    [Fact]
    public void A_supervisor_that_closed_lets_the_next_one_through()
    {
        // The opposite failure, and it is the one a liveness check invites: a role held for ever by
        // a window that is gone leaves the run with nobody watching and no way to notice.
        var mission = Running(Open(minutesAgo: 240));

        Assert.True(Assess(mission, oneIsAlreadyOpen: false).Wake);
    }

    [Fact]
    public void The_floor_still_applies_when_none_is_open()
    {
        // Both brakes, and they answer different questions. A supervisor that looked, found nothing
        // to add and closed leaves every trigger exactly as it found them — the clock is what stops
        // that from reopening one immediately.
        var mission = Running(Open(minutesAgo: 240));

        Assert.False(Assess(mission, lastWoken: Now.AddMinutes(-2)).Wake);
        Assert.True(Assess(mission, lastWoken: Now.AddMinutes(-20)).Wake);
    }
}
