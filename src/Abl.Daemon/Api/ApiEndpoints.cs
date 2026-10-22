using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Delivery;
using Abl.Core.Lifecycle;
using Abl.Core.Sessions;
using Abl.Core.Telemetry;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using Abl.Daemon.Tools;

namespace Abl.Daemon.Api;

/// <summary>
/// The REST surface the `abl` CLI talks to: one POST /api/ops/{op} per public op, all
/// taking a JSON body and returning JSON. Thin — every handler delegates straight to the
/// op core; the parity test asserts the handler table's keys equal OpSurface.All (FR-009).
/// </summary>
public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private delegate Task<object> OpHandler(
        JsonObject body, SessionService sessions, TrajectoryService trajectories,
        DeliveryService deliveries, DaemonStatus status, CancellationToken ct);

    private static readonly IReadOnlyDictionary<string, OpHandler> Handlers =
        new Dictionary<string, OpHandler>
        {
            ["session_open"] = async (b, s, t, d, x, ct) =>
                (object)await s.OpenAsync(Require(b, "project"), ct),
            ["session_list"] = (b, s, t, d, x, ct) => Task.FromResult<object>(new { items = s.List() }),
            ["session_close"] = (b, s, t, d, x, ct) => CloseAsync(s, Require(b, "session")),
            ["scene_state"] = async (b, s, t, d, x, ct) =>
                (object)new { text = await StateTextAsync(s, b, ct) },
            ["scene_inspect"] = async (b, s, t, d, x, ct) => (object)await s.InspectAsync(
                Require(b, "session"), SceneInspection.Parse(System.Text.Encoding.UTF8.GetBytes(
                    b["request"]?.ToJsonString() ?? "null")), ct),
            ["scene_exec"] = (b, s, t, d, x, ct) => ExecAsync(s, b, ct),
            ["scene_preview"] = async (b, s, t, d, x, ct) => (object)await s.PreviewAsync(
                Require(b, "session"), Int(b, "size") ?? 512, Str(b, "engine") ?? "eevee", ct),
            ["checkpoint_save"] = (b, s, t, d, x, ct) => Task.FromResult<object>(new
            {
                checkpoint = s.SaveCheckpoint(Require(b, "session"), StrList(b, "tags")),
            }),
            ["checkpoint_list"] = (b, s, t, d, x, ct) =>
                Task.FromResult<object>(new { items = s.ListCheckpoints(Str(b, "session")) }),
            ["checkpoint_branch"] = async (b, s, t, d, x, ct) =>
                (object)await s.BranchAsync(Require(b, "checkpoint"), Str(b, "project"), ct),
            ["checkpoint_tag"] = (b, s, t, d, x, ct) => Task.FromResult(Tag(s, b)),
            ["checkpoint_diff"] = async (b, s, t, d, x, ct) =>
                (object)await s.DiffAsync(Require(b, "a"), Require(b, "b"), ct),
            ["checkpoint_prune"] = (b, s, t, d, x, ct) =>
                Task.FromResult<object>(s.Prune(Bool(b, "force") ?? false)),
            ["trajectory_freeze"] = (b, s, t, d, x, ct) => Task.FromResult(Freeze(t, b)),
            ["trajectory_export"] = (b, s, t, d, x, ct) =>
                Task.FromResult<object>(t.Export(Require(b, "artifact"), Require(b, "out"))),
            ["trajectory_import"] = (b, s, t, d, x, ct) =>
                Task.FromResult<object>(t.Import(Require(b, "bundle"))),
            ["replay_run"] = async (b, s, t, d, x, ct) =>
                (object)await t.ReplayAsync(Require(b, "artifact"), ct),
            ["render_final"] = async (b, s, t, d, x, ct) => (object)await s.RenderFinalAsync(
                Str(b, "session"), Str(b, "checkpoint"),
                Str(b, "engine") ?? "cycles_gpu", Int(b, "res") ?? 1024,
                Int(b, "samples"), Str(b, "out"), Str(b, "lease") ?? "wait",
                Int(b, "seed") ?? 0, ct),
            ["batch_run"] = async (b, s, t, d, x, ct) => (object)await t.BatchAsync(
                Require(b, "artifact"),
                ParamTable(b),
                Int(b, "res") ?? 512, Int(b, "columns") ?? 3,
                Str(b, "engine") ?? "eevee", ct),
            ["delivery_export"] = async (b, s, t, d, x, ct) => (object)await d.ExportAsync(
                Require(b, "artifact"), Require(b, "out"),
                OptionalParamTable(b),
                Str(b, "engine") ?? "cycles_gpu", Int(b, "res") ?? 1024,
                Int(b, "samples"), Int(b, "seed") ?? 0, Int(b, "columns") ?? 3, ct),
            ["delivery_list"] = (b, s, t, d, x, ct) =>
                Task.FromResult<object>(d.List(Require(b, "payload"))),
            ["delivery_verify"] = (b, s, t, d, x, ct) =>
                Task.FromResult<object>(d.Verify(Require(b, "payload"))),
            ["daemon_status"] = (b, s, t, d, x, ct) => Task.FromResult<object>(x.Build()),
        };

    /// <summary>The handler table's key set — the parity contract test pins it to OpSurface.All.</summary>
    public static IReadOnlyList<string> Ops { get; } = [.. Handlers.Keys];

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/ops/{op}", async (
            string op,
            JsonElement? input,
            SessionService sessions,
            TrajectoryService trajectories,
            DeliveryService deliveries,
            DaemonStatus daemonStatus,
            CancellationToken ct) =>
        {
            var (status, json) = await DispatchAsync(op, input, sessions, trajectories, deliveries, daemonStatus, ct);
            return Results.Text(json, "application/json", System.Text.Encoding.UTF8, status);
        });
    }

    /// <summary>
    /// One REST op: the status code and the JSON body (a result, or the error envelope).
    /// The route handler only writes them out, so unit tests drive the REST surface here.
    /// </summary>
    internal static async Task<(int Status, string Json)> DispatchAsync(
        string op,
        JsonElement? input,
        SessionService sessions,
        TrajectoryService trajectories,
        DeliveryService deliveries,
        DaemonStatus daemonStatus,
        CancellationToken ct)
    {
        try
        {
            var body = input is { ValueKind: not JsonValueKind.Null } value
                ? JsonNode.Parse(value.GetRawText()) as JsonObject ?? throw new AblException("bad_request", "body must be an object")
                : new JsonObject();
            if (op == "scene_inspect" && input is { ValueKind: JsonValueKind.Object } raw)
            {
                try
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in raw.EnumerateObject())
                        if (!names.Add(property.Name)) throw new AblException("bad_request", "duplicate inspection envelope property");
                    if (!raw.TryGetProperty("request", out var inspection)) throw new AblException("bad_request", "missing inspection request");
                    body["request"] = SceneInspection.ToWorker(SceneInspection.Parse(System.Text.Encoding.UTF8.GetBytes(inspection.GetRawText())));
                }
                catch (AblException error)
                {
                    var id = raw.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String &&
                        s.GetString() is { Length: <= 128 } name ? name : null;
                    await SceneInspection.RecordRejectionAsync(error, id, sessions.ProjectOf(id));
                    throw;
                }
            }
            // Unknown ops never reach telemetry: the raw route segment must not
            // become a span name or metric label (caller-controlled cardinality).
            if (!Handlers.TryGetValue(op, out var handler))
            {
                throw new AblException(
                    "not_found", $"unknown op '{op}'", hint: $"ops: {string.Join(", ", Ops)}");
            }

            var json = await AblTelemetry.RunAsync(
                op, Str(body, "session"), Str(body, "project") ?? sessions.ProjectOf(Str(body, "session")), async () =>
                {
                    var result = await handler(body, sessions, trajectories, deliveries, daemonStatus, ct);
                    return JsonSerializer.Serialize(result, Json);
                }, PayloadSizing.Utf8Bytes);
            return (200, json);
        }
        catch (AblException ex)
        {
            return ErrorJson(ex);
        }
        // A worker failure on a transient-worker path (render/replay/batch) must not
        // escape as an empty-body 500 — clients can't parse that. Session ops convert
        // their own worker errors upstream; this is the surface backstop.
        catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped))
        {
            return ErrorJson(mapped);
        }
    }

    private static async Task<string> StateTextAsync(
        SessionService sessions, JsonObject body, CancellationToken ct)
    {
        var mode = Str(body, "mode") ?? "delta";
        var state = await sessions.StateAsync(Require(body, "session"), mode, ct);
        return mode == "full" ? StateText.RenderFull(state) : StateText.RenderDelta(state);
    }

    private static async Task<object> ExecAsync(
        SessionService sessions, JsonObject body, CancellationToken ct)
    {
        var result = await sessions.ExecAsync(
            Require(body, "session"),
            Require(body, "script"),
            body["params"] as JsonObject, ct);
        return new ExecToolResult(
            StateText.RenderDelta(result.StateDelta), result.Checkpoint, result.DurationMs);
    }

    private static async Task<object> CloseAsync(SessionService sessions, string id)
    {
        await sessions.CloseAsync(id);
        return new { closed = id };
    }

    private static object Tag(SessionService sessions, JsonObject body)
    {
        sessions.TagCheckpoint(Require(body, "checkpoint"), StrList(body, "tags"));
        return new { ok = true };
    }

    private static object Freeze(TrajectoryService trajectories, JsonObject body)
    {
        var artifact = trajectories.Freeze(
            Require(body, "session"), Require(body, "name"), Int(body, "from"), Int(body, "to"));
        return new FreezeToolResult(
            artifact.Name, artifact.Ops.Count, artifact.BaseCheckpoint, artifact.ExpectedDigest);
    }

    private static IReadOnlyList<JsonObject> ParamTable(JsonObject body) =>
        body["paramTable"] is JsonArray array
            ? [.. array.Select(row => row?.AsObject()
                ?? throw new AblException("bad_request", "paramTable rows must be objects"))]
            : throw new AblException("bad_request", "batch_run needs a paramTable array");

    // delivery_export's param table is OPTIONAL: absent = one image from the artifact's frozen
    // params; the first profile accepts zero/one row only. An empty [] is honored.
    private static IReadOnlyList<JsonObject> OptionalParamTable(JsonObject body)
    {
        if (body["paramTable"] is null)
        {
            return [];
        }

        if (body["paramTable"] is not JsonArray array)
        {
            throw new AblException("bad_request", "paramTable must be a JSON array of objects");
        }

        if (array.Count > 1) throw new AblException("bad_request", "first delivery profile supports only one parameter row");
        return [.. array.Select(row => row as JsonObject ?? throw new AblException("bad_request", "paramTable rows must be objects"))];
    }

    private static (int Status, string Json) ErrorJson(AblException ex) => (StatusFor(ex.Kind), JsonSerializer.Serialize(new
    {
        kind = ex.Kind,
        message = ex.Message,
        hint = ex.Hint,
        lastCheckpoint = ex.LastCheckpoint,
        traceback = ex.WorkerTraceback,
    }, Json));

    /// <summary>
    /// <c>gpu_unavailable</c> is 503, the family of <c>gpu_busy</c> (008 T016, adopted by 009
    /// plan D6): the request is valid, the host lacks the resource. No <c>Retry-After</c>; the
    /// kind and hint are the agent's discriminator.
    /// </summary>
    internal static int StatusFor(string kind) => kind switch
    {
        "not_found" => 404,
        "session_busy" => 409,
        "bad_request" or "bad_reference" => 400,
        "exec_failed" => 422,
        "gpu_busy" or "gpu_unavailable" => 503,
        "timeout" => 504,
        "worker_died" => 502,
        _ => 500,
    };

    private static string Require(JsonObject body, string key) =>
        Str(body, key) ?? throw new AblException("bad_request", $"missing required field '{key}'");

    private static string? Str(JsonObject body, string key) => body[key]?.GetValue<string>();

    private static int? Int(JsonObject body, string key) => body[key]?.GetValue<int>();

    private static bool? Bool(JsonObject body, string key) => body[key]?.GetValue<bool>();

    private static string[] StrList(JsonObject body, string key) =>
        body[key] is JsonArray array ? [.. array.Select(n => n!.GetValue<string>())] : [];
}
