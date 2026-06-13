using System.Diagnostics;
using System.Text;

namespace RemoteAgent;

/// <summary>
/// A Windows service-ek telepítése/eltávolítása sc.exe-vel. SYSTEM (LocalSystem) alatt
/// futnak, automatikus indítással. Admin jog kell hozzá. A fő agent mellett — ha ott van —
/// a RemoteAgent.Updater service-t is telepíti.
/// </summary>
public static class ServiceControl
{
    public const string ServiceName = "RemoteAgent";
    public const string UpdaterServiceName = "RemoteAgent.Updater";

    public static async Task<int> InstallAsync(string? owner = null, string? group = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine("Nem határozható meg az exe útvonala.");
            return 1;
        }

        var rc = await InstallServiceAsync(ServiceName, exe, ComposeDisplay(owner, "Agent", group), "RemoteAppClient távelérő agent.");
        if (rc != 0) return rc;

        // Updater service is, ha az exe ott van az agent mellett.
        var updaterExe = Path.Combine(Path.GetDirectoryName(exe)!, "RemoteAgent.Updater.exe");
        if (File.Exists(updaterExe))
            await InstallServiceAsync(UpdaterServiceName, updaterExe, ComposeDisplay(owner, "Updater", group), "RemoteAppClient self-update service.");
        else
            Console.WriteLine("(RemoteAgent.Updater.exe nincs az agent mellett — az Updater service kihagyva.)");

        return 0;
    }

    /// <summary>Megjelenített szolgáltatás-név: "{Owner} RemoteAppClient {component} ({group})" (owner/group opcionális).</summary>
    private static string ComposeDisplay(string? owner, string component, string? group)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(owner)) sb.Append(owner.Trim()).Append(' ');
        sb.Append("RemoteAppClient ").Append(component);
        if (!string.IsNullOrWhiteSpace(group)) sb.Append(" (").Append(group.Trim()).Append(')');
        return sb.ToString();
    }

    public static async Task<int> UninstallAsync()
    {
        await RemoveServiceAsync(UpdaterServiceName);
        return await RemoveServiceAsync(ServiceName);
    }

    private static async Task<int> InstallServiceAsync(string name, string exe, string displayName, string description)
    {
        if (await ServiceExistsAsync(name))
        {
            // Létezik: csak binPath frissítés + újraindítás (nincs delete/create verseny).
            await RunScAsync("stop", name);
            await RunScAsync("config", name, "binPath=", exe, "start=", "auto", "obj=", "LocalSystem");
            await ConfigureRecoveryAsync(name);
            await RunScAsync("start", name);
            Console.WriteLine($"A(z) {name} service frissítve és elindítva.");
            return 0;
        }

        var create = await RunScAsync("create", name, "binPath=", exe, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", displayName);
        if (create != 0)
        {
            Console.Error.WriteLine($"A(z) {name} service létrehozása sikertelen (admin jog kell?).");
            return create;
        }

        await RunScAsync("description", name, description);
        await ConfigureRecoveryAsync(name);
        await RunScAsync("start", name);
        Console.WriteLine($"A(z) {name} service telepítve és elindítva.");
        return 0;
    }

    private static async Task<int> RemoveServiceAsync(string name)
    {
        if (!await ServiceExistsAsync(name))
            return 0;
        await RunScAsync("stop", name);
        var del = await RunScAsync("delete", name);
        Console.WriteLine(del == 0 ? $"A(z) {name} service eltávolítva." : $"A(z) {name} törlése nem sikerült.");
        return del;
    }

    /// <summary>
    /// OS-szintű crash-recovery: ha a service váratlanul kilép (összeomlik), az SCM
    /// automatikusan újraindítja, növekvő késleltetéssel. A *beragadást* ez nem látja
    /// (azt a Helper watchdog kezeli) — ez csak a tényleges processz-kilépésre szól.
    /// A 'reset= 86400' = egy nap hibamentes futás után a számláló nullázódik.
    /// </summary>
    private static async Task ConfigureRecoveryAsync(string name) =>
        await RunScAsync("failure", name, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");

    private static async Task<bool> ServiceExistsAsync(string name) =>
        await RunScAsync("query", name) == 0; // 1060 = nincs ilyen service

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
