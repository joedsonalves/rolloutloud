using System.Text.Json;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Money;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// What a run costs, read from the agent's own transcript and priced from pricing.json. A figure
/// the operator reads — never a stop condition, since the spend cap and the wall clock were both
/// taken out. The arithmetic still has to be right: a bill nobody can trust is worse than none.
/// </summary>
public sealed class SpendTests : IDisposable
{
    private readonly string _root;
    private readonly string _projects;

    public SpendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rlspend-" + Guid.NewGuid().ToString("N")[..8]);
        _projects = Path.Combine(_root, "projects");
        Directory.CreateDirectory(_projects);
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

    // ---- the price table --------------------------------------------------------------------

    [Fact]
    public void The_four_token_kinds_are_priced_apart()
    {
        // Lumping them is the mistake that makes a bill meaningless: a cached run is mostly cache
        // reads, and pricing those as input overstates it by close to an order of magnitude.
        var opus = TokenPrices.Default.For("claude-opus-4-5-20260514")!;

        var asInput = opus.Cost(1_000_000, 0, 0, 0);
        var asCacheRead = opus.Cost(0, 0, 0, 1_000_000);

        Assert.True(asInput > asCacheRead * 5, $"input {asInput} vs cache read {asCacheRead}");
        Assert.True(opus.Cost(0, 1_000_000, 0, 0) > asInput);
    }

    [Fact]
    public void A_dated_build_is_priced_by_its_family()
    {
        // Transcripts carry ids like claude-opus-4-5-20260514. An exact-match table would go blind
        // on the first date bump, which is when it is least noticeable and most expensive.
        Assert.NotNull(TokenPrices.Default.For("claude-sonnet-5-20260101"));
        Assert.NotNull(TokenPrices.Default.For("claude-haiku-4-5-20251001"));
    }

    [Fact]
    public void The_longest_matching_prefix_wins()
    {
        // So an operator can price one build differently from its family without the family entry
        // shadowing it.
        var prices = new TokenPrices(
        [
            new ModelPrice
            {
                Model = "claude-opus",
                InputPerMillion = 15m, OutputPerMillion = 75m,
                CacheWritePerMillion = 18.75m, CacheReadPerMillion = 1.5m,
            },
            new ModelPrice
            {
                Model = "claude-opus-9",
                InputPerMillion = 99m, OutputPerMillion = 99m,
                CacheWritePerMillion = 99m, CacheReadPerMillion = 99m,
            },
        ]);

        Assert.Equal(99m, prices.For("claude-opus-9-20261201")!.InputPerMillion);
        Assert.Equal(15m, prices.For("claude-opus-4-20260101")!.InputPerMillion);
    }

    [Fact]
    public void An_unknown_model_has_no_price_rather_than_a_guessed_one()
    {
        // A made-up rate produces a bill with no relationship to reality and a cap that fires at
        // random. The tokens are counted and reported separately instead.
        Assert.Null(TokenPrices.Default.For("some-other-vendor-model"));
        Assert.Null(TokenPrices.Default.For(null));
    }

    [Fact]
    public void A_broken_price_file_falls_back_to_the_published_rates()
    {
        // The opposite call from the allowlist, and deliberately so. An unreadable allowlist must
        // grant nothing, because the risk is a command running. An unreadable price list has the
        // other shape: refusing to price anything would silently disable the cap the operator set.
        var path = Path.Combine(_root, "pricing.json");

        File.WriteAllText(path, "{ this is not json");
        Assert.Equal(TokenPrices.Default.All.Count, TokenPrices.Load(path).All.Count);

        File.WriteAllText(path, """{ "models": [] }""");
        Assert.Equal(TokenPrices.Default.All.Count, TokenPrices.Load(path).All.Count);

        Assert.Equal(TokenPrices.Default.All.Count, TokenPrices.Load(Path.Combine(_root, "gone.json")).All.Count);
    }

    [Fact]
    public void The_operator_can_correct_a_price_that_aged()
    {
        var path = Path.Combine(_root, "pricing.json");
        TokenPrices.WriteDefaults(path);

        var written = JsonDocument.Parse(File.ReadAllText(path));
        Assert.NotEmpty(written.RootElement.GetProperty("models").EnumerateArray());

        File.WriteAllText(path, """
            { "models": [ { "model": "claude-opus", "inputPerMillion": 1,
                            "outputPerMillion": 2, "cacheWritePerMillion": 3,
                            "cacheReadPerMillion": 4 } ] }
            """);

        Assert.Equal(1m, TokenPrices.Load(path).For("claude-opus-4-5")!.InputPerMillion);
    }

    // ---- reading a transcript ---------------------------------------------------------------

    private string WriteTranscript(string name, params string[] lines)
    {
        var directory = Path.Combine(_projects, "slug");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Turn(string model, long input, long output, long write, long read, string? requestId = null)
    {
        var id = requestId is null ? string.Empty : $"""  "requestId": "{requestId}",""";

        return $$"""
            { {{id}} "cwd": "{{CwdJson}}", "message": { "model": "{{model}}", "usage": {
              "input_tokens": {{input}}, "output_tokens": {{output}},
              "cache_creation_input_tokens": {{write}}, "cache_read_input_tokens": {{read}} } } }
            """.ReplaceLineEndings(" ");
    }

    private static string CwdJson => Cwd.Replace("\\", "\\\\");

    private static string Cwd { get; } = Path.Combine(Path.GetTempPath(), "repo-under-test");

    [Fact]
    public void Spend_is_every_turn_added_up_not_the_last_one()
    {
        // ⚠️ The distinction this whole class exists for. The context window is a LEVEL — the last
        // usage block. The bill is an INTEGRAL — every block, summed. Reading the last one and
        // calling it the cost reports a six-hour run as costing one turn.
        WriteTranscript(
            "a.jsonl",
            Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r1"),
            Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r2"),
            Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r3"));

        var reading = new ClaudeCodeSpendProbe(_projects).TryRead(Cwd, TokenPrices.Default, null);

        Assert.NotNull(reading);
        Assert.Equal(SpendSource.Measured, reading.Source);

        // Three million input tokens of Opus at $15/M.
        Assert.Equal(45m, reading.Usd);
    }

    [Fact]
    public void A_retried_turn_is_charged_once()
    {
        // ⚠️ Claude Code writes an entry per streamed message, and an interrupted-then-resumed turn
        // can appear twice with the same usage. Adding blindly double-charges exactly the sessions
        // that had trouble — which are the long ones, which are the ones a cap is for.
        WriteTranscript(
            "a.jsonl",
            Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "same"),
            Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "same"));

        var reading = new ClaudeCodeSpendProbe(_projects).TryRead(Cwd, TokenPrices.Default, null);

        Assert.Equal(15m, reading!.Usd);
    }

    [Fact]
    public void Output_tokens_are_charged_even_though_they_never_enter_the_window()
    {
        // The context meter leaves output out on purpose — it is not what the model had to read.
        // The bill must include it, because it was paid for.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 0, 1_000_000, 0, 0, "r1"));

        Assert.Equal(75m, new ClaudeCodeSpendProbe(_projects).TryRead(Cwd, TokenPrices.Default, null)!.Usd);
    }

    [Fact]
    public void Tokens_from_a_model_with_no_price_are_reported_rather_than_dropped()
    {
        // A bill that quietly omits a model it could not price is worse than one that admits the
        // gap: the operator reads a small number, trusts it, and the cap never fires.
        WriteTranscript(
            "a.jsonl",
            Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r1"),
            Turn("some-other-vendor", 500_000, 0, 0, 0, "r2"));

        var reading = new ClaudeCodeSpendProbe(_projects).TryRead(Cwd, TokenPrices.Default, null)!;

        Assert.Equal(15m, reading.Usd);
        Assert.Equal(500_000, reading.UnpricedTokens);
        Assert.Contains("no price", reading.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mission_spanning_several_sessions_is_charged_for_all_of_them()
    {
        // Charging only the newest transcript would under-report a long run by however many times
        // it was resumed — and resuming is a thing this product does on purpose.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r1"));
        WriteTranscript("b.jsonl", Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r2"));

        Assert.Equal(30m, new ClaudeCodeSpendProbe(_projects).TryRead(Cwd, TokenPrices.Default, null)!.Usd);
    }

    [Fact]
    public void A_readable_transcript_with_nothing_charged_yet_is_a_measured_zero()
    {
        // ⚠️ Found by running it, not by reading the diff. Treating "no turns since the mission
        // started" as unreadable sends the meter to its estimate, which prices the whole
        // accumulated context window — hours of an existing session — and charges it to a mission
        // that opened a second ago. A fresh mission with a $5 cap exhausted instantly at an
        // estimated $5.14, before making a single attempt.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "old"));

        var reading = new ClaudeCodeSpendProbe(_projects)
            .TryRead(Cwd, TokenPrices.Default, DateTimeOffset.UtcNow.AddYears(1));

        Assert.NotNull(reading);
        Assert.Equal(SpendSource.Measured, reading.Source);
        Assert.Equal(0m, reading.Usd);
    }

    [Fact]
    public void A_brand_new_mission_is_not_charged_for_the_session_that_preceded_it()
    {
        // Read from the mission's own start, not from now-minus-something. Turns burned before it
        // opened belong to whatever the operator was doing beforehand, and billing them to the
        // mission shows a run that has made no attempt yet already costing money.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "old"));

        var mission = MissionWith() with { StartedAt = DateTimeOffset.UtcNow.AddYears(1) };

        Assert.Equal(0m, Meter().Read(mission.AgentId, Cwd, mission.StartedAt).Usd);
    }

    [Fact]
    public void Nothing_readable_is_no_figure_rather_than_zero()
    {
        // Zero would read as "this run is free", which is the one answer that is never right.
        var reading = new ClaudeCodeSpendProbe(Path.Combine(_root, "nowhere"))
            .TryRead(Cwd, TokenPrices.Default, null);

        Assert.Null(reading);
        Assert.False(SpendReading.Unknown.HasNumber);
        Assert.DoesNotContain("$0", SpendReading.Unknown.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_half_written_line_does_not_lose_the_whole_transcript()
    {
        WriteTranscript(
            "a.jsonl",
            Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r1"),
            """{ "message": { "usage": { "input_tok""");

        Assert.Equal(15m, new ClaudeCodeSpendProbe(_projects).TryRead(Cwd, TokenPrices.Default, null)!.Usd);
    }

    // ---- what the reading is, and what it no longer does ------------------------------------

    private static Mission MissionWith() => new()
    {
        Id = "m1",
        Objective = "spend something",
        AgentId = "claude",
        StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
        Stop = new StopConditions(),
    };

    private SpendMeter Meter() =>
        new(() => TokenPrices.Default, [new ClaudeCodeSpendProbe(_projects)]);

    private RolloutLoud.Core.Workspace.RolloutPaths Paths() =>
        new(Path.Combine(_root, "engine"));

    private MissionStore Store() => new(Paths());

    [Fact]
    public void A_measured_transcript_produces_a_figure_marked_measured()
    {
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 100_000, 0, 0, 0, "r1"));

        var reading = Meter().Read("claude", Cwd, DateTimeOffset.UtcNow.AddHours(-1));

        Assert.True(reading.Usd > 0m);
        Assert.True(reading.IsMeasured);
    }

    [Fact]
    public void The_estimate_is_labelled_a_floor_rather_than_a_bill()
    {
        var reading = new SpendMeter(() => TokenPrices.Default, []).Estimate(1_000_000);

        Assert.Equal(SpendSource.Estimated, reading.Source);
        Assert.StartsWith("~$", reading.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expensive_run_is_not_stopped_by_what_it_cost()
    {
        // ⚠️ The regression this file exists to catch now that the spend cap is gone. A cost that
        // ends a run is a stop the operator did not choose in the terms the work is made of, and
        // it hides well: the mission reads Exhausted with a plausible resolution either way.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 100_000_000, 0, 0, 0, "r1"));

        var mission = MissionWith() with { State = MissionState.Running };
        var engine = new MissionEngine(mission, new MissionLedger(mission.Id), Store(), Paths());

        var decision = engine.ShouldContinue();

        Assert.True(decision.Continue);
        Assert.Equal(MissionState.Running, engine.Mission.State);
    }

    [Fact]
    public void A_run_that_started_days_ago_is_not_stopped_by_the_clock()
    {
        // The other half of the same removal. A mission left running overnight is the case the
        // wall clock used to end, and ending it is what the operator asked us to stop doing.
        var mission = MissionWith() with
        {
            State = MissionState.Running,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-4),
        };

        var engine = new MissionEngine(mission, new MissionLedger(mission.Id), Store(), Paths());

        Assert.True(engine.ShouldContinue().Continue);
        Assert.Equal(MissionState.Running, engine.Mission.State);
    }

    [Fact]
    public void The_attempt_cap_is_the_one_stop_condition_left()
    {
        // Removing two brakes is only safe while the third still works. Without this the mission
        // has nothing at all between it and a loop that never ends.
        var mission = MissionWith() with
        {
            State = MissionState.Running,
            Stop = new StopConditions { MaxAttempts = 2 },
        };

        var ledger = new MissionLedger(mission.Id);
        ledger.Record(Tried("a1", "the obvious thing"));
        ledger.Record(Tried("a2", "the next obvious thing"));

        var engine = new MissionEngine(mission, ledger, Store(), Paths());

        Assert.False(engine.ShouldContinue().Continue);
        Assert.Equal(MissionState.Exhausted, engine.Mission.State);
    }

    private static Attempt Tried(string id, string hypothesis) => new()
    {
        Id = id,
        MissionId = "m1",
        AgentId = "claude",
        Hypothesis = hypothesis,
        Command = "cmd /c exit 1",
        ExitCode = 1,
    };
}
