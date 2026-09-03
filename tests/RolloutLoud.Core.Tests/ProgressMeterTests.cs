using RolloutLoud.Core.Missions;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The novelty check asks whether attempts are different. This asks whether they are still buying
/// anything, and at what price — which catches the run where every attempt is distinct and nothing
/// is being learned.
/// </summary>
public class ProgressMeterTests
{
    private static Attempt Attempt(
        int index,
        int tokens,
        bool informative,
        AttemptOutcome outcome = AttemptOutcome.Failed) => new()
    {
        Id = $"a{index:00}",
        MissionId = "m1",
        AgentId = "claude",
        Hypothesis = $"idea {index}",
        // Real, distinct tool names. Attempt.Fingerprint normalises digits away, so a fixture
        // built from "tool-1", "tool-2", "tool-3" is ONE signature to the ledger — which silently
        // turns a novelty assertion into a test of nothing. I have written that trap down once
        // already and walked into it again here.
        Command = $"{Tools[index % Tools.Length]} --probe {Tools[index % Tools.Length]}-run",
        Outcome = outcome,
        Observation = informative ? $"Rules out class {index}." : null,
        ContextTokens = tokens,
        At = DateTimeOffset.UtcNow.AddMinutes(index),
    };

    private static readonly string[] Tools =
        ["nmap -sV", "ffuf -u", "nuclei -t", "curl -s", "dotnet test", "npm run e2e",
         "pytest -k", "grep -rn", "docker compose run"];

    private static List<Attempt> Run(params (int Tokens, bool Informative)[] attempts) =>
        [.. attempts.Select((a, i) => Attempt(i, a.Tokens, a.Informative))];

    [Fact]
    public void It_declines_to_judge_a_short_run()
    {
        // Below the minimum, one lucky attempt swings the ratio threefold, and an escalation on
        // that noise would tell an agent to abandon an approach that was working.
        var reading = ProgressMeter.Assess(Run((1000, true), (1000, true), (1000, true)));

        Assert.Equal(ProgressTrend.Unknown, reading.Trend);
        Assert.False(reading.ShouldEscalate);
    }

    [Fact]
    public void Steady_findings_at_a_steady_price_do_not_escalate()
    {
        // Fifty failures that each rule something out is progress. Interrupting it would be the
        // expensive mistake.
        var reading = ProgressMeter.Assess(Run(
            (10_000, true), (11_000, true), (12_000, true),
            (13_000, true), (14_000, true), (15_000, true)));

        Assert.Equal(ProgressTrend.Steady, reading.Trend);
        Assert.False(reading.ShouldEscalate);
    }

    [Fact]
    public void A_completely_stalled_window_is_caught_by_both_checks()
    {
        // Both fire here, and that overlap is fine — it is the cheap case. I wrote this test
        // expecting the novelty check to miss it and it did not: a window with no informative
        // attempt at all trips the "nothing new to say" branch regardless of how distinct the
        // commands are. The meter's own contribution is the case below, not this one.
        var attempts = Run(
            (10_000, true), (11_000, true), (12_000, true),
            (40_000, false), (45_000, false), (50_000, false));

        Assert.Equal(ProgressTrend.Stalled, ProgressMeter.Assess(attempts).Trend);
        Assert.True(EscalationLadder.ShouldEscalate(attempts, plateauThreshold: 3));
    }

    [Fact]
    public void A_finding_that_costs_much_more_than_it_used_to_is_the_case_novelty_misses()
    {
        // THE case this exists for. The run is still learning — one finding in the recent window —
        // so the novelty check is satisfied and passes it. But each answer now costs several times
        // what it did, which is an approach running out rather than a hard problem being worked,
        // and only the cost side can see that.
        var attempts = Run(
            (5_000, true), (5_000, true), (5_000, true),
            (60_000, false), (60_000, false), (60_000, true));

        var reading = ProgressMeter.Assess(attempts);

        Assert.Equal(ProgressTrend.Degrading, reading.Trend);
        Assert.True(reading.ShouldEscalate);
        Assert.True(reading.Ratio >= ProgressMeter.DegradingRatio);

        // And the novelty check on its own does not: there is still something being learned, and
        // the commands are all different.
        Assert.False(EscalationLadder.ShouldEscalate(attempts, plateauThreshold: 3));
    }

    [Fact]
    public void Getting_cheaper_is_reported_as_improving_and_left_alone()
    {
        var reading = ProgressMeter.Assess(Run(
            (90_000, true), (90_000, false), (90_000, false),
            (95_000, true), (96_000, true), (97_000, true)));

        Assert.Equal(ProgressTrend.Improving, reading.Trend);
        Assert.False(reading.ShouldEscalate);
    }

    [Fact]
    public void A_run_that_starts_badly_and_begins_producing_is_not_punished()
    {
        var reading = ProgressMeter.Assess(Run(
            (10_000, false), (11_000, false), (12_000, false),
            (13_000, true), (14_000, true), (15_000, true)));

        Assert.Equal(ProgressTrend.Improving, reading.Trend);
        Assert.Equal(3, reading.RecentFindings);
    }

    [Fact]
    public void Declared_and_refused_attempts_are_not_costs()
    {
        // A declaration has not happened yet, and a refusal never reached a model — it cost a
        // round trip, not a turn. Counting either would make a run look expensive for arguing
        // with itself.
        var attempts = Run(
            (10_000, true), (10_000, true), (10_000, true),
            (10_000, true), (10_000, true), (10_000, true));

        attempts.Add(Attempt(90, 999_999, false, AttemptOutcome.Duplicate));
        attempts.Add(Attempt(91, 999_999, false, AttemptOutcome.BlockedByScope));
        attempts.Add(Attempt(92, 999_999, false, AttemptOutcome.Declared));

        var reading = ProgressMeter.Assess(attempts);

        Assert.Equal(6, reading.SampleSize);
        Assert.Equal(ProgressTrend.Steady, reading.Trend);
    }

    [Fact]
    public void Without_token_readings_it_falls_back_to_wall_clock()
    {
        // An unmeasurable run is not a free one. Treating a missing reading as zero would make it
        // look like every finding was costless.
        var attempts = Enumerable.Range(0, 6).Select(i => new Attempt
        {
            Id = $"a{i}",
            MissionId = "m1",
            AgentId = "codex",
            Hypothesis = $"idea {i}",
            Command = $"{Tools[i % Tools.Length]} --probe beta",
            Outcome = AttemptOutcome.Failed,
            Observation = i < 3 ? "learned something" : null,
            Duration = TimeSpan.FromSeconds(i < 3 ? 10 : 300),
        }).ToList();

        var reading = ProgressMeter.Assess(attempts);

        Assert.Equal(CostUnit.Seconds, reading.Unit);
        Assert.Equal(ProgressTrend.Stalled, reading.Trend);
    }

    [Fact]
    public void With_neither_cost_signal_it_has_no_opinion()
    {
        var attempts = Enumerable.Range(0, 6).Select(i => new Attempt
        {
            Id = $"a{i}",
            MissionId = "m1",
            AgentId = "hermes",
            Hypothesis = $"idea {i}",
            Command = $"{Tools[i % Tools.Length]} --probe gamma",
            Outcome = AttemptOutcome.Failed,
            Observation = "learned something",
        }).ToList();

        var reading = ProgressMeter.Assess(attempts);

        Assert.Equal(CostUnit.None, reading.Unit);
        Assert.Equal(ProgressTrend.Unknown, reading.Trend);
        Assert.False(reading.ShouldEscalate);
    }

    [Fact]
    public void Reaching_the_objective_counts_as_a_finding()
    {
        var attempts = Run(
            (10_000, false), (10_000, false), (10_000, false),
            (10_000, false), (10_000, false));

        attempts.Add(Attempt(5, 10_000, informative: false, AttemptOutcome.Succeeded));

        var reading = ProgressMeter.Assess(attempts);

        Assert.True(reading.RecentFindings >= 1);
        Assert.NotEqual(ProgressTrend.Stalled, reading.Trend);
    }

    [Fact]
    public void The_verdict_names_the_prices_rather_than_just_the_trend()
    {
        // It goes into the escalation log, where "degrading" alone tells the operator nothing they
        // can check.
        var reading = ProgressMeter.Assess(Run(
            (5_000, true), (5_000, true), (5_000, true),
            (60_000, false), (60_000, false), (60_000, true)));

        Assert.Contains("tokens", reading.Verdict, StringComparison.Ordinal);
        Assert.Contains("×", reading.Verdict, StringComparison.Ordinal);
    }
}
