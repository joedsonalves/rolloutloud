using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// A real run reached a fork it could not settle alone and did what a CLI agent always does: it
/// printed a menu and stopped. That is a hand-back — the same move as "let me know if you'd like me
/// to try another approach", with better manners — and behind the Fourth Wall it is worse, because
/// the menu is visible only to the operator while the supervisor is the one who should answer.
/// </summary>
public sealed class AgentQuestionTests : IDisposable
{
    private readonly RolloutPaths _paths;
    private readonly MissionEngine _engine;

    public AgentQuestionTests()
    {
        _paths = new RolloutPaths(Path.Combine(Path.GetTempPath(), "rlask-" + Guid.NewGuid().ToString("N")[..8]));
        _paths.EnsureCreated();

        _engine = new MissionEngine(
            new Mission
            {
                Id = "m1",
                Objective = "settle a fork without stopping",
                AgentId = "claude",
                State = MissionState.Running,
            },
            new MissionLedger("m1"),
            new MissionStore(_paths),
            _paths);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_paths.RepositoryRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a run over.
        }
    }

    private static AgentQuestion Question(string text = "which programme?") => new()
    {
        Id = AgentQuestion.NewId(),
        From = "claude",
        Question = text,
        Options = ["visa", "stay on NBA", "oppo"],
        IfUnanswered = "I take visa and say in my next observation that I took it unanswered",
    };

    [Fact]
    public void Asking_does_not_stop_the_run()
    {
        // The entire point. An agent that blocks on a question has handed the decision to somebody
        // who may be asleep, and it looks identical whether the reason was good or bad.
        _engine.Ask(Question());

        Assert.Equal(MissionState.Running, _engine.Mission.State);
        Assert.True(_engine.ShouldContinue().Continue);
    }

    [Fact]
    public void An_unanswered_question_stays_open_and_costs_nothing()
    {
        _engine.Ask(Question());

        Assert.Single(_engine.Mission.Questions, q => q.IsOpen);
        Assert.Empty(_engine.CollectAnswers());
        Assert.True(_engine.ShouldContinue().Continue);
    }

    [Fact]
    public void The_answer_need_not_be_one_of_the_options()
    {
        // ⚠️ Deliberately unvalidated. An answer limited to the agent's own choices lets the agent
        // frame the decision it claims to be delegating — and on the run this was built for, the
        // right answer began "none of those, and here is what you left out".
        var asked = _engine.Ask(Question());

        var answered = _engine.Answer(asked.Id, "none of those as you framed them", "claude");

        Assert.NotNull(answered);
        Assert.Equal("none of those as you framed them", answered.Answer);
        Assert.DoesNotContain(answered.Answer, answered.Options);
    }

    [Fact]
    public void An_answer_is_handed_over_once_and_then_kept()
    {
        var asked = _engine.Ask(Question());
        _engine.Answer(asked.Id, "take visa", "claude");

        Assert.Single(_engine.CollectAnswers());
        Assert.Empty(_engine.CollectAnswers());

        // Still on the record, which is the account of how the run was steered.
        Assert.Single(_engine.Mission.Questions);
        Assert.Equal("take visa", _engine.Mission.Questions[0].Answer);
    }

    [Fact]
    public void Answering_the_same_question_twice_is_refused()
    {
        // The second answer would arrive at an agent that already acted on the first, which is a
        // change of instruction dressed as a reply.
        var asked = _engine.Ask(Question());

        Assert.NotNull(_engine.Answer(asked.Id, "take visa", "claude"));
        Assert.Null(_engine.Answer(asked.Id, "actually take oppo", "claude"));
    }

    [Fact]
    public void Answering_something_that_was_never_asked_is_refused()
    {
        Assert.Null(_engine.Answer("q-nonexistent", "sure", "claude"));
    }

    [Fact]
    public void The_answer_reads_back_with_the_question_it_answers()
    {
        // An agent forty turns on has forgotten what it asked. An answer arriving alone is a
        // sentence with no subject.
        var asked = _engine.Ask(Question("which programme?"));
        var answered = _engine.Answer(asked.Id, "visa", "claude")!;

        Assert.Contains("which programme?", answered.ForAgent(), StringComparison.Ordinal);
        Assert.Contains("visa", answered.ForAgent(), StringComparison.Ordinal);
        Assert.Contains("claude", answered.ForAgent(), StringComparison.Ordinal);
    }

    [Fact]
    public void Questions_and_answers_survive_a_restart()
    {
        // Behind the wall this is the only account of how a run was steered, and it has to outlive
        // the window — which this project's own build rule closes routinely.
        var asked = _engine.Ask(Question());
        _engine.Answer(asked.Id, "take visa", "claude");

        var reloaded = new MissionStore(_paths).LoadAll().Single(r => r.Mission.Id == "m1").Mission;

        Assert.Equal("take visa", Assert.Single(reloaded.Questions).Answer);
    }

    [Fact]
    public void Both_sides_of_the_exchange_are_in_the_mission_events()
    {
        // The operator has to be able to read what their delegate told the agent, and what the
        // agent asked in the first place. Behind the wall, invisible steering is the failure.
        var asked = _engine.Ask(Question());
        _engine.Answer(asked.Id, "take visa", "claude");

        Assert.Contains(_engine.Events, e => e.Kind == "question");
        Assert.Contains(_engine.Events, e => e.Kind == "answer");
    }
}
