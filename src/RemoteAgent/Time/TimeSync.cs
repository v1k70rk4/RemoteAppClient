using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace RemoteAgent.Time;

/// <summary>What a sync attempt did, for the event log and for telemetry.</summary>
public sealed record TimeSyncResult(bool ServiceStarted, bool SourceConfigured, bool Resynced, string Source, string? Error, double SteppedSeconds = 0)
{
    /// <summary>True when the sync actually moved the clock by something worth telling a human about.</summary>
    public bool Corrected => Math.Abs(SteppedSeconds) >= 2;

    public override string ToString() =>
        Error is not null ? $"idoszinkron hiba: {Error}"
        : (Corrected ? $"az orat {SteppedSeconds:+0;-0} mp-cel igazitottam; " : "")
          + $"idoforras: {Source}{(ServiceStarted ? " (szolgaltatas inditva)" : "")}{(SourceConfigured ? " (forras beallitva)" : "")}{(Resynced ? " (szinkronizalva)" : "")}";
}

/// <summary>
/// Keeps the machine clock close enough to the server's that signed commands are still accepted.
/// <para>
/// This exists because of a real outage: one freshly installed machine ran 88 seconds fast, the agent
/// refused every command as replay (the window is 60s), and from the console the device looked perfectly
/// healthy — online, green, reporting every minute. Nothing could be pushed to it, including the fix.
/// </para>
/// <para>
/// Domain-joined machines are deliberately left alone: their time comes from the domain hierarchy
/// (w32time Type = NT5DS), and overriding that would fight the DC and can break Kerberos. There we only
/// make sure the service is actually running and ask it to resync.
/// </para>
/// </summary>
public static class TimeSync
{
    private const string ServiceName = "w32time";
    private const string ParamsKey = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";

    /// <summary>Ensures the time service runs, has a usable source, and has just synchronised.</summary>
    public static TimeSyncResult Ensure(string peerList)
    {
        // Measure the correction: the wall clock minus the monotonic elapsed time is exactly how far
        // w32tm stepped us. Without this a sync that silently moved the machine three minutes leaves no
        // trace at all, which is the same kind of silence this whole feature exists to end.
        var sw = Stopwatch.StartNew();
        var wallBefore = DateTime.UtcNow;
        try
        {
            var started = EnsureServiceRunning();
            var domainManaged = string.Equals(RegString("Type"), "NT5DS", StringComparison.OrdinalIgnoreCase);

            // Only supply a source when the machine genuinely has none. An admin (or Intune) may have set a
            // deliberate NTP server; overwriting that would be worse than the drift we are trying to prevent.
            // "NoSync" or an empty NtpServer means nothing is disciplining this clock and it will drift
            // until commands start being refused - that is ours to fix.
            var configured = false;
            var noSource = string.Equals(RegString("Type"), "NoSync", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(RegString("NtpServer"));
            if (noSource && !domainManaged && !string.IsNullOrWhiteSpace(peerList))
            {
                Run("w32tm.exe", "/config", $"/manualpeerlist:{peerList}", "/syncfromflags:manual", "/update");
                configured = true;
            }

            var resynced = Run("w32tm.exe", "/resync", "/force").ExitCode == 0;
            if (!resynced) resynced = Run("w32tm.exe", "/resync").ExitCode == 0;   // /force is rejected on some builds

            var stepped = ((DateTime.UtcNow - wallBefore) - sw.Elapsed).TotalSeconds;
            return new TimeSyncResult(started, configured, resynced, QuerySource(), null, stepped);
        }
        catch (Exception ex)
        {
            return new TimeSyncResult(false, false, false, "", ex.Message);
        }
    }

    /// <summary>The clock discipline source w32time reports ("" when it cannot be read).</summary>
    public static string QuerySource()
    {
        var r = Run("w32tm.exe", "/query", "/source");
        return r.ExitCode == 0 ? r.Output.Trim() : "";
    }

    /// <summary>
    /// Starts w32time, enabling it first when it was set to disabled. True when we had to act.
    /// <para>
    /// Asks the service manager rather than reading "sc.exe query" output: that text is LOCALIZED, so
    /// looking for "RUNNING" silently never matched on a Hungarian Windows and every single sync claimed
    /// it had to start an already-running service.
    /// </para>
    /// </summary>
    private static bool EnsureServiceRunning()
    {
        using var sc = new ServiceController(ServiceName);
        try
        {
            if (sc.Status == ServiceControllerStatus.Running) return false;
        }
        catch (InvalidOperationException) { return false; }   // not installed here; nothing to start

        // A disabled service cannot be started, and on workgroup machines it is often left that way.
        Run("sc.exe", "config", ServiceName, "start=", "auto");
        try
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
        catch { /* already starting, or refused - the resync result below tells us either way */ }
        return true;
    }

    private static string RegString(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ParamsKey);
            return k?.GetValue(name) as string ?? "";
        }
        catch { return ""; }
    }

    private static (int ExitCode, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.HasExited ? proc.ExitCode : -1, output);
    }
}
