using RolloutLoud.Core.Context;
using RolloutLoud.Core.Money;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// Codex records what the API counted, but in the opposite shape from Claude Code — and every
/// assertion here comes from arithmetic on a real transcript rather than from what the field names
/// suggest, because three of these would have compiled, passed a careless test, and lied.
/// </summary>
public sealed class CodexSpendProbeTests : IDisposable
{
    private readonly string _root;
    private readonly string _sessions;

    public CodexSpendProbeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rlcodex-" + Guid.NewGuid().ToString("N")[..8]);
        _sessions = Path.Combine(_root, "sessions", "2026", "09", "03");
        Directory.CreateDirectory(_sessions);
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

    private static string Cwd { get; } = Path.Combine(Path.GetTempPath(), "codex-repo-under-test");

    private static string CwdJson => Cwd.Replace("\\", "\\\\");

    private static string Meta() =>
        $$"""{ "timestamp": "2026-09-03T10:00:00Z", "type": "session_meta", "payload": { "cwd": "{{CwdJson}}" } }""";

    private static string TurnContext(string model) =>
        $$"""{ "timestamp": "2026-09-03T10:00:01Z", "type": "turn_context", "payload": { "cwd": "{{CwdJson}}", "model": "{{model}}" } }""";

    /// <summary>A token_count event. `total` is cumulative; `last` is that turn.</summary>
    private static string TokenCount(
        long totalInput, long totalCached, long totalOutput,
        long lastInput, long lastCached, long lastOutput,
        long window = 258400) =>
        $$"""
        { "timestamp": "2026-09-03T10:00:02Z", "type": "event_msg", "payload": { "type": "token_count", "info": {
          "total_token_usage": { "input_tokens": {{totalInput}}, "cached_input_tokens": {{totalCached}}, "output_tokens": {{totalOutput}}, "reasoning_output_tokens": 0, "total_tokens": {{totalInput + totalOutput}} },
          "last_token_usage": { "input_tokens": {{lastInput}}, "cached_input_tokens": {{lastCached}}, "output_tokens": {{lastOutput}}, "reasoning_output_tokens": 0, "total_tokens": {{lastInput + lastOutput}} },
          "model_context_window": {{window}} } } }
        """.ReplaceLineEndings(" ");

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_sessions, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static TokenPrices Prices => TokenPrices.Default;

    // ---- the shape, which is the opposite of Claude Code's -----------------------------------

    [Fact]
    public void The_running_total_is_taken_not_summed()
    {
        // ⚠️ Claude Code writes a per-turn block, so the bill is the sum. Codex writes a RUNNING
        // TOTAL, so the bill is the last one. Summing these would charge a three-turn session
        // 1+2+3 times over — and the error grows with the length of the run, which is exactly the
        // run a spend cap exists for.
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(1_000_000, 0, 0, 1_000_000, 0, 0),
            TokenCount(2_000_000, 0, 0, 1_000_000, 0, 0),
            TokenCount(3_000_000, 0, 0, 1_000_000, 0, 0));

        var reading = new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null);

        // 3M fresh input on gpt-5.5 at $1.25/M, not 6M.
        Assert.NotNull(reading);
        Assert.Equal(3.75m, reading.Usd);
    }

    [Fact]
    public void Cached_input_is_part_of_input_not_additional_to_it()
    {
        // ⚠️ `input_tokens` is the whole prompt and `cached_input_tokens` is the part of it that was
        // cached, so billable fresh input is the DIFFERENCE. Reading them as two separate charges
        // bills the cached half twice — once cheap and once at ten times the price — and on a long
        // session the cached half is nearly all of it.
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(1_000_000, 900_000, 0, 1_000_000, 900_000, 0));

        var reading = new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null)!;

        // 100k fresh @ $1.25/M + 900k cached @ $0.125/M = $0.125 + $0.1125
        Assert.Equal(0.2375m, reading.Usd);

        var model = Assert.Single(reading.ByModel);
        Assert.Equal(100_000, model.InputTokens);
        Assert.Equal(900_000, model.CacheReadTokens);
    }

    [Fact]
    public void Output_is_charged()
    {
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(0, 0, 1_000_000, 0, 0, 1_000_000));

        Assert.Equal(10m, new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null)!.Usd);
    }

    // ---- the model split ---------------------------------------------------------------------

    [Fact]
    public void A_mini_is_not_billed_as_a_full_model()
    {
        // ⚠️ The trap that longest-PREFIX matching walks into. Anthropic ids put the family first,
        // so a prefix works. OpenAI puts the variant at the END — `gpt-5.4-mini` — so a key of
        // `gpt-5` matches it happily and bills a mini at five times its rate, silently.
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.4-mini"),
            TokenCount(1_000_000, 0, 0, 1_000_000, 0, 0));

        var reading = new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null)!;

        Assert.Equal(0.25m, reading.Usd);
        Assert.Equal("gpt-5.4-mini", Assert.Single(reading.ByModel).Model);
    }

    [Fact]
    public void A_session_that_switched_models_is_split_between_them()
    {
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(1_000_000, 0, 0, 1_000_000, 0, 0),
            TurnContext("gpt-5.4-mini"),
            TokenCount(2_000_000, 0, 0, 1_000_000, 0, 0));

        var reading = new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null)!;

        Assert.Equal(2, reading.ByModel.Count);

        // A million each: $1.25 on the full model, $0.25 on the mini.
        Assert.Equal(1.50m, reading.Usd);
    }

    [Fact]
    public void The_authoritative_total_wins_over_the_summed_turns()
    {
        // ⚠️ Measured on a real two-model transcript: summing per-turn events OVERSTATED the
        // running total by 16%. Something is counted in last_token_usage that never reaches the
        // total — most likely a compaction. So the per-turn sums decide only the RATIO between
        // models, and the running total decides the amount. A wrong total is a wrong bill; a
        // slightly wrong split is a slightly wrong breakdown, which is the right way round.
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(0, 0, 0, 1_000_000, 0, 0),
            TurnContext("gpt-5.4-mini"),
            // The turns say 2M between them; the running total says 1M.
            TokenCount(1_000_000, 0, 0, 1_000_000, 0, 0));

        var reading = new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null)!;

        // Half a million each after apportioning: $0.625 + $0.125.
        Assert.Equal(0.75m, reading.Usd);
        Assert.Equal(1_000_000, reading.ByModel.Sum(m => m.TotalTokens));
    }

    // ---- picking the right session ------------------------------------------------------------

    [Fact]
    public void Another_repositorys_session_is_not_charged_to_this_one()
    {
        var other = Path.Combine(Path.GetTempPath(), "some-other-repo").Replace("\\", "\\\\");

        Write(
            "rollout-a.jsonl",
            $$"""{ "type": "session_meta", "payload": { "cwd": "{{other}}" } }""",
            $$"""{ "type": "turn_context", "payload": { "cwd": "{{other}}", "model": "gpt-5.5" } }""",
            TokenCount(9_000_000, 0, 0, 9_000_000, 0, 0));

        var reading = new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null)!;

        Assert.Equal(0m, reading.Usd);
        Assert.Equal(SpendSource.Measured, reading.Source);
    }

    [Fact]
    public void A_readable_store_with_nothing_charged_yet_is_a_measured_zero()
    {
        // Same rule as the Claude Code probe, and for the same reason: returning null here sends
        // the meter to its estimate, which prices a whole accumulated window against a mission that
        // opened a second ago.
        Write("rollout-a.jsonl", Meta(), TurnContext("gpt-5.5"), TokenCount(1_000, 0, 0, 1_000, 0, 0));

        var reading = new CodexSpendProbe(Path.Combine(_root, "sessions"))
            .TryRead(Cwd, Prices, DateTimeOffset.UtcNow.AddYears(1))!;

        Assert.Equal(SpendSource.Measured, reading.Source);
        Assert.Equal(0m, reading.Usd);
    }

    [Fact]
    public void No_codex_store_at_all_is_unknown_rather_than_free()
    {
        Assert.Null(new CodexSpendProbe(Path.Combine(_root, "nowhere")).TryRead(Cwd, Prices, null));
    }

    [Fact]
    public void A_half_written_line_does_not_lose_the_session()
    {
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(1_000_000, 0, 0, 1_000_000, 0, 0),
            """{ "type": "event_msg", "payload": { "type": "token_""");

        Assert.Equal(1.25m, new CodexSpendProbe(Path.Combine(_root, "sessions")).TryRead(Cwd, Prices, null)!.Usd);
    }

    // ---- the context probe, which asks the other question -------------------------------------

    [Fact]
    public void The_context_probe_reads_the_last_turn_not_the_running_total()
    {
        // The mirror of the spend rule. Spend is the cumulative figure; the WINDOW is what the last
        // turn had to read. Reading the running total as the window would report a long session as
        // having a window several times the model's capacity.
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(3_000_000, 2_900_000, 5_000, 129_557, 128_384, 189));

        var reading = new CodexContextProbe(Path.Combine(_root, "sessions")).TryRead(Cwd)!;

        Assert.Equal(ContextSource.Measured, reading.Source);
        Assert.Equal(129_557, reading.Tokens);
    }

    [Fact]
    public void The_context_reading_says_how_full_the_window_is()
    {
        // A number the Claude Code transcript does not carry, so it is worth surfacing where it
        // exists rather than flattening both probes to the lowest common denominator.
        Write(
            "rollout-a.jsonl",
            Meta(),
            TurnContext("gpt-5.5"),
            TokenCount(200_000, 0, 0, 129_200, 0, 0, window: 258_400));

        Assert.Contains("50%", new CodexContextProbe(Path.Combine(_root, "sessions")).TryRead(Cwd)!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_context_probe_ignores_another_repositorys_session()
    {
        var other = Path.Combine(Path.GetTempPath(), "some-other-repo").Replace("\\", "\\\\");

        Write(
            "rollout-a.jsonl",
            $$"""{ "type": "session_meta", "payload": { "cwd": "{{other}}" } }""",
            TokenCount(500_000, 0, 0, 500_000, 0, 0));

        Assert.Null(new CodexContextProbe(Path.Combine(_root, "sessions")).TryRead(Cwd));
    }
}
