namespace RemoteClient.Lite;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Keep the Lite console's settings in their own folder, separate from the full client.
        ClientConfig.AppFolderName = "RemoteClient.Lite";
        // Equivalent to ApplicationConfiguration.Initialize(); done explicitly to avoid the generated
        // ApplicationConfiguration clashing with the full client's (both are global, visible via InternalsVisibleTo).
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.Run(new MainFormLite());
    }
}
