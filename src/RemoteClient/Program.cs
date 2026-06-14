namespace RemoteClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        RemoteAgent.Globalization.RuntimeLanguage.ApplyFromSharedSettings();
        ApplicationConfiguration.Initialize();
        ClientUpdater.CleanupOld(); // korábbi frissítés .old maradványa
        Application.Run(new MainForm());
    }
}
