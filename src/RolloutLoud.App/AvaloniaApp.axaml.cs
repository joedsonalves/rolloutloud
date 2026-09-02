using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RolloutLoud.App.Views;
using RolloutLoud.Core;
using RolloutLoud.Core.Bridge;

namespace RolloutLoud.App;

public partial class AvaloniaApp : Application
{
    private BridgeServer? _bridge;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
