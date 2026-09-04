using RolloutLoud.Core.Missions;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The scope was create-time only, which is fine when the operator knows the boundary in advance
/// and useless when <em>finding</em> it is the job. A run told to pick a programme and work inside
/// its published scope cannot name its targets on the command line that starts it — so it ran with
/// no boundary at all, on exactly the kind of work where the boundary matters most.
/// </summary>
public sealed class ScopeNarrowingTests : IDisposable
{
    private readonly RolloutPaths _paths;

    public ScopeNarrowingTests()
    {
        _paths = new RolloutPaths(Path.Combine(Path.GetTempPath(), "rlscope-" + Guid.NewGuid().ToString("N")[..8]));
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

    private MissionEngine Engine(MissionScope scope) => new(
        new Mission
        {
            Id = "m1",
            Objective = "find the boundary, then work inside it",
            AgentId = "claude",
            State = MissionState.Running,
            Scope = scope,
        },
        new MissionLedger("m1"),
        new MissionStore(_paths),
        _paths);

    private const string Auth = "the programme's public policy at hackerone.com/example";

    // ---- the first declaration on an unbounded run ---------------------------------------------

    [Fact]
    public void An_unbounded_run_can_be_bounded_once_it_knows_where()
    {
        var engine = Engine(MissionScope.Unrestricted);

        var narrowing = engine.DeclareScope(["app.example.com", "*.staging.example.com"], [], Auth);

        Assert.True(narrowing.Allowed);
        Assert.False(engine.Mission.Scope.Unbounded);
        Assert.Equal(2, engine.Mission.Scope.Targets.Count);
        Assert.Equal(Auth, engine.Mission.Scope.Authorization);
    }

    [Fact]
    public void The_boundary_starts_being_enforced_immediately()
    {
        // The whole point: before this call the bridge allowed anything, and after it the ledger
        // records a refusal for anything outside. If it did not take effect at once there would be
        // a window where the declaration was decoration.
        var engine = Engine(MissionScope.Unrestricted);

        Assert.True(engine.Mission.Scope.Evaluate("curl https://anything.example.org/").InScope);

        engine.DeclareScope(["app.example.com"], [], Auth);

        Assert.True(engine.Mission.Scope.Evaluate("curl https://app.example.com/login").InScope);
        Assert.False(engine.Mission.Scope.Evaluate("curl https://anything.example.org/").InScope);
    }

    // ---- narrowing only ------------------------------------------------------------------------

    [Fact]
    public void A_bounded_run_can_be_narrowed_further()
    {
        var engine = Engine(new MissionScope { Targets = ["*.example.com"], Authorization = Auth });

        Assert.True(engine.DeclareScope(["app.example.com"], [], Auth).Allowed);
        Assert.Equal(["app.example.com"], engine.Mission.Scope.Targets);
    }

    [Fact]
    public void It_can_never_be_widened()
    {
        // ⚠️ The rule that makes this a boundary rather than a note the run edits when it becomes
        // inconvenient. At attempt forty, "let me just look at the host next door" has to fail
        // against what attempt one wrote down.
        var engine = Engine(new MissionScope { Targets = ["app.example.com"], Authorization = Auth });

        var narrowing = engine.DeclareScope(["app.example.com", "other.example.org"], [], Auth);

        Assert.False(narrowing.Allowed);
        Assert.Contains("other.example.org", narrowing.Reason, StringComparison.Ordinal);
        Assert.Equal(["app.example.com"], engine.Mission.Scope.Targets);
    }

    [Fact]
    public void A_carve_out_cannot_be_dropped_by_redeclaring()
    {
        // Exclusions only accumulate: a carve-out somebody made is a decision, and dropping it
        // silently would undo it.
        var engine = Engine(new MissionScope
        {
            Targets = ["*.example.com"],
            Exclusions = ["admin.example.com"],
            Authorization = Auth,
        });

        engine.DeclareScope(["*.example.com"], [], Auth);

        Assert.Contains("admin.example.com", engine.Mission.Scope.Exclusions);
        Assert.False(engine.Mission.Scope.Evaluate("curl https://admin.example.com/").InScope);
    }

    [Fact]
    public void A_target_already_carved_out_cannot_be_declared_back_in()
    {
        var engine = Engine(new MissionScope
        {
            Targets = ["*.example.com"],
            Exclusions = ["admin.example.com"],
            Authorization = Auth,
        });

        Assert.False(engine.DeclareScope(["admin.example.com"], [], Auth).Allowed);
    }

    // ---- what a declaration has to carry --------------------------------------------------------

    [Fact]
    public void It_is_refused_without_something_that_authorises_it()
    {
        // ⚠️ Required here even though it is only a warning at creation. A boundary declared
        // mid-run was reviewed by nobody beforehand, so the written record is the only thing left
        // that makes the run attributable afterwards.
        var engine = Engine(MissionScope.Unrestricted);

        var narrowing = engine.DeclareScope(["app.example.com"], [], null);

        Assert.False(narrowing.Allowed);
        Assert.True(engine.Mission.Scope.Unbounded);
    }

    [Fact]
    public void An_empty_declaration_is_refused_rather_than_leaving_it_unbounded()
    {
        // Accepting it would leave the run in exactly the state this call exists to leave, while
        // reporting success — the worst of both.
        var engine = Engine(MissionScope.Unrestricted);

        Assert.False(engine.DeclareScope([], [], Auth).Allowed);
        Assert.False(engine.DeclareScope(["", "   "], [], Auth).Allowed);
        Assert.True(engine.Mission.Scope.Unbounded);
    }

    [Fact]
    public void A_refused_declaration_goes_into_the_record()
    {
        // An agent that tried to widen its own boundary is worth knowing about, and it is exactly
        // the drift the scope exists to catch.
        var engine = Engine(new MissionScope { Targets = ["app.example.com"], Authorization = Auth });

        engine.DeclareScope(["other.example.org"], [], Auth);

        Assert.Contains(engine.Events, e => e.Kind == "scope-refused");
    }

    [Fact]
    public void An_accepted_declaration_survives_a_restart()
    {
        var engine = Engine(MissionScope.Unrestricted);
        engine.DeclareScope(["app.example.com"], [], Auth);

        var reloaded = new MissionStore(_paths).LoadAll().Single(r => r.Mission.Id == "m1").Mission;

        Assert.Equal(["app.example.com"], reloaded.Scope.Targets);
        Assert.Equal(Auth, reloaded.Scope.Authorization);
    }
}
