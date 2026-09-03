using System.Text.Json;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Money;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The attempt cap counts moves and the wall clock counts minutes. Neither notices that a six-hour
/// run with offload on can make twenty expensive attempts instead of a hundred cheap ones, which is
/// the only one of the three the operator feels.
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
        // The same bug from the brake's side: the cap must not fire before the mission has spent
        // anything, however large the window already was.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "old"));

        var mission = MissionWith(1m) with { StartedAt = DateTimeOffset.UtcNow.AddYears(1) };
        var verdict = Meter().Evaluate(mission, Cwd, estimatedTokens: 900_000_000);

        Assert.False(verdict.OverBudget);
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

    // ---- the brake --------------------------------------------------------------------------

    private static Mission MissionWith(decimal? cap) => new()
    {
        Id = "m1",
        Objective = "spend something",
        AgentId = "claude",
        StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
        Stop = new StopConditions { MaxSpendUsd = cap },
    };

    private SpendMeter Meter() =>
        new(() => TokenPrices.Default, [new ClaudeCodeSpendProbe(_projects)]);

    private RolloutLoud.Core.Workspace.RolloutPaths Paths() =>
        new(Path.Combine(_root, "engine"));

    private MissionStore Store() => new(Paths());

    [Fact]
    public void No_cap_means_the_brake_never_fires()
    {
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 100_000_000, 0, 0, 0, "r1"));

        Assert.False(Meter().Evaluate(MissionWith(null), Cwd).OverBudget);
        Assert.False(Meter().Evaluate(MissionWith(0m), Cwd).OverBudget);
    }

    [Fact]
    public void Under_the_cap_the_run_carries_on()
    {
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 100_000, 0, 0, 0, "r1"));

        var verdict = Meter().Evaluate(MissionWith(50m), Cwd);

        Assert.False(verdict.OverBudget);
        Assert.True(verdict.Reading.Usd > 0m);
    }

    [Fact]
    public void Over_the_cap_it_stops_and_says_it_was_measured()
    {
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 4_000_000, 0, 0, 0, "r1"));

        var verdict = Meter().Evaluate(MissionWith(50m), Cwd);

        Assert.True(verdict.OverBudget);
        Assert.Contains("measured", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("resume", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_transcript_the_cap_still_fires_and_says_it_was_a_guess()
    {
        // The decision worth defending, and it is the opposite of the context meter's. Failing open
        // spends real money that cannot be got back; failing closed costs one `rollout resume`,
        // which already exists. So the brake fires on whatever number it has — and the reason says
        // which kind, so an operator who thinks it is high knows to raise the cap rather than to
        // stop trusting the tool.
        var meter = new SpendMeter(() => TokenPrices.Default, [new ClaudeCodeSpendProbe(Path.Combine(_root, "nowhere"))]);

        var verdict = meter.Evaluate(MissionWith(1m), Cwd, estimatedTokens: 10_000_000);

        Assert.True(verdict.OverBudget);
        Assert.Contains("ESTIMATE", verdict.Reason, StringComparison.Ordinal);
        Assert.False(verdict.Reading.IsMeasured);
    }

    [Fact]
    public void A_cap_under_a_cent_is_not_rounded_into_reading_as_zero()
    {
        // ⚠️ Two decimals is right for money and wrong for this message: a $0.001 cap formatted as
        // N2 says the run was stopped at a budget of "$0.00", which sends the operator hunting for
        // a bug in the brake instead of looking at the figure they typed.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 1_000_000, 0, 0, 0, "r1"));

        var reason = Meter().Evaluate(MissionWith(0.001m), Cwd).Reason;

        Assert.Contains("$0.001", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("of $0.00,", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_measured_figure_wins_over_the_estimate_when_both_exist()
    {
        // An estimate standing in for a measurement that was available would be a worse number
        // presented with the same confidence.
        WriteTranscript("a.jsonl", Turn("claude-opus-4-5", 100_000, 0, 0, 0, "r1"));

        var verdict = Meter().Evaluate(MissionWith(50m), Cwd, estimatedTokens: 900_000_000);

        Assert.False(verdict.OverBudget);
        Assert.True(verdict.Reading.IsMeasured);
    }

    [Fact]
    public void The_estimate_is_labelled_a_floor_rather_than_a_bill()
    {
        var reading = new SpendMeter(() => TokenPrices.Default, []).Estimate(1_000_000);

        Assert.Equal(SpendSource.Estimated, reading.Source);
        Assert.StartsWith("~$", reading.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_exhausts_the_mission_when_the_brake_fires()
    {
        // The brake has to reach MissionState, not just print a warning — an over-budget run that
        // keeps going is the failure this exists to prevent.
        var mission = MissionWith(50m) with { State = MissionState.Running };
        var engine = new MissionEngine(mission, new MissionLedger(mission.Id), Store(), Paths())
        {
            ReadSpend = _ => new BudgetVerdict
            {
                OverBudget = true,
                Reason = "Spend cap reached: $60.00 of $50.00, measured.",
                Reading = SpendReading.Unknown,
            },
        };

        var decision = engine.ShouldContinue();

        Assert.False(decision.Continue);
        Assert.Equal(MissionState.Exhausted, engine.Mission.State);
        Assert.Contains("$50.00", engine.Mission.Resolution!, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_spend_hook_the_engine_behaves_as_it_always_did()
    {
        // Left null in a test — and on a machine where nothing can be read — the money cap simply
        // does not exist rather than blocking the run.
        var mission = MissionWith(50m) with { State = MissionState.Running };
        var engine = new MissionEngine(mission, new MissionLedger(mission.Id), Store(), Paths());

        Assert.True(engine.ShouldContinue().Continue);
    }
}
