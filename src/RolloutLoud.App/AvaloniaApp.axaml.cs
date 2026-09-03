using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using RolloutLoud.App.Views;
using RolloutLoud.Core;
using RolloutLoud.Core.Bridge;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.App;

public partial class AvaloniaApp : Application
{
    private BridgeServer? _bridge;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyThemeOverride();
    }

    /// <summary>Applies the operator's stored theme, or <c>ROLLOUTLOUD_THEME</c> if it is set.</summary>
    private void ApplyThemeOverride() => Apply(UiPreferences.Load().Effective);

    /// <summary>
    /// Switches the theme live and remembers it.
    /// </summary>
    /// <remarks>
    /// Every colour is a DynamicResource against a ThemeDictionary, so changing the variant
    /// repaints the window without rebuilding it — which is what makes a header toggle worth
    /// having rather than a setting that needs a restart.
    /// </remarks>
    public static void SetTheme(ThemeChoice choice)
    {
        if (Current is AvaloniaApp app)
        {
            app.Apply(choice);
        }

        new UiPreferences { Theme = choice }.Save();
    }

    private void Apply(ThemeChoice choice) => RequestedThemeVariant = choice switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = new RolloutHost(Program.Paths, PlatformServices.CreateElevationService());

            _bridge = new BridgeServer(host);
            _bridge.Start();

            var viewModel = new MainViewModel(host, _bridge);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Core has no window to close, so the exit lives here. By the time this fires the
            // request has already been through ShutdownGate — the mission is Achieved, the gate
            // passed twice, and nothing else is open.
            host.ShutdownApproved += reason => Dispatcher.UIThread.Post(() =>
            {
                viewModel.NoteShutdown(reason);
                desktop.Shutdown();
            });

            // Take the bridge down with the window. Leaving the listener and its handshake file
            // behind would leave a live token pointing at a port nobody owns any more.
            desktop.ShutdownRequested += (_, _) => _bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
