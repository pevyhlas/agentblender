using System.Text;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Delivery;
using Abl.Core.Lifecycle;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Telemetry;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using Abl.Daemon.Api;
using Abl.Daemon.Tools;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Abl.Daemon;

/// <summary>
/// The daemon host (008 FR-007, plan B3): <c>abl daemon run</c> is its only entry point. It
/// builds with no command-line arguments, its content root in the state dir and no
/// <c>appsettings</c> files, so no ambient file or argument can steer Kestrel. The Phase 0
/// Host/Origin guard and both FR-013 loopback fail-fast guards are unchanged.
/// </summary>
public static class DaemonHost
{
    /// <summary>Serves until <paramref name="ct"/> is cancelled or the host is told to stop (SIGTERM, Ctrl-C).</summary>
    /// <returns>The process exit code: 0 after a clean shutdown, 1 when a guard refused to serve.</returns>
    /// <exception cref="ArgumentException">
    /// The configuration was resolved without a Blender search: the daemon never spawns a
    /// Blender the resolver did not probe or discover (1b eng-arch LOW-A).
    /// </exception>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public static async Task<int> RunAsync(AblConfig config, DaemonInstance instance, CancellationToken ct)
    {
        if (config.Blender is null)
        {
            throw new ArgumentException(
                "the daemon needs a configuration resolved with a Blender search (ConfigInputs.WithBlenderSearch)", nameof(config));
        }

        var options = config.Options;
        Directory.CreateDirectory(instance.StateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = instance.StateDir });

        // No appsettings*.json (or user-secrets) file: configuration comes from the resolver.
        // Environment variables remain, as explicit operator input (plan A6).
        var sources = ((IConfigurationBuilder)builder.Configuration).Sources;
        foreach (var file in sources.OfType<FileConfigurationSource>().ToList())
        {
            sources.Remove(file);
        }

        QuietLogs(builder, sources, options);

        // FR-013: loopback only. Widening this bind is a security-review trigger.
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(instance);
        builder.Services.AddSingleton<WorkerSupervisor>();
        builder.Services.AddSingleton<WorkerPool>();
        builder.Services.AddSingleton(new CheckpointStore(options.StoreDir));
        builder.Services.AddSingleton<GpuLease>();
        builder.Services.AddSingleton<DesktopTier>();
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddSingleton<TrajectoryService>();
        builder.Services.AddSingleton<DeliveryService>();
        builder.Services.AddSingleton<DaemonStatus>();

        builder.Services
            // 008 FR-013 (plan D3): when to use AgentBlender and when AgentGraphicDesign.
            .AddMcpServer(server => server.ServerInstructions = AgentGuidance.ServerInstructions)
            .WithHttpTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellation) =>
            {
                // Check the retained JSON element before typed binding erases repeated keys.
                // Other tools retain their current binding and error contracts.
                if (context.Params?.Name == "scene_inspect")
                {
                    try
                    {
                        if (context.Params.Arguments is null || !context.Params.Arguments.TryGetValue("request", out var raw))
                            throw new AblException("bad_request", "missing inspection request");
                        SceneInspection.Parse(Encoding.UTF8.GetBytes(raw.GetRawText()));
                    }
                    catch (AblException error)
                    {
                        var failure = new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = "bad_request: " + error.Message }],
                        };
                        var id = context.Params.Arguments is { } arguments && arguments.TryGetValue("session", out var s) &&
                            s.ValueKind == System.Text.Json.JsonValueKind.String && s.GetString() is { Length: <= 128 } name ? name : null;
                        await SceneInspection.RecordRejectionAsync(error, id, null);
                        return failure;
                    }
                }
                return await next(context, cancellation);
            }))
            .WithTools<AblTools>();

        AddTelemetry(builder, options);

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Abl.Daemon");

        // FR-001 (008): Host/Origin guard first, before routing, with no path exemption — it
        // covers /mcp (POST, GET/SSE, DELETE), /api/ops/* and /healthz (DNS rebinding defence).
        app.UseMiddleware<HostGuard>();
        app.UseRouting();

        // pid and version identify the instance for start/stop; lastWorkerError lets start
        // fail on a broken worker at once (008 FR-010, AC-18).
        app.MapGet("/healthz", (WorkerPool pool, SessionService sessions, DesktopTier desktop) => Results.Ok(new
        {
            status = "ok",
            version = AblVersion.Current,
            pid = instance.Pid,
            readyWorkers = pool.ReadyCount,
            sessions = sessions.List().Count,
            lastWorkerError = pool.LastWorkerError,
            desktopConfigured = desktop.IsConfigured,
            desktopTcpAvailable = desktop.TcpAvailable, // the daemon_status encoding (T051)
        }));

        app.MapMcp("/mcp");
        ApiEndpoints.Map(app);

        // FR-013 fail-fast, part 1: refuse configured non-loopback Kestrel endpoints BEFORE
        // anything binds — Kestrel endpoint configuration (Kestrel__Endpoints__*) silently
        // OVERRIDES UseUrls, and checking only after StartAsync would open the socket for a
        // moment (every 5 s under systemd Restart=on-failure).
        var configuredNonLoopback = app.Configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(endpoint => endpoint["Url"])
            .Where(url => url is not null && !IsLoopback(url))
            .ToList();
        if (configuredNonLoopback.Count > 0)
        {
            logger.LogCritical(
                "refusing to start: non-loopback Kestrel endpoint(s) configured {Urls} (FR-013 — the daemon is loopback-only)",
                string.Join(", ", configuredNonLoopback));
            await app.DisposeAsync();
            return 1;
        }

        // FR-013 fail-fast, part 2 (backstop for any other config path): assert the loopback
        // invariant on the addresses actually bound and refuse to serve otherwise.
        await app.StartAsync(ct);
        var offending = app.Urls.Where(url => !IsLoopback(url)).ToList();
        if (offending.Count > 0)
        {
            logger.LogCritical(
                "refusing to serve: non-loopback bind {Urls} (FR-013 — the daemon is loopback-only; remove Kestrel endpoint overrides)",
                string.Join(", ", offending));
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            return 1;
        }

        LogConfigProblems(logger, config);
        logger.LogInformation("{Summary}", StartupSummary(config, instance));

        // Pre-warm the pool so the first session_open never pays the cold start (Principle III).
        app.Services.GetRequiredService<WorkerPool>().Replenish();

        await app.WaitForShutdownAsync(ct);
        logger.LogInformation("daemon pid {Pid} stopping", instance.Pid);
        await app.DisposeAsync();
        return 0;
    }

    /// <summary>
    /// 008 FR-011: framework categories log at Warning and <c>Abl.*</c> at
    /// <c>daemon.logLevel</c> (default Information); a level stricter than Warning applies to
    /// both. The levels are the lowest-precedence configuration source, so a
    /// <c>Logging__LogLevel__*</c> variable (explicit operator input, plan A6) still overrides
    /// them. One line per entry, timestamped in UTC, so the log tail <c>start</c> prints on a
    /// failure (at most 20 lines, AC-18) holds whole events.
    /// </summary>
    private static void QuietLogs(WebApplicationBuilder builder, IList<IConfigurationSource> sources, AblOptions options)
    {
        var level = Enum.Parse<LogLevel>(options.LogLevel, ignoreCase: true);
        var framework = level > LogLevel.Warning ? level : LogLevel.Warning;
        sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = framework.ToString(),
                ["Logging:LogLevel:Abl"] = level.ToString(),
            },
        });
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });
    }

    /// <summary>The one startup line (FR-011): who serves where, with which Blender and options.</summary>
    internal static string StartupSummary(AblConfig config, DaemonInstance instance)
    {
        var options = config.Options;
        var blender = config.Blender switch
        {
            { Check: { } check } => $"refused ({check})",
            { Path: { } path } b => $"{b.Version?.ToString() ?? "?"} ({(b.Support is { } s ? SupportedBlender.Name(s) : "unknown")}{(b.UnsupportedAllowed ? ", allowUnsupported" : "")}) at {path}",
            _ => "none",
        };
        var desktop = options.Platform == HostPlatform.MacOS ? "unsupported"
            : options.DesktopEndpoint is null ? "off"
            : options.DesktopToken is null ? "no token"
            : options.DesktopEndpoint;
        return $"daemon {instance.Version} started: pid {instance.Pid}, {instance.Url}, store {options.StoreDir}, " +
            $"blender {blender}, log level {options.LogLevel}, telemetry {(options.TelemetryEnabled ? "on" : "off")}, " +
            $"desktop {desktop}, config digest {instance.EffectiveConfigDigest}";
    }

    private static void LogConfigProblems(ILogger logger, AblConfig config)
    {
        if (config[AblConfig.TokenKey].Error is { } tokenProblem)
        {
            logger.LogWarning("desktop tier refused: {Problem}", tokenProblem);
        }

        if (config[AblConfig.DesktopEndpointKey].Error is { } desktopProblem)
        {
            logger.LogWarning("desktop.endpoint: {Problem}", desktopProblem); // macOS: unsupported (008 FR-004)
        }

        if (config.Blender is { Check: { } blenderCheck } blender)
        {
            logger.LogWarning("{Check}: {Problem} (hint: {Hint})", blenderCheck, blender.Problem, blender.Hint);
        }
        else if (config.Blender is { Warning: { } blenderWarning })
        {
            logger.LogWarning("blender.version: {Warning}", blenderWarning);
        }
    }

    // FR-012: telemetry is OFF by default; it is enabled with ABL_TELEMETRY=1 or
    // telemetry.enabled. telemetry.otlpEndpoint / OTEL_EXPORTER_OTLP_ENDPOINT overrides the
    // collector default (:4318, http/protobuf); per-signal paths are set explicitly —
    // programmatic endpoints don't get them appended.
    private static void AddTelemetry(WebApplicationBuilder builder, AblOptions options)
    {
        if (!options.TelemetryEnabled)
        {
            return;
        }

        builder.Services.AddHostedService<RuntimeTelemetry>();
        var otlpBase = new Uri(options.OtlpEndpoint);
        var exportInterval = int.TryParse(
            Environment.GetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL"), out var ms) ? ms : 10_000;
        builder.Services.AddOpenTelemetry()
            // A generated service.instance.id would create a fresh metric series on every restart.
            .ConfigureResource(r => r.AddService("agentblender", serviceInstanceId: Environment.MachineName))
            // AlwaysOn: op spans start under ASP.NET's unrecorded hosting activity, and the
            // default parent-based sampler would drop them all.
            .WithTracing(t => t.SetSampler(new AlwaysOnSampler()).AddSource(AblTelemetry.SourceName).AddOtlpExporter(o =>
            {
                o.Endpoint = options.TracesEndpoint is { } traces ? new Uri(traces) : new Uri(otlpBase, "/v1/traces");
                o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
            }))
            .WithMetrics(m => m.AddMeter(AblTelemetry.SourceName)
            .AddView("abl.op.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 120000, 300000, 600000],
            })
            .AddView("abl.gpu.lease_wait", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [1, 10, 100, 1000, 5000, 10000, 30000, 60000, 120000, 300000, 600000],
            })
            .AddView("abl.op.payload_bytes", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [128, 512, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304],
            }).AddOtlpExporter((o, reader) =>
            {
                o.Endpoint = new Uri(otlpBase, "/v1/metrics");
                o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = exportInterval;
            }));
    }

    internal static bool IsLoopback(string url)
    {
        try
        {
            var host = new Uri(url.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0"))
                .Host.Trim('[', ']');
            return host == "localhost"
                || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
        }
        catch (UriFormatException)
        {
            return false; // an endpoint we cannot parse is an endpoint we refuse to serve
        }
    }
}
