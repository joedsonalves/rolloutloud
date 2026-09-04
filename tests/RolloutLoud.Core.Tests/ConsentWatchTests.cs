using RolloutLoud.Core.Consent;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// A supervising session told to improve RolloutLoud until it runs without the operator is editing
/// the tool that grants it its delegation. It can widen its own authority without lying to anybody,
/// because the mission is literally to make the operator unnecessary.
///
/// The operator was offered a freeze and chose a warning. This is what makes the warning worth
/// something: detected rather than self-reported, and written where it outlives the window.
/// </summary>
public class ConsentWatchTests
{
    [Theory]
    [InlineData("src/RolloutLoud.Core/Consent/DeputyGrant.cs")]
    [InlineData("src/RolloutLoud.Core/Buttons/ButtonAllowlist.cs")]
    [InlineData("src/RolloutLoud.Core/Missions/MissionScope.cs")]
    [InlineData("src/RolloutLoud.Core/Missions/FourthWall.cs")]
    [InlineData("src/RolloutLoud.Core/Missions/SuccessGate.cs")]
    [InlineData("src/RolloutLoud.Core/RolloutHost.cs")]
    public void Editing_the_machinery_that_decides_who_may_do_what_is_noticed(string path)
    {
        Assert.NotEmpty(ConsentWatch.Touched([path]));
    }

    [Fact]
    public void The_watch_watches_itself()
    {
        // A supervisor that could quietly shorten this list would have removed the warning rather
        // than earned it.
        Assert.NotEmpty(ConsentWatch.Touched(["src/RolloutLoud.Core/Consent/ConsentWatch.cs"]));
    }

    [Theory]
    [InlineData("src/RolloutLoud.Core/Missions/GateCritique.cs")]
    [InlineData("src/RolloutLoud.Core/Money/SpendMeter.cs")]
    [InlineData("README.md")]
    [InlineData("tests/RolloutLoud.Core.Tests/HandoverTests.cs")]
    public void Ordinary_code_is_not(string path)
    {
        // ⚠️ The list is short on purpose. One that covers half the repository makes every change a
        // warning, and a warning on every change is one nobody reads — the same failure as no
        // warning, with extra steps.
        Assert.Empty(ConsentWatch.Touched([path]));
    }

    [Fact]
    public void Windows_and_posix_separators_are_the_same_path()
    {
        Assert.NotEmpty(ConsentWatch.Touched([@"src\RolloutLoud.Core\Consent\DeputyGrant.cs"]));
    }

    [Fact]
    public void One_file_touched_twice_is_reported_once()
    {
        var touched = ConsentWatch.Touched(
        [
            "src/RolloutLoud.Core/Missions/MissionScope.cs",
            @"src\RolloutLoud.Core\Missions\MissionScope.cs",
        ]);

        Assert.Single(touched);
    }

    // ---- reading git ----------------------------------------------------------------------------

    [Fact]
    public void Modified_and_untracked_files_are_both_read()
    {
        // Porcelain rather than a diff because a NEW file replacing a guarded one is exactly the
        // shape a diff of tracked changes would miss.
        var porcelain = string.Join(
            '\n',
            " M src/RolloutLoud.Core/Consent/DeputyGrant.cs",
            "?? src/RolloutLoud.Core/Consent/DeputyGrant2.cs",
            " M README.md");

        var paths = ConsentWatch.PathsIn(porcelain);

        Assert.Contains("src/RolloutLoud.Core/Consent/DeputyGrant.cs", paths);
        Assert.Contains("README.md", paths);
        Assert.Equal(3, paths.Count);
    }

    [Fact]
    public void A_rename_is_read_as_its_new_path()
    {
        // Moving consent code somewhere unlisted is the way past this, and reading the OLD path
        // would miss it twice over.
        var paths = ConsentWatch.PathsIn(
            "R  src/RolloutLoud.Core/Consent/DeputyGrant.cs -> src/RolloutLoud.Core/Misc/Grant.cs");

        Assert.Contains("src/RolloutLoud.Core/Misc/Grant.cs", paths);
        Assert.DoesNotContain("src/RolloutLoud.Core/Consent/DeputyGrant.cs", paths);
    }

    [Fact]
    public void A_quoted_path_with_spaces_survives()
    {
        Assert.Contains(
            "src/RolloutLoud.Core/Consent/Deputy Grant.cs",
            ConsentWatch.PathsIn("?? \"src/RolloutLoud.Core/Consent/Deputy Grant.cs\""));
    }

    [Fact]
    public void A_clean_tree_touches_nothing()
    {
        Assert.Empty(ConsentWatch.PathsIn(string.Empty));
        Assert.Empty(ConsentWatch.Touched(ConsentWatch.PathsIn(" M README.md")));
    }

    [Fact]
    public void The_warning_says_what_it_is_and_why_it_is_only_a_warning()
    {
        // It has to be readable months later by somebody who does not remember choosing this.
        var line = ConsentWatch.Describe(["Consent/DeputyGrant.cs"], "this run");

        Assert.Contains("who may do what", line, StringComparison.Ordinal);
        Assert.Contains("its own authority", line, StringComparison.Ordinal);
        Assert.Contains("warned rather than asked", line, StringComparison.Ordinal);
    }
}
