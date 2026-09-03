using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace RolloutLoud.Core.Localization;

/// <summary>
/// The operator-facing strings, in the language the OS is set to.
/// </summary>
/// <remarks>
/// **Only the operator's chrome is translated, and that line is deliberate.** Everything the
/// agents read — briefings, bridge responses, ledger entries, the escalation ladder's
/// instructions — stays English. Those flow into the agents' own context and into a ledger that
/// gets handed from one CLI to another, and a ledger written half in Portuguese is one no agent
/// can summarise back. The window is for the person; the wire is for the models.
///
/// ⚠️ The resource names are the trap here, and it is one Vacuon already paid for. A file named
/// <c>Strings.en-US.json</c> matches <c>name.culture.extension</c>, so MSBuild treats it as a
/// **satellite assembly resource**: the build succeeds, the file is nowhere in the main assembly,
/// <see cref="Assembly.GetManifestResourceStream(string)"/> returns null, and the entire UI
/// renders as <c>[key]</c> with no error anywhere. The csproj pins this with
/// <c>WithCulture="false"</c> and an explicit <c>LogicalName</c>, and
/// <c>LocalizationTests</c> fails if a table stops loading.
/// </remarks>
public sealed class Localizer
{
    private const string DefaultLanguage = "en";
    private const string ResourcePrefix = "RolloutLoud.Core.Strings.";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    private readonly IReadOnlyDictionary<string, string> _strings;
    private readonly IReadOnlyDictionary<string, string> _fallback;

    private Localizer(string language, IReadOnlyDictionary<string, string> strings, IReadOnlyDictionary<string, string> fallback)
    {
        Language = language;
        _strings = strings;
        _fallback = fallback;
    }

    /// <summary>The languages that ship in the assembly.</summary>
    public static IReadOnlyList<string> Available { get; } = ["en", "pt", "es"];

    public static Localizer Current { get; private set; } = Load(DefaultLanguage);

    public string Language { get; }

    /// <summary>
    /// Picks the language from the OS, unless overridden.
    /// </summary>
    /// <remarks>
    /// <c>ROLLOUTLOUD_LANG</c> wins over the OS, and exists for two real cases: checking a
    /// translation without changing the machine's regional settings, and an operator whose OS is
    /// in one language while they would rather read the tool in another.
    ///
    /// The OS value is matched on the two-letter part only. <c>pt-BR</c> and <c>pt-PT</c> both
    /// get <c>pt</c>: shipping one Portuguese and matching the full tag would leave a Portuguese
    /// machine reading English because of a region code.
    /// </remarks>
    public static void Initialize(string? overrideLanguage = null)
    {
        var requested =
            overrideLanguage
            ?? Environment.GetEnvironmentVariable("ROLLOUTLOUD_LANG")
            ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        var language = Normalize(requested);
        Current = Load(language);
    }

    private static string Normalize(string requested)
    {
        var trimmed = requested.Trim();
        if (trimmed.Length == 0)
        {
            return DefaultLanguage;
        }

        var twoLetter = trimmed.Split('-', '_')[0].ToLowerInvariant();
        return Available.Contains(twoLetter) ? twoLetter : DefaultLanguage;
    }

    private static Localizer Load(string language)
    {
        var fallback = ReadTable(DefaultLanguage) ?? new Dictionary<string, string>();
        var strings = language == DefaultLanguage ? fallback : ReadTable(language) ?? fallback;
        return new Localizer(language, strings, fallback);
    }

    private static Dictionary<string, string>? ReadTable(string language)
    {
        var assembly = typeof(Localizer).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + language + ".json");
        if (stream is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The string for a key, falling back to English and then to the key itself.
    /// </summary>
    /// <remarks>
    /// Returns <c>[key]</c> rather than throwing when nothing matches, because a missing string
    /// should cost one ugly label rather than a crashed window — and because the bracket form is
    /// visible enough that it gets reported instead of quietly shipping.
    /// </remarks>
    public string this[string key] =>
        _strings.TryGetValue(key, out var value) ? value
        : _fallback.TryGetValue(key, out var english) ? english
        : "[" + key + "]";

    /// <summary>
    /// Translates a value that might be a key, or might be the operator's own text.
    /// </summary>
    /// <remarks>
    /// Used for fields that ship a translatable default but can be overridden in
    /// <c>agents.json</c>. The shipped value is a key and gets translated; anything the operator
    /// typed is not a key and comes back verbatim — which is right, because their note is already
    /// in whatever language they wrote it in, and running it through a lookup would show them
    /// <c>[my own words]</c>.
    /// </remarks>
    public string Resolve(string? valueOrKey)
    {
        if (string.IsNullOrWhiteSpace(valueOrKey))
        {
            return string.Empty;
        }

        return _strings.TryGetValue(valueOrKey, out var value) ? value
            : _fallback.TryGetValue(valueOrKey, out var english) ? english
            : valueOrKey;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);

    /// <summary>Keys present in the shipped English table. Used by the parity test.</summary>
    public IReadOnlyCollection<string> Keys => _strings.Keys.ToList();

    /// <summary>Exposed so the parity test can prove every table actually loaded.</summary>
    public static IReadOnlyDictionary<string, string>? TableFor(string language) => ReadTable(language);
}
