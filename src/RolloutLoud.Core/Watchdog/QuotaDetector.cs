using System.Globalization;
using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Watchdog;

/// <summary>
/// A round that ended because the session ran out of allowance, not out of ideas.
/// </summary>
public sealed record QuotaSignal(bool Exhausted, string Phrase, DateTimeOffset? ResetsAt)
{
    public static QuotaSignal None { get; } = new(false, string.Empty, null);
}

/// <summary>
/// Spots the difference between "I have no more ideas" and "I have no more tokens".
/// </summary>
/// <remarks>
/// These look identical from the outside — the agent stops mid-work and the round produces
/// nothing — and treating them the same is expensive in both directions. Restarting immediately
/// on a spent quota burns rounds against a wall until <see cref="WatchdogSettings.MaxBarrenRounds"/>
/// gives up and declares a broken setup, which is the wrong diagnosis and abandons a run that was
/// going fine. Waiting on a genuine dead end wastes hours doing nothing.
///
/// So a quota round is handled on its own path: it does not count as barren, and the supervisor
/// sleeps until the window reopens instead of retrying.
///
/// The reset time is read out of the message when the CLI gives one, because these are hourly or
/// multi-hour windows and guessing wrong costs either a wasted hour or a pointless retry storm.
/// <see cref="Grace"/> is added on top: coming back at the exact reset second is how you get one
/// more rejection from a clock that is a few seconds behind yours.
/// </remarks>
public static class QuotaDetector
{
    /// <summary>Added to a parsed reset time. A window that opens "at 3pm" is not reliably open at 3pm.</summary>
    public static TimeSpan Grace { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Used when the limit is recognised but no reset time is given.</summary>
    public static TimeSpan BlindWait { get; } = TimeSpan.FromMinutes(30);

    private static readonly string[] Exhaustion =
    [
        @"usage limit reached",
        @"\d+-hour limit reached",
        @"you'?ve (hit|reached) your (usage |rate )?limit",
        @"rate[ -]?limit(ed| exceeded| reached)?",
        @"quota (exceeded|exhausted|reached)",
        @"too many requests",
        @"insufficient (quota|credits|balance)",
        @"out of (credits|tokens)",
        @"\b429\b",
        @"limite de uso atingido",
        @"l[ií]mite de uso alcanzado",
    ];

    public static QuotaSignal Inspect(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return QuotaSignal.None;
        }

        // The whole output, not just the tail: a CLI often prints the limit notice and then a
        // usage summary or a stack trace after it, which would push the notice out of a tail
        // window and turn a quota stop into a mystery.
        foreach (var pattern in Exhaustion)
        {
            var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            if (match.Success)
            {
                return new QuotaSignal(true, match.Value, FindResetTime(output));
            }
        }

        return QuotaSignal.None;
    }

    /// <summary>How long to sleep before trying again.</summary>
    public static TimeSpan WaitFor(QuotaSignal signal, DateTimeOffset now)
    {
        if (signal.ResetsAt is not { } reset)
        {
            return BlindWait;
        }

        var wait = reset + Grace - now;

        // A reset time already in the past means the message was stale, or the clocks disagree.
        // Neither is a reason to skip the wait entirely and hammer the wall again.
        return wait <= TimeSpan.Zero ? Grace : wait;
    }

    private static DateTimeOffset? FindResetTime(string output)
    {
        var now = DateTimeOffset.Now;

        // "try again in 42 minutes" / "in 2 hours"
        var relative = Regex.Match(
            output,
            @"(?:try again|retry|wait)[^.\n]{0,20}?in\s+(\d+)\s*(second|minute|hour)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (relative.Success && int.TryParse(relative.Groups[1].Value, out var amount))
        {
            return relative.Groups[2].Value.ToLowerInvariant() switch
            {
                "second" => now.AddSeconds(amount),
                "minute" => now.AddMinutes(amount),
                _ => now.AddHours(amount),
            };
        }

        // A full timestamp, if one is offered.
        var iso = Regex.Match(
            output,
            @"resets?(?:\s+at)?\s+(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:?\d{2})?)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (iso.Success &&
            DateTimeOffset.TryParse(
                iso.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        // "resets at 3pm", "resets 10:30pm", "will reset at 14:00" — the common shape.
        var clock = Regex.Match(
            output,
            @"reset[s]?(?:\s+at)?\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (!clock.Success || !int.TryParse(clock.Groups[1].Value, out var hour))
        {
            return null;
        }

        var minute = clock.Groups[2].Success && int.TryParse(clock.Groups[2].Value, out var m) ? m : 0;
        var meridiem = clock.Groups[3].Value.ToLowerInvariant();

        if (meridiem == "pm" && hour < 12)
        {
            hour += 12;
        }
        else if (meridiem == "am" && hour == 12)
        {
            hour = 0;
        }

        if (hour > 23 || minute > 59)
        {
            return null;
        }

        var candidate = new DateTimeOffset(
            now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);

        // "resets at 3pm" said at 4pm means tomorrow. Reading it as today would compute a
        // negative wait and send the agent straight back into the wall.
        return candidate <= now ? candidate.AddDays(1) : candidate;
    }
}
