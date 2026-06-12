namespace RemoteClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        ClientUpdater.CleanupOld(); // korábbi frissítés .old maradványa
        Application.Run(new MainForm());
    }
}
