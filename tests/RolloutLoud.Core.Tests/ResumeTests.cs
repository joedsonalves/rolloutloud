using RolloutLoud.Core;
using RolloutLoud.Core.Buttons;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The ledger has always survived a restart. What did not was any way to get back to it — and,
/// less obviously, the fluid buttons an agent was waiting on.
/// </summary>
public sealed class ResumeTests : IDisposable
{
    private readonly RolloutPaths _paths;

    public ResumeTests()
    {
        _paths = new RolloutPaths(Path.Combine(Path.GetTempPath(), "rlres-" + Guid.NewGuid().ToString("N")[..8]));
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
            // A temp directory left behind is not worth failing a run over.
        }
    }

    private static FluidButton Button(string id, ButtonStatus status) => new()
    {
        Id = id,
        Title = "Start Chrome with remote debugging",
        Command = "echo chrome --remote-debugging-port=9222",
        Rationale = "I need a CDP endpoint and cannot start one myself.",
        RequestedBy = "hermes",
        MissionId = "m1",
        Status = status,
    };

    [Fact]
    public void A_pending_button_survives_a_restart()
    {
        // The failure this fixes: an agent posts a button because it cannot run something itself,
        // the window closes, and on reopen the button is gone — while the agent is still waiting
        // for a thing that no longer exists anywhere.
        var store = new ButtonStore(_paths);

        store.Save([Button("btn-1", ButtonStatus.Pending)]);

        var restored = new ButtonStore(_paths).Load();

        Assert.Single(restored);
        Assert.Equal("btn-1", restored[0].Id);
        Assert.Equal(ButtonStatus.Pending, restored[0].Status);
    }

    [Fact]
    public void Finished_buttons_are_not_carried_forward()
    {
        // History belongs in the run folders and the ledger. Carrying every button ever pressed
        // into every future session turns the panel into a log nobody reads.
        var store = new ButtonStore(_paths);

        store.Save(
        [
            Button("open", ButtonStatus.Pending),
            Button("done", ButtonStatus.Succeeded),
            Button("failed", ButtonStatus.Failed),
            Button("gone", ButtonStatus.Dismissed),
        ]);

        var restored = new ButtonStore(_paths).Load();

        Assert.Single(restored);
        Assert.Equal("open", restored[0].Id);
    }

    [Fact]
    public void A_button_that_was_running_comes_back_as_pending()
    {
        // Nothing is running it any more — the process that was died. Leaving it as Running would
        // show a spinner forever and hide the fact that it needs pressing again.
        new ButtonStore(_paths).Save([Button("btn-1", ButtonStatus.Running)]);

        var restored = new ButtonStore(_paths).Load();

        Assert.Single(restored);
        Assert.Equal(ButtonStatus.Pending, restored[0].Status);
        Assert.Contains("did not finish", restored[0].OutputExcerpt!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_broken_button_file_loads_as_empty()
    {
        Assert.Empty(new ButtonStore(_paths).Load());

        File.WriteAllText(_paths.ButtonsFile, "{ not json");

        Assert.Empty(new ButtonStore(_paths).Load());
    }

    [Fact]
    public void The_ledger_and_the_tier_come_back_intact()
    {
        var store = new MissionStore(_paths);

        var ledger = new MissionLedger("m1");
        foreach (var tool in new[] { "nmap -sV", "ffuf -u", "nuclei -t" })
        {
            ledger.Record(new Attempt
            {
                Id = tool,
                MissionId = "m1",
                AgentId = "claude",
                Hypothesis = $"idea about {tool}",
                Command = $"{tool} target",
                Outcome = AttemptOutcome.Failed,
                Observation = "Ruled a class out.",
            });
        }

        store.Save(
            new Mission
            {
                Id = "m1",
                Objective = "a long run",
                AgentId = "claude",
                State = MissionState.Running,
                EscalationTier = 2,
                RelayHistory = ["codex"],
            },
            ledger);

        var reloaded = store.Load("m1");

        Assert.NotNull(reloaded);
        Assert.Equal(3, reloaded!.Attempts.Count);
        Assert.Equal(2, reloaded.Mission.EscalationTier);
        Assert.Contains("codex", reloaded.Mission.RelayHistory);

        // And the restored ledger still refuses what was already tried.
        var restored = new MissionLedger("m1", reloaded.Attempts);
        Assert.False(restored.Admit("nmap -sV target", MissionScope.Unrestricted).Admitted);
    }

    [Theory]
    [InlineData(MissionState.Achieved)]
    [InlineData(MissionState.Exhausted)]
    [InlineData(MissionState.Aborted)]
    public void A_finished_mission_is_terminal_and_should_not_be_reopened(MissionState state)
    {
        // Quietly restarting one of these would undo a decision somebody made — including the
        // gate's. The bridge refuses with a 409 rather than reopening it.
        var mission = new Mission
        {
            Id = "m1",
            Objective = "already over",
            AgentId = "claude",
            State = state,
        };

        Assert.True(mission.IsTerminal);
    }

    [Theory]
    [InlineData(MissionState.Running)]
    [InlineData(MissionState.Paused)]
    [InlineData(MissionState.Draft)]
    public void An_unfinished_mission_is_resumable(MissionState state)
    {
        var mission = new Mission
        {
            Id = "m1",
            Objective = "still going",
            AgentId = "claude",
            State = state,
        };

        Assert.False(mission.IsTerminal);
    }

    [Fact]
    public void Resuming_a_mission_makes_it_the_active_one()
    {
        // ⚠️ Found by actually resuming, not by reading the handler. Without this the command
        // answers `resumed: true` with the mission id, and then the agent's very next call —
        // `attempt`, `gate`, `continue`, none of which name a mission — gets "no such mission, and
        // no active mission to fall back to". The agent believes it resumed and everything after
        // says the mission does not exist, which reads as a completely different bug.
        //
        // Third occurrence of this shape here: a mission enters the host by some route other than
        // the operator clicking, and nothing selects it. See the note about a mission opened
        // through the bridge not appearing selected in the window.
        using var host = new HostFixture(_paths);

        var first = host.Open("the one the operator was on");
        var resumed = host.Open("the one being picked back up");

        host.Value.SetActiveMission(first.Mission.Id);
        Assert.Equal(first.Mission.Id, host.Value.ActiveMissionId);

        host.Value.SetActiveMission(resumed.Mission.Id);

        Assert.Equal(resumed.Mission.Id, host.Value.ActiveMissionId);
        Assert.Same(resumed, host.Value.FindMission(null));
    }

    /// <summary>A host on a throwaway repository, so the test needs nothing installed.</summary>
    private sealed class HostFixture(RolloutPaths paths) : IDisposable
    {
        public RolloutHost Value { get; } = new(paths, new NoElevation());

        public MissionEngine Open(string objective) => Value.CreateMission(new Mission
        {
            Id = Mission.NewId(),
            Objective = objective,
            AgentId = "claude",
            State = MissionState.Running,
        });

        public void Dispose()
        {
        }

        private sealed class NoElevation : RolloutLoud.Core.Elevation.IElevationService
        {
            public bool IsElevated => false;

            public bool CanElevate => false;

            public string PromptDescription => "not in a test";

            public Task<bool> RelaunchElevatedAsync(string root, CancellationToken token = default) =>
                Task.FromResult(false);
        }
    }
}
