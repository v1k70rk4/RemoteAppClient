using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RemoteClient.Linux.Views;

namespace RemoteClient.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the saved UI language before any window is built ("auto"/empty = follow the OS culture).
        var lang = ClientConfig.Load().Language;
        if (!string.IsNullOrWhiteSpace(lang) && !string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
            RemoteClient.Localization.Strings.Language = lang;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
