using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Delivery;
using Abl.Core.Lifecycle;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Telemetry;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Abl.Daemon.Tools;

public sealed record ExecToolResult(string Delta, string Checkpoint, double DurationMs);

public sealed record FreezeToolResult(string Name, int Ops, string BaseCheckpoint, string ExpectedDigest);

/// <summary>
/// The MCP surface — a thin layer over the op core; names come from OpSurface and the
/// parity test keeps CLI/REST/MCP identical (FR-009). Results follow the token-economy
/// contracts: terse text state, one preview image, paths (never pixels) for finals.
/// </summary>
[McpServerToolType]
public sealed class AblTools(
    SessionService sessions,
    TrajectoryService trajectories,
    DeliveryService delivery,
    DaemonStatus status)
{
    [McpServerTool(Name = "session_open", UseStructuredContent = true)]
    [Description("Open a design session backed by a pre-warmed headless Blender worker.")]
    public Task<SessionInfo> SessionOpen(
        [Description("Project the session belongs to (e.g. my-game)")] string project,
        CancellationToken ct) => Guard("session_open", null, project, () => sessions.OpenAsync(project, ct));

    [McpServerTool(Name = "session_list", UseStructuredContent = true)]
    [Description("List live sessions: id, project, state, checkpoint head.")]
    public Task<IReadOnlyList<SessionInfo>> SessionList() =>
        Guard("session_list", null, null, () => Task.FromResult(sessions.List()));

    [McpServerTool(Name = "session_close")]
    [Description("Close a session and recycle its worker. Checkpoints and journal survive.")]
    public Task<string> SessionClose([Description("Session id")] string session) =>
        Guard("session_close", session, null, async () =>
        {
            await sessions.CloseAsync(session);
            return $"closed {session}";
        });

    [McpServerTool(Name = "scene_state")]
    [Description("Read scene state as terse text. Default mode=delta returns only changes since the last read; mode=full lists every object.")]
    public Task<string> SceneState(
        [Description("Session id")] string session,
        [Description("delta (default) or full")] string mode = "delta",
        CancellationToken ct = default) =>
        Guard("scene_state", session, null, async () =>
        {
            var state = await sessions.StateAsync(session, mode, ct);
            return mode == "full" ? StateText.RenderFull(state) : StateText.RenderDelta(state);
        });

    [McpServerTool(Name = "scene_exec", UseStructuredContent = true)]
    [Description("Execute a Python script (bpy available, `params` dict injected) atomically: on failure the scene is restored and state is unchanged. Auto-checkpoints on success and returns the state delta.")]
    public Task<ExecToolResult> SceneExec(
        [Description("Session id")] string session,
        [Description("Python source to run inside Blender")] string script,
        [Description("Optional params object the script reads as `params`")] JsonElement? parameters = null,
        CancellationToken ct = default) =>
        Guard("scene_exec", session, null, async () =>
        {
            var result = await sessions.ExecAsync(session, script, ToObject(parameters), ct);
            return new ExecToolResult(
                StateText.RenderDelta(result.StateDelta), result.Checkpoint, result.DurationMs);
        });

    [McpServerTool(Name = "scene_inspect", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read bounded selected-object world transforms, camera basis and world landmark projections as structured JSON without changing the checkpoint or delta baseline. Optional raw geometry/material/modifier facts have explicit bounded coverage, not replay or visual equivalence.")]
    public Task<JsonObject> SceneInspect(
        [Description("Session id")] string session,
        [Description("Selected object names, optional camera and named world-space landmarks; optional bounded fact sections")] SceneInspectionRequest request,
        CancellationToken ct = default) =>
        Guard("scene_inspect", session, null, () => sessions.InspectAsync(session, request, ct));

    [McpServerTool(Name = "scene_preview")]
    [Description("Render ONE downscaled preview (<=512px) of the current scene and return it inline.")]
    public Task<IEnumerable<ContentBlock>> ScenePreview(
        [Description("Session id")] string session,
        [Description("Longest edge in px, capped at 512")] int size = 512,
        [Description("eevee (default), cycles_gpu, cycles_cpu or workbench")] string engine = "eevee",
        CancellationToken ct = default) =>
        Guard<IEnumerable<ContentBlock>>("scene_preview", session, null, async () =>
        {
            var preview = await sessions.PreviewAsync(session, size, engine, ct);
            var bytes = await File.ReadAllBytesAsync(preview.Path, ct);
            return
            [
                ImageContentBlock.FromBytes(bytes, "image/png"),
                new TextContentBlock { Text = $"{preview.Path} ({preview.DurationMs:0} ms, {preview.Engine})" },
            ];
        });

    [McpServerTool(Name = "checkpoint_save")]
    [Description("Tag the current head checkpoint (state is auto-checkpointed on every exec). Returns the hash.")]
    public Task<string> CheckpointSave(
        [Description("Session id")] string session,
        [Description("Tags to attach")] string[]? tags = null) =>
        Guard("checkpoint_save", session, null, () => Task.FromResult(sessions.SaveCheckpoint(session, tags ?? [])));

    [McpServerTool(Name = "checkpoint_list")]
    [Description("List checkpoints as a terse tree: hash prefix, parent, tags, age, session.")]
    public Task<string> CheckpointList(
        [Description("Only this session's checkpoints")] string? session = null) =>
        Guard("checkpoint_list", session, null, () =>
        {
            var now = DateTimeOffset.UtcNow;
            var lines = sessions.ListCheckpoints(session)
                .Select(m => StateText.RenderCheckpointLine(m, now));
            return Task.FromResult(string.Join("\n", lines) is { Length: > 0 } text ? text : "(no checkpoints)");
        });

    [McpServerTool(Name = "checkpoint_branch", UseStructuredContent = true)]
    [Description("Open a NEW session whose worker holds the state of the given checkpoint (hash prefix or tag). This is also the crash-recovery move.")]
    public Task<SessionInfo> CheckpointBranch(
        [Description("Checkpoint hash, hash prefix, or tag")] string checkpoint,
        [Description("Project for the new session (defaults to the checkpoint's)")] string? project = null,
        CancellationToken ct = default) =>
        Guard("checkpoint_branch", null, project, () => sessions.BranchAsync(checkpoint, project, ct));

    [McpServerTool(Name = "checkpoint_tag")]
    [Description("Attach tags to a checkpoint.")]
    public Task<string> CheckpointTag(
        [Description("Checkpoint hash, hash prefix, or tag")] string checkpoint,
        [Description("Tags to attach")] string[] tags) =>
        Guard("checkpoint_tag", null, null, () =>
        {
            sessions.TagCheckpoint(checkpoint, tags);
            return Task.FromResult("ok");
        });

    [McpServerTool(Name = "checkpoint_diff", UseStructuredContent = true)]
    [Description("Object-set delta between two checkpoints.")]
    public Task<JsonObject> CheckpointDiff(
        [Description("Checkpoint A (hash prefix or tag)")] string a,
        [Description("Checkpoint B (hash prefix or tag)")] string b,
        CancellationToken ct = default) => Guard("checkpoint_diff", null, null, () => sessions.DiffAsync(a, b, ct));

    [McpServerTool(Name = "checkpoint_prune", UseStructuredContent = true)]
    [Description("Delete unreferenced checkpoints. Refuses live heads, frozen-artifact refs, and (unless force) tagged checkpoints; returns the refused hashes.")]
    public Task<PruneResult> CheckpointPrune(
        [Description("Also delete tagged checkpoints")] bool force = false) =>
        Guard("checkpoint_prune", null, null, () => Task.FromResult(sessions.Prune(force)));

    [McpServerTool(Name = "trajectory_freeze", UseStructuredContent = true)]
    [Description("Freeze a session's exec trajectory (optionally a seq range) into a named, self-contained, replayable artifact.")]
    public Task<FreezeToolResult> TrajectoryFreeze(
        [Description("Session id")] string session,
        [Description("Artifact name")] string name,
        [Description("First exec seq to include")] int? from = null,
        [Description("Last exec seq to include")] int? to = null) =>
        Guard("trajectory_freeze", session, null, () =>
        {
            var artifact = trajectories.Freeze(session, name, from, to);
            return Task.FromResult(new FreezeToolResult(
                artifact.Name, artifact.Ops.Count, artifact.BaseCheckpoint, artifact.ExpectedDigest));
        });

    [McpServerTool(Name = "trajectory_export", UseStructuredContent = true)]
    [Description("Export a frozen artifact as a git-committable bundle directory (artifact.json + base.blend). Commit it to a repo; trajectory_import reconstitutes it on any machine.")]
    public Task<ExportResult> TrajectoryExport(
        [Description("Frozen artifact name")] string artifact,
        [Description("Destination directory (bundle lands at <out>/<name>/)")] string @out) =>
        Guard("trajectory_export", null, null, () => Task.FromResult(trajectories.Export(artifact, @out)));

    [McpServerTool(Name = "trajectory_import", UseStructuredContent = true)]
    [Description("Import a bundle directory (e.g. from a repo checkout) into the store, integrity-verified; replay_run then reconstitutes the scene digest-verified.")]
    public Task<ImportResult> TrajectoryImport(
        [Description("Bundle directory containing artifact.json + base.blend")] string bundle) =>
        Guard("trajectory_import", null, null, () => Task.FromResult(trajectories.Import(bundle)));

    [McpServerTool(Name = "replay_run", UseStructuredContent = true)]
    [Description("Replay a frozen artifact on a FRESH worker. PASS means the final canonical scene digest equals the frozen expectation. If the artifact was frozen on a different Blender version, artifactBlenderVersion/workerBlenderVersion are set (informational — the digest is the gate).")]
    public Task<ReplayResult> ReplayRun(
        [Description("Frozen artifact name")] string artifact,
        CancellationToken ct = default) => Guard("replay_run", null, null, () => trajectories.ReplayAsync(artifact, ct));

    [McpServerTool(Name = "render_final", UseStructuredContent = true)]
    [Description("Full-quality render to DISK. Returns path + metadata only — final pixels never enter model context. Served by the local GPU, or a desktop GPU tier when one is configured (it falls back to local automatically if the desktop is unreachable); the result's `tier` field reports which served it (local|desktop). For Cycles engines the result's `device` reports the device AB configured for the render. The first cycles_gpu render on a Mac can take about a minute while Metal compiles its kernels; do not cancel it as hung. `abl doctor --check gpu.render` primes the cache.")]
    public Task<RenderResult> RenderFinal(
        [Description("Session id (or use checkpoint)")] string? session = null,
        [Description("Checkpoint hash/prefix/tag (or use session)")] string? checkpoint = null,
        [Description("eevee, cycles_gpu (default), cycles_cpu or workbench")] string engine = "cycles_gpu",
        [Description("Resolution in px (square)")] int res = 1024,
        [Description("Sampling count")] int? samples = null,
        [Description("Output path (defaults under the renders dir)")] string? @out = null,
        [Description("GPU lease policy: wait (default), cpu_fallback or fail. cpu_fallback renders cycles_cpu when the GPU is busy or the host has no usable GPU")] string lease = "wait",
        [Description("Render seed")] int seed = 0,
        CancellationToken ct = default) =>
        Guard("render_final", session, null, () => sessions.RenderFinalAsync(session, checkpoint, engine, res, samples, @out, lease, seed, ct));

    [McpServerTool(Name = "batch_run")]
    [Description("Run a frozen artifact once per param row (rows overlay frozen params), render each variant to disk, and return ONE labeled contact sheet inline. Delegates to the desktop GPU tier when it is reachable (pixels ship back to this host), else runs locally; the serving tier (local|desktop) is reported, and for Cycles engines the batch's `device`.")]
    public Task<IEnumerable<ContentBlock>> BatchRun(
        [Description("Frozen artifact name")] string artifact,
        [Description("Array of param objects, one per variant")] JsonElement paramTable,
        [Description("Per-variant render resolution")] int res = 512,
        [Description("Contact sheet columns")] int columns = 3,
        [Description("Render engine for variants")] string engine = "eevee",
        CancellationToken ct = default) =>
        Guard<IEnumerable<ContentBlock>>("batch_run", null, null, async () =>
        {
            var rows = ToParamTable(paramTable);
            var result = await trajectories.BatchAsync(artifact, rows, res, columns, engine, ct);
            var bytes = await File.ReadAllBytesAsync(result.ContactSheet, ct);
            return
            [
                ImageContentBlock.FromBytes(bytes, "image/png"),
                new TextContentBlock
                {
                    Text = $"sheet: {result.ContactSheet} (tier {result.Tier})\n"
                        + (result.Device is { } device ? $"device: {device}\n" : "")
                        + (result.FallbackReason is { } reason ? $"fallback: {reason}\n" : "")
                        + (result.FallbackHint is { } hint ? $"hint: {hint}\n" : "")
                        + string.Join("\n", result.Renders),
                },
            ];
        });

    [McpServerTool(Name = "delivery_export")]
    [Description("Export one frozen procedural still into a fresh completed generation: exact AGD v1 descriptor+PNG payload, separate producer receipt/recipe/replay source, and ONE contact-sheet preview inline. Zero/one parameter row supported; families reject. Local tier. Renders with cycles_gpu by default; on a host without a GPU pass engine=cycles_cpu. AGD preflight/decoded verification are separate from admission.")]
    public Task<IEnumerable<ContentBlock>> DeliveryExport(
        [Description("Frozen artifact name")] string artifact,
        [Description("Output parent; a fresh named generation is published beneath it without replacing prior output")] string @out,
        [Description("Optional zero/one parameter-overlay row; multiple still variants are not yet supported")] JsonElement? paramTable = null,
        [Description("Render engine (cycles_gpu default)")] string engine = "cycles_gpu",
        [Description("Square resolution in px")] int res = 1024,
        [Description("Sampling count")] int? samples = null,
        [Description("Render seed")] int seed = 0,
        [Description("Contact sheet columns")] int columns = 3,
        CancellationToken ct = default) =>
        Guard<IEnumerable<ContentBlock>>("delivery_export", null, null, async () =>
        {
            // A present-but-non-array paramTable is a caller mistake (e.g. a bare object instead
            // of [ {...} ]): reject it, exactly as REST/CLI do — never silently drop the params
            // and ship a single-asset delivery. Only absent/null means "single asset".
            var rows = OptionalParamTable(paramTable);
            var result = await delivery.ExportAsync(artifact, @out, rows, engine, res, samples, seed, columns, ct);
            var bytes = await File.ReadAllBytesAsync(result.ContactSheet, ct);
            return
            [
                ImageContentBlock.FromBytes(bytes, "image/png"),
                new TextContentBlock
                {
                    Text = $"delivery {result.Name}: {result.Items} image(s)\n"
                         + $"payload:       {result.PayloadDir}\n"
                        + $"descriptor:    {result.DescriptorPath}\n"
                        + $"replay-source: {result.ReplaySourceDir}\n"
                        + $"contact sheet: {result.ContactSheet}\n"
                         + $"verify encoded/source consistency on generation: {Path.GetDirectoryName(result.PayloadDir)}; admitted=false, provenanceVerified=false",
                },
            ];
        });

    [McpServerTool(Name = "delivery_list")]
    [Description("Read a completed generation's declared producer facts from its outside-payload receipt; does not verify rights or replay.")]
    public Task<string> DeliveryList(
        [Description("Completed generation directory, parent of payload/")] string payload) =>
        Guard("delivery_list", null, null, () =>
        {
            var result = delivery.List(payload);
            var header = $"{result.Name} [{result.Profile}]"
                + (result.BlenderVersion is { } bv ? $" (blender {bv})" : "");
            var lines = result.Items.Select(i =>
                $"{i.File}  {i.Width}x{i.Height}  sha {ShortHash(i.Sha256)}  scene {ShortHash(i.SceneDigest)}  {i.Recipe}");
            return Task.FromResult(string.Join("\n", lines.Prepend(header)));
        });

    [McpServerTool(Name = "delivery_verify", UseStructuredContent = true)]
    [Description("Verify bounded encoded integrity, exact still payload inventory and preserved source/snapshot consistency. pass=true is self-consistency, not admission, decoded samples, provenance/rights or source replay. CLI counterpart works without a daemon.")]
    public Task<DeliveryVerifyResult> DeliveryVerify(
        [Description("Completed generation directory, parent of payload/")] string payload) =>
        Guard("delivery_verify", null, null, () => Task.FromResult(delivery.Verify(payload)));

    [McpServerTool(Name = "daemon_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Report the running daemon: version, pid, url, log file, the effective configuration with each key's source (secrets redacted) and its digest, the Blender in use and its support status, ready workers and the last worker error, sessions, GPU, desktop and telemetry state. Call it when something looks misconfigured.")]
    public Task<JsonObject> GetDaemonStatus() =>
        Guard("daemon_status", null, null, () => Task.FromResult(status.Build()));

    // --- helpers ---

    private static string ShortHash(string hash) => hash.Length > 12 ? hash[..12] : hash;

    internal static IReadOnlyList<JsonObject> ToParamTable(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new AblException("bad_request", "paramTable must be a JSON array of objects");
        }

        return [.. element.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(e.GetRawText())!.AsObject()
            : throw new AblException("bad_request", "paramTable rows must be objects"))];
    }

    /// <summary>
    /// An OPTIONAL param table: absent/null → an empty table (a single-asset delivery); anything
    /// present is validated by <see cref="ToParamTable"/> — a non-array is a caller mistake and
    /// throws bad_request, never a silent single-asset fallback (surface parity with REST/CLI).
    /// </summary>
    internal static IReadOnlyList<JsonObject> OptionalParamTable(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 1)
            throw new AblException("bad_request", "first delivery profile supports only one parameter row");
        return ToParamTable(e);
    }

    private static JsonObject? ToObject(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } value
            ? JsonNode.Parse(value.GetRawText())!.AsObject()
            : null;

    /// <summary>
    /// Instruments the op (FR-012) and maps the structured core error to an MCP tool
    /// error the agent can act on.
    /// </summary>
    private async Task<T> Guard<T>(
        string op, string? session, string? project, Func<Task<T>> action)
    {
        try
        {
            return await AblTelemetry.RunAsync(op, session, project ?? sessions.ProjectOf(session), action, MeasurePayload);
        }
        catch (AblException ex)
        {
            throw new McpException(Describe(ex));
        }
        // Same backstop as the REST surface: a worker failure on a transient-worker path
        // (render/replay/batch) reaches the agent as a structured tool error, not an
        // unhandled exception.
        catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped))
        {
            throw new McpException(Describe(mapped));
        }
    }

    private static string Describe(AblException ex)
    {
        var parts = new List<string> { $"{ex.Kind}: {ex.Message}" };
        if (ex.Hint is not null)
        {
            parts.Add($"hint: {ex.Hint}");
        }

        if (ex.LastCheckpoint is not null)
        {
            parts.Add($"lastCheckpoint: {ex.LastCheckpoint}");
        }

        if (ex.WorkerTraceback is not null)
        {
            parts.Add($"traceback:\n{ex.WorkerTraceback}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>Logical returned UTF-8 content, excluding transport envelopes.</summary>
    private static long MeasurePayload<T>(T result) => result switch
    {
        string text => PayloadSizing.Utf8Bytes(text),
        IEnumerable<ContentBlock> blocks => PayloadSizing.JsonBytes(blocks.ToArray()),
        _ => PayloadSizing.JsonBytes(result),
    };
}
