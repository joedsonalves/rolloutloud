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
    ];

    public static TokenPrices Default { get; } = new(Defaults);

    private readonly IReadOnlyList<ModelPrice> _prices;

    public TokenPrices(IEnumerable<ModelPrice> prices) =>
        // Longest prefix first, so "claude-opus-4-5" beats "claude-opus" when both are listed and
        // the operator has priced one build differently from its family.
        _prices = [.. prices.OrderByDescending(p => p.Model.Length)];

    public IReadOnlyList<ModelPrice> All => _prices;

    /// <summary>The price for a model id from a transcript, or null when nothing covers it.</summary>
    public ModelPrice? For(string? model) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : _prices.FirstOrDefault(p => model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase));

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
