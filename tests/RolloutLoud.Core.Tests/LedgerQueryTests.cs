using RolloutLoud.Core.Missions;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The briefing caps its summary so a long run cannot flood a context. That left an agent needing
/// an older attempt with nowhere to go — and the endpoint that existed made the opposite mistake,
/// returning every attempt in full, so the only way to ask about the past was to import all of it.
/// </summary>
public class LedgerQueryTests
{
    private static readonly string[] Tools =
        ["nmap -sV", "ffuf -u", "nuclei -t", "curl -s", "dotnet test", "pytest -k",
         "grep -rn", "docker compose run", "npm run e2e", "gobuster dir"];

    private static List<Attempt> Ledger(int count = 60) =>
    [
        .. Enumerable.Range(0, count).Select(i => new Attempt
        {
            Id = $"a{i:000}",
            MissionId = "m1",
            AgentId = i % 3 == 0 ? "codex" : "claude",
            Hypothesis = i == 7 ? "The golden file was committed with CRLF" : $"idea {i}",
            Command = $"{Tools[i % Tools.Length]} --case {i}",
            Outcome = i % 5 == 0 ? AttemptOutcome.Duplicate : AttemptOutcome.Failed,
            Observation = i % 5 == 0 ? null : $"Rules out class {i}.",
            Tier = i / 20,
            ExitCode = 1,
            ArtifactDirectory = $"/runs/a{i:000}",
            At = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
        }),
    ];

    [Fact]
    public void One_call_cannot_fetch_the_whole_ledger()
    {
        // The point of the feature. An agent that can import two hundred attempts will, and that
        // undoes offload in a single request.
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 10_000 });

        Assert.Equal(LedgerQueryResult.MaxLimit, result.Entries.Count);
        Assert.Equal(60, result.Total);
    }

    [Fact]
    public void The_default_page_is_small()
    {
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery());

        Assert.Equal(LedgerQueryResult.DefaultLimit, result.Entries.Count);
    }

    [Fact]
    public void Results_are_newest_first()
    {
        // A question about the past is almost always about the recent past. Paging from the start
        // of a long ledger to reach it would cost several calls to arrive at what the first should
        // have returned.
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 3 });

        Assert.Equal("a059", result.Entries[0].Id);
        Assert.Equal("a058", result.Entries[1].Id);
    }

    [Fact]
    public void Commands_and_artifact_paths_are_left_out_unless_asked_for()
    {
        // "What has been ruled out" almost never needs the exact argv, and sending it costs the
        // caller context for nothing.
        var lean = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 1 }).Entries[0];

        Assert.Null(lean.Command);
        Assert.Null(lean.Artifacts);
        Assert.Null(lean.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(lean.Hypothesis));

        var full = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 1, Full = true }).Entries[0];

        Assert.NotNull(full.Command);
        Assert.NotNull(full.Artifacts);
    }

    [Fact]
    public void A_text_search_finds_the_one_attempt_that_matters()
    {
        // The case A8 exists for: an agent remembers something was tried and needs that one entry,
        // not the forty around it.
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Contains = "golden file" });

        Assert.Single(result.Entries);
        Assert.Equal("a007", result.Entries[0].Id);
    }

    [Fact]
    public void The_search_covers_the_command_and_the_observation_too()
    {
        Assert.NotEmpty(LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Contains = "gobuster" }).Entries);
        Assert.NotEmpty(LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Contains = "Rules out class 12" }).Entries);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("duplicate")]
    public void Filtering_by_outcome_works(string outcome)
    {
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Outcome = outcome, Limit = 50 });

        Assert.NotEmpty(result.Entries);
        Assert.All(result.Entries, e => Assert.Equal(outcome, e.Outcome, ignoreCase: true));
    }

    [Fact]
    public void Blocked_is_accepted_as_a_name_for_BlockedByScope()
    {
        // The bridge tells agents "blocked"; the enum says BlockedByScope. Making them type the
        // internal name would be the tool leaking its own vocabulary.
        var attempts = Ledger(10);
        attempts.Add(attempts[0] with { Id = "blocked-1", Outcome = AttemptOutcome.BlockedByScope });

        var result = LedgerQueryRunner.Run(attempts, new LedgerQuery { Outcome = "blocked" });

        Assert.Single(result.Entries);
        Assert.Equal("blocked-1", result.Entries[0].Id);
    }

    [Fact]
    public void Filtering_by_agent_answers_what_the_previous_one_tried()
    {
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Agent = "codex", Limit = 50 });

        Assert.NotEmpty(result.Entries);
        Assert.All(result.Entries, e => Assert.Equal("codex", e.Agent));
    }

    [Fact]
    public void Filtering_by_tier_and_by_time_both_narrow()
    {
        var byTier = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Tier = 2, Limit = 50 });
        Assert.All(byTier.Entries, e => Assert.Equal(2, e.Tier));

        var since = new DateTimeOffset(2026, 9, 3, 0, 50, 0, TimeSpan.Zero);
        var byTime = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Since = since, Limit = 50 });
        Assert.All(byTime.Entries, e => Assert.True(e.At >= since));
    }

    [Fact]
    public void The_answer_says_how_many_matched_so_the_caller_can_narrow()
    {
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 5 });

        Assert.Equal(60, result.Matched);
        Assert.Contains("Narrow with", result.Guidance, StringComparison.Ordinal);
        Assert.Contains("55 older", result.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void When_everything_matching_fits_it_says_so_instead_of_nagging()
    {
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Contains = "golden file" });

        Assert.DoesNotContain("Narrow with", result.Guidance, StringComparison.Ordinal);
        Assert.Contains("All 1", result.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void No_match_is_reported_as_an_answer_rather_than_an_absence()
    {
        // "Nothing like this has been tried" is exactly what an agent asking wants to hear, and
        // an empty list with no comment reads like a failed call.
        var result = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Contains = "something nobody tried" });

        Assert.Empty(result.Entries);
        Assert.Contains("not a repeat", result.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_ledger_says_you_are_first()
    {
        var result = LedgerQueryRunner.Run([], new LedgerQuery());

        Assert.Contains("You are first", result.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filter_that_matches_nothing_is_not_reported_as_an_empty_ledger()
    {
        // The worst lie this endpoint could tell: "you are first" to an agent with sixty attempts
        // behind it, which reads as licence to try the very thing that already failed.
        var result = LedgerQueryRunner.Run(
            Ledger(), new LedgerQuery { Since = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.Empty(result.Entries);
        Assert.DoesNotContain("You are first", result.Guidance, StringComparison.Ordinal);
        Assert.Equal(60, result.Total);
    }

    [Fact]
    public void Paging_walks_backwards_through_the_matches()
    {
        var first = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 5 });
        var second = LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 5, Offset = 5 });

        Assert.Equal("a059", first.Entries[0].Id);
        Assert.Equal("a054", second.Entries[0].Id);
        Assert.Empty(first.Entries.Select(e => e.Id).Intersect(second.Entries.Select(e => e.Id)));
    }

    [Fact]
    public void A_nonsense_limit_is_clamped_rather_than_rejected()
    {
        Assert.NotEmpty(LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = 0 }).Entries);
        Assert.NotEmpty(LedgerQueryRunner.Run(Ledger(), new LedgerQuery { Limit = -5, Offset = -3 }).Entries);
    }
}
