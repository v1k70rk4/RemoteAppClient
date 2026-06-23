namespace RemoteClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        RemoteAgent.Globalization.RuntimeLanguage.ApplyFromSharedSettings();
        ApplicationConfiguration.Initialize();

        // Surface unhandled exceptions with their full stack so a UI fault is diagnosable instead of a bare
        // "Object reference not set" dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);

        ClientUpdater.CleanupOld(); // leftover .old from previous update
        Application.Run(new MainForm());
    }

    private static void ShowError(Exception? ex) =>
        MessageBox.Show(ex?.ToString() ?? "Unknown error", "RemoteAppClient — error", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
