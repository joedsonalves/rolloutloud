using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Watchdog;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The operator's idea, and it generalises a decision this project already made: the relay collects
/// its handover note BEFORE the switch, while the agent that has the context still exists. Same
/// arithmetic as offload — a session at 600,000 tokens costs multiples per turn of one at 50,000.
/// </summary>
public sealed class HandoverTests : IDisposable
{
    private readonly string _root;

    public HandoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rlhand-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a run over.
        }
    }

    private static readonly HandoverSettings Settings = new();

    /// <summary>Attempts whose cost per finding is flat: cheap findings, steadily.</summary>
    private static List<Attempt> Steady(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new Attempt
        {
            Id = $"a{i:000}",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = $"idea {i}",
            Command = $"probe --case {i}",
            Outcome = AttemptOutcome.Failed,
            Observation = $"Rules out class {i}.",
            ContextTokens = 10_000,
            At = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
        }),
    ];

    // ---- the ceiling is a floor, not the criterion ---------------------------------------------

    [Fact]
    public void The_window_ceiling_hands_over_even_with_nothing_learned()
    {
        // The backstop, for the run where degradation never fires because nothing is being found at
        // all. 200,000 is the operator's number.
        var decision = HandoverWatch.Assess([], window: 200_000, Settings);

        Assert.True(decision.HandOver);
        Assert.Equal(HandoverReason.Ceiling, decision.Reason);
        Assert.Contains("200,000", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Below_the_ceiling_and_learning_steadily_it_carries_on()
    {
        Assert.False(HandoverWatch.Assess(Steady(20), window: 150_000, Settings).HandOver);
    }

    [Fact]
    public void No_reading_at_all_never_hands_over_on_the_ceiling()
    {
        // Guessing "it must be big by now" would replace a session that had barely started. Same
        // call the offload threshold makes: no reading, no action.
        Assert.False(HandoverWatch.Assess(Steady(20), window: null, Settings).HandOver);
    }

    [Fact]
    public void A_short_run_is_not_judged_on_a_trend()
    {
        // A trend computed from three data points is a coin toss with a decimal point on it.
        var barely = Steady(3);

        Assert.False(HandoverWatch.Assess(barely, window: 10_000, Settings).HandOver);
    }

    // ---- cost per finding is the strong signal --------------------------------------------------

    [Fact]
    public void A_session_whose_findings_got_expensive_hands_over_under_the_ceiling()
    {
        // ⚠️ The correction that matters. The offload threshold once used a number the operator
        // guessed, and the fix was to measure. A session whose findings have doubled in price is
        // the one worth replacing, whatever its window happens to read.
        var attempts = Steady(6);

        attempts.AddRange(Enumerable.Range(6, 6).Select(i => new Attempt
        {
            Id = $"a{i:000}",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = $"idea {i}",
            Command = $"probe --case {i}",
            Outcome = AttemptOutcome.Failed,
            Observation = i % 6 == 0 ? $"Rules out class {i}." : null,
            ContextTokens = 200_000,
            At = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
        }));

        var decision = HandoverWatch.Assess(attempts, window: 50_000, Settings);

        Assert.True(decision.HandOver);
        Assert.Equal(HandoverReason.Degrading, decision.Reason);
    }

    [Fact]
    public void The_prompt_asks_for_what_the_ledger_cannot_carry()
    {
        // What was tried is already recorded. What the session came to BELIEVE, and which of its own
        // assumptions it dropped, are only in its head.
        Assert.Contains("came to believe", HandoverWatch.HandoverPrompt, StringComparison.Ordinal);
        Assert.Contains("stopped trusting", HandoverWatch.HandoverPrompt, StringComparison.Ordinal);
        Assert.Contains("do not repeat it", HandoverWatch.HandoverPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_does_not_tell_the_session_to_stop()
    {
        // It is asked while healthy, precisely so it keeps working. A handover that reads as "you
        // are done" would turn a cost optimisation into an early finish.
        Assert.Contains("you are not finished", HandoverWatch.HandoverPrompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the session brain ------------------------------------------------------------------------

    [Fact]
    public void A_handover_survives_a_power_cut()
    {
        // Missions and ledgers already did. What did not was the supervising side: the chain of
        // handovers, and the reasoning that let one session pick up from the last.
        var brain = new SessionBrain(_root);

        brain.Record("m1", new Handover
        {
            Role = "supervisor",
            From = "claude",
            Believes = "the web surface is spent; the mobile app is the only untouched asset",
            Dropped = "that the scope grew since the last round — it did not",
        });

        var reloaded = new SessionBrain(_root).Chain("m1");

        Assert.Single(reloaded);
        Assert.Contains("mobile app", reloaded[0].Believes, StringComparison.Ordinal);
        Assert.Contains("did not", reloaded[0].Dropped!, StringComparison.Ordinal);
    }

    [Fact]
    public void One_role_does_not_read_the_other_roles_handovers()
    {
        // A worker reading its own supervisor's assessment of it changes what the worker does.
        var brain = new SessionBrain(_root);

        brain.Record("m1", new Handover { Role = "supervisor", From = "s1", Believes = "the worker is padding" });
        brain.Record("m1", new Handover { Role = "worker", From = "w1", Believes = "the login flow is the lever" });

        var forWorker = brain.Narrate("m1", "worker");

        Assert.Contains("login flow", forWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("padding", forWorker, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_chain_is_capped_rather_than_pasted_in_full()
    {
        // ⚠️ The point of the whole mechanism. Twenty handovers pasted in full is the expensive
        // window the handovers existed to escape, rebuilt one note at a time.
        var brain = new SessionBrain(_root);

        foreach (var i in Enumerable.Range(0, 10))
        {
            brain.Record("m1", new Handover { Role = "worker", From = $"w{i}", Believes = $"belief {i}" });
        }

        var narrated = brain.Narrate("m1", "worker");

        Assert.Contains("belief 9", narrated, StringComparison.Ordinal);
        Assert.DoesNotContain("belief 0", narrated, StringComparison.Ordinal);
        Assert.Contains("earlier handover(s) not shown", narrated, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_session_is_told_it_is_the_first()
    {
        // "No previous session" and "the brain failed to load" must not read the same, or a fresh
        // session starts from nothing while believing it started from everything.
        Assert.Contains("You are the first", new SessionBrain(_root).Narrate("m1", "worker"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_handover_is_framed_as_an_opinion_rather_than_a_finding()
    {
        var brain = new SessionBrain(_root);
        brain.Record("m1", new Handover { Role = "worker", From = "w1", Believes = "the cache is the problem" });

        Assert.Contains("one session's opinion", brain.Narrate("m1", "worker"), StringComparison.Ordinal);
    }
}
