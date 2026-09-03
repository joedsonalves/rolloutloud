using System.Text.Json;

namespace RolloutLoud.Core.Context;

/// <summary>
/// Reads how large a Codex session's window has become, from Codex's own session file.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="ClaudeCodeProbe"/>, and the same distinction applies: this wants a
/// <b>level</b>, not a sum. Codex's <c>token_count</c> event carries <c>last_token_usage</c> — what
/// that turn had to read — alongside the running <c>total_token_usage</c>, and it is the former that
/// answers "how big is the window now".
///
/// <b>Input only, cached included, output excluded.</b> <c>input_tokens</c> is already the whole
/// prompt with <c>cached_input_tokens</c> as the part of it that was cached, so it is the window as
/// it stands. Adding the cached figure on top would double the number; leaving output in would count
/// what the model produced rather than what it had to read, which is not what the offload threshold
/// is asking about.
///
/// Codex also reports <c>model_context_window</c>, so the reading can say how full the window is
/// rather than only how big — a number the Claude Code transcript does not give.
///
/// ⚠️ Another program's private format. Every failure path returns null so the meter falls back to
/// estimating rather than reporting a confident zero.
/// </remarks>
public sealed class CodexContextProbe : IContextProbe
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true };

    private readonly string _sessionsRoot;

    public CodexContextProbe()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"))
    {
    }

    public CodexContextProbe(string sessionsRoot) => _sessionsRoot = sessionsRoot;

    public string? AgentId => "codex";

    public ContextReading? TryRead(string repositoryRoot)
    {
        try
        {
            // Newest first, and stop at the first file that is both this repository's and has a
            // token count. An old session for another project must not answer for this one.
            foreach (var file in new DirectoryInfo(_sessionsRoot)
                         .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (Read(file.FullName, repositoryRoot) is not { } reading)
                {
                    continue;
                }

                var age = DateTimeOffset.UtcNow - file.LastWriteTimeUtc;

                return new ContextReading
                {
                    Tokens = reading.Tokens,
                    Source = ContextSource.Measured,
                    Detail = Describe(reading, age),
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }

        return null;
    }

    private static string Describe(Window window, TimeSpan age)
    {
        // ⚠️ Not "P0": that renders as "50 %" under a pt-BR culture and "50%" under en-US, so the
        // same code says two different things depending on the machine it runs on. The percentage
        // is computed and the sign appended, which reads identically everywhere.
        var fullness = window.Capacity > 0
            ? $", {Math.Round(100.0 * window.Tokens / window.Capacity)}% of a {window.Capacity:N0}-token window"
            : string.Empty;

        return age < TimeSpan.FromMinutes(5)
            ? $"from the live Codex session file{fullness}"
            : $"from a Codex session file last written {age:hh\\:mm} ago — that session may be over{fullness}";
    }

    private readonly record struct Window(int Tokens, int Capacity);

    private static Window? Read(string path, string repositoryRoot)
    {
        var matchesRepository = false;
        Window? latest = null;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line, Options);
                    var root = document.RootElement;

                    if (!root.TryGetProperty("payload", out var payload))
                    {
                        continue;
                    }

                    if (payload.TryGetProperty("cwd", out var cwd) &&
                        cwd.ValueKind == JsonValueKind.String &&
                        string.Equals(
                            Path.TrimEndingDirectorySeparator(cwd.GetString() ?? string.Empty),
                            Path.TrimEndingDirectorySeparator(repositoryRoot),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matchesRepository = true;
                    }

                    if (!root.TryGetProperty("type", out var kind) ||
                        kind.GetString() != "event_msg" ||
                        !payload.TryGetProperty("type", out var inner) ||
                        inner.GetString() != "token_count" ||
                        !payload.TryGetProperty("info", out var info) ||
                        !info.TryGetProperty("last_token_usage", out var usage))
                    {
                        continue;
                    }

                    var tokens = (int)Math.Min(int.MaxValue, Number(usage, "input_tokens"));

                    if (tokens > 0)
                    {
                        latest = new Window(tokens, (int)Math.Min(int.MaxValue, Number(info, "model_context_window")));
                    }
                }
                catch (JsonException)
                {
                    // A half-written last line while the session is live.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return matchesRepository ? latest : null;
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
}
