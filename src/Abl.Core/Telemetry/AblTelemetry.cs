using System.Diagnostics;
using System.Diagnostics.Metrics;
using Abl.Core.Workers;

namespace Abl.Core.Telemetry;

/// <summary>
/// The one instrumentation choke point (FR-012): every public op flows through RunAsync
/// on both surfaces (MCP tools and REST). Spans carry {op, session, project, outcome,
/// payload_bytes}; metrics keep low-cardinality labels {op, outcome} only. No scene
/// payloads, script text, pixels, or paths ever become label values — the hygiene test
/// pins that. Export is OFF unless ABL_TELEMETRY=1 (daemon wiring).
/// </summary>
public static class AblTelemetry
{
    private sealed class OpContext { public string Tier = "none"; }
    private static readonly AsyncLocal<OpContext?> Context = new();

    public const string SourceName = "agentblender";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> Ops =
        Meter.CreateCounter<long>("abl.ops", description: "public ops served, by op and outcome");

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("abl.op.duration", "ms", "op wall time");

    private static readonly Histogram<long> PayloadBytes =
        Meter.CreateHistogram<long>("abl.op.payload_bytes", "By",
            "bytes returned to the caller — the token-economy feedback signal");

    private static readonly Histogram<double> LeaseWait =
        Meter.CreateHistogram<double>("abl.gpu.lease_wait", "ms", "time waiting for a GPU lease");

    public sealed record RuntimeSnapshot(int ReadyWorkers, int Sessions, bool DesktopConfigured, int DesktopAvailable);
    private static Func<RuntimeSnapshot>? _runtime;
    private static readonly ObservableGauge<long> Heartbeat = Meter.CreateObservableGauge(
        "abl.daemon.heartbeat", () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "s", "last daemon collection timestamp");
    private static readonly ObservableGauge<int> ReadyWorkers = Meter.CreateObservableGauge(
        "abl.workers.ready", () => _runtime?.Invoke().ReadyWorkers ?? 0);
    private static readonly ObservableGauge<int> Sessions = Meter.CreateObservableGauge(
        "abl.sessions.active", () => _runtime?.Invoke().Sessions ?? 0);
    private static readonly ObservableGauge<int> DesktopConfigured = Meter.CreateObservableGauge(
        "abl.desktop.configured", () => _runtime?.Invoke().DesktopConfigured == true ? 1 : 0);
    private static readonly ObservableGauge<int> DesktopAvailable = Meter.CreateObservableGauge(
        "abl.desktop.available", () => _runtime?.Invoke().DesktopAvailable ?? -1);

    public static void ObserveRuntime(Func<RuntimeSnapshot> read) => _runtime = read;

    public static void SetTier(WorkerTier tier)
    {
        var value = tier == WorkerTier.Desktop ? "desktop" : "local";
        if (Context.Value is { } context) context.Tier = value;
        Activity.Current?.SetTag("abl.tier", value);
    }

    public static void RecordLeaseWait(string tier, double ms, string outcome) =>
        LeaseWait.Record(ms,
            new KeyValuePair<string, object?>("tier", tier is "desktop" ? "desktop" : "local"),
            new KeyValuePair<string, object?>("outcome", outcome));

    public static async Task<T> RunAsync<T>(
        string op, string? session, string? project, Func<Task<T>> action, Func<T, long>? payloadSize = null)
    {
        using var activity = Source.StartActivity($"abl.{op}");
        var previous = Context.Value;
        var context = new OpContext();
        Context.Value = context;
        activity?.SetTag("abl.op", op);
        if (session is not null)
        {
            activity?.SetTag("abl.session", session);
        }

        if (project is not null)
        {
            activity?.SetTag("abl.project", project);
        }

        var stopwatch = Stopwatch.StartNew();
        var outcome = "error";
        try
        {
            var result = await action().ConfigureAwait(false);
            outcome = "ok";
            if (payloadSize?.Invoke(result) is { } bytes)
            {
                activity?.SetTag("abl.payload_bytes", bytes);
                PayloadBytes.Record(bytes, new KeyValuePair<string, object?>("op", op),
                    new KeyValuePair<string, object?>("tier", context.Tier));
            }

            return result;
        }
        catch (AblException ex)
        {
            outcome = ex.Kind;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Kind);
            throw;
        }
        catch
        {
            outcome = "error";
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            activity?.SetTag("abl.outcome", outcome);
            activity?.SetTag("abl.tier", context.Tier);
            var tags = new TagList { { "op", op }, { "outcome", outcome }, { "tier", context.Tier } };
            Ops.Add(1, tags);
            Duration.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
            Context.Value = previous;
        }
    }
}
