using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using RolloutLoud.Core.Bridge;

namespace RolloutLoud.Core.Workspace;

/// <summary>
/// Finds out whether a RolloutLoud is already running for a repository.
/// </summary>
/// <remarks>
/// One instance per repository, and the reason is not tidiness. The bridge publishes its endpoint
/// and token to <c>.rolloutloud/bridge.json</c>, and that file is how every agent that was not
/// launched from a button finds the tool. Two instances in the same folder means the second
/// overwrites it — and every agent the first one launched is then holding a token for a port
/// nobody is listening on, failing with 401s that look like a bug in the bridge.
///
/// So: detect, focus the window that already exists, and exit. That is also what an operator
/// expects from double-clicking an app that is already open.
///
/// **Two windows at once is still supported — in two different folders.** The repository is the
/// anchor for everything, so two repositories is two instances, each with its own port, its own
/// missions and its own agents. Within one folder, the way to run several agents at once is
/// several missions in the one window, which is what the mission list is for.
/// </remarks>
public static class RunningInstance
{
    public sealed record Found(BridgeHandshake Handshake, Process Process);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the handshake and proves the instance is actually alive.
    /// </summary>
    /// <remarks>
    /// Three checks, because a stale handshake is the normal case rather than the exception: the
    /// file outlives a crash, a kill, and a machine that lost power. A live PID is not enough
    /// either — PIDs are recycled, and the recycled one is usually something unrelated. Only a
    /// health response from the port in the file proves it is our process on our port.
    /// </remarks>
    public static Found? Detect(RolloutPaths paths, TimeSpan? timeout = null)
    {
        if (!File.Exists(paths.BridgeHandshakeFile))
        {
            return null;
        }

        BridgeHandshake? handshake;
        try
        {
            handshake = JsonSerializer.Deserialize<BridgeHandshake>(
                File.ReadAllText(paths.BridgeHandshakeFile), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }

        if (handshake is null)
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(handshake.ProcessId);
            if (process.HasExited)
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            // No such process: the handshake outlived it.
            return null;
        }

        return Responds(handshake, timeout ?? TimeSpan.FromSeconds(2)) ? new Found(handshake, process) : null;
    }

    private static bool Responds(BridgeHandshake handshake, TimeSpan timeout)
    {
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            using var response = http.GetAsync(handshake.Endpoint + "/v1/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a handshake whose process is gone, so the next start is not blocked by a ghost.
    /// </summary>
    public static void ClearStale(RolloutPaths paths)
    {
        if (Detect(paths) is not null || !File.Exists(paths.BridgeHandshakeFile))
        {
            return;
        }

        try
        {
            File.Delete(paths.BridgeHandshakeFile);
        }
        catch (IOException)
        {
            // Another instance is mid-write. It will win, which is the right outcome.
        }
    }
}
