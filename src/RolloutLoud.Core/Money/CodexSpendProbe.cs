using System.Text.Json;

namespace RolloutLoud.Core.Money;

/// <summary>
/// Reads what Codex's own session file says the API counted.
/// </summary>
/// <remarks>
/// Codex writes one JSONL per session under
/// <c>~/.codex/sessions/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/rollout-&lt;timestamp&gt;-&lt;id&gt;.jsonl</c>, and emits a
/// <c>token_count</c> event carrying <c>total_token_usage</c>, <c>last_token_usage</c> and
/// <c>model_context_window</c>. Verified against real transcripts on this machine rather than
/// inferred from documentation.
///
/// <b>The shape is the opposite of Claude Code's, and getting it backwards is an order-of-magnitude
/// error in both directions.</b> Claude Code writes a per-turn usage block, so the bill is the sum.
/// Codex writes a <em>running total</em>, so the bill is the <em>last</em> one — summing those would
/// charge a forty-turn session roughly forty times over.
///
/// Two more facts that arithmetic on a real file settled, not the field names:
///
/// <list type="bullet">
/// <item><c>input_tokens</c> <b>includes</b> <c>cached_input_tokens</c>, so billable fresh input is
/// the difference. Treating them as disjoint double-counts the cached half at the dear rate, which
/// on a long session is most of the bill.</item>
/// <item><c>reasoning_output_tokens</c> is a subset of <c>output_tokens</c>, so it is not added.</item>
/// </list>
///
/// ⚠️ <b>Summing per-turn events is not a substitute for the running total.</b> On a single-model
/// transcript the two agree exactly; on a two-model one, summing overstated by 16% — something is
/// counted in <c>last_token_usage</c> that never reaches the total, most likely a compaction. So the
/// amount comes from the authoritative total, and the per-turn sums are used only to <em>apportion</em>
/// it between models. The total is then exact and only the split is approximate, which is the right
/// way round: a wrong total is a wrong bill, a slightly wrong split is a slightly wrong breakdown.
/// </remarks>
public sealed class CodexSpendProbe : ISpendProbe
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true };

    private readonly string _sessionsRoot;

    public CodexSpendProbe()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"))
    {
    }

    public CodexSpendProbe(string sessionsRoot) => _sessionsRoot = sessionsRoot;

    public string? AgentId => "codex";

    public SpendReading? TryRead(string repositoryRoot, TokenPrices prices, DateTimeOffset? since)
    {
        List<FileInfo> present;
        try
        {
            present = [.. new DirectoryInfo(_sessionsRoot)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }

        if (present.Count == 0)
        {
            return null;
        }

        var totals = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var sessions = 0;

        foreach (var file in present)
        {
            if (since is not null && file.LastWriteTimeUtc < since.Value.UtcDateTime)
            {
                continue;
            }

            if (Accumulate(file.FullName, repositoryRoot, totals))
            {
                sessions++;
            }
        }

        if (sessions == 0)
        {
            // Same rule as the Claude Code probe: a readable store with nothing charged in the
            // window is a MEASURED zero. Returning null would send the meter to its estimate, and
            // the estimate would price a whole accumulated window against a mission seconds old.
            return new SpendReading
            {
                Usd = 0m,
                Source = SpendSource.Measured,
                Detail = since is null
                    ? "no Codex session in this repository has recorded a token count"
                    : "no Codex turns charged since this mission started",
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

            var cost = price.Cost(tally.FreshInput, tally.Output, 0, tally.CacheRead);
            total += cost;

            byModel.Add(new ModelSpend
            {
                Model = model,
                Usd = cost,
                InputTokens = tally.FreshInput,
                OutputTokens = tally.Output,
                CacheReadTokens = tally.CacheRead,
            });
        }

        return new SpendReading
        {
            Usd = total,
            Source = SpendSource.Measured,
            Detail = sessions == 1
                ? "from the Codex session file's own running token count"
                : $"from {sessions} Codex session files' own running token counts",
            UnpricedTokens = unpriced,
            ByModel = [.. byModel.OrderByDescending(m => m.Usd)],
        };
    }

    /// <summary>Fresh input, cache reads and output, already separated.</summary>
    private readonly record struct Tally(long FreshInput, long CacheRead, long Output)
    {
        public long Total => FreshInput + CacheRead + Output;

        public Tally Plus(long fresh, long cached, long output) =>
            new(FreshInput + fresh, CacheRead + cached, Output + output);
    }

    /// <summary>
    /// Adds one session's authoritative total into the running tallies, split between the models it
    /// used. Returns false when the file is not this repository's, or charged nothing.
    /// </summary>
    private static bool Accumulate(string path, string repositoryRoot, Dictionary<string, Tally> totals)
    {
        var perModel = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var model = "unknown";
        var matchesRepository = false;
        Usage? running = null;

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

                    // Both session_meta and turn_context carry cwd, so the repository is stated
                    // outright — no inferred directory-name rule of the kind the Claude Code probe
                    // has to guess at and then confirm.
                    if (payload.TryGetProperty("cwd", out var cwd) &&
                        cwd.ValueKind == JsonValueKind.String &&
                        Same(cwd.GetString(), repositoryRoot))
                    {
                        matchesRepository = true;
                    }

                    // A session can switch models part-way through, and they are not priced alike.
                    // The model lives here rather than on the token_count event, so it has to be
                    // tracked as the file is walked.
                    if (payload.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        model = m.GetString()!;
                    }

                    if (!IsTokenCount(root, payload) ||
                        !payload.TryGetProperty("info", out var info))
                    {
                        continue;
                    }

                    if (Read(info, "total_token_usage") is { } cumulative)
                    {
                        running = cumulative;
                    }

                    if (Read(info, "last_token_usage") is { } turn)
                    {
                        perModel[model] = perModel.GetValueOrDefault(model)
                            .Plus(turn.Fresh, turn.Cached, turn.Output);
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
            return false;
        }

        if (!matchesRepository || running is not { } authoritative || perModel.Count == 0)
        {
            return false;
        }

        Apportion(authoritative, perModel, totals);
        return true;
    }

    /// <summary>
    /// Spreads the session's authoritative total across the models it used.
    /// </summary>
    /// <remarks>
    /// The per-turn sums decide the ratio; the running total decides the amount. With one model the
    /// two agree exactly and this is a no-op, which is the common case. With several, the sums can
    /// exceed the total — measured at 16% on a real transcript — and scaling keeps the bill right
    /// while still saying which model spent it.
    /// </remarks>
    private static void Apportion(
        Usage authoritative,
        Dictionary<string, Tally> perModel,
        Dictionary<string, Tally> totals)
    {
        var summed = perModel.Values.Aggregate(
            new Tally(0, 0, 0),
            (a, b) => a.Plus(b.FreshInput, b.CacheRead, b.Output));

        foreach (var (model, tally) in perModel)
        {
            totals[model] = totals.GetValueOrDefault(model).Plus(
                Share(tally.FreshInput, summed.FreshInput, authoritative.Fresh),
                Share(tally.CacheRead, summed.CacheRead, authoritative.Cached),
                Share(tally.Output, summed.Output, authoritative.Output));
        }
    }

    private static long Share(long part, long whole, long authoritative) =>
        whole <= 0 ? 0 : (long)Math.Round((double)part / whole * authoritative);

    private static bool IsTokenCount(JsonElement root, JsonElement payload) =>
        root.TryGetProperty("type", out var kind) &&
        kind.ValueKind == JsonValueKind.String &&
        kind.GetString() == "event_msg" &&
        payload.TryGetProperty("type", out var inner) &&
        inner.ValueKind == JsonValueKind.String &&
        inner.GetString() == "token_count";

    /// <summary>One usage block, with the overlaps already taken out.</summary>
    private readonly record struct Usage(long Fresh, long Cached, long Output);

    /// <summary>
    /// Reads a usage block, separating fresh input from cached.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>input_tokens</c> is the total and <c>cached_input_tokens</c> is the part of it that was
    /// cached, so fresh input is the difference. Reading them as two separate charges bills the
    /// cached half twice — once at the cache rate and once at the input rate, which is ten times
    /// dearer — and on a long session the cached half is nearly all of it. Clamped at zero because
    /// a bill must never go negative if the two ever disagree.
    /// </remarks>
    private static Usage? Read(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = Number(usage, "input_tokens");
        var cached = Number(usage, "cached_input_tokens");

        return new Usage(Math.Max(0, input - cached), cached, Number(usage, "output_tokens"));
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static bool Same(string? a, string b) =>
        !string.IsNullOrEmpty(a) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}
