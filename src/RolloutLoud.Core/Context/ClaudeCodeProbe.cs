using System.Text.Json;

namespace RolloutLoud.Core.Context;

/// <summary>
/// Reads Claude Code's own transcript for what the API actually counted.
/// </summary>
/// <remarks>
/// Claude Code writes a JSONL transcript per session under
/// <c>~/.claude/projects/&lt;slug&gt;/&lt;session&gt;.jsonl</c>, and every assistant entry carries a
/// <c>usage</c> block with <c>input_tokens</c>, <c>cache_read_input_tokens</c>,
/// <c>cache_creation_input_tokens</c> and <c>output_tokens</c>. Those are the numbers the API
/// charged, so this is a measurement rather than an estimate — verified on this machine against a
/// live session reading 959,687 tokens, almost all of it cache reads.
///
/// **The window is input + both cache figures.** Output is what the model produced, not what it
/// had to read, and counting it would inflate the number that decides whether to offload.
/// Cache reads dominate a long session and leaving them out would understate the window by an
/// order of magnitude — which is the whole quantity being asked about.
///
/// ⚠️ **This reads another program's private files, and that is a real dependency.** The format is
/// not a published contract and can change without notice. Every failure path returns null so the
/// meter falls back to estimating rather than breaking, and the reading says it was measured only
/// when it genuinely was.
/// </remarks>
public sealed class ClaudeCodeProbe : IContextProbe
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true };

    private readonly string _projectsRoot;

    public ClaudeCodeProbe()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"))
    {
    }

    /// <summary>Points the probe at a different transcript root. Used by the tests.</summary>
    public ClaudeCodeProbe(string projectsRoot) => _projectsRoot = projectsRoot;

    public string? AgentId => "claude";

    public ContextReading? TryRead(string repositoryRoot)
    {
        var directory = FindProjectDirectory(repositoryRoot);
        if (directory is null)
        {
            return null;
        }

        FileInfo? newest;
        try
        {
            newest = new DirectoryInfo(directory)
                .EnumerateFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (newest is null)
        {
            return null;
        }

        var tokens = LastWindowSize(newest.FullName);
        if (tokens is null)
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - newest.LastWriteTimeUtc;

        return new ContextReading
        {
            Tokens = tokens.Value,
            Source = ContextSource.Measured,
            Detail = age < TimeSpan.FromMinutes(5)
                ? $"from the live Claude Code transcript ({newest.Name[..8]}…)"
                : $"from a Claude Code transcript last written {age:hh\\:mm} ago — that session may be over",
        };
    }

    /// <summary>
    /// The window as of the most recent assistant turn.
    /// </summary>
    /// <remarks>
    /// Read backwards and stop at the first entry with a non-zero total. The last few lines of a
    /// transcript are often an all-zero usage block written as the session closes, and taking that
    /// literally would report a window of nothing for a session that had just used a million
    /// tokens — which is the reading that matters most and the one it would get wrong.
    /// </remarks>
    private static int? LastWindowSize(string transcript)
    {
        try
        {
            var lines = File.ReadAllLines(transcript);

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                int total;
                try
                {
                    using var document = JsonDocument.Parse(line, Options);

                    if (!document.RootElement.TryGetProperty("message", out var message) ||
                        !message.TryGetProperty("usage", out var usage))
                    {
                        continue;
                    }

                    total =
                        Number(usage, "input_tokens") +
                        Number(usage, "cache_read_input_tokens") +
                        Number(usage, "cache_creation_input_tokens");
                }
                catch (JsonException)
                {
                    // A partially written last line while the session is live. Skip it.
                    continue;
                }

                if (total > 0)
                {
                    return total;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    /// <summary>
    /// Finds the transcript directory for a repository.
    /// </summary>
    /// <remarks>
    /// The slug is the working directory with every non-alphanumeric character replaced by a dash,
    /// so <c>C:\A B\C</c> becomes <c>C--A-B-C</c>. That rule is inferred rather than documented, so
    /// it is only the fast path: the result is confirmed against the <c>cwd</c> field inside the
    /// transcript, and a mismatch falls back to scanning every project directory for one whose
    /// transcripts name this repository. A rule change upstream then costs a slower lookup rather
    /// than a silently wrong answer.
    /// </remarks>
    private string? FindProjectDirectory(string repositoryRoot)
    {
        var root = _projectsRoot;

        if (!Directory.Exists(root))
        {
            return null;
        }

        var guess = Path.Combine(root, Slug(repositoryRoot));
        if (Directory.Exists(guess) && MentionsRepository(guess, repositoryRoot))
        {
            return guess;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(root))
            {
                if (MentionsRepository(candidate, repositoryRoot))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static bool MentionsRepository(string directory, string repositoryRoot)
    {
        try
        {
            var newest = new DirectoryInfo(directory)
                .EnumerateFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is null)
            {
                return false;
            }

            // Only the first few lines: cwd is written on every entry, and reading a
            // hundred-megabyte transcript to confirm a directory name would cost more than the
            // measurement is worth.
            foreach (var line in File.ReadLines(newest.FullName).Take(20))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line, Options);

                    if (document.RootElement.TryGetProperty("cwd", out var cwd) &&
                        cwd.ValueKind == JsonValueKind.String &&
                        string.Equals(
                            Path.TrimEndingDirectorySeparator(cwd.GetString() ?? string.Empty),
                            Path.TrimEndingDirectorySeparator(repositoryRoot),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // Not every line is well-formed while a session is being written.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    public static string Slug(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Concat(trimmed.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
    }
}
