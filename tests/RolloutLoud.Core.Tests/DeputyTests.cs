using RolloutLoud.Core.Consent;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The operator letting a supervising session click for them. Not a bypass — nothing at OS level
/// was in the way, the check was a line in RolloutLoud's own code — but the same consent stated
/// once in advance instead of once per click, which is the only version of "you may act as me" that
/// is not simply the rule being ignored.
/// </summary>
public sealed class DeputyTests : IDisposable
{
    private readonly string _root;
    private readonly string _file;

    public DeputyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rldep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "deputy.json");
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

    private static DeputyGrant Grant(string missionId = "m1") => new()
    {
        MissionId = missionId,
        Deputy = "claude",
        MayLaunchOutsideAnchor = true,
        MayClickUnlistedButtons = true,
    };

    // ---- fails closed, in every direction -----------------------------------------------------

    [Fact]
    public void No_file_delegates_nothing()
    {
        // Same rule as the allowlist, for the same reason: a delegation that appears when the
        // policy file is absent is worse than none, because the operator believes there is a
        // policy.
        Assert.Null(DeputyRegister.Load(Path.Combine(_root, "not-there.json")).For("m1"));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{ "grants": [] }""")]
    [InlineData("""{ "grants": [ { "deputy": "claude" } ] }""")]
    public void A_broken_or_empty_or_missionless_grant_delegates_nothing(string contents)
    {
        // The third one matters most: a grant with no mission would otherwise be a delegation with
        // no boundary, which is the one shape this must never produce from a malformed file.
        File.WriteAllText(_file, contents);

        Assert.Null(DeputyRegister.Load(_file).For("m1"));
        Assert.Empty(DeputyRegister.Load(_file).All);
    }

    // ---- the boundary that actually holds -----------------------------------------------------

    [Fact]
    public void A_grant_covers_the_mission_it_names_and_no_other()
    {
        // The real boundary. The deputy NAME is a label for the audit line — one token
        // authenticates every caller on this bridge — so what bounds a delegation is the mission.
        var register = new DeputyRegister([Grant("m1")]);

        Assert.NotNull(register.For("m1"));
        Assert.Null(register.For("m2"));
        Assert.Null(register.For(null));
        Assert.Null(register.For(""));
    }

    [Fact]
    public void The_two_capabilities_are_separate()
    {
        // Leaving one off has to mean something, or the checkbox is decoration.
        var launchOnly = new DeputyRegister(
        [
            Grant() with { MayClickUnlistedButtons = false },
        ]).For("m1")!;

        Assert.True(launchOnly.MayLaunchOutsideAnchor);
        Assert.False(launchOnly.MayClickUnlistedButtons);
    }

    [Fact]
    public void Nothing_in_a_grant_can_cover_elevation()
    {
        // Not a test of a flag — a test that no flag exists. Elevating changes the privilege of
        // every process started afterwards, so it stays a decision the operator makes at the OS
        // prompt, and there must be no field here that could be read as granting it.
        var fields = typeof(DeputyGrant)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(fields, name => name.Contains("Elevat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Shutdown", StringComparison.OrdinalIgnoreCase));
    }

    // ---- round trip and withdrawal ------------------------------------------------------------

    [Fact]
    public void What_the_operator_grants_is_what_comes_back()
    {
        DeputyRegister.Write(_file, [Grant() with { Note = "granted for the PENTEST run" }]);

        var loaded = DeputyRegister.Load(_file).For("m1")!;

        Assert.Equal("claude", loaded.Deputy);
        Assert.True(loaded.MayLaunchOutsideAnchor);
        Assert.True(loaded.MayClickUnlistedButtons);
        Assert.Equal("granted for the PENTEST run", loaded.Note);
    }

    [Fact]
    public void Deleting_the_file_withdraws_the_delegation()
    {
        // The operator's only lever once a run is going, and it has to work without a restart —
        // which is why the register is loaded fresh rather than cached.
        DeputyRegister.Write(_file, [Grant()]);
        Assert.NotNull(DeputyRegister.Load(_file).For("m1"));

        File.Delete(_file);

        Assert.Null(DeputyRegister.Load(_file).For("m1"));
    }

    [Fact]
    public void Regranting_a_mission_replaces_rather_than_stacks()
    {
        // Two grants for one mission would make "what am I delegating?" a question with two
        // answers, and the operator would be reading the wrong one half the time.
        DeputyRegister.Write(_file,
        [
            Grant() with { MayClickUnlistedButtons = false },
            Grant() with { MayClickUnlistedButtons = true },
        ]);

        Assert.True(DeputyRegister.Load(_file).For("m1")!.MayClickUnlistedButtons);
    }
}
