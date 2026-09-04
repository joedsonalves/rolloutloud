using System.Text.Json;
using System.Text.Json.Serialization;

namespace RolloutLoud.Core.Consent;

/// <summary>
/// The operator letting a supervising session click on their behalf, for one mission.
/// </summary>
/// <remarks>
/// The problem it answers: some decisions in this product are deliberately the operator's — opening
/// an agent outside the anchor, running a command the allowlist does not cover. When a session is
/// asked to <em>play</em> the operator, those become a wall that only the operator can climb, and
/// the run stops until somebody is at the keyboard.
///
/// <b>This is not a bypass, and the distinction is the whole design.</b> Nothing at OS level was
/// standing in the way — the check is a line in RolloutLoud's own code. Routing around our own rule
/// would leave the rule looking intact while meaning nothing. So the rule changes in the open: the
/// operator grants specific consents <em>ahead of time, in writing</em>, which is exactly the shape
/// <see cref="Buttons.ButtonAllowlist"/> already has. Same consent, moved earlier, and recorded.
///
/// <b>Per mission, and it dies with the mission.</b> A grant covers the mission it names and no
/// other, so a button belonging to a run the operator never looked at is not covered by a
/// delegation they gave for one they did.
///
/// <b>Only the window writes this.</b> There is deliberately no bridge route that creates a grant.
/// A supervising session that could grant itself is not delegated, it is helping itself, and the
/// audit line would be a fiction.
///
/// ⚠️ <b><see cref="Deputy"/> is a label, not a boundary.</b> One token authenticates every caller
/// on this bridge, so the name is for the record rather than for enforcement. What actually bounds
/// a grant is the mission it names and the two capabilities it can carry — and saying that plainly
/// beats implying an identity check that is not there.
/// </remarks>
public sealed record DeputyGrant
{
    /// <summary>The mission this delegation covers. It covers no other.</summary>
    public required string MissionId { get; init; }

    /// <summary>Who the operator delegated to. For the audit line; see the remarks.</summary>
    public string Deputy { get; init; } = "a supervising session";

    /// <summary>May click the button that opens an agent outside RolloutLoud's anchor.</summary>
    public bool MayLaunchOutsideAnchor { get; init; }

    /// <summary>
    /// May click fluid buttons the allowlist does not cover.
    /// </summary>
    /// <remarks>
    /// The broader of the two, and worth the operator knowing it: this is the mechanism for "a
    /// human decides this one", and a grant covers an arbitrary command rather than a named class.
    /// It stays bounded by the mission and by the fact that every click is recorded as delegated —
    /// but the boundary is narrower than the allowlist's, which at least matches on the command.
    /// </remarks>
    public bool MayClickUnlistedButtons { get; init; }

    public DateTimeOffset GrantedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Why the operator delegated, in their words. Goes into the audit line.</summary>
    public string? Note { get; init; }

    public bool Covers(string missionId) =>
        string.Equals(MissionId, missionId, StringComparison.Ordinal);
}

/// <summary>
/// What the operator has delegated, re-read from disk on every question.
/// </summary>
/// <remarks>
/// Fails closed in every direction — absent, unreadable, malformed, a grant with no mission — for
/// the same reason the allowlist does: a delegation that appears when the policy file is corrupt is
/// worse than none, because the operator believes there is a policy.
///
/// Re-read rather than cached, so deleting the file withdraws the delegation immediately. That is
/// the operator's only lever once a run is going, and it has to work without a restart.
/// </remarks>
public sealed class DeputyRegister
{
    public static DeputyRegister Empty { get; } = new([]);

    private readonly IReadOnlyList<DeputyGrant> _grants;

    public DeputyRegister(IEnumerable<DeputyGrant> grants) =>
        _grants = [.. grants.Where(g => !string.IsNullOrWhiteSpace(g.MissionId))];

    public IReadOnlyList<DeputyGrant> All => _grants;

    /// <summary>The grant covering a mission, or null when the operator delegated nothing.</summary>
    public DeputyGrant? For(string? missionId) =>
        string.IsNullOrWhiteSpace(missionId)
            ? null
            : _grants.LastOrDefault(g => g.Covers(missionId));

    public static DeputyRegister Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Empty;
            }

            var parsed = JsonSerializer.Deserialize<GrantFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            return parsed?.Grants is { Count: > 0 } grants ? new DeputyRegister(grants) : Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// Replaces the grants on disk. Called from the window only.
    /// </summary>
    /// <remarks>
    /// Grants for missions that are no longer open are dropped as they pass through, so the file
    /// does not accumulate delegations for runs that ended weeks ago — a stale permission is one
    /// that stopped being a decision.
    /// </remarks>
    public static void Write(string path, IEnumerable<DeputyGrant> grants)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        File.WriteAllText(path, JsonSerializer.Serialize(
            new GrantFile { Grants = [.. grants] },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record GrantFile
    {
        [JsonPropertyName("grants")]
        public List<DeputyGrant>? Grants { get; init; }
    }
}
