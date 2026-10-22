using System.Text.Json.Nodes;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T036/T039: the <c>daemon_status</c> op over the op core with scripted fake workers.
/// AC-27: MCP and REST return one object and no surface carries a token value (the CLI half is
/// <see cref="LifecycleCoreTests"/>, all three over a real daemon are in the integration
/// lifecycle tests); <c>lastWorkerError</c> takes a spawn refusal's check from the
/// <see cref="ConfigException"/> it carries; a config-class refusal's surface hint is that
/// check's remedy (1b eng-code L1); <c>gpu</c> comes from the last local hello.
/// </summary>
public sealed class DaemonStatusTests
{
    [Fact]
    public async Task McpAndRestReturnTheSameObjectAndNoTokenValue()
    {
        await using var h = new GpuHarness(HostPlatform.Linux);
        h.LocalScript.Gpu = FakeGpu.Optix();
        var session = await h.Sessions.OpenAsync("status", default);

        var mcp = JsonNode.Parse(GpuHarness.McpJson(await h.Tools.GetDaemonStatus()))!;
        var (code, rest) = await h.RestAsync("daemon_status", []);

        Assert.Equal(200, code);
        Assert.True(JsonNode.DeepEquals(mcp, rest), $"MCP {mcp.ToJsonString()}\nREST {rest.ToJsonString()}");
        Assert.DoesNotContain(GpuHarness.TokenValue, mcp.ToJsonString());
        Assert.DoesNotContain(GpuHarness.TokenValue, rest.ToJsonString());

        Assert.True(rest["running"]!.GetValue<bool>());
        Assert.Equal(4242, rest["pid"]!.GetValue<int>());
        Assert.Equal("http://127.0.0.1:7811", rest["url"]!.GetValue<string>());
        Assert.Equal(AblVersion.Current, rest["version"]!.GetValue<string>());
        Assert.Equal(h.Config.EffectiveDigest, rest["config"]!["effectiveDigest"]!.GetValue<string>());
        Assert.Equal("<redacted>", rest["config"]!["effective"]!["desktop.token"]!["value"]!.GetValue<string>());
        Assert.Equal("env", rest["config"]!["effective"]!["desktop.token"]!["source"]!.GetValue<string>());
        Assert.Equal(h.Options.StoreDir, rest["config"]!["effective"]!["paths.store"]!["value"]!.GetValue<string>());
        Assert.Equal(1, rest["sessions"]!.GetValue<int>());
        Assert.Equal("optix", rest["gpu"]!["backend"]!.GetValue<string>());
        Assert.NotNull(rest["workers"]!["lastSpawnAt"]);
        Assert.Null(rest["workers"]!["lastWorkerError"]);
        Assert.Equal("supported", rest["desktop"]!["support"]!.GetValue<string>());
        Assert.False(rest["telemetry"]!["enabled"]!.GetValue<bool>());
        await h.Sessions.CloseAsync(session.Id);
    }

    [Fact]
    public async Task AC11_AMetalHelloGivesBackendDevicesAndObservedAtOnMcpAndRestWithoutMetalrt()
    {
        // 009 T020/T022: gpu is {backend, devices, observedAt} from the last ready local
        // worker's hello (the CLI half is LifecycleCoreTests); metalrt stays in the hello.
        await using var h = new GpuHarness(HostPlatform.MacOS);
        h.LocalScript.Gpu = FakeGpu.Metal();
        var before = DateTimeOffset.UtcNow;
        var session = await h.Sessions.OpenAsync("status", default);

        var mcp = JsonNode.Parse(GpuHarness.McpJson(await h.Tools.GetDaemonStatus()))!;
        var (code, rest) = await h.RestAsync("daemon_status", []);

        Assert.Equal(200, code);
        Assert.True(JsonNode.DeepEquals(mcp["gpu"], rest["gpu"]), $"MCP {mcp["gpu"]?.ToJsonString()}\nREST {rest["gpu"]?.ToJsonString()}");
        var gpu = rest["gpu"]!.AsObject();
        Assert.Equal(["backend", "devices", "observedAt"], gpu.Select(p => p.Key));
        Assert.Equal("metal", gpu["backend"]!.GetValue<string>());
        var device = Assert.Single(gpu["devices"]!.AsArray())!.AsObject();
        Assert.Equal(["name", "type"], device.Select(p => p.Key));
        Assert.Equal(FakeGpu.MetalName, device["name"]!.GetValue<string>());
        Assert.Equal("METAL", device["type"]!.GetValue<string>());
        var observedAt = DateTimeOffset.Parse(gpu["observedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(observedAt, before, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("metalrt", rest.ToJsonString());
        Assert.DoesNotContain("metalrt", mcp.ToJsonString());
        await h.Sessions.CloseAsync(session.Id);
    }

    [Fact]
    public async Task GpuIsNullBeforeAnyHelloAndTheDesktopIsUnsupportedOnMacOS()
    {
        await using var h = new GpuHarness(HostPlatform.MacOS);
        var status = h.Status.Build();

        Assert.Null(status["gpu"]);
        Assert.Null(status["workers"]!["lastSpawnAt"]);
        Assert.Equal("unsupported", status["desktop"]!["support"]!.GetValue<string>());
        Assert.Null(status["desktop"]!["tcpAvailable"]);
    }

    [Fact]
    public async Task ASpawnRefusalsCheckReachesLastWorkerError()
    {
        // The check ID is read from the ConfigException inside the WorkerDeadException, never
        // parsed from the message (1b eng-code L1).
        var options = new AblOptions { PoolFloor = 1, StoreDir = Path.Combine(Path.GetTempPath(), "abl-st-" + Guid.NewGuid().ToString("N")[..8]) };
        await using var pool = new WorkerPool(options, new WorkerSupervisor(options, NullLogger<WorkerSupervisor>.Instance), NullLogger<WorkerPool>.Instance)
        {
            // Tagged as the real supervisor tags a runtime-dir refusal: the stage is read, not assumed.
            Spawner = _ => throw WorkerSpawnStage.Tag(
                new WorkerDeadException(
                    "runtime.dir: /run/abl is 0755", new ConfigException("runtime.dir", "/run/abl is 0755", "chmod 700 /run/abl")),
                WorkerSpawnStage.Preflight),
        };
        var config = AblConfig.Resolve(new ConfigInputs { Env = new Dictionary<string, string>(), Home = "/home/u", FileSystem = new FakeConfigFileSystem() });
        await using var sessions = new Abl.Core.Sessions.SessionService(
            options, pool, new Abl.Core.Store.CheckpointStore(options.StoreDir), new Abl.Core.Rendering.GpuLease(),
            new DesktopTier(options, NullLogger<DesktopTier>.Instance), NullLogger<Abl.Core.Sessions.SessionService>.Instance);
        var status = new DaemonStatus(
            new DaemonInstance(1234, DateTimeOffset.UtcNow, "/s", null, 7811, config.EffectiveDigest, Detached: true),
            config, pool, sessions, new DesktopTier(options, NullLogger<DesktopTier>.Instance));

        pool.Replenish();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (pool.LastWorkerError is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var error = status.Build()["workers"]!["lastWorkerError"]!;
        Assert.Equal("runtime.dir", error["check"]!.GetValue<string>());
        Assert.Equal("preflight", error["stage"]!.GetValue<string>());
        Assert.Null(status.Build()["blender"]); // resolved without a Blender search
        Directory.Delete(options.StoreDir, recursive: true);
    }

    [Fact]
    public void AConfigurationRefusalCarriesItsRemedyNotTheTransientRetryHint()
    {
        Assert.True(WorkerErrors.TryMap(
            new WorkerDeadException("blender.found: no Blender", new ConfigException("blender.found", "no Blender", SupportedBlender.FoundRemedy)),
            out var refused));
        Assert.Equal("worker_died", refused.Kind);
        Assert.Equal(SupportedBlender.FoundRemedy, refused.Hint);

        Assert.True(WorkerErrors.TryMap(new WorkerDeadException("socket closed"), out var died));
        Assert.Contains("transient", died.Hint);
    }

    [Fact]
    public void TheStartupSummaryIsOneLineNamingTheInstance()
    {
        var config = AblConfig.Resolve(new ConfigInputs { Env = new Dictionary<string, string>(), Home = "/home/u", FileSystem = new FakeConfigFileSystem() });
        var line = Abl.Daemon.DaemonHost.StartupSummary(
            config, new DaemonInstance(1234, DateTimeOffset.UtcNow, "/s", null, 7811, config.EffectiveDigest, Detached: true));

        Assert.StartsWith($"daemon {AblVersion.Current} started: pid 1234, http://127.0.0.1:7811, store /home/u/.local/share/agentblender/store, blender none,", line);
        Assert.Contains($"config digest {config.EffectiveDigest}", line);
        Assert.DoesNotContain('\n', line);
    }
}
