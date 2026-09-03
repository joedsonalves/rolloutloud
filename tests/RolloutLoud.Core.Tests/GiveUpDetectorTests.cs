using RolloutLoud.Core.Watchdog;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// The highest-risk component in the product: it decides, unattended, whether to spend money
/// restarting an agent. A false positive turns the tool into noise, so the false-positive cases
/// get as much coverage as the true ones.
/// </summary>
public class GiveUpDetectorTests
{
    [Theory]
    // English — the sentence this whole product exists to refuse.
    [InlineData("I was unable to find a critical vulnerability. Let me know if you'd like me to try another approach.")]
    [InlineData("That path is exhausted. Would you like me to pivot to the API surface instead?")]
    [InlineData("Shall I continue with the authenticated endpoints?")]
    [InlineData("Do you want me to keep going?")]
    [InlineData("I've covered the obvious surface. Please let me know how to proceed.")]
    // Portuguese
    [InlineData("Não consegui um critical com as abordagens diretas. Me avise se quiser que eu tente outra coisa.")]
    [InlineData("Encerrei a varredura. Quer que eu parta para a API autenticada?")]
    [InlineData("Aguardo seu retorno para seguir.")]
    // Spanish
    [InlineData("No pude encontrar un critical. Avísame si quieres que pruebe otro enfoque.")]
    [InlineData("¿Quieres que continúe con los endpoints autenticados?")]
    public void Handing_the_decision_back_restarts_the_run(string output)
    {
        var signal = GiveUpDetector.Inspect(output);

        Assert.Equal(GiveUpConfidence.HandedBack, signal.Confidence);
        Assert.True(signal.ShouldRestart);
    }

    [Theory]
    // Every one of these is an agent WORKING and narrating a dead end. Restarting here is the
    // failure mode that makes an operator switch the watchdog off for good.
    [InlineData("I was unable to resolve DNS for that host, so I am going through the IP directly now.")]
    [InlineData("No critical findings on the login form. Moving to the file upload endpoint.")]
    [InlineData("Não consegui resolver o DNS desse host, então vou pelo IP.")]
    [InlineData("No pude autenticarme con esas credenciales; probando el flujo OAuth.")]
    public void A_narrated_dead_end_is_reported_but_does_not_restart(string output)
    {
        var signal = GiveUpDetector.Inspect(output);

        Assert.Equal(GiveUpConfidence.Reported, signal.Confidence);
        Assert.False(signal.ShouldRestart);
    }

    [Theory]
    [InlineData("Declared attempt 14 to the bridge and running it now.")]
    [InlineData("Found a reflected parameter. Building a proof of concept.")]
    [InlineData("Rodando a próxima tentativa.")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_progress_produces_no_signal(string? output)
    {
        Assert.Equal(GiveUpConfidence.None, GiveUpDetector.Inspect(output).Confidence);
    }

    [Fact]
    public void Only_the_closing_statement_is_judged()
    {
        // An agent that mentions handing back in the middle of a long transcript, then carries on
        // working, has not given up. Judging the whole transcript would flag almost every long run.
        var transcript =
            "Early on I thought: should I ask whether you want me to continue? " +
            new string('x', 3000) +
            " Declared attempt 22 and running it now.";

        Assert.False(GiveUpDetector.Inspect(transcript).ShouldRestart);
    }

    [Fact]
    public void The_excerpt_shows_the_operator_what_actually_matched()
    {
        // The activity log has to be able to say WHY it restarted, or the operator cannot tell a
        // good restart from a bad one — and cannot report a false positive usefully.
        var signal = GiveUpDetector.Inspect(
            "Ran six variations of the injection payload with no result. Let me know if you'd like me to try another approach.");

        Assert.True(signal.ShouldRestart);
        Assert.Contains("another approach", signal.Excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(signal.Phrase));
    }

    [Fact]
    public void Handing_back_wins_over_a_plain_report_in_the_same_output()
    {
        // Both appear together constantly — "I was unable to X. Let me know if…". The restart has
        // to key on the second half.
        var signal = GiveUpDetector.Inspect(
            "I was unable to escalate privileges. Let me know if you would like me to try another approach.");

        Assert.Equal(GiveUpConfidence.HandedBack, signal.Confidence);
    }
}
