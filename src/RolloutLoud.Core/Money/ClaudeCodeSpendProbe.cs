using System.Text.Json;
using RolloutLoud.Core.Context;

namespace RolloutLoud.Core.Money;

/// <summary>
/// Adds up what Claude Code's transcript says the API charged for.
/// </summary>
/// <remarks>
/// Reads the same JSONL as <see cref="ClaudeCodeProbe"/> and asks a different question of it.
/// That probe wants the <em>last</em> usage block, because the window is a level. This wants
/// <em>every</em> block, because the bill is a sum — and it keeps the four token kinds apart,
/// because they are charged at rates that differ by a factor of fifty between output and cache
/// read.
///
/// It reads the whole transcript rather than tailing it, which is the honest cost of the
/// measurement: a long session is a large file and this walks all of it. Acceptable because the
/// question is asked between attempts, not inside a loop, and because the alternative — remembering
/// a running total across restarts — would go wrong exactly when RolloutLoud was restarted
/// mid-mission, which is a case this product handles on purpose.
///
/// ⚠️ <b>Another program's private file format, again.</b> Every failure path returns null so the
/// meter falls back to an estimate rather than reporting a confident zero. A cap that silently
/// stops firing is the failure mode to avoid: the operator believes they have a brake.
/// </remarks>
public sealed class ClaudeCodeSpendProbe : ISpendProbe
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true };

    private readonly string _projectsRoot;

    public ClaudeCodeSpendProbe()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"))
    {
    }

    public ClaudeCodeSpendProbe(string projectsRoot) => _projectsRoot = projectsRoot;

    public string? AgentId => "claude";

    public SpendReading? TryRead(string repositoryRoot, TokenPrices prices, DateTimeOffset? since)
    {
        var directory = FindProjectDirectory(repositoryRoot);
        if (directory is null)
        {
            return null;
        }

        List<FileInfo> present;
        List<FileInfo> transcripts;
        try
        {
            // A mission that outlives one session spans several transcripts, and charging only the
            // newest would under-report a long run by however many times it was resumed. A file
            // last written before the mission opened cannot hold a turn after it, so skipping those
            // is free — but whether any file is there at all is a separate question, kept separate.
            present = [.. new DirectoryInfo(directory).EnumerateFiles("*.jsonl")];
            transcripts = [.. present.Where(f => since is null || f.LastWriteTimeUtc >= since.Value.UtcDateTime)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (present.Count == 0)
        {
            return null;
        }

        var totals = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var lines = 0;

        foreach (var transcript in transcripts)
        {
            lines += Accumulate(transcript.FullName, totals, since);
        }

        // ⚠️ Zero charged turns in a readable transcript is a MEASURED zero, not an unknown, and
        // conflating the two kills brand-new missions. Returning null here sends the meter to its
        // estimate, which prices the whole accumulated context window — a session that has been
        // running for hours — and charges it to a mission that opened a second ago. Observed: a
        // fresh mission with a $5 cap exhausted immediately at an estimated $5.14, before it had
        // made a single attempt.
        //
        // "I can read this and nothing has been charged since the mission started" is a fact. "I
        // cannot read this at all" is the only case the estimate is for, and that is `present`.
        if (lines == 0)
        {
            return new SpendReading
            {
                Usd = 0m,
                Source = SpendSource.Measured,
                Detail = since is null
                    ? "the transcript records no charged turns yet"
                    : "no charged turns since this mission started",
            };
        }

        var byModel = new List<ModelSpend>();
        var total = 0m;
        var unpriced = 0L;

        foreach (var (model, tally) in totals)
        {
            var price = prices.For(model);

            if (price is null)
            {
                unpriced += tally.Total;
                continue;
            }

            var cost = price.Cost(tally.Input, tally.Output, tally.CacheWrite, tally.CacheRead);
            total += cost;

            byModel.Add(new ModelSpend
            {
                Model = model,
                Usd = cost,
                InputTokens = tally.Input,
                OutputTokens = tally.Output,
                CacheWriteTokens = tally.CacheWrite,
                CacheReadTokens = tally.CacheRead,
            });
        }

        return new SpendReading
        {
            Usd = total,
            Source = SpendSource.Measured,
            Detail = transcripts.Count == 1
                ? $"summed from the Claude Code transcript ({lines:N0} charged turns)"
                : $"summed from {transcripts.Count} Claude Code transcripts ({lines:N0} charged turns)",
            UnpricedTokens = unpriced,
            ByModel = [.. byModel.OrderByDescending(m => m.Usd)],
        };
    }

    private readonly record struct Tally(long Input, long Output, long CacheWrite, long CacheRead)
    {
        public long Total => Input + Output + CacheWrite + CacheRead;

        public Tally Plus(long i, long o, long w, long r) =>
            new(Input + i, Output + o, CacheWrite + w, CacheRead + r);
    }

    /// <summary>
    /// Adds one transcript's charged turns into the running totals. Returns how many it counted.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Turns are keyed by request id where one exists, so a retried turn is charged once.</b>
    /// Claude Code writes an entry per streamed message, and a turn that was interrupted and
    /// resumed can appear more than once with the same usage. Adding blindly double-charges exactly
    /// the sessions that had trouble — which are the long ones, which are the ones a spend cap is
    /// for.
    /// </remarks>
    private static int Accumulate(string path, Dictionary<string, Tally> totals, DateTimeOffset? since)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var counted = 0;

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

                    if (!root.TryGetProperty("message", out var message) ||
                        !message.TryGetProperty("usage", out var usage))
                    {
                        continue;
                    }

                    if (since is not null &&
                        root.TryGetProperty("timestamp", out var stamp) &&
                        stamp.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(stamp.GetString(), out var at) &&
                        at < since.Value)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("requestId", out var id) &&
                        id.ValueKind == JsonValueKind.String &&
                        !seen.Add(id.GetString()!))
                    {
                        continue;
                    }

                    var input = Number(usage, "input_tokens");
                    var output = Number(usage, "output_tokens");
                    var write = Number(usage, "cache_creation_input_tokens");
                    var read = Number(usage, "cache_read_input_tokens");

                    if (input + output + write + read == 0)
                    {
                        continue;
                    }

                    var model = message.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()!
                        : "unknown";

                    totals[model] = totals.GetValueOrDefault(model).Plus(input, output, write, read);
                    counted++;
                }
                catch (JsonException)
                {
                    // A half-written last line while the session is live.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return counted;
        }

        return counted;
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    /// <summary>Same lookup as the context probe, and deliberately the same inferred rule.</summary>
    private string? FindProjectDirectory(string repositoryRoot)
    {
        if (!Directory.Exists(_projectsRoot))
        {
            return null;
        }

        var guess = Path.Combine(_projectsRoot, ClaudeCodeProbe.Slug(repositoryRoot));
        if (Directory.Exists(guess))
        {
            return guess;
        }

        try
        {
            return Directory.EnumerateDirectories(_projectsRoot).FirstOrDefault(Mentions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        bool Mentions(string candidate)
        {
            try
            {
                var newest = new DirectoryInfo(candidate)
                    .EnumerateFiles("*.jsonl")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest is null)
                {
                    return false;
                }

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
    }
}
