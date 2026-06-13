using System.IO.Pipes;
using System.Text.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>
/// A HELYI agent csak-olvasható status-pipe-ját olvassa ("RemoteAgent.status"): él-e a C2,
/// kész-e a tunnel, mikor volt utolsó szerver-kontakt. Így a konzol valós időben látja a
/// környezet állapotát, a szerver körbejárása nélkül. Null, ha az agent/pipe nem elérhető.
/// </summary>
public static class StatusClient
{
    public const string PipeName = "RemoteAgent.status";

    public static async Task<StatusReport?> QueryAgentAsync(int timeoutMs = 1500, CancellationToken ct = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutMs, ct);
            using var ms = new MemoryStream();
            await pipe.CopyToAsync(ms, ct);
            return ms.Length == 0 ? null : JsonSerializer.Deserialize(ms.ToArray(), AgentJsonContext.Default.StatusReport);
        }
        catch { return null; }
    }
}
