using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// A project whose main agent fires ten subagents from the first turn produces thousands of run
/// folders in a month. Nothing used to remove any of them.
/// </summary>
public sealed class HousekeepingTests : IDisposable
{
    private readonly RolloutPaths _paths;

    public HousekeepingTests()
    {
        _paths = new RolloutPaths(Path.Combine(Path.GetTempPath(), "rl-" + Guid.NewGuid().ToString("N")[..8]));
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_paths.RepositoryRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test run over.
        }
    }

    private string MakeRun(string name, TimeSpan age, int bytes = 512)
    {
        var directory = _paths.RunDirectory(name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "subagent.txt"), new string('x', bytes));

        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - age);
        return directory;
    }

    [Fact]
    public void Old_run_folders_are_removed_and_the_bytes_reported()
    {
        MakeRun("old-1", TimeSpan.FromDays(40));
        MakeRun("old-2", TimeSpan.FromDays(60));
        MakeRun("recent", TimeSpan.FromHours(2));

        var report = new Housekeeper(_paths).Tidy();

        Assert.Equal(2, report.RunsRemoved);
        Assert.Equal(1, report.RunsKept);
        Assert.True(report.BytesReclaimed > 0);
        Assert.True(Directory.Exists(_paths.RunDirectory("recent")));
    }

    [Fact]
    public void A_count_limit_catches_what_an_age_limit_misses()
    {
        // The failure mode of a project that fires ten subagents a session: everything is recent,
        // and there are thousands of them. An age limit alone would keep every one.
        for (var i = 0; i < 20; i++)
        {
            MakeRun($"run-{i:00}", TimeSpan.FromMinutes(i));
        }

        var housekeeper = new Housekeeper(_paths)
        {
            Policy = new HousekeepingPolicy { MaxRuns = 5, MaxRunAge = TimeSpan.FromDays(365) },
        };

        var report = housekeeper.Tidy();

        Assert.Equal(15, report.RunsRemoved);
        Assert.Equal(5, report.RunsKept);

        // Newest kept: run-00 is the most recently written.
        Assert.True(Directory.Exists(_paths.RunDirectory("run-00")));
        Assert.False(Directory.Exists(_paths.RunDirectory("run-19")));
    }

    [Fact]
    public void A_run_belonging_to_an_open_mission_is_never_pruned()
    {
        // Deleting the evidence under a running mission leaves ledger entries pointing at nothing,
        // which is worse than any amount of disk.
        MakeRun("keep-me", TimeSpan.FromDays(400));
        MakeRun("prune-me", TimeSpan.FromDays(400));

        var report = new Housekeeper(_paths).Tidy(["keep-me"]);

        Assert.Equal(1, report.RunsRemoved);
        Assert.True(Directory.Exists(_paths.RunDirectory("keep-me")));
        Assert.False(Directory.Exists(_paths.RunDirectory("prune-me")));
    }

    [Fact]
    public void Finished_missions_are_archived_rather_than_deleted()
    {
        // The ledger is the most expensive thing the tool produces. Moving it out of the load path
        // keeps startup fast; deleting it would throw away the reasoning.
        var store = new MissionStore(_paths);

        var finished = new Mission
        {
            Id = "m-old",
            Objective = "done long ago",
            AgentId = "claude",
            State = MissionState.Achieved,
            EndedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };

        store.Save(finished, new MissionLedger("m-old"));

        var report = new Housekeeper(_paths).Tidy();

        Assert.Equal(1, report.MissionsArchived);
        Assert.False(File.Exists(_paths.MissionFile("m-old")));
        Assert.True(File.Exists(Path.Combine(_paths.MissionsDirectory, "archive", "m-old.json")));

        // And LoadAll no longer sees it, which is the point.
        Assert.DoesNotContain(store.LoadAll(), r => r.Mission.Id == "m-old");
    }

    [Fact]
    public void An_open_mission_is_never_archived_however_old()
    {
        var store = new MissionStore(_paths);

        store.Save(
            new Mission
            {
                Id = "m-running",
                Objective = "still going",
                AgentId = "claude",
                State = MissionState.Running,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
            },
            new MissionLedger("m-running"));

        var report = new Housekeeper(_paths).Tidy();

        Assert.Equal(0, report.MissionsArchived);
        Assert.True(File.Exists(_paths.MissionFile("m-running")));
    }

    [Fact]
    public void A_mission_that_only_just_finished_is_left_alone()
    {
        var store = new MissionStore(_paths);

        store.Save(
            new Mission
            {
                Id = "m-fresh",
                Objective = "just finished",
                AgentId = "claude",
                State = MissionState.Achieved,
                EndedAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
            new MissionLedger("m-fresh"));

        Assert.Equal(0, new Housekeeper(_paths).Tidy().MissionsArchived);
    }

    [Fact]
    public void Tidying_an_empty_workspace_does_nothing_and_says_so()
    {
        var report = new Housekeeper(_paths).Tidy();

        Assert.False(report.DidAnything);
        Assert.Equal(0, report.RunsRemoved);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void Sizes_are_reported_in_units_a_person_reads(long bytes, string expected)
    {
        Assert.Equal(expected, HousekeepingReport.Format(bytes));
    }
}
