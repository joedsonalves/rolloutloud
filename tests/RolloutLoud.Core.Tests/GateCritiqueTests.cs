using RolloutLoud.Core.Missions;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// Letting an agent compose its own mission means letting it compose its own success gate, and a
/// gate the agent wrote for itself is not a gate — it is the agent's opinion of done wearing a
/// command's clothes. These are the shapes that opinion takes.
/// </summary>
public class GateCritiqueTests
{
    private static GateReview Of(string command) =>
        GateCritique.Review(new SuccessGate { Kind = GateKind.Command, Command = command });

    private static bool Flags(string command, GateWeakness weakness) =>
        Of(command).Findings.Any(f => f.Weakness == weakness);

    // ---- the gate that cannot fail ---------------------------------------------------------

    [Theory]
    [InlineData("dotnet test || true")]
    [InlineData("pytest -q; true")]
    [InlineData("nuclei -u https://target ; echo done")]
    [InlineData("go test ./... || echo 'no matter'")]
    public void A_check_neutralised_by_what_follows_it_is_caught(string command)
    {
        // The dangerous one, because it reads as rigorous. The shell reports the LAST command, so
        // whatever the real check found, the gate passes and the mission is Achieved.
        Assert.True(Flags(command, GateWeakness.CannotFail), command);
        Assert.True(Of(command).HasSeriousFinding);
    }

    [Theory]
    [InlineData("exit 0")]
    [InlineData("true")]
    [InlineData("cmd /c exit 0")]
    public void A_gate_that_is_nothing_but_success_is_caught(string command) =>
        Assert.True(Flags(command, GateWeakness.CannotFail), command);

    [Fact]
    public void An_always_true_command_that_is_not_last_decides_nothing()
    {
        // `echo` before the real check is noise, not a hole. Warning about it would teach the
        // operator that this checker cries wolf, and the next warning is the one that matters.
        Assert.False(Flags("echo probing && curl -fsS https://target/health", GateWeakness.CannotFail));
    }

    // ---- the gate the agent satisfies by writing a file -------------------------------------

    [Theory]
    [InlineData("test -f REPORT.md")]
    [InlineData("[ -f findings.json ]")]
    [InlineData("Test-Path .\\out\\critical.txt")]
    [InlineData("ls artifacts/proof.png")]
    [InlineData("cat SUMMARY.md")]
    public void A_gate_that_only_asks_whether_a_file_exists_is_caught(string command)
    {
        // Writing a file is the one thing an agent can always do. This checks that a report
        // exists, not that the objective was met.
        Assert.True(Flags(command, GateWeakness.SelfCertifying), command);
        Assert.True(Of(command).HasSeriousFinding, command);
    }

    [Fact]
    public void Sudo_and_an_absolute_path_do_not_hide_it()
    {
        // ⚠️ The obvious implementation reads the verb of `sudo test -f out.txt` as "sudo" and finds
        // nothing wrong with the textbook case. A checker that misses the plain form of the thing
        // it checks for is worse than none: the operator now believes the gate was looked at.
        Assert.True(Flags("sudo test -f out.txt", GateWeakness.SelfCertifying));
        Assert.True(Flags("/usr/bin/test -f out.txt", GateWeakness.SelfCertifying));
        Assert.True(Flags("CI=1 test -f out.txt", GateWeakness.SelfCertifying));
    }

    [Fact]
    public void Searching_a_file_is_flagged_but_searching_a_pipe_is_not()
    {
        // The distinction that keeps this useful. Grepping a file asks the agent to confirm its own
        // report; grepping a pipe reads what a tool produced a moment ago, which is the
        // re-derive-it shape we actually want. Flagging both would train the operator to skip both.
        Assert.True(Flags("grep -q CRITICAL findings.json", GateWeakness.SelfCertifying));
        Assert.False(Flags("nuclei -u https://target | grep -q critical", GateWeakness.SelfCertifying));
    }

    // ---- the gate that closes the loop ------------------------------------------------------

    [Theory]
    [InlineData("grep -q Achieved .rolloutloud/missions/m-1.json")]
    [InlineData("findstr /C:critical runs\\a007\\output.txt")]
    public void A_gate_reading_RolloutLouds_own_records_is_caught(string command)
    {
        // Every ledger entry and every run folder was written by the agent through the bridge.
        // This would be asking the agent to confirm its own report, one step removed.
        Assert.True(Flags(command, GateWeakness.Circular), command);
    }

    [Fact]
    public void A_gate_that_runs_an_agent_CLI_is_caught()
    {
        // Re-running a gate from a clean process buys nothing when the thing being re-run is
        // another model's opinion.
        Assert.True(Flags("claude -p \"did we find a critical?\"", GateWeakness.JudgedByAModel));
        Assert.True(Flags("codex exec \"grade the work\"", GateWeakness.JudgedByAModel));
    }

    [Fact]
    public void No_gate_at_all_is_said_out_loud()
    {
        var review = GateCritique.Review(SuccessGate.OperatorJudged);

        Assert.Contains(review.Findings, f => f.Weakness == GateWeakness.NoMachineCheck);
        Assert.True(review.HasSeriousFinding);
    }

    // ---- and the half that keeps it worth reading -------------------------------------------

    [Theory]
    [InlineData("dotnet test tests/RolloutLoud.Core.Tests")]
    [InlineData("pytest -q tests/regression")]
    [InlineData("npm run e2e -- --headed=false")]
    [InlineData("cargo test --release")]
    [InlineData("curl -fsS https://target/health && dotnet test")]
    [InlineData("nuclei -u https://target -severity critical -silent | grep -q .")]
    public void A_gate_that_re_derives_the_result_is_left_alone(string command)
    {
        // The counterpart matters as much as the catching. A checker that warns about every gate is
        // one the operator learns to click past — and then it is worse than absent, because they
        // believe something is watching.
        var review = Of(command);

        Assert.Empty(review.Findings);
        Assert.Contains("Nothing to flag", review.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void The_headline_leads_with_the_worst_finding()
    {
        // A serious finding buried under a mild one is a serious finding the operator skims past.
        var review = Of("grep -q ok notes.txt && test -f REPORT.md");

        Assert.Contains(
            review.Findings.First(f => f.Concern == GateConcern.Serious).Detail,
            review.Headline,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_name_the_fragment_that_caused_them()
    {
        // The operator has to be able to see which part of a long command line is the problem
        // without re-deriving the analysis themselves.
        var finding = Of("dotnet test ./src || true").Findings.Single();

        Assert.Equal("true", finding.Fragment);
    }
}
