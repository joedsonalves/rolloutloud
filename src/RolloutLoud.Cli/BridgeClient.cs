using System.Text;
using System.Text.Json;
using RolloutLoud.Core.Bridge;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Cli;

/// <summary>
/// Talks to a running RolloutLoud, finding it the same way an agent does.
/// </summary>
/// <remarks>
/// Discovery order matters and is worth stating: environment first, then the handshake file.
/// A CLI launched from a RolloutLoud button carries the endpoint in its environment and is
/// therefore certain to be talking to the instance that launched it. A CLI the operator opened
/// by hand has no environment, and falls back to the file in the repository it is sitting in —
/// which is correct, because the repository is the anchor for everything else too.
/// </remarks>
public sealed class BridgeClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private BridgeClient(string endpoint, string token)
    {
        Endpoint = endpoint;
        _http.DefaultRequestHeaders.Add(BridgeContracts.TokenHeader, token);
    }

    public string Endpoint { get; }

    public static BridgeClient? Discover(RolloutPaths paths)
    {
        var endpoint = Environment.GetEnvironmentVariable("ROLLOUTLOUD_BRIDGE");
        var token = Environment.GetEnvironmentVariable("ROLLOUTLOUD_TOKEN");

        if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(token))
        {
            return new BridgeClient(endpoint, token);
        }

        if (!File.Exists(paths.BridgeHandshakeFile))
        {
            return null;
        }

        try
        {
            var handshake = JsonSerializer.Deserialize<BridgeHandshake>(
                File.ReadAllText(paths.BridgeHandshakeFile), Json);

            return handshake is null ? null : new BridgeClient(handshake.Endpoint, handshake.Token);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public async Task<string> GetAsync(string route)
    {
        using var response = await _http.GetAsync(Endpoint + route).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// POSTs JSON. Always sends a body, even an empty object.
    /// </summary>
    /// <remarks>
    /// ⚠️ The empty body is load-bearing on Windows. HttpListener sits behind http.sys, which
    /// rejects a POST carrying neither Content-Length nor chunked encoding with a 411 before the
    /// request ever reaches managed code — so `curl -X POST .../invoke` with no `-d` fails with an
    /// HTML error page and no explanation. Sending `{}` costs two bytes and removes the trap.
    /// </remarks>
    public async Task<string> PostAsync(string route, object? payload = null)
    {
        var body = JsonSerializer.Serialize(payload ?? new { }, Json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint + route, content).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
