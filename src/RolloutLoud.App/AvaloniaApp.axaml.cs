using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using RolloutLoud.App.Views;
using RolloutLoud.Core;
using RolloutLoud.Core.Bridge;

namespace RolloutLoud.App;

public partial class AvaloniaApp : Application
{
    private BridgeServer? _bridge;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyThemeOverride();
    }

    /// <summary>
    /// Honours <c>ROLLOUTLOUD_THEME</c>, otherwise follows the OS.
    /// </summary>
    /// <remarks>
    /// Following the OS is the default and the right one. The override exists for two real cases:
    /// checking that both palettes are actually readable without changing the machine's settings
    /// — every colour is defined in both variants, and the only way to know is to look — and the
    /// operator who runs a dark OS but wants this window light, or the reverse.
    /// </remarks>
    private void ApplyThemeOverride()
    {
        var requested = Environment.GetEnvironmentVariable("ROLLOUTLOUD_THEME")?.Trim().ToLowerInvariant();

        RequestedThemeVariant = requested switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = new RolloutHost(Program.Paths, PlatformServices.CreateElevationService());

            _bridge = new BridgeServer(host);
            _bridge.Start();

            var viewModel = new MainViewModel(host, _bridge);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Take the bridge down with the window. Leaving the listener and its handshake file
            // behind would leave a live token pointing at a port nobody owns any more.
            desktop.ShutdownRequested += (_, _) => _bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
