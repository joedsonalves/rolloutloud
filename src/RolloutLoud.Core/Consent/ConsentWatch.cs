using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Consent;

/// <summary>
/// Notices when a run is editing the machinery that decides who may do what.
/// </summary>
/// <remarks>
/// <b>The situation this exists for is specific.</b> A supervising session with a delegation, told
/// to improve RolloutLoud until it runs without the operator, is editing the tool that grants it
/// that delegation. It can widen its own authority without lying to anyone, and the mission is
/// literally to make the operator unnecessary. Nothing here is dishonest; the incentive is just
/// pointed the wrong way.
///
/// The operator was offered a freeze — consent files become a proposal that waits for their click —
/// and chose a warning instead. Their call, and their tool. What is left is to make the warning
/// worth something, because the premise of the mission is that nobody is reading the log live:
///
/// <list type="bullet">
/// <item><b>Detected, not self-reported.</b> Asking the session to declare it would be asking the
/// thing being watched to raise its hand.</item>
/// <item><b>Written where it outlives the window.</b> A line in a scrolling activity log is gone by
/// morning, and morning is when the operator looks.</item>
/// </list>
///
/// ⚠️ It watches paths, not intent. A rename that moves consent code somewhere unlisted goes
/// unseen, and there is no version of this short of a freeze that does not. The operator knows;
/// they were told before they chose.
/// </remarks>
public static class ConsentWatch
{
    /// <summary>
    /// The parts that decide who may do what. Everything else is ordinary code.
    /// </summary>
    /// <remarks>
    /// Deliberately short. A list that covers half the repository makes every change a warning, and
    /// a warning on every change is one nobody reads — which is the same failure as no warning, with
    /// extra steps.
    /// </remarks>
    public static IReadOnlyList<string> Guarded { get; } =
    [
        "Consent/DeputyGrant.cs",
        "Consent/ConsentWatch.cs",
        "Buttons/ButtonAllowlist.cs",
        "Missions/MissionScope.cs",
        "Missions/FourthWall.cs",
        "Missions/SuccessGate.cs",
        "Elevation/IElevationService.cs",
        "RolloutHost.cs",
    ];

    /// <summary>Which guarded parts a set of changed paths touches.</summary>
    /// <remarks>
    /// Matched on the tail of the path so it works on whatever <c>git status</c> prints, forward or
    /// backslashes. <c>RolloutHost.cs</c> is on the list because the launch sentinel and the
    /// delegation lookup live in it — it is where the checks are, not merely near them.
    /// </remarks>
    public static IReadOnlyList<string> Touched(IEnumerable<string> changedPaths) =>
    [
        .. changedPaths
            .Select(p => p.Replace('\\', '/').Trim())
            .Where(p => p.Length > 0)
            .Where(p => Guarded.Any(g => p.EndsWith(g.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// Reads the paths out of <c>git status --porcelain</c>.
    /// </summary>
    /// <remarks>
    /// Porcelain rather than a diff because it catches untracked files too — a new file that
    /// replaces a guarded one is exactly the shape that a diff of tracked changes would miss.
    /// </remarks>
    public static IReadOnlyList<string> PathsIn(string porcelain) =>
    [
        .. porcelain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line.TrimEnd('\r'), @"^..\s+", string.Empty))
            // A rename prints "old -> new"; the new path is the one that matters.
            .Select(path => path.Contains(" -> ", StringComparison.Ordinal)
                ? path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..]
                : path)
            .Select(path => path.Trim('"')),
    ];

    /// <summary>The line written to the durable record, and to the activity log.</summary>
    public static string Describe(IReadOnlyList<string> touched, string by) =>
        $"{DateTimeOffset.Now:u}  {by} has uncommitted changes to the consent machinery: " +
        string.Join(", ", touched) +
        "  — this is the code that decides who may do what, including its own authority. " +
        "You chose to be warned rather than asked; this is the warning.";
}
