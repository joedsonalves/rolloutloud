using Avalonia.Markup.Xaml;
using RolloutLoud.Core.Localization;

namespace RolloutLoud.App.Views;

/// <summary>
/// <c>{loc:Tr Key=agents.title}</c> — a translated string in XAML.
/// </summary>
/// <remarks>
/// Resolves once, at load, rather than binding to a live source. The language is decided from the
/// OS before the first window is constructed and does not change while the app runs, so a
/// notifying binding would cost a subscription per label to observe something that never fires.
///
/// If a language picker is ever added, this is the thing that has to change — and the honest way
/// to do it is to rebuild the window, not to make 70 labels observable.
/// </remarks>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Current[Key];
}
