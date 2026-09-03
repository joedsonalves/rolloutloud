using RolloutLoud.Core.Offload;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The parser is deliberately forgiving, and these cover why.
/// </summary>
/// <remarks>
/// A subagent round has already been paid for by the time this runs — the model ran, the command
/// ran — so discarding the answer over its formatting throws away both the money and the
/// information. And a parser that fails often turns the barren-round counter into a formatting
/// detector: three prose answers in a row would stop a mission that was working fine.
/// </remarks>
public class VerdictParserTests
{
    private const string WellFormed = """
        HYPOTHESIS: The fixture writes LF and the assertion expects CRLF
        COMMAND:    dotnet test tests/Integration --filter Category=Fixtures
        OUTCOME:    failed
        LEARNED:    Green with CRLF forced too, so line endings are ruled out.
        NEXT:       Look at the temp directory the fixture writes into
        """;

    [Fact]
    public void The_documented_shape_parses_field_by_field()
    {
        var verdict = VerdictParser.Parse(WellFormed);

        Assert.True(verdict.WellFormed);
        Assert.Equal("failed", verdict.Outcome);
        Assert.Contains("expects CRLF", verdict.Hypothesis, StringComparison.Ordinal);
        Assert.Contains("ruled out", verdict.Learned, StringComparison.Ordinal);
        Assert.Contains("temp directory", verdict.Next!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_fence_around_it_changes_nothing()
    {
        // Agents wrap the block in a fence constantly. Anchoring to the start of input would find
        // none of those.
        var verdict = VerdictParser.Parse("Here is my answer:\n\n```\n" + WellFormed + "\n```\n");

        Assert.True(verdict.WellFormed);
        Assert.Equal("failed", verdict.Outcome);
    }

    [Fact]
    public void Bullets_and_lower_case_labels_still_parse()
    {
        var verdict = VerdictParser.Parse("""
            - hypothesis: the cache is stale between runs
            - outcome: succeeded
            - learned: clearing it made the suite green twice in a row
            """);

        Assert.True(verdict.WellFormed);
        Assert.Equal("succeeded", verdict.Outcome);
    }

    [Fact]
    public void Prose_is_salvaged_rather_than_discarded()
    {
        var verdict = VerdictParser.Parse(
            "I looked into the fixture. The writer is fine on both platforms, but the assertion " +
            "compares against a golden file committed with CRLF, so line endings are a red herring.");

        Assert.False(verdict.WellFormed);
        Assert.Contains("red herring", verdict.Learned, StringComparison.Ordinal);
        Assert.Contains("unformatted", verdict.Learned, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unstated_outcome_reads_as_failed()
    {
        // The honest default. A subagent that did not say it succeeded did not succeed, and
        // recording an unclear round as a win is the one mistake this whole product prevents.
        var verdict = VerdictParser.Parse("""
            HYPOTHESIS: something
            LEARNED:    something else
            """);

        Assert.Equal("failed", verdict.Outcome);
    }

    [Fact]
    public void An_unfilled_template_is_not_mistaken_for_an_answer()
    {
        // A subagent that echoes the placeholder back has told you nothing, and treating
        // "<what you expected, one line>" as a hypothesis would put a stub in the ledger where a
        // ruled-out theory should be.
        var verdict = VerdictParser.Parse("""
            HYPOTHESIS: <what you expected, one line>
            LEARNED:    <what this rules out>
            """);

        Assert.False(verdict.WellFormed);
    }

    [Fact]
    public void Nothing_at_all_is_an_error_rather_than_a_failure()
    {
        // Silence says nothing about the idea, so it must not be filed as one that was ruled out.
        var verdict = VerdictParser.Parse("");

        Assert.Equal("errored", verdict.Outcome);
        Assert.False(verdict.WellFormed);
    }

    [Fact]
    public void Next_saying_none_becomes_no_next_step()
    {
        var verdict = VerdictParser.Parse(WellFormed.Replace(
            "Look at the temp directory the fixture writes into", "none", StringComparison.Ordinal));

        Assert.Null(verdict.Next);
    }

    [Fact]
    public void The_compact_line_carries_the_outcome_and_the_learning()
    {
        // This is what reaches the main agent's context, and the only thing that should.
        var compact = VerdictParser.Parse(WellFormed).Compact;

        Assert.Contains("[failed]", compact, StringComparison.Ordinal);
        Assert.Contains("ruled out", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("HYPOTHESIS:", compact, StringComparison.Ordinal);
        Assert.True(compact.Length < 400, "the compact line has to stay compact");
    }
}
