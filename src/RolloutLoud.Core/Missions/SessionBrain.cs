using System.Text;
using System.Text.Json;

namespace RolloutLoud.Core.Missions;

/// <summary>
/// One session's handover: what it came to believe, and what it stopped trusting.
/// </summary>
/// <remarks>
/// Deliberately not "everything it knew". A transcript is not a handover — it is the raw material a
/// handover exists to replace, and copying it forward would recreate the expensive window the switch
/// was made to escape.
///
/// The three fields are the ones a ledger cannot carry. What was tried is already recorded; what the
/// session came to <em>believe</em>, which of its own assumptions it dropped, and what it would do
/// next are only in its head — and are the first three things somebody picking the work up cold
/// would ask for.
/// </remarks>
public sealed record Handover
{
    public required string Role { get; init; }

    public required string From { get; init; }

    /// <summary>What it came to believe. Not what it tried — the ledger has that.</summary>
    public required string Believes { get; init; }

    /// <summary>Assumptions it stopped trusting. The half that saves the next session a day.</summary>
    public string? Dropped { get; init; }

    /// <summary>The most promising thing it had not got to.</summary>
    public string? Next { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Window size when it wrote this, so the next one can see the cost it inherited.</summary>
    public int? WindowTokens { get; init; }
}

/// <summary>
/// What a supervisor knows, kept where a crash cannot take it and the worker will not read it.
/// </summary>
/// <remarks>
/// <b>Not the Obsidian vault, on purpose.</b> That vault is the operator's long memory of the
/// product and it is read by whoever is working in the repository — which on a self-improving run is
/// the worker. A supervisor's running notes are a different thing with a different reader: they are
/// how the <em>next</em> supervisor picks up, and a worker reading its own supervisor's assessment
/// of it changes what the worker does.
///
/// So it lives under the anchor's own state folder, alongside the ledger. When the mission works in
/// another repository that is real separation; when it works in the anchor it is a guard rail, the
/// same kind and with the same honesty as everything else here — it stops the reach, not the
/// determined.
///
/// <b>What it is for is a power cut.</b> Missions and ledgers already survive one. What did not was
/// the supervising side: the chain of handovers, and the reasoning that made this supervisor pick up
/// where the last one left off. Losing that turns a restart into a fresh start.
/// </remarks>
public sealed class SessionBrain
{
    private readonly Lock _gate = new();
    private readonly string _root;

    public SessionBrain(string root) => _root = root;

    /// <summary>Everything handed over on a mission, oldest first.</summary>
    public IReadOnlyList<Handover> Chain(string missionId)
    {
        lock (_gate)
        {
            try
            {
                var file = FileFor(missionId);

                return File.Exists(file)
                    ? JsonSerializer.Deserialize<List<Handover>>(File.ReadAllText(file)) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A brain that cannot be read must not stop a run. The next session picks up with
                // the ledger alone, which is worse and is not nothing.
                return [];
            }
        }
    }

    public void Record(string missionId, Handover handover)
    {
        lock (_gate)
        {
            var chain = Chain(missionId).ToList();
            chain.Add(handover);

            try
            {
                Directory.CreateDirectory(_root);
                File.WriteAllText(
                    FileFor(missionId),
                    JsonSerializer.Serialize(chain, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same call as reading: losing the note costs the next session context, never the
                // run.
            }
        }
    }

    /// <summary>
    /// Whether anything was handed over on this mission for this role.
    /// </summary>
    /// <remarks>
    /// <see cref="Narrate"/> answers "you are the first on this" rather than nothing, which is the
    /// right thing to hand an agent that asks — and the wrong thing to paste into a briefing, where
    /// it becomes a section about the absence of a section. The caller composing a briefing asks
    /// this first.
    /// </remarks>
    public bool HasAny(string missionId, string role) =>
        Chain(missionId).Any(h => string.Equals(h.Role, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>When the most recent handover for this role was written, or null if there is none.</summary>
    /// <remarks>
    /// This is the token the turn handover spends. A session is replaced only when a note exists
    /// that no replacement has used yet, which is what keeps the swap from firing twice on the same
    /// ceiling — the outgoing session has to have done its part before it is closed.
    /// </remarks>
    public DateTimeOffset? LatestAt(string missionId, string role) =>
        Chain(missionId)
            .Where(h => string.Equals(h.Role, role, StringComparison.OrdinalIgnoreCase))
            .Select(h => (DateTimeOffset?)h.At)
            .DefaultIfEmpty(null)
            .Max();

    /// <summary>
    /// The chain as a fresh session should read it, newest last.
    /// </summary>
    /// <remarks>
    /// Capped, and the cap is the point of the whole mechanism: a chain of twenty handovers pasted
    /// in full is the expensive window the handovers existed to escape, rebuilt one note at a time.
    /// The most recent few are what a session picking up actually needs; the rest is history, and
    /// history belongs on disk rather than in a context.
    /// </remarks>
    public string Narrate(string missionId, string role, int keep = 3)
    {
        var mine = Chain(missionId)
            .Where(h => string.Equals(h.Role, role, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mine.Count == 0)
        {
            return "No previous session handed anything over. You are the first on this.";
        }

        var sb = new StringBuilder();

        if (mine.Count > keep)
        {
            sb.AppendLine(
                $"[{mine.Count - keep} earlier handover(s) not shown. They are in the session brain " +
                "if you need them, and needing them is rare.]");
            sb.AppendLine();
        }

        foreach (var handover in mine.TakeLast(keep))
        {
            sb.AppendLine($"### From {handover.From}, {handover.At:yyyy-MM-dd HH:mm}" +
                          (handover.WindowTokens is { } w ? $" (window was {w:N0})" : string.Empty));
            sb.AppendLine();
            sb.AppendLine($"**Came to believe:** {handover.Believes}");

            if (!string.IsNullOrWhiteSpace(handover.Dropped))
            {
                sb.AppendLine($"**Stopped trusting:** {handover.Dropped}");
            }

            if (!string.IsNullOrWhiteSpace(handover.Next))
            {
                sb.AppendLine($"**Would do next:** {handover.Next}");
            }

            sb.AppendLine();
        }

        sb.AppendLine(
            "⚠️ That is one session's opinion, not a finding. It is here because it names the " +
            "assumptions worth re-testing — including the ones it says it stopped trusting.");

        return sb.ToString();
    }

    private string FileFor(string missionId) => Path.Combine(_root, missionId + ".json");
}
