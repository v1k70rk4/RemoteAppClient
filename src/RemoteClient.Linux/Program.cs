using Avalonia;

namespace RemoteClient.Linux;

internal static class Program
{
    // Avalonia entry point. STAThread is harmless on Linux and required on Windows.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
