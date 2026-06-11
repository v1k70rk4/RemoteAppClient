using RemoteAgent.Telemetry;

namespace RemoteServer.Telemetry;

/// <summary>A beérkező telemetria nyelője (DB-backed implementáció: <c>DbTelemetrySink</c>).</summary>
public interface ITelemetrySink
{
    Task IngestAsync(string deviceId, TelemetryPayload payload, CancellationToken ct);
}
