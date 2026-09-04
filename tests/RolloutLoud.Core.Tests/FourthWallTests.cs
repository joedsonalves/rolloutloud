using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Offload;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The mode where whoever is steering a run is denied its raw material on purpose — because a
/// supervisor that reads everything is a second worker, and because on a pentest that material is
/// written by the target.
/// </summary>
public class FourthWallTests
{
    private static Attempt Attempt(int i) => new()
    {
        Id = $"a{i:000}",
        MissionId = "m1",
        AgentId = "claude",
        Hypothesis = $"idea {i}",
        Command = $"nmap -sV --top-ports 1000 host{i}",
        Outcome = AttemptOutcome.Failed,
        Observation = $"Rules out class {i}.",
        Tier = 0,
        ExitCode = 1,
        ArtifactDirectory = $"/runs/a{i:000}",
        At = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
    };

    private static Mission Mission(bool wall) => new()
    {
        Id = "m1",
        Objective = "reach a critical inside the declared scope",
        AgentId = "claude",
        FourthWall = wall,
        Deliverable = wall ? "report/DRAFT.md" : null,
    };

    // ---- what the wall takes out --------------------------------------------------------------

    [Fact]
    public void The_argv_the_exit_code_and_the_artifact_folder_go()
    {
        var entry = LedgerQueryRunner.Run([Attempt(1)], new LedgerQuery { Full = true }).Entries[0];

        Assert.NotNull(entry.Command);

        var redacted = FourthWall.Redact(entry);

        Assert.Null(redacted.Command);
        Assert.Null(redacted.ExitCode);
        Assert.Null(redacted.Artifacts);
    }

    [Fact]
    public void What_the_attempt_ruled_out_stays()
    {
        // The split is not arbitrary: it is the one the ledger query already argued for on its own
        // terms, that "what has been ruled out" almost never needs the exact argv. This mode turns
        // that default into a rule.
        var redacted = FourthWall.Redact(
            LedgerQueryRunner.Run([Attempt(1)], new LedgerQuery { Full = true }).Entries[0]);

        Assert.Equal("idea 1", redacted.Hypothesis);
        Assert.Equal("Rules out class 1.", redacted.Learned);
        Assert.Equal("Failed", redacted.Outcome);
        Assert.Equal("claude", redacted.Agent);
    }

    // ---- the ledger the working agent reads ---------------------------------------------------

    [Fact]
    public void The_summary_stops_echoing_the_command_back()
    {
        var ledger = new MissionLedger("m1", [Attempt(1), Attempt(2)]);

        Assert.Contains("ran:", ledger.Summarize(), StringComparison.Ordinal);
        Assert.DoesNotContain("ran:", ledger.Summarize(hideCommands: true), StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_keeps_everything_that_stops_a_repeat()
    {
        // The cost of the wall reaching the working agent too, and why it is affordable: exact
        // repeats were never held off by this echo — Admit blocks them by fingerprint before
        // anything runs — and what stops a repeat of a KIND of idea is the hypothesis and what it
        // ruled out, both of which stay.
        var summary = new MissionLedger("m1", [Attempt(1), Attempt(2)]).Summarize(hideCommands: true);

        Assert.Contains("idea 1", summary, StringComparison.Ordinal);
        Assert.Contains("Rules out class 1.", summary, StringComparison.Ordinal);
        Assert.Contains("Do not repeat any of these", summary, StringComparison.Ordinal);
    }

    // ---- what the working agent is told -------------------------------------------------------

    [Fact]
    public void The_agent_is_told_nobody_will_read_its_output()
    {
        // The second-order effect, and it is free: an agent told that nobody will read its raw
        // output writes a better observation, because that becomes the only channel rather than a
        // summary of something the reader could go and check for themselves.
        var briefing = BriefingComposer.ForMainSession(
            Mission(wall: true), new MissionLedger("m1"), identityAttached: false);

        Assert.Contains("cannot see your raw output", briefing, StringComparison.Ordinal);
        Assert.Contains("report/DRAFT.md", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_mission_says_none_of_that()
    {
        var briefing = BriefingComposer.ForMainSession(
            Mission(wall: false), new MissionLedger("m1"), identityAttached: false);

        Assert.DoesNotContain("cannot see your raw output", briefing, StringComparison.Ordinal);
        Assert.DoesNotContain("Fourth Wall", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void The_briefing_still_hides_the_commands_when_there_is_no_deliverable()
    {
        // A wall with no window is a worse mode, not an invalid one — the operator may simply not
        // have a single file in mind. It must not quietly stop being a wall.
        var mission = Mission(wall: true) with { Deliverable = null };
        var briefing = BriefingComposer.ForMainSession(
            mission, new MissionLedger("m1", [Attempt(1)]), identityAttached: false);

        Assert.Contains("cannot see your raw output", briefing, StringComparison.Ordinal);
        Assert.DoesNotContain("ran:", briefing, StringComparison.Ordinal);
    }

    // ---- the audit ----------------------------------------------------------------------------

    [Fact]
    public void What_was_withheld_is_counted_so_its_size_can_be_stated()
    {
        // The counterweight to the guard-rail caveat. A wall whose height nobody can state is one
        // people quietly stop believing in.
        var audit = new FourthWallAudit();

        audit.Record("m1", 3 * FourthWall.FieldsPerEntry);
        audit.Record("m1", 2 * FourthWall.FieldsPerEntry);
        audit.Record("m2", FourthWall.FieldsPerEntry);

        Assert.Equal(15, audit.For("m1"));
        Assert.Equal(3, audit.For("m2"));
        Assert.Equal(18, audit.Total);
        Assert.Equal(0, audit.For("never-heard-of-it"));
    }

    [Fact]
    public void Counting_nothing_is_not_recorded_as_something()
    {
        var audit = new FourthWallAudit();

        audit.Record("m1", 0);
        audit.Record("m1", -4);

        Assert.Equal(0, audit.For("m1"));
    }

    // ---- the room on the other side of the wall -----------------------------------------------

    [Fact]
    public void A_mission_knows_when_it_works_outside_the_anchor()
    {
        // The half that makes the wall physical rather than editorial: redaction keeps raw material
        // out of a supervisor's replies, but putting the worker in another repository in its own
        // process means it was never in reach to begin with.
        var anchor = Path.Combine(Path.GetTempPath(), "anchor");
        var elsewhere = Path.Combine(Path.GetTempPath(), "somewhere-else");

        Assert.False(Mission(wall: true).WorksElsewhere(anchor));
        Assert.False((Mission(wall: true) with { WorkingDirectory = anchor }).WorksElsewhere(anchor));
        Assert.True((Mission(wall: true) with { WorkingDirectory = elsewhere }).WorksElsewhere(anchor));
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("anchor/")]
    [InlineData("anchor\\")]
    public void The_same_folder_written_differently_is_still_the_same_folder(string spelling)
    {
        // ⚠️ A trailing separator would otherwise make a mission "work elsewhere" in its own anchor,
        // which produces a button asking the operator to consent to opening where the agent already
        // is — consent theatre, and the fastest way to teach someone to click without reading.
        var anchor = Path.Combine(Path.GetTempPath(), "anchor");
        var written = Path.Combine(Path.GetTempPath(), spelling.Replace('/', Path.DirectorySeparatorChar));

        Assert.False((Mission(wall: true) with { WorkingDirectory = written }).WorksElsewhere(anchor));
    }

    [Fact]
    public void The_briefing_names_the_foreign_repositorys_own_rules()
    {
        // Listed from what is actually on disk, never assumed. A briefing that tells an agent to
        // read a file the repository does not have teaches it that this document guesses — and
        // nothing auto-loads a file called LEIA-PRIMEIRO.md the way CLAUDE.md is auto-loaded.
        var folder = Path.Combine(Path.GetTempPath(), "rlwork-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(folder, "ESCOPOS"));
        File.WriteAllText(Path.Combine(folder, "LEIA-PRIMEIRO.md"), "rules");
        File.WriteAllText(Path.Combine(folder, "PADRAO-ATAQUE-INICIAL.md"), "playbook");

        try
        {
            var briefing = BriefingComposer.ForMainSession(
                Mission(wall: true) with { WorkingDirectory = folder },
                new MissionLedger("m1"),
                identityAttached: false);

            Assert.Contains("Read this repository before your first attempt", briefing, StringComparison.Ordinal);
            Assert.Contains("LEIA-PRIMEIRO.md", briefing, StringComparison.Ordinal);
            Assert.Contains("PADRAO-ATAQUE-INICIAL.md", briefing, StringComparison.Ordinal);
            Assert.Contains("ESCOPOS/", briefing, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_mission_that_stays_home_says_nothing_about_reading_a_repository()
    {
        var briefing = BriefingComposer.ForMainSession(
            Mission(wall: true), new MissionLedger("m1"), identityAttached: false);

        Assert.DoesNotContain("Read this repository before", briefing, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlistable_folder_still_produces_a_briefing()
    {
        // The mission is worth composing even when the folder cannot be read: the agent is about to
        // open there and can look for itself, and throwing would lose the mission instead.
        var briefing = BriefingComposer.ForMainSession(
            Mission(wall: true) with { WorkingDirectory = Path.Combine(Path.GetTempPath(), "not-there-" + Guid.NewGuid()) },
            new MissionLedger("m1"),
            identityAttached: false);

        Assert.Contains("Read this repository before your first attempt", briefing, StringComparison.Ordinal);
        Assert.Contains("reach a critical", briefing, StringComparison.Ordinal);
    }

    // ---- authorisation --------------------------------------------------------------------------

    [Fact]
    public void A_declared_target_behind_the_wall_still_needs_someone_to_have_approved_it()
    {
        // Refused rather than warned, and only here. Everywhere else the operator is watching the
        // traffic and can catch drift themselves; behind the wall nobody is, by design, so the
        // written record is the only thing left that makes the run attributable afterwards.
        var scope = new MissionScope { Targets = ["app.example.com"] };

        Assert.True(scope.NeedsAuthorization);
        Assert.False((scope with { Authorization = "PO-4471, signed" }).NeedsAuthorization);
    }

    [Fact]
    public void A_local_mission_behind_the_wall_needs_no_authorisation()
    {
        // No targets, nothing to be attributable for. Requiring a reference here would be ceremony,
        // and ceremony is how a required field becomes a field people type "n/a" into.
        Assert.False(MissionScope.Unrestricted.NeedsAuthorization);
    }
}
