// racctl - read-only command-line access to the RemoteServer admin API.
//
// Meant for troubleshooting from a developer's or operator's own machine: the server's log, a health
// snapshot and the device list, without shell access to the box. It authenticates with a per-admin access
// token (minted in the console under Server settings -> Diagnostics) and reaches the server the same way
// the console does - through the local agent's broker and its SSH forward - so it only works on an
// enrolled device with the agent running, and a copied token alone gets nobody anywhere.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;
using RemoteClient;

Console.OutputEncoding = new UTF8Encoding(false);
var argv = args.ToList();
if (argv.Count == 0) return Usage(2);
var command = argv[0].ToLowerInvariant();
argv.RemoveAt(0);

try
{
    switch (command)
    {
        case "-h" or "--help" or "help":
            return Usage(0);
        case "version" or "--version":
            Console.WriteLine("racctl " + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?"));
            return 0;
        case "token":
            return Token(argv);
        case "logs":
            return await RunAsync(async api =>
            {
                int tail = 500; string? level = null, since = null, grep = null, day = null;
                for (int i = 0; i < argv.Count; i++)
                {
                    switch (argv[i])
                    {
                        case "-n" or "--tail": tail = int.Parse(Next(argv, ref i)); break;
                        case "-l" or "--level": level = Next(argv, ref i); break;
                        case "-s" or "--since": since = Next(argv, ref i); break;
                        case "-g" or "--grep": grep = Next(argv, ref i); break;
                        case "-d" or "--day": day = Next(argv, ref i); break;
                        default: throw new UsageException("unknown option for logs: " + argv[i]);
                    }
                }
                var text = await api.GetServerLogsAsync(tail, level, since, grep, day) ?? throw new TooOldException();
                Console.Out.Write(text);
            });
        case "diag":
            return await RunAsync(async api =>
            {
                var d = await api.GetServerDiagAsync() ?? throw new TooOldException();
                Console.WriteLine(Pretty(JsonSerializer.Serialize(d, AgentJsonContext.Default.ServerDiag)));
            });
        case "status":
            return await RunAsync(async api =>
                Console.WriteLine(Pretty(JsonSerializer.Serialize(await api.GetServerUpdateStatusAsync(), AgentJsonContext.Default.ServerUpdateStatus))));
        case "devices":
            return await RunAsync(async api =>
            {
                var list = await api.GetDevicesAsync();
                Console.WriteLine($"{"hostname",-28} {"state",-10} {"last seen (UTC)",-20} {"ip",-16} {"public ip",-16} {"agent",-10} problem");
                foreach (var d in list.OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase))
                {
                    var state = DeviceLiveness.Of(d);
                    var seen = d.LastSeenAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                    Console.WriteLine($"{Cut(d.Hostname, 28),-28} {state,-10} {seen,-20} {Cut(d.IpAddress, 16),-16} {Cut(d.PublicIpAddress, 16),-16} {Cut(d.AgentVersion, 10),-10} {d.Problem}");
                }
                Console.WriteLine($"-- {list.Count} devices");
            });
        case "events":
            return await RunAsync(async api =>
            {
                if (argv.Count == 0) throw new UsageException("events needs a device id");
                var id = argv[0]; int n = 50;
                for (int i = 1; i < argv.Count; i++)
                    if (argv[i] is "-n" or "--tail") n = int.Parse(Next(argv, ref i));
                    else throw new UsageException("unknown option for events: " + argv[i]);
                Console.WriteLine(Pretty(await api.GetRawAsync($"/admin/devices/{Uri.EscapeDataString(id)}/events?limit={n}")));
            });
        case "audit":
            return await RunAsync(async api =>
            {
                int n = 100;
                for (int i = 0; i < argv.Count; i++)
                    if (argv[i] is "-n" or "--tail") n = int.Parse(Next(argv, ref i));
                    else throw new UsageException("unknown option for audit: " + argv[i]);
                Console.WriteLine(Pretty(await api.GetRawAsync($"/admin/audit?limit={n}")));
            });
        case "get":
            return await RunAsync(async api =>
            {
                if (argv.Count != 1 || !argv[0].StartsWith("/admin/", StringComparison.Ordinal))
                    throw new UsageException("get needs one path starting with /admin/");
                var body = await api.GetRawAsync(argv[0]);
                Console.WriteLine(body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? Pretty(body) : body);
            });
        case "update":
            return await RunAsync(async api =>
            {
                // Same steps as the console's Server update tab: stage the tar (+ optional upgrade.sql), drop the
                // trigger, then watch for the helper's verdict. Needs a token minted with the update scope.
                if (argv.Count == 0) throw new UsageException("update needs the server package (RemoteServer-linux-x64.tar.gz from build.ps1 -ServerOnly)");
                var tar = argv[0]; string? sql = null; bool wait = true;
                for (int i = 1; i < argv.Count; i++)
                {
                    switch (argv[i])
                    {
                        case "--sql": sql = Next(argv, ref i); break;
                        case "--no-wait": wait = false; break;
                        default: throw new UsageException("unknown option for update: " + argv[i]);
                    }
                }
                if (!File.Exists(tar)) throw new UsageException("no such file: " + tar);
                if (sql is not null && !File.Exists(sql)) throw new UsageException("no such file: " + sql);

                var before = await api.GetServerUpdateStatusAsync();
                if (!before.HelperReady) { Error("the server's self-update helper is not installed (remoteserver-update.path)"); throw new ExitException(1); }
                Console.WriteLine($"server {before.Version} · uploading {Path.GetFileName(tar)} ({new FileInfo(tar).Length / 1024 / 1024} MB)...");
                await api.UploadServerPackageAsync("tar", tar);
                if (sql is not null)
                {
                    Console.WriteLine($"uploading {Path.GetFileName(sql)}...");
                    await api.UploadServerPackageAsync("sql", sql);
                }
                Console.WriteLine("update triggered: backup, stop, " + (sql is null ? "swap" : "schema upgrade, swap") + ", start, health check (about a minute)");
                await api.TriggerServerUpdateAsync();
                if (wait) await WaitForResultAsync(api);
            });
        case "apply":
            return await RunAsync(async api =>
            {
                bool wait = !argv.Contains("--no-wait");
                var s = await api.GetServerUpdateStatusAsync();
                if (!s.HelperReady) { Error("the server's self-update helper is not installed"); throw new ExitException(1); }
                if (!s.StagedTar) { Error("nothing staged: upload a package first (racctl update <tar.gz>)"); throw new ExitException(1); }
                Console.WriteLine($"server {s.Version} · applying the staged package ({s.StagedTarSize / 1024 / 1024} MB{(s.StagedSql ? " + upgrade.sql" : "")})");
                await api.TriggerServerUpdateAsync();
                if (wait) await WaitForResultAsync(api);
            });
        case "rollback":
            return await RunAsync(async api =>
            {
                bool wait = !argv.Contains("--no-wait");
                var s = await api.GetServerUpdateStatusAsync();
                if (!s.BackupAvailable) { Error("no server backup to roll back to"); throw new ExitException(1); }
                Console.WriteLine($"server {s.Version} · rolling back to the last backup (binaries + database)");
                await api.RollbackServerAsync();
                if (wait) await WaitForResultAsync(api);
            });
        default:
            Error("unknown command: " + command);
            return Usage(2);
    }
}
catch (UsageException ex) { Error(ex.Message); return 2; }
catch (FormatException ex) { Error("bad number: " + ex.Message); return 2; }

// ---------------------------------------------------------------------------------------------------

static int Usage(int code)
{
    Console.WriteLine("""
        racctl - read-only access to the RemoteServer admin API (log, health, fleet)

        usage:
          racctl token <token>            store the access token (DPAPI, current user)
          racctl token --clear            forget the stored token
          racctl logs [-n N] [--level info|warn|error|debug] [--since 30m|2h|1d|<iso>] [--grep TEXT] [--day YYYY-MM-DD]
          racctl diag                     health snapshot (JSON)
          racctl status                   self-update status (JSON)
          racctl devices                  device table
          racctl events <deviceId> [-n N] device history (JSON)
          racctl audit [-n N]             audit log (JSON)
          racctl get /admin/<path>        any read-only admin GET, raw

        with a token minted for server update (Permissions: read + server update):
          racctl update <server.tar.gz> [--sql upgrade.sql] [--no-wait]   stage and apply a server package
          racctl apply [--no-wait]        apply the package already staged on the server
          racctl rollback [--no-wait]     roll back to the last server backup (binaries + database)

        The token comes from the console: Server settings -> Diagnostics -> Access tokens. It works only on an
        enrolled device with the local agent running (the agent's broker provides the tunnel), and only for
        the routes above. Environment variable RACCTL_TOKEN overrides the stored token.

        exit codes: 0 ok · 1 error / update failed · 2 usage · 3 no local agent · 4 token rejected or out of scope · 5 server too old
        """);
    return code;
}

static string Next(List<string> a, ref int i)
{
    if (i + 1 >= a.Count) throw new UsageException("missing value after " + a[i]);
    return a[++i];
}

static string Cut(string? s, int n) => string.IsNullOrEmpty(s) ? "-" : s.Length <= n ? s : s[..(n - 1)] + "…";

static void Error(string message) => Console.Error.WriteLine("racctl: " + message);

/// <summary>Re-indents JSON without needing reflection metadata for its shape.</summary>
static string Pretty(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            doc.WriteTo(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
    catch (JsonException) { return json; }
}

// ---- token storage --------------------------------------------------------------------------------

static string TokenPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ClientConfig.AppFolderName, "racctl.token");

static int Token(List<string> a)
{
    var path = TokenPath();
    if (a.Count == 1 && a[0] == "--clear")
    {
        if (File.Exists(path)) File.Delete(path);
        Console.WriteLine("token cleared");
        return 0;
    }
    if (a.Count != 1 || !a[0].StartsWith("rac_", StringComparison.Ordinal) || a[0].Length < 20)
        throw new UsageException("token needs the value from the console (starts with rac_), or --clear");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(a[0]), null, DataProtectionScope.CurrentUser));
    Console.WriteLine($"token {a[0][..12]}… stored in {path}");
    return 0;
}

static string? LoadToken()
{
    var env = Environment.GetEnvironmentVariable("RACCTL_TOKEN");
    if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
    try
    {
        var path = TokenPath();
        if (!File.Exists(path)) return null;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
    }
    catch (CryptographicException) { return null; }
}

// ---- transport --------------------------------------------------------------------------------------

static async Task<int> RunAsync(Func<AdminApi, Task> body)
{
    var token = LoadToken();
    if (token is null)
    {
        Error("no access token stored. Create one in the console (Server settings -> Diagnostics -> Access tokens), then:  racctl token <token>");
        return 4;
    }
    using var broker = await BrokerClient.TryConnectAsync();
    if (broker is null)
    {
        Error("the local RemoteAgent broker is not reachable. racctl runs on an enrolled device with the agent service running.");
        return 3;
    }
    var cfg = ClientConfig.Load();
    using var api = new AdminApi(ct => broker.ForwardAsync(cfg.AdminApiPort, ct));
    api.SetToken(token);
    try
    {
        await body(api);
        return 0;
    }
    catch (ExitException ex) { return ex.Code; }
    catch (TooOldException) { Error("the server does not have this endpoint yet - update the server (2.2.0 or newer)"); return 5; }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { Error("token rejected: revoked, expired, or its owner is no longer an active admin"); return 4; }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { Error("this token's scope does not allow that (a read-only token cannot update the server; mint one with 'read + server update')"); return 4; }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests) { Error("too many rejected tokens from this address; try again in ten minutes"); return 4; }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { Error("not found: the server is too old for this command, or the path is wrong"); return 5; }
    catch (HttpRequestException ex) { Error("request failed: " + ex.Message); return 1; }
    catch (InvalidOperationException ex) { Error(ex.Message); return 3; }   // broker refused the forward
}

/// <summary>Polls the update status until the helper has written its verdict. The server restarts in the
/// middle, so a refused connection is expected and just means "still working".</summary>
static async Task WaitForResultAsync(AdminApi api)
{
    var deadline = DateTime.UtcNow.AddMinutes(6);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(3000);
        ServerUpdateStatus s;
        try { s = await api.GetServerUpdateStatusAsync(); }
        catch (HttpRequestException) { Console.Write("."); continue; }
        catch (TaskCanceledException) { Console.Write("."); continue; }
        catch (InvalidOperationException) { Console.Write("."); continue; }   // forward being rebuilt
        if (s.LastResult is { } r)
        {
            Console.WriteLine();
            Console.WriteLine((r.Ok ? "OK" : "FAILED") + " · " + r.At + " · server now " + s.Version);
            Console.WriteLine(r.Message.TrimEnd());
            if (!r.Ok) throw new ExitException(1);
            return;
        }
        Console.Write(".");
    }
    Console.WriteLine();
    Error("no verdict within 6 minutes; look at the console's Server update tab");
    throw new ExitException(1);
}

sealed class UsageException(string message) : Exception(message);
sealed class TooOldException : Exception;
sealed class ExitException(int code) : Exception { public int Code { get; } = code; }
