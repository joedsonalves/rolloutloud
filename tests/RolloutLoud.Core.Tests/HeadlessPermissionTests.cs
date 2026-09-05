using RolloutLoud.Core.Agents;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The two mechanisms built to work unattended were the two that could not write.
/// </summary>
/// <remarks>
/// Supervised rounds and subagent rounds both built their argv from <see cref="AgentDescriptor.PromptArguments"/>
/// alone, so neither carried the CLI's bypass flag — which lived in <c>ElevatedArguments</c>, read
/// only by the launch button. Every headless round could read a file and not write one.
///
/// ⚠️ <b>Nothing failed.</b> A reconnaissance round comes back "succeeded" and well-formed whether
/// the agent chose not to write or could not, and 371 tests passed either way, because none of them
/// looked at the argv a headless round actually builds. That is what these look at.
/// </remarks>
public class HeadlessPermissionTests
{
    private static AgentDescriptor Agent(string id) =>
        AgentCatalog.Defaults.Single(a => a.Id == id);

    [Theory]
    [InlineData(AgentCatalog.Claude, "--dangerously-skip-permissions")]
    [InlineData(AgentCatalog.Codex, "--dangerously-bypass-approvals-and-sandbox")]
    [InlineData(AgentCatalog.Hermes, "--yolo")]
    public void A_headless_round_carries_the_bypass_flag(string id, string flag)
    {
        Assert.Contains(flag, Agent(id).HeadlessArgumentsFor("do the thing"));
    }

    [Theory]
    [InlineData(AgentCatalog.Claude)]
    [InlineData(AgentCatalog.Codex)]
    [InlineData(AgentCatalog.Hermes)]
    public void A_session_RolloutLoud_opens_carries_it_too(string id)
    {
        // Both lists, because the difference between them is OS rights, not prompting. A window
        // opened by the normal button is still a window nobody is sitting in front of.
        var agent = Agent(id);

        Assert.Equal(agent.ElevatedArguments, agent.ArgumentsFor(LaunchMode.Normal));
        Assert.Equal(agent.ElevatedArguments, agent.ArgumentsFor(LaunchMode.Elevated));
    }

    [Fact]
    public void The_flag_sits_where_each_CLI_wants_it()
    {
        // Position is not cosmetic: an argv the CLI rejects surfaces as a round that ran, printed a
        // usage message to stderr and returned nothing — indistinguishable from an agent with
        // nothing to say. Codex takes it after the subcommand, Hermes globally before one.
        Assert.Equal(
            ["exec", "--dangerously-bypass-approvals-and-sandbox", "go"],
            Agent(AgentCatalog.Codex).HeadlessArgumentsFor("go"));

        Assert.Equal(["--yolo", "-z", "go"], Agent(AgentCatalog.Hermes).HeadlessArgumentsFor("go"));
    }

    [Fact]
    public void OpenClaw_gets_no_invented_flag()
    {
        // It has no launch-time bypass at all — permission is persisted host state, set by hand.
        // Falling back to the prompt arguments is the honest answer; making one up would produce
        // an argv it rejects on every round.
        var openClaw = Agent(AgentCatalog.OpenClaw);

        Assert.Empty(openClaw.HeadlessArguments);
        Assert.Equal(["agent", "--message", "go"], openClaw.HeadlessArgumentsFor("go"));
    }

    [Fact]
    public void An_operator_who_overrode_an_agent_before_this_existed_still_runs()
    {
        // agents.json carries whole descriptors. One written before HeadlessArguments existed
        // deserialises with the list empty, and must keep behaving as it did rather than losing
        // its prompt arguments entirely.
        var old = new AgentDescriptor
        {
            Id = "mine",
            DisplayName = "Mine",
            Executable = "mine",
            InstructionFile = "MINE.md",
            PromptArguments = ["run", "{prompt}"],
        };

        Assert.Equal(["run", "go"], old.HeadlessArgumentsFor("go"));
    }
}
