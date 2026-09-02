using System.Text.Json;
using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Buttons;

/// <summary>
/// The patterns an agent is allowed to invoke without a human clicking.
/// </summary>
/// <remarks>
/// This file is the consent boundary of the whole product, so it fails closed in every direction:
/// missing file, unreadable file, malformed file, empty pattern, pattern that matches everything —
/// all of them yield "no auto-invocation", never "allow". A tool that opens up when its policy
/// file is corrupt is worse than one with no policy at all, because the operator believes there
/// is a policy.
///
/// ⚠️ A bare <c>*</c> is rejected on purpose. It is the pattern a tired operator writes at 2am to
/// stop being interrupted, and it turns the allowlist into decoration. If that is genuinely
/// wanted, it belongs in a switch labelled what it is, not smuggled in as a pattern.
/// </remarks>
public sealed class ButtonAllowlist
{
    private readonly List<string> _patterns;

    private ButtonAllowlist(IEnumerable<string> patterns)
    {
        _patterns = [.. patterns
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && p != "*" && p != "**")];
    }

    public static ButtonAllowlist Empty { get; } = new([]);

    public IReadOnlyList<string> Patterns => _patterns;

    /// <summary>
    /// The starting policy: exactly the case that motivated fluid buttons, and nothing else.
    /// Shipping with one useful entry teaches the format; shipping with ten teaches the operator
    /// to stop reading it.
    /// </summary>
    public static IReadOnlyList<string> SuggestedPatterns { get; } =
    [
        "*chrome* --remote-debugging-port=*",
    ];

    public static ButtonAllowlist Load(string allowlistFile)
    {
        if (!File.Exists(allowlistFile))
        {
            return Empty;
        }

        try
        {
            var patterns = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(allowlistFile));
            return patterns is null ? Empty : new ButtonAllowlist(patterns);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Fail closed. An unreadable policy is not an absent policy.
            return Empty;
        }
    }

    public static void Write(string allowlistFile, IEnumerable<string> patterns)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(allowlistFile)!);
        File.WriteAllText(
            allowlistFile,
            JsonSerializer.Serialize(patterns, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Whether the agent may run this itself. Glob semantics, case-insensitive, whitespace
    /// collapsed first so that formatting differences do not decide a security question.
    /// </summary>
    public bool Allows(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || _patterns.Count == 0)
        {
            return false;
        }

        var normalized = Regex.Replace(command.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));

        foreach (var pattern in _patterns)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            if (Regex.IsMatch(normalized, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                return true;
            }
        }

        return false;
    }

    public ButtonDisposition DispositionFor(string command) =>
        Allows(command) ? ButtonDisposition.AutoInvokable : ButtonDisposition.NeedsOperator;
}
