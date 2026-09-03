using Xunit;

using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The ledger is what stops the loop going in circles, so these cover the ways it could stop
/// working while still compiling and looking correct.
/// </summary>
public class LedgerTests
{
    private static MissionScope AppScope => new()
    {
        Targets = ["app.example.com", "10.0.4.0/24"],
        Authorization = "test",
    };

    [Fact]
    public void Fingerprint_ignores_numbers_so_a_changed_port_is_not_a_new_idea()
    {
        // The failure this guards: an agent "varies its approach" by moving to another port and
        // the ledger counts it as novel, so the plateau detector never fires and the run grinds
        // through a thousand ports of the same dead idea.
        Assert.Equal(
            Attempt.Fingerprint("sqlmap -u https://app.example.com:443/login"),
            Attempt.Fingerprint("sqlmap -u https://app.example.com:8443/login"));
    }

    [Fact]
    public void Fingerprint_distinguishes_a_genuinely_different_tool()
    {
        Assert.NotEqual(
            Attempt.Fingerprint("sqlmap -u https://app.example.com/login"),
            Attempt.Fingerprint("ffuf -u https://app.example.com/FUZZ"));
    }

    [Fact]
    public void Declaring_reserves_the_idea_before_any_result_arrives()
    {
        // The bug this was written for: admission used to register nothing, so the same command
        // could be declared twice before either finished — which is exactly what happens when
        // two agents share one mission and both reach for the obvious first.
        var ledger = new MissionLedger("m1");
        var command = "sqlmap -u https://app.example.com/login";

        Assert.True(ledger.Admit(command, AppScope).Admitted);

        ledger.Record(new Attempt
        {
            Id = "a1",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = "Injectable login",
            Command = command,
            Outcome = AttemptOutcome.Declared,
        });

        var second = ledger.Admit(command, AppScope);
        Assert.False(second.Admitted);
        Assert.Equal(AttemptOutcome.Duplicate, second.Outcome);
    }

    [Fact]
    public void Reporting_a_result_replaces_the_declaration_instead_of_appending()
    {
        var ledger = new MissionLedger("m1");
        var command = "nmap -sV app.example.com";

        ledger.Record(new Attempt
        {
            Id = "a1",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = "Service sweep",
            Command = command,
            Outcome = AttemptOutcome.Declared,
        });

        ledger.Record(new Attempt
        {
            Id = "a2",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = "Service sweep",
            Command = command,
            Outcome = AttemptOutcome.Failed,
            Observation = "Only 80 and 443, both current versions.",
        });

        // One entry, not two: an intention and its outcome are the same attempt, and doubling
        // them would make every briefing read as twice the history it is.
        Assert.Single(ledger.Attempts);
        Assert.Equal(AttemptOutcome.Failed, ledger.Attempts[0].Outcome);
    }

    [Fact]
    public void Out_of_scope_commands_are_refused_with_the_scope_named()
    {
        var ledger = new MissionLedger("m1");

        var admission = ledger.Admit("nmap -sV admin.other-company.com", AppScope);

        Assert.False(admission.Admitted);
        Assert.Equal(AttemptOutcome.BlockedByScope, admission.Outcome);
        Assert.Contains("app.example.com", admission.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exclusion_beats_a_matching_target()
    {
        // The carve-out case: a range is in scope but one host inside it is not, and a broad
        // pattern must not quietly re-include it.
        var scope = new MissionScope
        {
            Targets = ["*.example.com"],
            Exclusions = ["payments.example.com"],
            Authorization = "test",
        };

        Assert.False(scope.Evaluate("curl https://payments.example.com/health").InScope);
        Assert.True(scope.Evaluate("curl https://app.example.com/health").InScope);
    }

    [Fact]
    public void A_scope_with_targets_but_no_authorization_is_flagged()
    {
        var scope = new MissionScope { Targets = ["app.example.com"] };

        Assert.True(scope.NeedsAuthorization);
        Assert.False(AppScope.NeedsAuthorization);
    }

    [Fact]
    public void Summarize_caps_its_output_so_a_long_run_cannot_flood_the_briefing()
    {
        var ledger = new MissionLedger("m1");
        for (var i = 0; i < 200; i++)
        {
            ledger.Record(new Attempt
            {
                Id = $"a{i}",
                MissionId = "m1",
                AgentId = "claude",
                Hypothesis = $"Idea {i}",
                Command = $"probe --target app.example.com --case {i}",
                Outcome = AttemptOutcome.Failed,
                Observation = "Nothing.",
            });
        }

        var summary = ledger.Summarize(maxEntries: 10);

        Assert.Contains("earlier attempt(s) omitted", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Idea 0 ", summary, StringComparison.Ordinal);
        Assert.Contains("Idea 199", summary, StringComparison.Ordinal);
    }
}
