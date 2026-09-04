namespace RolloutLoud.Core.Workspace;

/// <summary>
/// Every path the tool writes to, derived from one anchor: the repository root.
/// </summary>
/// <remarks>
/// The anchor matters more than it looks. The elevated launch buttons open their CLI *in the
/// repository that hosts RolloutLoud*, so "where is the repo" is the same question as "where
/// does the agent start". Deriving both from one value keeps them from drifting apart.
/// </remarks>
public sealed class RolloutPaths
{
    public RolloutPaths(string repositoryRoot)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        StateRoot = Path.Combine(RepositoryRoot, ".rolloutloud");
    }

    /// <summary>Directory the elevated CLIs are launched in.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Machine-local state. Git-ignored: ledgers carry target output.</summary>
    public string StateRoot { get; }

    public string MissionsDirectory => Path.Combine(StateRoot, "missions");
    public string RunsDirectory => Path.Combine(StateRoot, "runs");
    public string ButtonsFile => Path.Combine(StateRoot, "buttons.json");
    public string AllowlistFile => Path.Combine(StateRoot, "allowlist.json");
    public string BridgeHandshakeFile => Path.Combine(StateRoot, "bridge.json");
    public string AgentsFile => Path.Combine(StateRoot, "agents.json");

    /// <summary>
    /// What a token costs, per model. Overrides the shipped list.
    /// </summary>
    /// <remarks>
    /// A file rather than a constant because this table ages: prices change, models are renamed,
    /// and a figure baked into a release is wrong within months — at which point the money brake is
    /// stopping runs on a number nobody has checked since.
    /// </remarks>
    public string PricingFile => Path.Combine(StateRoot, "pricing.json");

    /// <summary>
    /// What the operator has delegated to a supervising session, per mission.
    /// </summary>
    /// <remarks>
    /// Written by the window only — never by the bridge. A session that could grant itself is not
    /// delegated, and the audit line saying it was would be a fiction. Deleting this file withdraws
    /// every delegation immediately.
    /// </remarks>
    public string DeputyFile => Path.Combine(StateRoot, "deputy.json");

    /// <summary>Details the operator has lent to agents. Absent by default, and absence means no.</summary>
    public string IdentityFile => Path.Combine(StateRoot, "identity.json");

    /// <summary>Every time an agent was handed the identity, and for which site.</summary>
    public string IdentityAuditFile => Path.Combine(StateRoot, "identity-access.log");
    public string VaultDirectory => Path.Combine(RepositoryRoot, "ROLLOUTLOUD-Vault");

    public string MissionFile(string missionId) => Path.Combine(MissionsDirectory, missionId + ".json");
    public string RunDirectory(string attemptId) => Path.Combine(RunsDirectory, attemptId);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(MissionsDirectory);
        Directory.CreateDirectory(RunsDirectory);
    }

    /// <summary>
    /// Walks up from <paramref name="start"/> looking for the repository that hosts RolloutLoud.
    /// Falls back to <paramref name="start"/> so the tool still runs outside a git checkout.
    /// </summary>
    public static RolloutPaths Discover(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        for (var probe = dir; probe is not null; probe = probe.Parent)
        {
            if (Directory.Exists(Path.Combine(probe.FullName, ".git")) ||
                Directory.Exists(Path.Combine(probe.FullName, ".rolloutloud")))
            {
                return new RolloutPaths(probe.FullName);
            }
        }

        return new RolloutPaths(dir.FullName);
    }
}
