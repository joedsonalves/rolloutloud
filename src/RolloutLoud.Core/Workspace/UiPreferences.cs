using System.Text.Json;
using System.Text.Json.Serialization;

namespace RolloutLoud.Core.Workspace;

public enum ThemeChoice
{
    /// <summary>Whatever the OS is set to.</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// The operator's own preferences, stored per user rather than per repository.
/// </summary>
/// <remarks>
/// Deliberately **not** in <c>.rolloutloud/</c>. Everything else the tool writes belongs to a
/// repository — missions, ledgers, the allowlist — and is right to live beside it. A theme
/// belongs to the person: picking light in one project and getting dark in the next would be a
/// bug, not a feature, and it is the kind that never gets reported because it just feels broken.
///
/// Failures are swallowed on both sides. A preferences file that cannot be read costs the default
/// theme; one that cannot be written costs the choice not sticking. Neither is worth a dialog,
/// and neither is worth failing to start over.
/// </remarks>
public sealed record UiPreferences
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Dark by default, and that is a decision rather than a fallback.
    /// </summary>
    /// <remarks>
    /// The window sits next to terminal windows all day, and terminals are dark. Following the OS
    /// would be the more conventional default, but it means the first thing most people see is a
    /// bright panel between two dark ones. Light is one click away and remembered afterwards.
    /// </remarks>
    public ThemeChoice Theme { get; init; } = ThemeChoice.Dark;

    public static string FilePath
    {
        get
        {
            var root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);

            // On a Unix box with neither LOCALAPPDATA nor XDG set, GetFolderPath returns empty
            // rather than throwing, and Path.Combine would then write into the current directory.
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(root, "RolloutLoud", "ui.json");
        }
    }

    public static UiPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath), Options) ?? new UiPreferences()
                : new UiPreferences();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UiPreferences();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice not sticking is a small annoyance; refusing to run is not.
        }
    }

    /// <summary>
    /// The theme actually in force, honouring <c>ROLLOUTLOUD_THEME</c> over the stored choice.
    /// </summary>
    /// <remarks>
    /// The environment variable wins so a translation or contrast check can be run without
    /// touching what the operator picked and then having to put it back.
    ///
    /// [JsonIgnore] because this is derived: without it the serialiser writes it into ui.json
    /// beside Theme, where it is stale the moment the environment changes and reads like a second
    /// setting that does nothing.
    /// </remarks>
    [JsonIgnore]
    public ThemeChoice Effective =>
        Environment.GetEnvironmentVariable("ROLLOUTLOUD_THEME")?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeChoice.Light,
            "dark" => ThemeChoice.Dark,
            "system" => ThemeChoice.System,
            _ => Theme,
        };
}
