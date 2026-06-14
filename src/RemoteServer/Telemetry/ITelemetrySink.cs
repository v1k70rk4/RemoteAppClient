using RemoteAgent.Telemetry;

namespace RemoteServer.Telemetry;

/// <summary>Sink for incoming telemetry. DB-backed implementation: <c>DbTelemetrySink</c>.</summary>
public interface ITelemetrySink
{
    Task IngestAsync(string deviceId, TelemetryPayload payload, string? publicIp, CancellationToken ct);
}
