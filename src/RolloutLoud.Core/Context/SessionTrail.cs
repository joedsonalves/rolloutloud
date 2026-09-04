using System.Text.Json;

namespace RolloutLoud.Core.Context;

/// <summary>
/// Which transcript belongs to which session RolloutLoud started.
/// </summary>
/// <remarks>
/// <b>Two sessions in one repository share a transcript folder</b>, and every meter here reads that
/// folder. That was harmless while the supervisor sat in the anchor and the worker sat somewhere
/// else — and it stops being harmless the moment the work IS the anchor, which is exactly what a
/// run that improves this tool looks like. The context reading would take whichever file was newest
/// and the spend would sum both, so a per-role ceiling would fire on somebody else's tokens.
///
/// It is the same mistake as reading the meters at the anchor when the agent worked elsewhere, one
/// level in: right folder, wrong session.
///
/// <b>Attribution is by appearance, because nothing hands back a session id.</b> The launcher notes
/// which transcripts existed before it started a role; the one that shows up afterwards is that
/// role's. Recorded on disk so it survives the restart that this project's own build rule
/// guarantees.
///
/// ⚠️ It can be wrong. Two sessions started in the same second, or the operator opening their own
/// CLI in the same folder at the wrong moment, and the claim goes to the wrong file. So a reading
/// through this says which transcript it used, and an unattributed role falls back to the old
/// whole-folder behaviour rather than reporting nothing — a rough number that exists beats a
/// precise one that is missing.
/// </remarks>
public sealed class SessionTrail
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _claims;

    public SessionTrail(string path)
    {
        _path = path;
        _claims = Load(path);
    }

    /// <summary>The transcripts present in a folder right now. Taken before a launch.</summary>
    public static IReadOnlySet<string> Snapshot(string transcriptsRoot)
    {
        try
        {
            return new DirectoryInfo(transcriptsRoot)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Select(f => f.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Claims whatever transcript appeared after a launch for the role that launched.
    /// </summary>
    /// <remarks>
    /// Called a little after the launch rather than immediately: the CLI writes its first line when
    /// it has finished starting, which behind an antivirus scan is not instant. Claiming nothing is
    /// the right outcome when nothing appeared — a wrong claim is worse than none, because the
    /// meter would then report a confident figure for the wrong session.
    /// </remarks>
    public string? Claim(string key, string transcriptsRoot, IReadOnlySet<string> before)
    {
        var appeared = Snapshot(transcriptsRoot)
            .Where(f => !before.Contains(f))
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault();

        if (appeared is null)
        {
            return null;
        }

        lock (_gate)
        {
            _claims[key] = appeared;
            Save();
        }

        return appeared;
    }

    /// <summary>The transcript claimed for a role, or null when none was.</summary>
    public string? For(string key)
    {
        lock (_gate)
        {
            if (!_claims.TryGetValue(key, out var path))
            {
                return null;
            }

            // A claim whose file has gone is worse than no claim: it would make every reading
            // return nothing while looking like it had an answer.
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>Forgets a role's claim, so its next launch starts attribution over.</summary>
    public void Release(string key)
    {
        lock (_gate)
        {
            if (_claims.Remove(key))
            {
                Save();
            }
        }
    }

    /// <summary>The key a role is filed under. One session per role per mission.</summary>
    public static string KeyFor(string missionId, string role) => missionId + ":" + role;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_claims, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Attribution is an optimisation over reading the whole folder. Losing it costs
            // precision, never the run.
        }
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                  ?? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
