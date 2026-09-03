using System.Text.Json;
using System.Text.Json.Serialization;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.Core.Buttons;

/// <summary>
/// Keeps fluid buttons across a restart.
/// </summary>
/// <remarks>
/// <c>RolloutPaths.ButtonsFile</c> was declared on the first day and never written to, so buttons
/// lived only in memory. The consequence is specific and bad: an agent posts a button because it
/// cannot run something itself, the operator closes the window, and on reopen the button is gone —
/// while the agent is still waiting for a thing that no longer exists anywhere. It waits until its
/// own timeout, then reports that it was blocked on something the operator never saw.
///
/// Only **open** buttons are kept. A finished one is history, and history belongs in the run
/// folders and the ledger; carrying every button ever pressed into every future session would turn
/// the panel into a log nobody reads.
///
/// ⚠️ A restored button's disposition is not trusted from the file. It is re-checked against the
/// allowlist as it stands now, for the same reason invocation re-checks it: the operator may have
/// changed their mind in between, and the file would be asserting a permission they revoked.
/// </remarks>
public sealed class ButtonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly RolloutPaths _paths;

    public ButtonStore(RolloutPaths paths) => _paths = paths;

    public IReadOnlyList<FluidButton> Load()
    {
        if (!File.Exists(_paths.ButtonsFile))
        {
            return [];
        }

        try
        {
            var buttons = JsonSerializer.Deserialize<List<FluidButton>>(
                File.ReadAllText(_paths.ButtonsFile), Options) ?? [];

            // A button left mid-run when the process died is not running any more — nothing is.
            // Leaving it as Running would show a spinner forever and hide the fact that it needs
            // pressing again.
            return
            [
                .. buttons
                    .Where(b => b.IsOpen)
                    .Select(b => b.Status == ButtonStatus.Running
                        ? b with
                        {
                            Status = ButtonStatus.Pending,
                            OutputExcerpt = "Was running when RolloutLoud closed; it did not finish.",
                        }
                        : b),
            ];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<FluidButton> buttons)
    {
        try
        {
            Directory.CreateDirectory(_paths.StateRoot);

            var open = buttons.Where(b => b.IsOpen).ToList();

            if (open.Count == 0 && !File.Exists(_paths.ButtonsFile))
            {
                return;
            }

            var temporary = _paths.ButtonsFile + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(open, Options));
                File.Move(temporary, _paths.ButtonsFile, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // Best effort.
                }

                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the button file is a degraded restart, not a reason to fail the request that
            // created the button.
        }
    }
}
