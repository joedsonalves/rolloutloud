using RolloutLoud.Core.Missions;

namespace RolloutLoud.Core.Workspace;

public sealed record HousekeepingPolicy
{
    /// <summary>Run folders older than this go, however few there are.</summary>
    public TimeSpan MaxRunAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Run folders kept regardless of age, newest first.</summary>
    /// <remarks>
    /// A count as well as an age, because the two failures are different. A project that runs for
    /// months accumulates by age; a project that fires ten subagents a session accumulates by
    /// count in a week, and an age limit alone would leave twelve thousand directories in one
    /// folder — which is slow to enumerate long before it is large on disk.
    /// </remarks>
    public int MaxRuns { get; init; } = 500;

    /// <summary>Finished missions older than this are moved aside, not deleted.</summary>
    public TimeSpan ArchiveMissionsAfter { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Tidy up on startup rather than waiting to be asked.</summary>
    public bool RunOnStartup { get; init; } = true;
}

public sealed record HousekeepingReport
{
    public int RunsRemoved { get; init; }

    public long BytesReclaimed { get; init; }

    public int MissionsArchived { get; init; }

    public int RunsKept { get; init; }

    public int MissionsActive { get; init; }

    public long BytesOnDisk { get; init; }

    public bool DidAnything => RunsRemoved > 0 || MissionsArchived > 0;

    public string Summary =>
        $"{RunsKept} run folder(s), {MissionsActive} mission(s), {Format(BytesOnDisk)} on disk" +
        (DidAnything
            ? $" — tidied {RunsRemoved} run(s) ({Format(BytesReclaimed)}) and archived {MissionsArchived} mission(s)."
            : ".");

    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>
/// Keeps <c>.rolloutloud/</c> from growing without limit.
/// </summary>
/// <remarks>
/// Nothing ever deleted a run folder, and nothing ever stopped loading a finished mission. That is
/// fine for a project where an operator starts one mission a day, and it is not fine for one where
/// the main agent fires ten subagents from the first turn — which is how several real projects
/// work. Measured: a subagent round costs about 1.7 KB with a stub that answers in five lines, and
/// tens of kilobytes with a real one that returns a transcript. Ten a session, several sessions a
/// day, is thousands of directories inside a month.
///
/// The count limit matters more than the size limit. Thirty megabytes is nothing; twelve thousand
/// directories in one folder is slow to enumerate, slow to open, and slow to back up, long before
/// the disk notices.
///
/// **Missions are archived, never deleted.** They hold the ledger — the record of what was ruled
/// out — and that is the most expensive thing the tool produces. Moving them out of the load path
/// keeps startup fast and the mission list readable without throwing away the reasoning.
/// </remarks>
public sealed class Housekeeper
{
    private readonly RolloutPaths _paths;

    public Housekeeper(RolloutPaths paths) => _paths = paths;

    public HousekeepingPolicy Policy { get; set; } = new();

    /// <summary>
    /// Prunes run folders and archives finished missions.
    /// </summary>
    /// <param name="protectedRuns">
    /// Run folders that must survive whatever their age — the artifact directories of missions
    /// that are still open. Deleting the evidence under a running mission would leave ledger
    /// entries pointing at nothing, which is worse than any amount of disk.
    /// </param>
    public HousekeepingReport Tidy(IReadOnlyCollection<string>? protectedRuns = null)
    {
        var keep = new HashSet<string>(
            protectedRuns ?? [],
            StringComparer.OrdinalIgnoreCase);

        var (removed, reclaimed, kept) = PruneRuns(keep);
        var archived = ArchiveMissions();

        return new HousekeepingReport
        {
            RunsRemoved = removed,
            BytesReclaimed = reclaimed,
            RunsKept = kept,
            MissionsArchived = archived,
            MissionsActive = CountMissions(),
            BytesOnDisk = Measure(_paths.StateRoot),
        };
    }

    /// <summary>Run folders belonging to missions that are still open.</summary>
    public static IReadOnlyCollection<string> ProtectedRunsFor(IEnumerable<Mission> missions, MissionStore store)
    {
        var protectedRuns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mission in missions.Where(m => !m.IsTerminal))
        {
            var record = store.Load(mission.Id);
            if (record is null)
            {
                continue;
            }

            foreach (var attempt in record.Attempts)
            {
                if (!string.IsNullOrWhiteSpace(attempt.ArtifactDirectory))
                {
                    protectedRuns.Add(Path.GetFileName(attempt.ArtifactDirectory.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                }
            }
        }

        return protectedRuns;
    }

    private (int Removed, long Reclaimed, int Kept) PruneRuns(HashSet<string> keep)
    {
        if (!Directory.Exists(_paths.RunsDirectory))
        {
            return (0, 0, 0);
        }

        List<DirectoryInfo> runs;
        try
        {
            runs = [.. new DirectoryInfo(_paths.RunsDirectory)
                .EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0, 0);
        }

        var cutoff = DateTime.UtcNow - Policy.MaxRunAge;
        var removed = 0;
        long reclaimed = 0;
        var kept = 0;
        var index = 0;

        foreach (var run in runs)
        {
            index++;

            var tooOld = run.LastWriteTimeUtc < cutoff;
            var tooMany = index > Policy.MaxRuns;

            if (keep.Contains(run.Name) || (!tooOld && !tooMany))
            {
                kept++;
                continue;
            }

            var size = Measure(run.FullName);

            try
            {
                run.Delete(recursive: true);
                removed++;
                reclaimed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something has it open. It will be gone on the next pass; a failed tidy is not
                // worth an error the operator has to act on.
                kept++;
            }
        }

        return (removed, reclaimed, kept);
    }

    private int ArchiveMissions()
    {
        if (!Directory.Exists(_paths.MissionsDirectory))
        {
            return 0;
        }

        var archive = Path.Combine(_paths.MissionsDirectory, "archive");
        var cutoff = DateTimeOffset.UtcNow - Policy.ArchiveMissionsAfter;
        var store = new MissionStore(_paths);
        var archived = 0;

        foreach (var file in Directory.EnumerateFiles(_paths.MissionsDirectory, "*.json"))
        {
            var record = store.Load(Path.GetFileNameWithoutExtension(file));

            // Only finished missions, and only once they have been finished a while. An open
            // mission is never moved however old it is — somebody may still be working it.
            if (record is null ||
                !record.Mission.IsTerminal ||
                (record.Mission.EndedAt ?? record.Mission.CreatedAt) > cutoff)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(archive);
                File.Move(file, Path.Combine(archive, Path.GetFileName(file)), overwrite: true);
                archived++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try again next time.
            }
        }

        return archived;
    }

    private int CountMissions()
    {
        try
        {
            return Directory.Exists(_paths.MissionsDirectory)
                ? Directory.EnumerateFiles(_paths.MissionsDirectory, "*.json").Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long Measure(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
