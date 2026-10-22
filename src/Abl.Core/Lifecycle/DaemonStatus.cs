using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core.Config;
using Abl.Core.Sessions;
using Abl.Core.Workers;

namespace Abl.Core.Lifecycle;

/// <summary>
/// The <c>daemon_status</c> op (008 FR-010, plan B5): one bounded object (about 1 KB) that
/// the MCP tool, <c>POST /api/ops/daemon_status</c> and <c>abl daemon status</c> all return,
/// so their shared fields are identical (AC-27). Secrets are redacted by the resolver's
/// <see cref="AblConfig.ToJson"/>; no field carries a token value. The CLI adds only its
/// <c>local</c> object.
/// </summary>
public sealed class DaemonStatus(
    DaemonInstance instance, AblConfig config, WorkerPool pool, SessionService sessions, DesktopTier desktop)
{
    /// <summary>The op's result.</summary>
    public JsonObject Build()
    {
        var options = config.Options;
        var configJson = config.ToJson();
        configJson.Remove("blender"); // reported once, at the top level

        return new JsonObject
        {
            ["running"] = true,
            ["version"] = instance.Version,
            ["pid"] = instance.Pid,
            ["startedAt"] = instance.StartedAtText,
            ["url"] = instance.Url,
            ["logFile"] = instance.LogFile,
            ["config"] = configJson,

            // FR-003, plan A4: the Blender in use, its support and allowUnsupported, so
            // unsupported use is visible; null only for a configuration resolved without a
            // Blender search, which the daemon refuses to run.
            ["blender"] = config.Blender?.ToJson(),
            ["workers"] = new JsonObject
            {
                ["ready"] = pool.ReadyCount,
                ["floor"] = options.PoolFloor,
                ["lastSpawnAt"] = pool.LastSpawnAt?.ToString("O", CultureInfo.InvariantCulture),
                ["lastWorkerError"] = pool.LastWorkerError is { } error
                    ? JsonSerializer.SerializeToNode(error, JsonSerializerOptions.Web)
                    : null,
            },
            ["sessions"] = sessions.List().Count,

            // 009 T020 (plan "daemon_status.gpu", amends 008 B5): from the last ready local
            // worker's hello, or null before any. metalrt stays in the hello and the doctor
            // gpu.backend detail (plan D12); the desktop's GPU is not reported here.
            ["gpu"] = pool.LastLocalGpu is { } gpu ? Gpu(gpu.Gpu, gpu.At) : null,
            ["desktop"] = new JsonObject
            {
                ["configured"] = desktop.IsConfigured,
                ["support"] = options.Platform == HostPlatform.MacOS ? "unsupported" : "supported",
                ["tcpAvailable"] = desktop.TcpAvailable,
            },
            ["telemetry"] = new JsonObject { ["enabled"] = options.TelemetryEnabled },
        };
    }

    /// <summary>The <c>gpu</c> object: <c>{backend, devices: [{name, type}], observedAt}</c>, no <c>metalrt</c>.</summary>
    internal static JsonObject Gpu(GpuCapability gpu, DateTimeOffset observedAt) => new()
    {
        ["backend"] = gpu.Backend,
        ["devices"] = new JsonArray([.. gpu.Devices.Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["type"] = d.Type })]),
        ["observedAt"] = observedAt.ToString("O", CultureInfo.InvariantCulture),
    };
}
