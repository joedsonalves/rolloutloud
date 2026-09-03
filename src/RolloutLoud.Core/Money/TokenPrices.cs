using System.Text.Json;
using System.Text.Json.Serialization;

namespace RolloutLoud.Core.Money;

/// <summary>
/// What one model costs, in US dollars per million tokens, split by what the token was doing.
/// </summary>
/// <remarks>
/// The four numbers are not interchangeable and lumping them is the mistake that makes a spend
/// figure meaningless. A cached six-hour run is dominated by cache reads, which are roughly a tenth
/// of the input price — pricing them as input overstates the bill by close to an order of
/// magnitude, and an operator who sets a fifty-dollar cap would watch it fire at five dollars of
/// real spend and conclude the brake is broken.
/// </remarks>
public sealed record ModelPrice
{
    public required string Model { get; init; }

    /// <summary>Fresh input tokens.</summary>
    public required decimal InputPerMillion { get; init; }

    /// <summary>Tokens the model produced.</summary>
    public required decimal OutputPerMillion { get; init; }

    /// <summary>Writing the cache. Dearer than input, and paid once per prefix.</summary>
    public required decimal CacheWritePerMillion { get; init; }

    /// <summary>Reading the cache back. The cheap one, and the one a long run is made of.</summary>
    public required decimal CacheReadPerMillion { get; init; }

    public decimal Cost(long input, long output, long cacheWrite, long cacheRead) =>
        (input * InputPerMillion +
         output * OutputPerMillion +
         cacheWrite * CacheWritePerMillion +
         cacheRead * CacheReadPerMillion) / 1_000_000m;
}

/// <summary>
/// The price list, and the operator's ability to correct it.
/// </summary>
/// <remarks>
/// <b>This table ages, and that is the whole design constraint.</b> Prices change, models are
/// renamed, and a figure hardcoded in a release is wrong within months — at which point the money
/// brake is confidently stopping runs on a number nobody has checked since. So the shipped list is
/// a default and <c>.rolloutloud/pricing.json</c> overrides it, re-read whenever the file changes.
/// Same call, for the same reason, as <see cref="Agents.AgentCatalog"/>: a number that rots should
/// be a file edit, not a rebuild.
///
/// <b>Matching is by prefix, longest first.</b> Transcripts carry ids like
/// <c>claude-opus-4-5-20260514</c> — a dated build of a model whose price is set by the family. An
/// exact-match table would go blind on the first date bump, which is the moment it is least
/// noticeable and most expensive.
///
/// ⚠️ <b>An unknown model is priced at nothing, not at a guess.</b> Inventing a rate would produce a
/// bill with no relationship to reality and a cap that fires at random. The unpriced tokens are
/// counted and reported separately instead, so a spend reading can say "and 400k tokens I have no
/// price for" rather than quietly leaving them out of a number the operator is trusting.
/// </remarks>
public sealed class TokenPrices
{
    /// <summary>
    /// Published list prices in USD per million tokens, as of 03/09/2026.
    /// </summary>
    /// <remarks>
    /// Anthropic's own rates, taken from the pricing page rather than inferred from a bill. The
    /// other three CLIs are absent on purpose: RolloutLoud cannot read their transcripts either, so
    /// a price for them would be a number attached to no measurement. Adding a probe and adding a
    /// price is one job, not two.
    /// </remarks>
    public static IReadOnlyList<ModelPrice> Defaults { get; } =
    [
        new ModelPrice
        {
            Model = "claude-opus",
            InputPerMillion = 15.00m,
            OutputPerMillion = 75.00m,
            CacheWritePerMillion = 18.75m,
            CacheReadPerMillion = 1.50m,
        },
        new ModelPrice
        {
            Model = "claude-sonnet",
            InputPerMillion = 3.00m,
            OutputPerMillion = 15.00m,
            CacheWritePerMillion = 3.75m,
            CacheReadPerMillion = 0.30m,
        },
        new ModelPrice
        {
            Model = "claude-haiku",
            InputPerMillion = 1.00m,
            OutputPerMillion = 5.00m,
            CacheWritePerMillion = 1.25m,
            CacheReadPerMillion = 0.10m,
        },
        new ModelPrice
        {
            Model = "claude-fable",
            InputPerMillion = 3.00m,
            OutputPerMillion = 15.00m,
            CacheWritePerMillion = 3.75m,
            CacheReadPerMillion = 0.30m,
        },

        // Codex, whose transcripts name the model as gpt-5.5, gpt-5.4-mini, gpt-5.3-codex and so
        // on. Prefix matching means the family entry covers each point release, which is what stops
        // the table going blind the week a new one ships.
        //
        // ⚠️ Codex has no cache WRITE charge in its accounting — the session file reports input,
        // cached input and output, and nothing else. The write rate is set equal to input rather
        // than to zero, so that if a future version starts reporting one it is priced sanely rather
        // than silently free.
        new ModelPrice
        {
            Model = "gpt-5*-mini",
            InputPerMillion = 0.25m,
            OutputPerMillion = 2.00m,
            CacheWritePerMillion = 0.25m,
            CacheReadPerMillion = 0.025m,
        },
        new ModelPrice
        {
            Model = "gpt-5*",
            InputPerMillion = 1.25m,
            OutputPerMillion = 10.00m,
            CacheWritePerMillion = 1.25m,
            CacheReadPerMillion = 0.125m,
        },
    ];

    public static TokenPrices Default { get; } = new(Defaults);

    private readonly IReadOnlyList<ModelPrice> _prices;

    public TokenPrices(IEnumerable<ModelPrice> prices) =>
        // Most specific first, measured in LITERAL characters rather than raw length, so
        // "gpt-5*-mini" (ten literal) beats "gpt-5*" (five) and a wildcard cannot buy specificity
        // by being long.
        _prices = [.. prices.OrderByDescending(p => p.Model.Count(c => c != '*'))];

    public IReadOnlyList<ModelPrice> All => _prices;

    /// <summary>The price for a model id from a transcript, or null when nothing covers it.</summary>
    public ModelPrice? For(string? model) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : _prices.FirstOrDefault(p => Matches(model, p.Model));

    /// <summary>
    /// Whether a model id is covered by a price key.
    /// </summary>
    /// <remarks>
    /// ⚠️ A plain prefix is an <b>Anthropic-shaped</b> rule, and applying it to Codex misprices
    /// silently. Anthropic puts the family first — <c>claude-opus-4-5-20260514</c> — so
    /// <c>claude-opus</c> covers every build. OpenAI puts the version in the middle and the variant
    /// at the <b>end</b>: the real ids here are <c>gpt-5.5</c>, <c>gpt-5.4-mini</c>,
    /// <c>gpt-5.1-codex-max</c>. A key of <c>gpt-5-mini</c> never matches <c>gpt-5.4-mini</c>, while
    /// <c>gpt-5</c> matches it happily — and a mini billed as a full model is around five times too
    /// dear, with nothing anywhere reporting a problem.
    ///
    /// So a key may carry <c>*</c>. A key without one still behaves exactly as a prefix, which is
    /// what every entry written before this did, so no existing pricing.json changes meaning.
    /// </remarks>
    private static bool Matches(string model, string pattern)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return model.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var segments = pattern.Split('*');
        var at = 0;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.Length == 0)
            {
                continue;
            }

            // The first segment is anchored; the rest may appear anywhere after what came before.
            // A trailing segment is not anchored to the end, so "gpt-5*-mini" still covers a dated
            // build like "gpt-5.4-mini-20260714".
            var found = i == 0
                ? model.StartsWith(segment, StringComparison.OrdinalIgnoreCase) ? 0 : -1
                : model.IndexOf(segment, at, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return false;
            }

            at = found + segment.Length;
        }

        return true;
    }

    /// <summary>
    /// Loads the operator's price list, falling back to the shipped one.
    /// </summary>
    /// <remarks>
    /// Falls back on every failure path rather than failing closed, and the asymmetry with the
    /// allowlist is deliberate. An unreadable allowlist must grant nothing, because the risk is a
    /// command running that should not have. An unreadable price list has the opposite shape: the
    /// safe answer is the published rates, and refusing to price anything would silently disable
    /// the very cap the operator set.
    /// </remarks>
    public static TokenPrices Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            var parsed = JsonSerializer.Deserialize<PricingFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            var usable = parsed?.Models?
                .Where(m => !string.IsNullOrWhiteSpace(m.Model))
                .ToList();

            return usable is { Count: > 0 } ? new TokenPrices(usable) : Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    public static void WriteDefaults(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, JsonSerializer.Serialize(
            new PricingFile { Models = [.. Defaults] },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record PricingFile
    {
        [JsonPropertyName("models")]
        public List<ModelPrice>? Models { get; init; }
    }
}
