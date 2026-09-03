using RolloutLoud.Core;
using RolloutLoud.Core.Elevation;
using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// An agent composing its own mission is what the operator asked for — they type a sentence into a
/// CLI and the agent, which knows the repository, turns it into a testable objective. The cost is
/// that composing a mission means composing its success gate, and a gate the agent wrote for itself
/// hands back the one decision this product exists to take away from it. So a proposal waits.
/// </summary>
public sealed class MissionProposalTests : IDisposable
{
    private readonly RolloutPaths _paths;
    private readonly RolloutHost _host;

    public MissionProposalTests()
    {
        _paths = new RolloutPaths(Path.Combine(Path.GetTempPath(), "rlprop-" + Guid.NewGuid().ToString("N")[..8]));
        _paths.EnsureCreated();
        _host = new RolloutHost(_paths, new NoElevation());
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

    private sealed class NoElevation : IElevationService
    {
        public bool IsElevated => false;

        public bool CanElevate => false;

        public string PromptDescription => "not in a test";

        public Task<bool> RelaunchElevatedAsync(string root, CancellationToken token = default) =>
            Task.FromResult(false);
    }

    private static MissionProposal Draft(string gate = "dotnet test tests/Unit", string by = "claude") => new()
    {
        Id = MissionProposal.NewId(),
        Objective = "Make the intermittent failure in the checkout suite reproducible, then fix it",
        ProposedBy = by,
        AgentId = by,
        GateCommand = gate,
        Rationale = "That suite is the one that flakes, and it fails cleanly when the bug is present.",
        Review = new GateReview { Findings = [], Headline = string.Empty },
    };

    [Fact]
    public void Proposing_does_not_start_anything()
    {
        // The whole point. Composing a mission means composing its gate, and an agent that can
        // install its own finish line has taken back the decision the gate exists to hold.
        var proposal = _host.Propose(Draft());

        Assert.Equal(ProposalState.Pending, proposal.State);
        Assert.Empty(_host.Missions);
        Assert.Null(_host.ActiveMissionId);
    }

    [Fact]
    public void The_gate_is_critiqued_on_arrival_and_the_verdict_rides_with_the_proposal()
    {
        var proposal = _host.Propose(Draft(gate: "test -f REPORT.md"));

        Assert.True(proposal.Review.HasSeriousFinding);
        Assert.Contains(proposal.Review.Findings, f => f.Weakness == GateWeakness.SelfCertifying);
    }

    [Fact]
    public void Accepting_builds_a_real_mission_and_starts_it()
    {
        var proposal = _host.Propose(Draft());

        var engine = _host.AcceptProposal(proposal.Id);

        Assert.NotNull(engine);
        Assert.Equal(MissionState.Running, engine.Mission.State);
        Assert.Equal(GateKind.Command, engine.Mission.Gate.Kind);
        Assert.Equal("dotnet test tests/Unit", engine.Mission.Gate.Command);

        // And it is the one an agent calling the bridge without naming a mission will get.
        Assert.Equal(engine.Mission.Id, _host.ActiveMissionId);

        var after = _host.FindProposal(proposal.Id);
        Assert.Equal(ProposalState.Accepted, after!.State);
        Assert.Equal(engine.Mission.Id, after.MissionId);
    }

    [Fact]
    public void What_starts_is_what_the_operator_left_behind_not_what_arrived()
    {
        // The cheap half of the feature. "This gate checks a file you write" is useful to be told
        // and useless if fixing it means discarding and going back to the terminal.
        var proposal = _host.Propose(Draft(gate: "test -f REPORT.md"));

        var engine = _host.AcceptProposal(
            proposal.Id,
            proposal with { GateCommand = "dotnet test tests/Checkout", Objective = "Fix the flake" });

        Assert.Equal("dotnet test tests/Checkout", engine!.Mission.Gate.Command);
        Assert.Equal("Fix the flake", engine.Mission.Objective);
    }

    [Fact]
    public void Rejecting_gives_the_agent_the_reason_back()
    {
        var proposal = _host.Propose(Draft());

        var rejected = _host.RejectProposal(proposal.Id, "The gate runs the wrong suite.");

        Assert.Equal(ProposalState.Rejected, rejected!.State);
        Assert.Equal("The gate runs the wrong suite.", rejected.Decision);
        Assert.Empty(_host.Missions);
    }

    [Fact]
    public void A_rejection_with_no_reason_still_says_something()
    {
        // An agent told only "rejected" has nothing to change and will re-propose the same thing.
        var proposal = _host.Propose(Draft());

        Assert.False(string.IsNullOrWhiteSpace(_host.RejectProposal(proposal.Id, null)!.Decision));
    }

    [Fact]
    public void Accepting_twice_opens_one_mission_not_two()
    {
        // ⚠️ A double click on Start is one event away from two calls. The tidy-looking version —
        // check pending, create, then mark accepted — lets both through: two engines, two ledgers,
        // one objective, and the second silently becomes the active one.
        var proposal = _host.Propose(Draft());

        var first = _host.AcceptProposal(proposal.Id);
        var second = _host.AcceptProposal(proposal.Id);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(_host.Missions);
    }

    [Fact]
    public void A_decided_proposal_cannot_be_decided_again()
    {
        var proposal = _host.Propose(Draft());
        _host.RejectProposal(proposal.Id, "no");

        Assert.Null(_host.AcceptProposal(proposal.Id));
        Assert.Null(_host.RejectProposal(proposal.Id, "no again"));
    }

    [Fact]
    public void A_second_proposal_from_the_same_agent_replaces_the_first()
    {
        // An agent revises: it proposes, reads the critique, proposes better. Leaving both on the
        // desk asks the operator to choose between two drafts of one idea, where only the newer
        // one can be right.
        var first = _host.Propose(Draft(gate: "test -f REPORT.md"));
        var second = _host.Propose(Draft(gate: "dotnet test tests/Checkout"));

        Assert.Equal(ProposalState.Withdrawn, _host.FindProposal(first.Id)!.State);
        Assert.Equal(second.Id, _host.PendingProposal!.Id);
    }

    [Fact]
    public void A_proposal_from_another_agent_does_not_displace_one()
    {
        var claude = _host.Propose(Draft(by: "claude"));
        _host.Propose(Draft(by: "codex"));

        Assert.Equal(ProposalState.Pending, _host.FindProposal(claude.Id)!.State);

        // Oldest first, so a queue drains in the order the agents asked.
        Assert.Equal(claude.Id, _host.PendingProposal!.Id);
    }

    [Fact]
    public void Nothing_is_pending_once_everything_is_answered()
    {
        var proposal = _host.Propose(Draft());
        _host.AcceptProposal(proposal.Id);

        Assert.Null(_host.PendingProposal);
    }

    [Fact]
    public void An_unauthorised_target_list_is_carried_through_to_the_operator()
    {
        // The scope warning is the operator's, not the agent's: the run has to be attributable
        // later, and the agent naming targets is exactly when that matters.
        var proposal = _host.Propose(Draft() with { Scope = ["staging.example.com"] });

        Assert.True(proposal.NeedsAuthorization);

        var authorised = _host.Propose(
            Draft() with { Scope = ["staging.example.com"], Authorization = "PO-4471, signed" });

        Assert.False(authorised.NeedsAuthorization);
    }

    [Fact]
    public void An_accepted_proposal_produces_the_same_mission_the_direct_route_would()
    {
        // Acceptance goes through the ordinary path on purpose, so no invariant downstream has to
        // know this mission came from a proposal, and no code path exists that a proposal can
        // reach and a mission cannot.
        var engine = _host.AcceptProposal(_host.Propose(Draft() with
        {
            Scope = ["staging.example.com"],
            Authorization = "PO-4471",
            MaxAttempts = 40,
            MaxHours = 2,
            Offload = "always",
        }).Id);

        Assert.Equal(40, engine!.Mission.Stop.MaxAttempts);
        Assert.Equal(TimeSpan.FromHours(2), engine.Mission.Stop.MaxWallClock);
        Assert.Equal(OffloadTrigger.Always, engine.Mission.Offload.Trigger);
        Assert.Equal("PO-4471", engine.Mission.Scope.Authorization);
        Assert.False(engine.Mission.Scope.Unbounded);
    }

    [Fact]
    public void Defaults_match_the_direct_route_when_the_agent_leaves_them_out()
    {
        var engine = _host.AcceptProposal(_host.Propose(Draft()).Id);

        Assert.Equal(200, engine!.Mission.Stop.MaxAttempts);
        Assert.Equal(TimeSpan.FromHours(6), engine.Mission.Stop.MaxWallClock);
        Assert.Equal(OffloadTrigger.Off, engine.Mission.Offload.Trigger);
        Assert.True(engine.Mission.Scope.Unbounded);
    }
}
