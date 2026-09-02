using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace RolloutLoud.App.Views;

/// <summary>What the operator chose. Three outcomes, because collapsing them loses one.</summary>
public enum ElevationChoice
{
    /// <summary>Restart RolloutLoud through the OS prompt, then launch.</summary>
    Elevate,

    /// <summary>Launch now, with the bypass flag but no administrative rights.</summary>
    LaunchUnelevated,

    /// <summary>Do nothing.</summary>
    Cancel,
}

/// <summary>
/// The warning shown when an elevated launch is clicked and RolloutLoud is not elevated.
/// </summary>
/// <remarks>
/// Built in code rather than XAML because it is one dialog with three buttons and no state, and
/// the answer it returns has to be a real three-way choice rather than OK/Cancel:
///
/// - **Elevate and restart** — the honest fix, one OS prompt.
/// - **Launch anyway** — legitimate more often than it sounds. Most missions never touch a
///   privileged resource, and the CLI's own bypass flag works fine without an elevated parent.
/// - **Cancel** — clicked the wrong button.
///
/// Collapsing that into a yes/no is what produces the failure this dialog exists to prevent: an
/// operator who wanted a bypass-flag session, was asked "elevate?", said no, and got nothing.
/// </remarks>
public static class ElevationPrompt
{
    public static async Task<ElevationChoice> AskAsync(string agentName, string platformDescription)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        var completion = new TaskCompletionSource<ElevationChoice>();

        var elevateButton = new Button
        {
            Content = "Elevate and restart",
            Padding = new Avalonia.Thickness(16, 8),
            IsDefault = true,
        };

        var anywayButton = new Button
        {
            Content = "Launch anyway (unelevated)",
            Padding = new Avalonia.Thickness(16, 8),
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Avalonia.Thickness(16, 8),
            IsCancel = true,
        };

        var dialog = new Window
        {
            Title = "RolloutLoud is not elevated",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"You asked to launch {agentName} elevated, but RolloutLoud itself is not.",
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text =
                            "A child process cannot hold more privilege than its parent, so the CLI would " +
                            "start with its approval prompts off but without administrative rights — and " +
                            "the difference only shows up later, when a privileged command fails.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                    },
                    new TextBlock
                    {
                        Text = platformDescription,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                    },
                    new TextBlock
                    {
                        Text =
                            "If this mission does not need administrative rights, launching unelevated is " +
                            "the better choice — the bypass flag works either way.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.6,
                        FontSize = 12,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, anywayButton, elevateButton },
                    },
                },
            },
        };

        elevateButton.Click += (_, _) => { completion.TrySetResult(ElevationChoice.Elevate); dialog.Close(); };
        anywayButton.Click += (_, _) => { completion.TrySetResult(ElevationChoice.LaunchUnelevated); dialog.Close(); };
        cancelButton.Click += (_, _) => { completion.TrySetResult(ElevationChoice.Cancel); dialog.Close(); };

        // Closing with the title bar is a cancel, not a silent elevate.
        dialog.Closed += (_, _) => completion.TrySetResult(ElevationChoice.Cancel);

        if (owner is null)
        {
            dialog.Show();
        }
        else
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }

        return await completion.Task.ConfigureAwait(true);
    }
}
