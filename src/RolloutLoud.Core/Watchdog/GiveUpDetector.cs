using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Watchdog;

public enum GiveUpConfidence
{
    None,

    /// <summary>Reported a failure. On its own this is a fact, not a surrender.</summary>
    Reported,

    /// <summary>Handed the decision back to the operator. This is the moment the run dies.</summary>
    HandedBack,
}

public sealed record GiveUpSignal(GiveUpConfidence Confidence, string Phrase, string Excerpt)
{
    public static GiveUpSignal None { get; } = new(GiveUpConfidence.None, string.Empty, string.Empty);

    public bool ShouldRestart => Confidence == GiveUpConfidence.HandedBack;
}

/// <summary>
/// Spots the sentence where an agent decides it is finished.
/// </summary>
/// <remarks>
/// This is the highest-value signal in the product and the easiest one to get wrong, so it is
/// deliberately two-tiered rather than one keyword list.
///
/// **"I was unable to resolve DNS for this host" is a fact.** An agent reporting it is working,
/// not quitting, and restarting it there would interrupt a run that is going fine — the failure
/// mode where the watchdog turns the tool into noise. So a failure report on its own is
/// <see cref="GiveUpConfidence.Reported"/> and changes nothing.
///
/// **"Let me know if you'd like me to try another approach" is a surrender.** It hands the next
/// decision to a human who is asleep, and it is the exact sentence this whole product exists to
/// refuse. Those phrases are <see cref="GiveUpConfidence.HandedBack"/> and they restart the run.
///
/// The distinction is grammatical rather than semantic: the strong list is entirely made of
/// constructions that address the operator and ask them to choose. That is narrow enough to
/// almost never fire on a status report, and it is what the restart is keyed on.
///
/// Three languages, because the operator can run any of these CLIs in their own language and an
/// English-only detector would silently never fire.
/// </remarks>
public static class GiveUpDetector
{
    /// <summary>How much of the tail counts as "the closing statement".</summary>
    private const int TailLength = 700;

    /// <summary>
    /// Constructions that hand the next decision to the operator. Matching one of these anywhere
    /// in the closing statement is enough.
    /// </summary>
    private static readonly string[] HandsBack =
    [
        // English
        @"let me know if you",
        @"let me know whether",
        @"would you like me to",
        @"do you want me to",
        @"shall i (try|continue|proceed)",
        @"if you'?d like me to",
        @"please (let me know|advise|confirm)",
        @"i can try .{0,40} if you",
        @"how would you like",

        // Portuguese
        @"me avise se",
        @"me avisa se",
        @"quer que eu",
        @"deseja que eu",
        @"gostaria que eu",
        @"posso tentar .{0,40} se voc",
        @"caso queira que eu",
        @"aguardo (sua |seu )?(retorno|orienta)",

        // Spanish
        @"av[ií]same si",
        @"quieres que",
        @"deseas que",
        @"si lo prefieres",
        @"puedo intentar .{0,40} si (t[uú]|lo)",
        @"qued[oa] a la espera",
    ];

    /// <summary>
    /// Plain failure reports. Informative, never sufficient on their own.
    /// </summary>
    private static readonly string[] Reports =
    [
        @"i (was|am) unable to",
        @"i (could|couldn'?t|can'?t) (not )?find",
        @"no (critical|vulnerabilit|issue)",
        @"i did not (find|succeed)",
        @"n[ãa]o consegui",
        @"n[ãa]o foi poss[ií]vel",
        @"n[ãa]o encontrei",
        @"no pude",
        @"no he podido",
        @"no fue posible",
        @"no encontr[eé]",
    ];

    public static GiveUpSignal Inspect(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return GiveUpSignal.None;
        }

        // Only the closing statement is examined. An agent that says "I was unable to reach the
        // host, so I switched to the API" mentions failure in the middle of working — judging the
        // whole transcript would flag every honest narration of a dead end.
        var tail = output.Length <= TailLength ? output : output[^TailLength..];

        foreach (var pattern in HandsBack)
        {
            var match = Match(tail, pattern);
            if (match is not null)
            {
                return new GiveUpSignal(GiveUpConfidence.HandedBack, match, Excerpt(tail, match));
            }
        }

        foreach (var pattern in Reports)
        {
            var match = Match(tail, pattern);
            if (match is not null)
            {
                return new GiveUpSignal(GiveUpConfidence.Reported, match, Excerpt(tail, match));
            }
        }

        return GiveUpSignal.None;
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return match.Success ? match.Value : null;
    }

    private static string Excerpt(string text, string around)
    {
        var index = text.IndexOf(around, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return around;
        }

        var start = Math.Max(0, index - 60);
        var length = Math.Min(text.Length - start, around.Length + 140);
        return text.Substring(start, length).ReplaceLineEndings(" ").Trim();
    }
}
