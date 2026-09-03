using Xunit;

using RolloutLoud.Core.Buttons;
using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The allowlist and the escalation ladder. Both decide something on the operator's behalf while
/// they are not watching, which is why they get their own tests.
/// </summary>
public class GuardrailTests
{
    [Fact]
    public void A_missing_allowlist_allows_nothing()
    {
        var allowlist = ButtonAllowlist.Load(Path.Combine(Path.GetTempPath(), "no-such-allowlist.json"));

        Assert.False(allowlist.Allows("echo anything"));
    }

    [Fact]
    public void A_malformed_allowlist_fails_closed()
    {
        // A tool that opens up when its policy file is corrupt is worse than one with no policy,
        // because the operator believes there is a policy.
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(file, "{ this is not json");

        try
        {
            Assert.False(ButtonAllowlist.Load(file).Allows("echo anything"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_bare_wildcard_is_dropped_rather_than_honoured()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        ButtonAllowlist.Write(file, ["*"]);

        try
        {
            var allowlist = ButtonAllowlist.Load(file);

            Assert.Empty(allowlist.Patterns);
            Assert.False(allowlist.Allows("rm -rf /"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_pattern_matches_regardless_of_case_and_extra_whitespace()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        ButtonAllowlist.Write(file, ["*chrome* --remote-debugging-port=*"]);

        try
        {
            var allowlist = ButtonAllowlist.Load(file);

            // Formatting differences must not decide a security question in either direction.
            Assert.True(allowlist.Allows("start CHROME.EXE    --remote-debugging-port=9222"));
            Assert.False(allowlist.Allows("start chrome.exe --headless"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_ladder_holds_still_while_attempts_are_still_teaching_something()
    {
        // Genuinely different tools, each ruling something out. Fifty failures like these is
        // progress, and shoving the agent up a tier here would interrupt work that is going well.
        //
        // ⚠️ Note the tool names have to differ by more than a number. Attempt.Fingerprint
        // normalises digits away, so `tool-1` and `tool-2` are one idea to the ledger — which is
        // the intended trade (a changed port must not read as novel) but does mean a fixture
        // built out of numbered names measures nothing.
        string[] tools = ["nmap -sV", "ffuf -u", "nuclei -u", "nikto -h", "wpscan --url", "gobuster dir -u"];

        var attempts = tools.Select((tool, i) => new Attempt
        {
            Id = $"a{i}",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = $"Idea {i}",
            Command = $"{tool} https://app.example.com",
            Outcome = AttemptOutcome.Failed,
            Observation = $"Ruled out class {i}.",
        }).ToList();

        Assert.False(EscalationLadder.ShouldEscalate(attempts, plateauThreshold: 5));
    }

    [Fact]
    public void The_ladder_moves_when_the_ideas_collapse_onto_one_shape()
    {
        var attempts = Enumerable.Range(0, 6).Select(i => new Attempt
        {
            Id = $"a{i}",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = "Same idea, new number",
            Command = $"sqlmap -u https://app.example.com/login --level {i}",
            Outcome = AttemptOutcome.Failed,
            Observation = "Nothing.",
        }).ToList();

        Assert.True(EscalationLadder.ShouldEscalate(attempts, plateauThreshold: 5));
    }

    [Fact]
    public void Declarations_do_not_drive_the_ladder()
    {
        // Escalating on work that has not finished would shove the agent mid-idea, and the shove
        // would land as "that approach is exhausted" about an approach still running.
        var attempts = Enumerable.Range(0, 6).Select(i => new Attempt
        {
            Id = $"a{i}",
            MissionId = "m1",
            AgentId = "claude",
            Hypothesis = $"Idea {i}",
            Command = $"tool-{i} --target app.example.com",
            Outcome = AttemptOutcome.Declared,
        }).ToList();

        Assert.False(EscalationLadder.ShouldEscalate(attempts, plateauThreshold: 5));
    }

    [Fact]
    public void Every_tier_has_an_instruction_and_a_name()
    {
        for (var tier = 0; tier <= EscalationLadder.MaxTier; tier++)
        {
            Assert.False(string.IsNullOrWhiteSpace(EscalationLadder.NameOf(tier)));
            Assert.False(string.IsNullOrWhiteSpace(EscalationLadder.InstructionFor(tier)));
        }
    }
}
