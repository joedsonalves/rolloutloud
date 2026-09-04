namespace RolloutLoud.Core.Missions;

/// <summary>
/// The mode where whoever is steering the run is denied its raw material on purpose.
/// </summary>
/// <remarks>
/// <b>It is not "you see nothing". It is "you see the deliverable and the ledger, not the raw
/// material"</b> — which is how a reviewer actually works, and the operator's own view of a run.
///
/// Two reasons, and both are already load-bearing elsewhere in this product.
///
/// <b>A supervisor that reads everything is not a supervisor, it is a second worker.</b> The whole
/// argument for offload is that a fresh subagent produces better attempts because it does not carry
/// forty failures' worth of tunnel vision. That applies at least as much to the session doing the
/// judging — and it is the session whose context is most expensive to fill.
///
/// <b>In a pentest, target output is attacker-controlled.</b> The injection guard exists because
/// that text reaches contexts and persists in the ledger. Keeping the supervising session out of
/// the raw traffic removes it from the blast radius entirely, rather than fencing it and hoping.
///
/// <b>The wall has exactly one window, and it is the deliverable.</b> See
/// <see cref="Mission.Deliverable"/>: the one path the supervisor is meant to read, because it is
/// the thing the work is for. Reading the report and saying what is missing is the job; reading the
/// scan output is not.
///
/// ⚠️ <b>This is a guard rail, not a sandbox</b>, in exactly the sense <see cref="MissionScope"/>
/// is. It redacts what the bridge serves. It cannot stop a supervising session from opening a run
/// folder with its own file tools, and nothing in this product could. It exists to stop honest
/// drift — the reach for the transcript "just to check" — and to make the size of what was withheld
/// visible, so nobody believes the wall is taller than it is.
/// </remarks>
public static class FourthWall
{
    /// <summary>Fields taken out of one ledger entry. Three, and they are always the same three.</summary>
    public const int FieldsPerEntry = 3;

    /// <summary>
    /// Strips an entry back to what a question about the past actually needs.
    /// </summary>
    /// <remarks>
    /// The hypothesis and what it ruled out stay; the argv, the exit code and the artifact folder
    /// go. That split is not arbitrary — it is the one the ledger query already argued for on its
    /// own terms: "what has been ruled out" almost never needs the exact argv, which is why those
    /// fields were opt-in in the first place. This mode turns the default into a rule.
    /// </remarks>
    public static LedgerEntry Redact(LedgerEntry entry) => entry with
    {
        Command = null,
        ExitCode = null,
        Artifacts = null,
    };

    /// <summary>Why <c>full=true</c> is refused, said to whoever asked for it.</summary>
    public const string FullRefused =
        "You get the hypothesis and what it ruled out. The argv, the exit code and the artifact " +
        "folder are the raw material this mode exists to keep out of your context — ask the agent " +
        "what it did, or read the deliverable, which is the one thing behind this wall you are " +
        "meant to see.";

    /// <summary>
    /// What the briefing tells the working agent about being read at a distance.
    /// </summary>
    /// <remarks>
    /// The second-order effect, and it is free: an agent told that nobody will read its output
    /// writes a better <c>learned</c> and a better deliverable, because those become the only
    /// channel rather than a summary of something the reader could go and check.
    /// </remarks>
    public const string AgentNotice =
        "**Whoever is supervising this run cannot see your raw output.** Not the command lines, not " +
        "the exit codes, not the artifact folders — RolloutLoud withholds them on this mission. What " +
        "reaches them is what you write in `learned`, and the deliverable.\n\n" +
        "So write both for someone who was not there. \"Failed\" tells them nothing; \"the endpoint " +
        "answered 403 to an unauthenticated PUT, so the missing control is not authorisation\" tells " +
        "them what you ruled out. If something only makes sense with the output in front of you, put " +
        "the part that matters into the observation — it is the only copy they will ever read.";
}

/// <summary>Running count of what a Fourth Wall mission has kept from its supervisor.</summary>
/// <remarks>
/// Counted rather than merely done, because the operator's question about this mode is "how much did
/// it not see?" — and a wall whose height nobody can state is one people quietly stop believing in.
/// It is also the honest counterweight to the guard-rail caveat: the number says what the bridge
/// withheld, and says nothing about what was read around it.
/// </remarks>
public sealed class FourthWallAudit
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _withheld = new(StringComparer.Ordinal);

    public void Record(string missionId, int fields)
    {
        if (fields <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _withheld[missionId] = _withheld.GetValueOrDefault(missionId) + fields;
        }
    }

    public int For(string missionId)
    {
        lock (_gate) { return _withheld.GetValueOrDefault(missionId); }
    }

    public int Total
    {
        get { lock (_gate) { return _withheld.Values.Sum(); } }
    }
}
