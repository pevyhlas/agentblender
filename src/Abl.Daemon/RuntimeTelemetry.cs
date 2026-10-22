using Abl.Core.Sessions;
using Abl.Core.Telemetry;
using Abl.Core.Workers;

namespace Abl.Daemon;

/// <summary>Probe independently of traffic; idle authoring is not a telemetry outage.</summary>
public sealed class RuntimeTelemetry(
    WorkerPool pool, SessionService sessions, DesktopTier desktop, ILogger<RuntimeTelemetry> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AblTelemetry.ObserveRuntime(() => new(pool.ReadyCount, sessions.List().Count,
            desktop.IsConfigured, desktop.LastAvailability));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await desktop.ProbeAvailableAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "desktop availability probe failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
