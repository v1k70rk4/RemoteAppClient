namespace RemoteClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        RemoteAgent.Globalization.RuntimeLanguage.ApplyFromSharedSettings();
        ApplicationConfiguration.Initialize();
        ClientUpdater.CleanupOld(); // leftover .old from previous update
        Application.Run(new MainForm());
    }
}
