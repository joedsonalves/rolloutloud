using Avalonia;
using RolloutLoud.Core.Localization;
using RolloutLoud.Core.Workspace;

namespace RolloutLoud.App;

internal static class Program
{
    /// <summary>
    /// Repository the whole session is anchored to.
    /// </summary>
    /// <remarks>
    /// The operator's rule, stated plainly: run RolloutLoud from a folder and the elevated CLIs
    /// open in that folder. So the anchor is the current directory at startup, resolved up to the
    /// repository root — and it is captured here, before Avalonia or anything else has a chance
    /// to change the process working directory out from under it.
    ///
    /// <c>--repo</c> exists for exactly one caller: the elevated relaunch, which starts from a
    /// different working directory and has to be told where it came from.
    /// </remarks>
    internal static RolloutPaths Paths { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        var repo = ReadRepositoryArgument(args) ?? Directory.GetCurrentDirectory();
        Paths = RolloutPaths.Discover(repo);

        // Before any window is built: the markup extension that resolves labels reads the
        // language once, at load, so this has to happen first or the whole UI comes up English.
        Localizer.Initialize();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AvaloniaApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string? ReadRepositoryArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--repo" or "-r")
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
