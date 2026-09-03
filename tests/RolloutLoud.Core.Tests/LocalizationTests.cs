using System.Text.RegularExpressions;
using RolloutLoud.Core.Localization;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// Guards the two ways localisation breaks without anything failing to compile.
/// </summary>
public class LocalizationTests
{
    [Fact]
    public void Every_shipped_table_actually_loads_from_the_assembly()
    {
        // The satellite-assembly trap: name a resource Strings.en.json and MSBuild compiles it
        // into a satellite assembly instead of this one. Build succeeds, nothing warns,
        // GetManifestResourceStream returns null, and the whole UI renders as [key]. This test is
        // the only thing standing between that and a release.
        foreach (var language in Localizer.Available)
        {
            var table = Localizer.TableFor(language);

            Assert.True(
                table is { Count: > 0 },
                $"Strings.{language}.json did not load. Check WithCulture=\"false\" and LogicalName in " +
                "RolloutLoud.Core.csproj — a culture-shaped resource name silently becomes a satellite assembly.");
        }
    }

    [Fact]
    public void Every_table_has_exactly_the_same_keys()
    {
        var english = Localizer.TableFor("en")!;

        foreach (var language in Localizer.Available.Where(l => l != "en"))
        {
            var table = Localizer.TableFor(language)!;

            var missing = english.Keys.Except(table.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var extra = table.Keys.Except(english.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.True(missing.Count == 0, $"Strings.{language}.json is missing: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"Strings.{language}.json has keys English does not: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void Every_table_uses_the_same_placeholders_for_a_given_key()
    {
        // A translation that drops {0}, or invents a {2}, throws FormatException at run time —
        // in whichever language the operator happens to use, which is the one nobody tested.
        var english = Localizer.TableFor("en")!;

        foreach (var language in Localizer.Available.Where(l => l != "en"))
        {
            var table = Localizer.TableFor(language)!;

            foreach (var (key, source) in english)
            {
                Assert.Equal(Placeholders(source), Placeholders(table[key]));
            }
        }
    }

    [Fact]
    public void An_unknown_language_falls_back_to_english_rather_than_failing()
    {
        Localizer.Initialize("kl-GL");

        Assert.Equal("en", Localizer.Current.Language);
        Assert.Equal("AGENTS", Localizer.Current["agents.title"]);
    }

    [Fact]
    public void A_region_variant_resolves_to_its_language()
    {
        // pt-BR and pt-PT both have to land on pt. Matching the full tag would leave a Brazilian
        // machine reading English because of a region code.
        Localizer.Initialize("pt-BR");
        Assert.Equal("pt", Localizer.Current.Language);

        Localizer.Initialize("es-419");
        Assert.Equal("es", Localizer.Current.Language);

        Localizer.Initialize("en");
    }

    [Fact]
    public void A_missing_key_is_visible_rather_than_fatal()
    {
        Localizer.Initialize("en");

        Assert.Equal("[no.such.key]", Localizer.Current["no.such.key"]);
    }

    private static string Placeholders(string value) =>
        string.Join(
            ",",
            Regex.Matches(value, @"\{(\d+)\}", RegexOptions.None, TimeSpan.FromSeconds(1))
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(v => v, StringComparer.Ordinal));
}
