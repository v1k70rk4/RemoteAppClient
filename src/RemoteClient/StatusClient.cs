using System.IO.Pipes;
using System.Text.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>
/// Reads the local agent read-only status pipe ("RemoteAgent.status"): C2, tunnel state,
/// and last server contact. This lets the console show local environment state in real
/// time without going through the server. Returns null when agent/pipe is unavailable.
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
