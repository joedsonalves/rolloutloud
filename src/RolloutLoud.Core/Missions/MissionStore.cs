using System.Text.Json;
using System.Text.Json.Serialization;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Core.Missions;

/// <summary>
/// One mission plus its ledger, on disk.
/// </summary>
/// <remarks>
/// Persistence is not a convenience here. A mission outlives the agent working it — the CLI
/// crashes, the operator closes the window, the context fills and the session is restarted — and
/// every one of those is normal rather than exceptional in a six-hour run. If the ledger lived in
/// the agent, each restart would begin by repeating the first forty attempts.
/// </remarks>
public sealed record MissionRecord
{
    public required Mission Mission { get; init; }

    public IReadOnlyList<Attempt> Attempts { get; init; } = [];
}

public sealed class MissionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly RolloutPaths _paths;

    public MissionStore(RolloutPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public void Save(Mission mission, MissionLedger ledger)
    {
        var record = new MissionRecord { Mission = mission, Attempts = ledger.Attempts };
        var file = _paths.MissionFile(mission.Id);

        // Write beside, then move. A half-written ledger read back after a crash would present
        // fabricated history to the next agent, which is worse than presenting none.
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
        File.Move(temporary, file, overwrite: true);
    }

    public MissionRecord? Load(string missionId)
    {
        var file = _paths.MissionFile(missionId);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MissionRecord>(File.ReadAllText(file), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<MissionRecord> LoadAll()
    {
        if (!Directory.Exists(_paths.MissionsDirectory))
        {
            return [];
        }

        var records = new List<MissionRecord>();
        foreach (var file in Directory.EnumerateFiles(_paths.MissionsDirectory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<MissionRecord>(File.ReadAllText(file), Options);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // One unreadable mission must not hide the rest.
            }
        }

        return [.. records.OrderByDescending(r => r.Mission.CreatedAt)];
    }
}
