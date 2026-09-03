using System.Text.Json;
using System.Text.RegularExpressions;

namespace RolloutLoud.Core.Workspace;

/// <summary>
/// Details the operator has chosen to lend an agent — an email to sign up with, a display name,
/// a phone number — for work that genuinely needs an identity.
/// </summary>
/// <remarks>
/// **Absence is the answer.** No file means no identity was lent, and an agent that asks gets a
/// refusal telling it not to create accounts. That is the operator's design and it is the right
/// one: consent here is an explicit act — writing a file — rather than a default that has to be
/// found and switched off.
///
/// Three things this deliberately does NOT do:
///
/// - **It is never folded into a briefing.** The agent has to ask, naming the site, which is what
///   produces an audit trail. Injecting it into every briefing would put the operator's email
///   into the context of every round whether it was needed or not.
/// - **It does not hold passwords or payment details.** Fields are for identity, not for
///   credentials. An agent that needs a secret should ask for a fluid button so the operator runs
///   the privileged step themselves.
/// - **It does not pretend to be secure.** This is plaintext on disk, readable by anything running
///   as the operator, and every read is sent to a model provider as part of the agent's context.
///   The file says so, and so does the documentation.
///
/// <see cref="AllowedSites"/> is the same idea as the mission scope: the agent may only be handed
/// the identity for a site the operator listed in advance.
/// </remarks>
public sealed record AttachedIdentity
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Free text shown to the agent alongside the fields — house rules, in the operator's words.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Sites the identity may be used on. Empty means none, which makes an empty file safe.
    /// </summary>
    /// <remarks>
    /// Empty-means-none rather than empty-means-all, for the same reason the button allowlist
    /// drops a bare <c>*</c>: a file someone created and did not finish filling in must not be a
    /// wider grant than one they never created at all.
    /// </remarks>
    public IReadOnlyList<string> AllowedSites { get; init; } = [];

    /// <summary>The details themselves. Names are the operator's to choose.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsUsable => Fields.Count > 0 && AllowedSites.Count > 0;

    public static AttachedIdentity? Load(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AttachedIdentity>(File.ReadAllText(file), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Unreadable is treated as absent. Failing closed here means an agent is told there
            // is no identity, which is the safe misreading of a broken file.
            return null;
        }
    }

    /// <summary>Whether this identity may be handed over for a given site.</summary>
    public bool AllowsSite(string? site)
    {
        if (string.IsNullOrWhiteSpace(site) || AllowedSites.Count == 0)
        {
            return false;
        }

        var normalized = Normalize(site);

        foreach (var allowed in AllowedSites)
        {
            var pattern = "^" + Regex.Escape(Normalize(allowed)).Replace("\\*", "[^/]*", StringComparison.Ordinal) + "$";
            if (Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces a site to a host, so "https://App.Example.com/signup" and "app.example.com" match.
    /// </summary>
    private static string Normalize(string site)
    {
        var trimmed = site.Trim().ToLowerInvariant();
        trimmed = Regex.Replace(trimmed, @"^[a-z]+://", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1));
        trimmed = trimmed.Split('/')[0];
        return trimmed.Split(':')[0];
    }

    /// <summary>A starter file, written only when the operator asks for one.</summary>
    public static void WriteTemplate(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var template = new AttachedIdentity
        {
            Note =
                "Details an agent may use to sign up on the sites listed below. Delete this file to " +
                "withdraw it — with no file, agents are told not to create accounts at all. " +
                "This is plaintext on disk, and anything read from it becomes part of the agent's " +
                "context, which means it reaches the model provider. Do not put passwords, payment " +
                "details or recovery codes here.",
            AllowedSites = ["app.staging.example.com"],
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = "you+rolloutloud@example.com",
                ["displayName"] = "Test Account",
            },
        };

        File.WriteAllText(file, JsonSerializer.Serialize(template, Options));
    }
}

/// <summary>The answer to an agent asking for the attached identity.</summary>
public sealed record IdentityDisclosure
{
    public required bool Granted { get; init; }

    public required string Reason { get; init; }

    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static IdentityDisclosure Refused(string reason) => new() { Granted = false, Reason = reason };
}
