using System.Diagnostics;
using System.Text;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent;

/// <summary>
/// Installs and removes Windows services through sc.exe. They run as SYSTEM (LocalSystem)
/// with automatic startup and require admin rights to install. When present next to the
/// main agent, RemoteAgent.Updater is installed as well.
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
            Console.Error.WriteLine(L.ServiceControl_CouldNotDetermineExecutablePath);
            return 1;
        }

        var rc = await InstallServiceAsync(ServiceName, exe, ComposeDisplay(owner, "Agent", group), L.ServiceControl_RemoteAppClientRemoteAccessAgent);
        if (rc != 0) return rc;

        // Also install the Updater service when its exe is next to the agent.
        var updaterExe = Path.Combine(Path.GetDirectoryName(exe)!, "RemoteAgent.Updater.exe");
        if (File.Exists(updaterExe))
            await InstallServiceAsync(UpdaterServiceName, updaterExe, ComposeDisplay(owner, "Updater", group), "RemoteAppClient self-update service.");
        else
            Console.WriteLine(L.ServiceControl_RemoteAgentUpdaterExeIsNot);

        return 0;
    }

    /// <summary>Display service name: "{Owner} RemoteAppClient {component} ({group})"; owner/group are optional.</summary>
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
            // Existing service: update binPath and restart only, avoiding delete/create races.
            await RunScAsync("stop", name);
            await RunScAsync("config", name, "binPath=", exe, "start=", "auto", "obj=", "LocalSystem");
            await ConfigureRecoveryAsync(name);
            await RunScAsync("start", name);
            Console.WriteLine(L.Format(L.ServiceControl_ServiceUpdatedAndStarted, name));
            return 0;
        }

        var create = await RunScAsync("create", name, "binPath=", exe, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", displayName);
        if (create != 0)
        {
            Console.Error.WriteLine(L.Format(L.ServiceControl_CouldNotCreateServiceAdmin, name));
            return create;
        }

        await RunScAsync("description", name, description);
        await ConfigureRecoveryAsync(name);
        await RunScAsync("start", name);
        Console.WriteLine(L.Format(L.ServiceControl_ServiceInstalledAndStarted, name));
        return 0;
    }

    private static async Task<int> RemoveServiceAsync(string name)
    {
        if (!await ServiceExistsAsync(name))
            return 0;
        await RunScAsync("stop", name);
        var del = await RunScAsync("delete", name);
        Console.WriteLine(del == 0 ? L.Format(L.ServiceControl_ServiceRemoved, name) : L.Format(L.ServiceControl_CouldNotDeleteService, name));
        return del;
    }

    /// <summary>
    /// OS-level crash recovery: when the service exits unexpectedly, SCM restarts it
    /// with increasing delay. This cannot see hangs; the Helper watchdog handles those.
    /// It only reacts to actual process exit. 'reset= 86400' resets the counter after
    /// one clean day.
    /// </summary>
    private static async Task ConfigureRecoveryAsync(string name) =>
        await RunScAsync("failure", name, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");

    private static async Task<bool> ServiceExistsAsync(string name) =>
        await RunScAsync("query", name) == 0; // 1060 = no such service

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
