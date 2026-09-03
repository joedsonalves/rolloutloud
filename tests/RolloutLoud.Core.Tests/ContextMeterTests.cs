using RolloutLoud.Core.Context;
using RolloutLoud.Core.Missions;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The threshold trigger was dead code: ShouldOffload was written, offered in the window and on
/// the bridge, and called by nothing. The briefing made it worse by asking the agent to judge its
/// own context size — the one thing this product exists to take away from it.
/// </summary>
public sealed class ContextMeterTests : IDisposable
{
    private readonly string _projects;
    private readonly string _repository;

    public ContextMeterTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "rlctx-" + Guid.NewGuid().ToString("N")[..8]);
        _projects = Path.Combine(root, "projects");
        _repository = Path.Combine(root, "repo");

        Directory.CreateDirectory(_projects);
        Directory.CreateDirectory(_repository);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_projects)!, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a run over.
        }
    }

    private void WriteTranscript(string slug, params string[] lines)
    {
        var directory = Path.Combine(_projects, slug);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "session.jsonl"), lines);
    }

    /// <summary>One assistant entry in the shape Claude Code actually writes.</summary>
    private string Entry(int input, int cacheRead, int cacheCreate, int output = 100) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            cwd = _repository,
            message = new
            {
                usage = new
                {
                    input_tokens = input,
                    cache_read_input_tokens = cacheRead,
                    cache_creation_input_tokens = cacheCreate,
                    output_tokens = output,
                },
            },
        });

    [Fact]
    public void The_window_is_input_plus_both_cache_figures()
    {
        // Cache reads dominate a long session. Leaving them out understates the window by an order
        // of magnitude — which is the entire quantity being asked about.
        WriteTranscript(ClaudeCodeProbe.Slug(_repository), Entry(input: 2, cacheRead: 953_105, cacheCreate: 6_580));

        var reading = new ClaudeCodeProbe(_projects).TryRead(_repository);

        Assert.NotNull(reading);
        Assert.Equal(959_687, reading!.Tokens);
        Assert.True(reading.IsMeasured);
    }

    [Fact]
    public void Output_tokens_are_not_part_of_the_window()
    {
        // Output is what the model produced, not what it had to read. Counting it would inflate
        // the number that decides whether to offload.
        WriteTranscript(ClaudeCodeProbe.Slug(_repository), Entry(input: 1000, cacheRead: 0, cacheCreate: 0, output: 50_000));

        Assert.Equal(1000, new ClaudeCodeProbe(_projects).TryRead(_repository)!.Tokens);
    }

    [Fact]
    public void A_trailing_all_zero_entry_does_not_read_as_an_empty_window()
    {
        // Transcripts end with a zeroed usage block as the session closes. Taking that literally
        // would report nothing for a session that had just spent a million tokens.
        WriteTranscript(
            ClaudeCodeProbe.Slug(_repository),
            Entry(input: 5, cacheRead: 400_000, cacheCreate: 1_000),
            Entry(input: 0, cacheRead: 0, cacheCreate: 0, output: 0));

        Assert.Equal(401_005, new ClaudeCodeProbe(_projects).TryRead(_repository)!.Tokens);
    }

    [Fact]
    public void A_half_written_last_line_is_skipped_rather_than_fatal()
    {
        // The transcript of a live session is being appended to while this reads it.
        var directory = Path.Combine(_projects, ClaudeCodeProbe.Slug(_repository));
        Directory.CreateDirectory(directory);

        File.WriteAllLines(
            Path.Combine(directory, "session.jsonl"),
            [Entry(input: 10, cacheRead: 90, cacheCreate: 0), "{\"type\":\"assis"]);

        Assert.Equal(100, new ClaudeCodeProbe(_projects).TryRead(_repository)!.Tokens);
    }

    [Fact]
    public void A_transcript_for_a_different_repository_is_not_used()
    {
        // The slug rule is inferred rather than documented, so the directory is confirmed against
        // the cwd inside the transcript. A wrong answer here would report somebody else's window.
        var directory = Path.Combine(_projects, "C--somewhere-else");
        Directory.CreateDirectory(directory);

        File.WriteAllLines(
            Path.Combine(directory, "session.jsonl"),
            ["""{"type":"assistant","cwd":"C:\\elsewhere","message":{"usage":{"input_tokens":500000}}}"""]);

        Assert.Null(new ClaudeCodeProbe(_projects).TryRead(_repository));
    }

    [Fact]
    public void A_renamed_slug_is_still_found_by_scanning()
    {
        // If the slug rule changes upstream, the fallback finds it by cwd — a slower lookup rather
        // than a silently wrong answer.
        WriteTranscript("something-the-rule-would-never-produce", Entry(input: 100, cacheRead: 900, cacheCreate: 0));

        var reading = new ClaudeCodeProbe(_projects).TryRead(_repository);

        Assert.NotNull(reading);
        Assert.Equal(1000, reading!.Tokens);
    }

    [Fact]
    public void With_no_transcript_it_estimates_from_what_was_sent()
    {
        var meter = new ContextMeter([new ClaudeCodeProbe(_projects)]);

        meter.RecordSent("claude", new string('x', 40_000));

        var reading = meter.Read("claude", _repository);

        Assert.Equal(ContextSource.Estimated, reading.Source);
        Assert.Equal(10_000, reading.Tokens);
    }

    [Fact]
    public void With_nothing_at_all_it_says_so_rather_than_guessing()
    {
        var reading = new ContextMeter([new ClaudeCodeProbe(_projects)]).Read("claude", _repository);

        Assert.Equal(ContextSource.Unknown, reading.Source);
        Assert.False(reading.HasNumber);
    }

    [Fact]
    public void A_measured_reading_beats_the_running_estimate()
    {
        // RolloutLoud only sees its own half of an interactive conversation, so its estimate is a
        // floor. When the CLI's own record is available, that is the answer.
        WriteTranscript(ClaudeCodeProbe.Slug(_repository), Entry(input: 0, cacheRead: 500_000, cacheCreate: 0));

        var meter = new ContextMeter([new ClaudeCodeProbe(_projects)]);
        meter.RecordSent("claude", new string('x', 4_000));

        var reading = meter.Read("claude", _repository);

        Assert.True(reading.IsMeasured);
        Assert.Equal(500_000, reading.Tokens);
    }

    [Fact]
    public void A_launch_resets_the_running_estimate()
    {
        var meter = new ContextMeter([new ClaudeCodeProbe(_projects)]);

        meter.RecordSent("claude", new string('x', 40_000));
        meter.Reset("claude");

        Assert.False(meter.Read("claude", _repository).HasNumber);
    }

    [Theory]
    [InlineData(OffloadTrigger.Off, false)]
    [InlineData(OffloadTrigger.Always, true)]
    public void The_two_unconditional_triggers_do_not_need_a_reading(OffloadTrigger trigger, bool expected)
    {
        var mission = Mission() with { Offload = new OffloadSettings { Trigger = trigger } };

        var decision = new ContextMeter([new ClaudeCodeProbe(_projects)]).ShouldOffload(mission, _repository);

        Assert.Equal(expected, decision.Offload);
    }

    [Fact]
    public void Past_the_threshold_it_says_to_offload()
    {
        WriteTranscript(ClaudeCodeProbe.Slug(_repository), Entry(input: 0, cacheRead: 200_000, cacheCreate: 0));

        var mission = Mission() with
        {
            Offload = new OffloadSettings { Trigger = OffloadTrigger.ContextThreshold, TokenThreshold = 120_000 },
        };

        var decision = new ContextMeter([new ClaudeCodeProbe(_projects)]).ShouldOffload(mission, _repository);

        Assert.True(decision.Offload);
        Assert.True(decision.Reading.IsMeasured);
    }

    [Fact]
    public void Under_the_threshold_it_says_to_carry_on()
    {
        WriteTranscript(ClaudeCodeProbe.Slug(_repository), Entry(input: 0, cacheRead: 50_000, cacheCreate: 0));

        var mission = Mission() with
        {
            Offload = new OffloadSettings { Trigger = OffloadTrigger.ContextThreshold, TokenThreshold = 120_000 },
        };

        Assert.False(new ContextMeter([new ClaudeCodeProbe(_projects)]).ShouldOffload(mission, _repository).Offload);
    }

    [Fact]
    public void With_no_reading_the_threshold_trigger_does_not_offload()
    {
        // Guessing "probably expensive by now" would send every action through a subagent from the
        // first turn — which is what Always is for, and the operator chose otherwise.
        var mission = Mission() with
        {
            Offload = new OffloadSettings { Trigger = OffloadTrigger.ContextThreshold },
        };

        var decision = new ContextMeter([new ClaudeCodeProbe(_projects)]).ShouldOffload(mission, _repository);

        Assert.False(decision.Offload);
        Assert.False(decision.Reading.HasNumber);
    }

    private static Mission Mission() => new()
    {
        Id = "m1",
        Objective = "measure the window",
        AgentId = "claude",
    };
}
