using System.Diagnostics;
using System.Text;
using L = RemoteAgent.Localization.Strings;

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
            Console.Error.WriteLine(L.ServiceControl_001);
            return 1;
        }

        var rc = await InstallServiceAsync(ServiceName, exe, ComposeDisplay(owner, "Agent", group), L.ServiceControl_002);
        if (rc != 0) return rc;

        // Updater service is, ha az exe ott van az agent mellett.
        var updaterExe = Path.Combine(Path.GetDirectoryName(exe)!, "RemoteAgent.Updater.exe");
        if (File.Exists(updaterExe))
            await InstallServiceAsync(UpdaterServiceName, updaterExe, ComposeDisplay(owner, "Updater", group), "RemoteAppClient self-update service.");
        else
            Console.WriteLine(L.ServiceControl_008);

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
            Console.WriteLine(L.Format(L.ServiceControl_003, name));
            return 0;
        }

        var create = await RunScAsync("create", name, "binPath=", exe, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", displayName);
        if (create != 0)
        {
            Console.Error.WriteLine(L.Format(L.ServiceControl_004, name));
            return create;
        }

        await RunScAsync("description", name, description);
        await ConfigureRecoveryAsync(name);
        await RunScAsync("start", name);
        Console.WriteLine(L.Format(L.ServiceControl_005, name));
        return 0;
    }

    private static async Task<int> RemoveServiceAsync(string name)
    {
        if (!await ServiceExistsAsync(name))
            return 0;
        await RunScAsync("stop", name);
        var del = await RunScAsync("delete", name);
        Console.WriteLine(del == 0 ? L.Format(L.ServiceControl_006, name) : L.Format(L.ServiceControl_007, name));
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
