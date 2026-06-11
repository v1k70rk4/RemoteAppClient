using System.Diagnostics;

namespace RemoteAgent;

/// <summary>
/// A Windows service telepítése/eltávolítása sc.exe-vel. SYSTEM (LocalSystem) alatt
/// fut, automatikus indítással. Admin jog kell hozzá.
/// </summary>
public static class ServiceControl
{
    public const string ServiceName = "RemoteAgent";

    public static async Task<int> InstallAsync()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine("Nem határozható meg az exe útvonala.");
            return 1;
        }

        // Ha már létezik: csak frissítjük a binPath-t és újraindítjuk (nincs delete/create verseny).
        if (await ServiceExistsAsync())
        {
            await RunScAsync("stop", ServiceName);
            await RunScAsync("config", ServiceName, "binPath=", exe, "start=", "auto", "obj=", "LocalSystem");
            await RunScAsync("start", ServiceName);
            Console.WriteLine($"A(z) {ServiceName} service frissítve és elindítva.");
            return 0;
        }

        var create = await RunScAsync(
            "create", ServiceName,
            "binPath=", exe,
            "start=", "auto",
            "obj=", "LocalSystem",
            "DisplayName=", "RemoteAppClient Agent");
        if (create != 0)
        {
            Console.Error.WriteLine("A service létrehozása sikertelen (admin jog kell?).");
            return create;
        }

        await RunScAsync("description", ServiceName, "RemoteAppClient távelérő agent.");
        await RunScAsync("start", ServiceName);
        Console.WriteLine($"A(z) {ServiceName} service telepítve és elindítva.");
        return 0;
    }

    private static async Task<bool> ServiceExistsAsync() =>
        await RunScAsync("query", ServiceName) == 0; // 1060 = nincs ilyen service

    public static async Task<int> UninstallAsync()
    {
        await RunScAsync("stop", ServiceName);
        var del = await RunScAsync("delete", ServiceName);
        Console.WriteLine(del == 0 ? $"A(z) {ServiceName} service eltávolítva." : "A service törlése nem sikerült.");
        return del;
    }

    private static async Task<int> RunScAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
}
