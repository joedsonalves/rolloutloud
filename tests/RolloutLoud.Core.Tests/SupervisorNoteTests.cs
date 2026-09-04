using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The bridge's other direction. Everything else on it carries what the agent did; this carries
/// what somebody reading the result wants next — and without it, Fourth Wall described a reviewing
/// job with nowhere to put the review.
/// </summary>
public sealed class SupervisorNoteTests : IDisposable
{
    private readonly RolloutPaths _paths;
    private readonly MissionEngine _engine;

    public SupervisorNoteTests()
    {
        _paths = new RolloutPaths(Path.Combine(Path.GetTempPath(), "rlrev-" + Guid.NewGuid().ToString("N")[..8]));
        _paths.EnsureCreated();

        var mission = new Mission
        {
            Id = "m1",
            Objective = "produce the thing",
            AgentId = "claude",
            State = MissionState.Running,
            Deliverable = "docs/PLAN.md",
        };

        _engine = new MissionEngine(mission, new MissionLedger("m1"), new MissionStore(_paths), _paths);
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

    private static SupervisorNote Note(string text = "the plan does not say what happens on rollback") => new()
    {
        Id = SupervisorNote.NewId(),
        From = "claude",
        Note = text,
        Missing = ["a rollback path", "who is paged when it fails"],
    };

    [Fact]
    public void A_review_is_recorded_against_the_mission()
    {
        _engine.Review(Note());

        var recorded = Assert.Single(_engine.Mission.Reviews);
        Assert.Equal("claude", recorded.From);
        Assert.Equal(2, recorded.Missing.Count);
    }

    [Fact]
    public void A_review_never_changes_the_state_of_the_run()
    {
        // The line worth holding. A supervisor is not a stop condition — the gate and the budgets
        // are — and a second model able to end a run is the self-judgement this product exists to
        // remove, wearing a reviewer's hat.
        _engine.Review(Note() with { Blocking = true });

        Assert.Equal(MissionState.Running, _engine.Mission.State);
        Assert.True(_engine.ShouldContinue().Continue);
    }

    [Fact]
    public void Notes_are_handed_over_once_and_then_kept()
    {
        // Repeating a note every turn would make the briefing an echo chamber and teach the agent
        // to skim the section; dropping it would lose the only trace of how a run was steered,
        // which behind the wall is the only trace there is.
        _engine.Review(Note("first"));
        _engine.Review(Note("second"));

        var delivered = _engine.CollectReviews();
        Assert.Equal(2, delivered.Count);

        Assert.Empty(_engine.CollectReviews());
        Assert.Equal(2, _engine.Mission.Reviews.Count);
        Assert.All(_engine.Mission.Reviews, r => Assert.False(r.IsPending));
    }

    [Fact]
    public void A_note_written_after_a_collection_is_still_delivered()
    {
        _engine.Review(Note("first"));
        _engine.CollectReviews();

        _engine.Review(Note("second"));

        var second = Assert.Single(_engine.CollectReviews());
        Assert.Equal("second", second.Note);
    }

    [Fact]
    public void A_blocking_note_reads_as_do_this_next_rather_than_stop()
    {
        // The wording matters as much as the flag: an agent told to "stop" by a supervisor would
        // hand the decision back, which is the failure this whole product is built against.
        var text = (Note() with { Blocking = true }).ForAgent();

        Assert.Contains("before your next attempt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stop", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_gaps_are_listed_rather_than_buried_in_the_prose()
    {
        // A list survives being skimmed and a paragraph does not — and an agent forty turns deep is
        // skimming. It is also the part that can be read back later as "was this addressed?".
        var text = Note().ForAgent();

        Assert.Contains("- still missing: a rollback path", text, StringComparison.Ordinal);
        Assert.Contains("- still missing: who is paged when it fails", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_survives_a_restart()
    {
        // The record of how a run was steered has to outlive the window, or the operator loses the
        // only account of what their delegate told the agent.
        _engine.Review(Note("keep this"));

        var reloaded = new MissionStore(_paths).LoadAll().Single(r => r.Mission.Id == "m1").Mission;

        Assert.Equal("keep this", Assert.Single(reloaded.Reviews).Note);
    }

    [Fact]
    public void The_deliverable_is_not_a_pentest_idea()
    {
        // The operator's point, and a fair one: the app is general. A deliverable is whatever the
        // run is FOR — a migration plan here — and nothing in the machinery should assume a report.
        Assert.Equal("docs/PLAN.md", _engine.Mission.Deliverable);

        _engine.Review(Note("the benchmark table has no baseline"));

        Assert.Contains("baseline", _engine.Mission.Reviews[0].Note, StringComparison.Ordinal);
    }
}
