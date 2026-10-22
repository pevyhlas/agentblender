using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core.Rendering;
using Abl.Core.Store;
using Abl.Core.Workers;
using Abl.Core.Telemetry;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Abl.Core.Sessions;

public sealed record ExecResult(JsonObject StateDelta, double DurationMs, string Checkpoint);

public sealed record PreviewResult(string Path, double DurationMs, string Engine, int Size);

/// <summary>
/// A final render: pixels on disk, provenance in the sidecar. <see cref="Device"/> is the
/// device AB configured for a Cycles render (009 FR-005), omitted for other engines;
/// <see cref="FallbackReason"/> keeps its tier-only meaning (003).
/// </summary>
public sealed record RenderResult(
    string Path, string SidecarPath, double DurationMs, string SourceCheckpoint, string Tier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FallbackReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RenderDevice? Device = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackHint => FallbackReason == "worker_upgrade_required" ? WorkerTransfers.UpgradeHint : null;
}

/// <summary>
/// The op core behind every surface (Principle 1): sessions, exec pipeline
/// (journal → worker → delta → auto-checkpoint), state reads, checkpoint ops,
/// previews and final renders behind the GPU lease.
/// </summary>
public sealed class SessionService(
    AblOptions options,
    WorkerPool pool,
    CheckpointStore store,
    GpuLease gpuLease,
    DesktopTier desktop,
    ILogger<SessionService> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public CheckpointStore Store => store;

    // --- session lifecycle (FR-001) ---

    public async Task<SessionInfo> OpenAsync(string project, CancellationToken ct)
    {
        var worker = await pool.TakeAsync(ct).ConfigureAwait(false);
        try
        {
            var id = NewId();
            var head = await SaveHeadAsync(worker, parent: null, sessionId: id, ct).ConfigureAwait(false);
            var session = new Session(id, project, worker, head);
            _sessions[id] = session;
            store.AppendJournal(id, new TrajectoryEntry(0, "open", null, null, head, 0, DateTimeOffset.UtcNow));
            logger.LogInformation("session {Id} open for {Project} on worker {Worker}", id, project, worker.Id);
            return Info(session);
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyList<SessionInfo> List() =>
        [.. _sessions.Values.OrderBy(s => s.CreatedAt).Select(Info)];

    public string? ProjectOf(string? id) => id is not null && _sessions.TryGetValue(id, out var s) ? s.Project : null;

    /// <summary>The session's Blender process id (diagnostics; crash tests kill it).</summary>
    public int WorkerPid(string id) => Find(id).Worker.ProcessId;

    public async Task CloseAsync(string id)
    {
        var session = Find(id);
        _sessions.TryRemove(id, out _);
        session.State = SessionState.Dead;
        await session.Worker.ShutdownAsync().ConfigureAwait(false);
        await session.Worker.DisposeAsync().ConfigureAwait(false);
        logger.LogInformation("session {Id} closed", id);
        pool.Replenish();
    }

    /// <summary>checkpoint_branch: a NEW session whose worker holds that state (D-03).</summary>
    public async Task<SessionInfo> BranchAsync(string checkpointRef, string? project, CancellationToken ct)
    {
        var hash = ResolveCheckpoint(checkpointRef);
        var meta = store.Get(hash)!;
        var worker = await pool.TakeAsync(ct).ConfigureAwait(false);
        try
        {
            await worker.RequestAsync(
                    "load", new JsonObject { ["path"] = store.ObjectPath(hash) }, options.ExecTimeout, ct)
                .ConfigureAwait(false);
            var id = NewId();
            var session = new Session(id, project ?? meta.SessionId, worker, hash);
            _sessions[id] = session;
            store.AppendJournal(id, new TrajectoryEntry(0, "branch", null, null, hash, 0, DateTimeOffset.UtcNow));
            return Info(session);
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // --- the exec pipeline (FR-002/003, D-03 auto-checkpoint) ---

    public async Task<ExecResult> ExecAsync(string id, string script, JsonObject? parameters, CancellationToken ct)
    {
        var session = Find(id);
        using var scope = session.EnterOp(() => KillSession(session));

        var seq = ++session.Seq;
        var scriptPath = store.SessionScriptPath(id, seq);
        await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);

        var request = new JsonObject
        {
            ["script"] = scriptPath,
            ["params"] = parameters?.DeepClone(),
            ["recover_to"] = store.ObjectPath(session.Head),
        };
        var response = await GuardedAsync(session, "exec", request, options.ExecTimeout, ct).ConfigureAwait(false);

        var duration = response["duration_ms"]?.GetValue<double>() ?? 0;
        try
        {
            var head = await SaveHeadAsync(session.Worker, session.Head, id, ct).ConfigureAwait(false);
            // Publish the mutable head only after its trajectory entry is durable enough to read.
            store.AppendJournal(id, new TrajectoryEntry(
                seq, "exec", script, parameters, head, duration, DateTimeOffset.UtcNow));
            session.Head = head;
            return new ExecResult(response["state_delta"]!.AsObject(), duration, head);
        }
        catch (Exception ex) when (ex is WorkerTimeoutException or WorkerDeadException)
        {
            throw MapSessionFault(session, ex, "branch from the last checkpoint");
        }
        catch (Exception ex) when (ex is WorkerOpException or IOException or UnauthorizedAccessException)
        {
            KillSession(session); // exec mutated the worker, but its commit could not complete
            throw new AblException("worker_died", "checkpoint/journal commit failed; the worker was discarded",
                hint: $"recover with checkpoint_branch {session.Head[..12]}", lastCheckpoint: session.Head);
        }
    }

    public async Task<JsonObject> StateAsync(string id, string mode, CancellationToken ct)
    {
        var session = Find(id);
        using var scope = session.EnterOp(() => KillSession(session));
        var response = await GuardedAsync(
                session, "state", new JsonObject { ["mode"] = mode }, options.ExecTimeout, ct)
            .ConfigureAwait(false);
        return response["state"]?.AsObject() ?? [];
    }

    public async Task<JsonObject> InspectAsync(string id, SceneInspectionRequest request, CancellationToken ct)
    {
        var fields = SceneInspection.ToWorker(request);
        var session = Find(id);
        using var scope = session.EnterOp(() => KillSession(session));
        AblTelemetry.SetTier(WorkerTier.Local);
        var response = await GuardedAsync(session, "inspect", fields, options.ExecTimeout, ct).ConfigureAwait(false);
        var result = response["inspection"]!.AsObject();
        result["sourceCheckpoint"] = session.Head;
        return result;
    }

    // --- checkpoint ops (FR-006) ---

    /// <summary>Explicit save = tag the current head (state only moves through exec).</summary>
    public string SaveCheckpoint(string id, IReadOnlyList<string> tags)
    {
        var session = Find(id);
        if (tags.Count > 0)
        {
            store.AddTags(session.Head, tags);
        }

        return session.Head;
    }

    public IReadOnlyList<CheckpointMeta> ListCheckpoints(string? sessionId) => store.List(sessionId);

    public void TagCheckpoint(string checkpointRef, IReadOnlyList<string> tags) =>
        store.AddTags(ResolveCheckpoint(checkpointRef), tags);

    /// <summary>Object-set delta between two checkpoints, via a scratch worker.</summary>
    public async Task<JsonObject> DiffAsync(string refA, string refB, CancellationToken ct)
    {
        var a = ResolveCheckpoint(refA);
        var b = ResolveCheckpoint(refB);
        var worker = await pool.TakeAsync(ct).ConfigureAwait(false);
        try
        {
            var objectsA = await LoadInventoryAsync(worker, a, ct).ConfigureAwait(false);
            var objectsB = await LoadInventoryAsync(worker, b, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["added"] = new JsonArray([.. objectsB.Except(objectsA).Order().Select(n => JsonValue.Create(n))]),
                ["removed"] = new JsonArray([.. objectsA.Except(objectsB).Order().Select(n => JsonValue.Create(n))]),
            };
        }
        finally
        {
            await worker.ShutdownAsync().ConfigureAwait(false);
            await worker.DisposeAsync().ConfigureAwait(false);
            pool.Replenish();
        }
    }

    public PruneResult Prune(bool force)
    {
        var protectedHashes = _sessions.Values.Select(s => s.Head).ToHashSet(StringComparer.Ordinal);
        return store.Prune(protectedHashes, force);
    }

    // --- previews & renders (FR-004/005/011) ---

    public async Task<PreviewResult> PreviewAsync(
        string id, int size, string engine, CancellationToken ct)
    {
        AblTelemetry.SetTier(WorkerTier.Local);
        var session = Find(id);
        using var scope = session.EnterOp(() => KillSession(session));
        size = Math.Min(size, 512); // FR-004: previews never exceed 512px
        // 009 FR-003: the guard runs before the lease, so a refusal never waits for it.
        var decision = GpuGuard.Decide(session.Worker.Gpu, engine, null, GpuPath.ScenePreview, options.Platform);
        if (decision.Kind == GpuDecisionKind.Refuse)
        {
            throw GpuGuard.Unavailable(decision, WorkerTier.Local);
        }

        var outPath = PreviewPath(id, session.Seq);
        using var hold = await AcquireGpuAsync(gpuLease, "wait", ct).ConfigureAwait(false);
        var response = await GuardedAsync(session, "render", new JsonObject
        {
            ["engine"] = engine,
            ["res"] = size,
            ["out"] = outPath,
        }, options.RenderTimeout, ct, GpuPath.ScenePreview).ConfigureAwait(false);
        return new PreviewResult(outPath, response["duration_ms"]?.GetValue<double>() ?? 0, engine, size);
    }

    public async Task<RenderResult> RenderFinalAsync(
        string? sessionId,
        string? checkpointRef,
        string engine,
        int res,
        int? samples,
        string? outPath,
        string leaseMode,
        int seed,
        CancellationToken ct)
    {
        if (sessionId is not null)
        {
            var session = Find(sessionId);
            using var scope = session.EnterOp(() => KillSession(session));

            // FR-004: prefer the desktop for this heavy op, rendering from the session's
            // (checkpointed) head. The worker restores temporary render overrides.
            // Desktop unavailability/bringup failure falls back to the local worker;
            // desktop render failures are surfaced without affecting that local worker.
            string? fallback = null;
            var routed = await TryRenderOnDesktopAsync(
                session.Head, engine, res, samples, outPath, leaseMode, seed, reason => fallback = reason, ct).ConfigureAwait(false);
            if (routed is not null)
            {
                return routed;
            }

            try
            {
                return await RenderOnWorkerAsync(
                        session.Worker, session.Head, engine, res, samples, outPath, leaseMode, seed, ct,
                        gpuLease, WorkerTier.Local, fallback)
                    .ConfigureAwait(false);
            }
            // The session's OWN worker: a death/timeout strands the session, so recover it.
            catch (Exception ex) when (ex is WorkerOpException or WorkerTimeoutException or WorkerDeadException)
            {
                throw MapSessionFault(
                    session, ex, "adjust the render arguments and retry; the checkpoint is unchanged", GpuPath.RenderFinal);
            }
        }

        var hash = ResolveCheckpoint(
            checkpointRef ?? throw new AblException("bad_request", "render_final needs a session or a checkpoint"));

        string? checkpointFallback = null;
        var desktopResult = await TryRenderOnDesktopAsync(
            hash, engine, res, samples, outPath, leaseMode, seed, reason => checkpointFallback = reason, ct).ConfigureAwait(false);
        if (desktopResult is not null)
        {
            return desktopResult;
        }

        var worker = await pool.TakeAsync(ct).ConfigureAwait(false);
        try
        {
            await worker.RequestAsync(
                    "load", new JsonObject { ["path"] = store.ObjectPath(hash) }, options.ExecTimeout, ct)
                .ConfigureAwait(false);
            return await RenderOnWorkerAsync(
                    worker, hash, engine, res, samples, outPath, leaseMode, seed, ct, gpuLease, WorkerTier.Local, checkpointFallback)
                .ConfigureAwait(false);
        }
        // A transient pooled worker (disposed+replenished below): convert its failures to
        // the structured envelope here, inside the telemetry choke point, so the outcome
        // records the real kind — not raw-exception "error".
        catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped, GpuPath.RenderFinal, options.Platform))
        {
            throw mapped;
        }
        finally
        {
            await worker.ShutdownAsync().ConfigureAwait(false);
            await worker.DisposeAsync().ConfigureAwait(false);
            pool.Replenish();
        }
    }

    /// <summary>
    /// Attempt a render on the desktop tier (feature 003). Returns the result on success,
    /// or <c>null</c> when the desktop is unavailable — not configured, not reachable, or it
    /// failed during BRINGUP (connect, token, warm, checkpoint-ship) — so the caller renders
    /// locally (SC-002 transparent fallback). Only a failure of the RENDER op that actually
    /// ran on the desktop is surfaced (attributed to the tier): a bad op is bad on any tier,
    /// and a slower local retry would only double a timeout. The remote worker is transient,
    /// so no session state is ever involved either way.
    /// </summary>
    private async Task<RenderResult?> TryRenderOnDesktopAsync(
        string sourceCheckpoint, string engine, int res, int? samples, string? outPath,
        string leaseMode, int seed, Action<string> fallback, CancellationToken ct)
    {
        if (!desktop.IsConfigured) return null;
        if (!await desktop.ProbeAvailableAsync(ct).ConfigureAwait(false))
        {
            fallback("desktop_unavailable");
            return null;
        }

        WorkerHandle? worker = null;
        try
        {
            // BRINGUP — connect + auth + warm + ship the base checkpoint. ANY failure here
            // (connect timeout as OperationCanceledException, warm/ship timeout, dead worker,
            // bad token, a store read error) means the desktop is unavailable → fall back to
            // local. Only a genuine caller-cancellation escapes.
            try
            {
                worker = await desktop.ConnectWorkerAsync(ct).ConfigureAwait(false);
                RequireDesktopGpu(worker, engine);
                await WorkerTransfers.LoadAsync(worker, store.ObjectPath(sourceCheckpoint), false, options.ExecTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the CALLER cancelled — not a desktop fault
            }
            catch (Exception ex)
            {
                fallback(ex is AblException { Kind: "worker_upgrade_required" }
                    ? "worker_upgrade_required" : "desktop_bringup_failed");
                logger.LogWarning(ex, "desktop bringup failed; falling back to the local tier");
                return null;
            }

            // RENDER — the op runs on the desktop worker. Surface its failures, attributed to
            // the tier so they are actionable and not mistaken for a local-GPU fault.
            try
            {
                return await RenderOnWorkerAsync(
                        worker, sourceCheckpoint, engine, res, samples, outPath, leaseMode, seed, ct,
                        desktop.GpuLease, WorkerTier.Desktop)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AblException ex) // gpu_busy on the desktop lease, etc.
            {
                throw DesktopAttributed(ex);
            }
            catch (Exception ex) when (WorkerErrors.TryLegacyFrameLimit(ex, worker.SupportsBlobChunks, out var upgrade))
            {
                throw DesktopAttributed(upgrade);
            }
            catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped, GpuPath.RenderFinal, options.Platform))
            {
                throw DesktopAttributed(mapped);
            }
            // Defense-in-depth: an unexpected host-side fault finishing the desktop render
            // (malformed shipped pixels, a disk write error) becomes a structured error, never
            // an empty-body 500.
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                throw DesktopAttributed(new AblException("worker_died", $"desktop render failed: {ex.Message}"));
            }
        }
        finally
        {
            if (worker is not null)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static AblException DesktopAttributed(AblException ex) => new(
        ex.Kind, $"{ex.Message} [desktop tier]", ex.Hint, ex.LastCheckpoint, ex.WorkerTraceback);

    /// <summary>
    /// 009 FR-007 (plan D9): for <c>cycles_gpu</c>, a desktop worker without a GPU backend is a
    /// bringup failure, judged by the same <see cref="GpuGuard"/> predicate as the local path,
    /// so the render falls back to local. Other engines stay on such a desktop. Shared with
    /// <c>TrajectoryService</c>'s batch bringup.
    /// </summary>
    internal static void RequireDesktopGpu(WorkerHandle worker, string engine)
    {
        if (!GpuGuard.Allows(worker.Gpu, engine))
        {
            throw new AblException(
                "gpu_unavailable", $"the desktop worker reports gpu backend {worker.Gpu.Backend} for {engine}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            try
            {
                await CloseAsync(id).ConfigureAwait(false);
            }
            catch (AblException)
            {
            }
        }
    }

    // --- internals ---

    internal async Task<RenderResult> RenderOnWorkerAsync(
        WorkerHandle worker,
        string sourceCheckpoint,
        string engine,
        int res,
        int? samples,
        string? outPath,
        string leaseMode,
        int seed,
        CancellationToken ct,
        GpuLease lease,
        WorkerTier tier,
        string? fallbackReason = null)
    {
        AblTelemetry.SetTier(tier);

        // 009 FR-003 (guard matrix): cycles_gpu on a worker whose hello reported no GPU backend
        // is refused, never silently rendered on the CPU; lease=cpu_fallback renders
        // cycles_cpu without the GPU lease. Decided first, so a refusal creates no output
        // directory and takes no lease.
        var decision = GpuGuard.Decide(worker.Gpu, engine, leaseMode, GpuPath.RenderFinal, options.Platform);
        if (decision.Kind == GpuDecisionKind.Refuse)
        {
            throw GpuGuard.Unavailable(decision, tier);
        }

        outPath ??= Path.Combine(
            options.RendersDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sourceCheckpoint[..12]}-{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        // plan D7: device.fallback records why a cpu_fallback request rendered on the CPU.
        string? deviceFallback = null;
        GpuLease.Hold? hold = null;
        try
        {
            if (decision.Kind == GpuDecisionKind.RenderCpuFallback)
            {
                engine = "cycles_cpu";
                deviceFallback = "gpu_unavailable";
            }
            else
            {
                hold = await AcquireGpuAsync(lease, leaseMode, ct).ConfigureAwait(false);
                if (hold is null && leaseMode == "cpu_fallback")
                {
                    engine = "cycles_cpu";
                    deviceFallback = "gpu_busy";
                }
            }

            // A remote worker cannot see the daemon host's disk, so it renders to its own temp and
            // ships the PNG bytes back (FR-005 output half); a local worker writes straight to
            // outPath. Either way the pixels end up on the daemon host's disk, sidecar-referenced.
            var remote = tier == WorkerTier.Desktop;
            JsonObject Request(string renderEngine) => new()
            {
                ["engine"] = renderEngine,
                ["res"] = res,
                ["samples"] = samples,
                ["seed"] = seed,
                ["out"] = outPath,
                ["return_bytes"] = remote && !worker.SupportsBlobChunks,
                ["return_blob"] = remote && worker.SupportsBlobChunks,
            };

            // Worker failures are converted by the caller, which knows whether this is the
            // session's own worker (recover the session) or a transient pooled one (the
            // session, if any, is untouched).
            JsonObject response;
            try
            {
                response = await worker.RequestAsync("render", Request(engine), options.RenderTimeout, ct)
                    .ConfigureAwait(false);
            }
            // plan D7: the worker's device check failed although its hello reported a backend.
            // cpu_fallback keeps its promise: release the GPU lease and send one cycles_cpu
            // render to the same worker, whose render state the failed op restored (006).
            catch (WorkerOpException op) when (op.Kind == WorkerErrors.WorkerGpuUnavailable
                && engine == "cycles_gpu" && leaseMode == "cpu_fallback")
            {
                hold?.Dispose();
                hold = null;
                engine = "cycles_cpu";
                deviceFallback = "gpu_unavailable";
                response = await worker.RequestAsync("render", Request(engine), options.RenderTimeout, ct)
                    .ConfigureAwait(false);
            }

            var duration = response["duration_ms"]?.GetValue<double>() ?? 0;
            var device = response["device"] is JsonObject reported
                ? new RenderDevice(
                    reported["backend"]!.GetValue<string>(), reported["name"]?.GetValue<string>(), deviceFallback)
                : null;

            if (remote)
            {
                await WorkerTransfers.WritePixelsAsync(worker, response, outPath, options.ExecTimeout, ct).ConfigureAwait(false);
            }

            // FR-005: pixels stay on disk; the sidecar records provenance (incl. serving tier
            // and, for Cycles, the same device object as the result).
            var sidecarPath = outPath + ".json";
            var sidecar = new JsonObject
            {
                ["engine"] = engine,
                ["res"] = res,
                ["samples"] = samples,
                ["seed"] = seed,
                ["sourceCheckpoint"] = sourceCheckpoint,
                ["tier"] = tier.ToString().ToLowerInvariant(),
                ["fallbackReason"] = fallbackReason,
                ["blenderVersion"] = worker.BlenderVersion,
                ["durationMs"] = duration,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            if (device is not null)
            {
                sidecar["device"] = JsonSerializer.SerializeToNode(device, JsonSerializerOptions.Web);
            }

            await File.WriteAllTextAsync(sidecarPath, sidecar.ToJsonString(), ct).ConfigureAwait(false);
            return new RenderResult(
                outPath, sidecarPath, duration, sourceCheckpoint, tier.ToString().ToLowerInvariant(), fallbackReason, device);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    internal string PreviewPath(string sessionId, int seq)
    {
        var dir = Path.Combine(options.StoreDir, "sessions", sessionId, "previews");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{seq:D4}.png");
    }

    private async Task<GpuLease.Hold?> AcquireGpuAsync(GpuLease lease, string leaseMode, CancellationToken ct)
    {
        var hold = await lease.TryAcquireAsync(options.GpuLeaseWait, ct).ConfigureAwait(false);
        if (hold is not null)
        {
            return hold;
        }

        return leaseMode switch
        {
            "cpu_fallback" => null,
            "fail" => throw new AblException(
                "gpu_busy",
                $"the GPU lease stayed busy for {options.GpuLeaseWait.TotalSeconds:0}s",
                hint: "retry, use lease=wait, or lease=cpu_fallback"),
            _ => await lease.TryAcquireAsync(options.RenderTimeout, ct).ConfigureAwait(false)
                ?? throw new AblException("gpu_busy", "the GPU lease never freed within the render timeout"),
        };
    }

    /// <summary>Runs a worker op for a session, mapping death/timeout to the recovery error (FR-010).</summary>
    private async Task<JsonObject> GuardedAsync(
        Session session, string op, JsonObject fields, TimeSpan timeout, CancellationToken ct, GpuPath? path = null)
    {
        try
        {
            return await session.Worker.RequestAsync(op, fields, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkerOpException or WorkerTimeoutException or WorkerDeadException)
        {
            throw MapSessionFault(session, ex, op == "exec"
                ? "fix the script and exec again; state is unchanged"
                : "retry the operation; the durable checkpoint is unchanged", path);
        }
    }

    /// <summary>
    /// Convert a SESSION-owned worker's failure to the structured error. An op failure
    /// left the worker alive with pre-op state restored (recover_to), so the session stays
    /// usable — the sanctioned <c>bad_request</c> (e.g. a bad engine) is honored so it
    /// reads the same on every surface, any other op failure is <c>exec_failed</c>. A
    /// timeout or death means the worker is gone: kill+recycle the session and hand back
    /// the one-call recovery (FR-010). On a guarded <paramref name="path"/> a worker-raised
    /// <c>gpu_unavailable</c> carries that path's <see cref="GpuGuard.HintFor"/> hint instead
    /// of <paramref name="opFailHint"/> (009 plan D6); unguarded ops keep their hint.
    /// </summary>
    private AblException MapSessionFault(Session session, Exception ex, string opFailHint, GpuPath? path = null)
    {
        switch (ex)
        {
            case WorkerOpException op:
                var kind = WorkerErrors.OpKind(op.Kind);
                return new AblException(
                    kind, op.Message,
                    hint: kind == WorkerErrors.WorkerBadRequest ? null
                        : kind == WorkerErrors.WorkerGpuUnavailable && path is { } guarded
                            ? GpuGuard.HintFor(guarded, options.Platform)
                            : opFailHint,
                    lastCheckpoint: session.Head, workerTraceback: op.WorkerTraceback);
            case WorkerTimeoutException:
                KillSession(session);
                return new AblException(
                    "timeout", ex.Message,
                    hint: $"the worker was killed; recover with checkpoint_branch {session.Head[..12]}",
                    lastCheckpoint: session.Head);
            default: // WorkerDeadException (the when-filter admits only these three)
                KillSession(session);
                return new AblException(
                    "worker_died", ex.Message,
                    hint: $"recover with checkpoint_branch {session.Head[..12]}",
                    lastCheckpoint: session.Head);
        }
    }

    private void KillSession(Session session)
    {
        session.State = SessionState.Dead;
        var output = session.Worker.RecentOutput;
        if (output.Count > 0)
        {
            logger.LogWarning(
                "session {Id} worker died; last output: {Output}",
                session.Id, string.Join(" | ", output.TakeLast(5)));
        }

        // Release the dead worker's OS handles (Process + socket) NOW: the recovery move
        // is checkpoint_branch into a new session, so a client may never session_close
        // the dead id, and a long-running daemon must not accumulate handles.
        session.Worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        pool.Replenish();
    }

    private async Task<string> SaveHeadAsync(
        WorkerHandle worker, string? parent, string sessionId, CancellationToken ct)
    {
        var tmp = store.TmpBlendPath();
        try
        {
            var response = await worker
                .RequestAsync("save", new JsonObject { ["path"] = tmp }, options.ExecTimeout, ct)
                .ConfigureAwait(false);
            return store.Ingest(tmp, parent, sessionId,
                digest: response["digest"]!.GetValue<string>(), blenderVersion: response["blender_version"]!.GetValue<string>());
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private async Task<HashSet<string>> LoadInventoryAsync(WorkerHandle worker, string hash, CancellationToken ct)
    {
        await worker.RequestAsync(
                "load", new JsonObject { ["path"] = store.ObjectPath(hash) }, options.ExecTimeout, ct)
            .ConfigureAwait(false);
        var response = await worker.RequestAsync(
                "state", new JsonObject { ["mode"] = "full" }, options.ExecTimeout, ct)
            .ConfigureAwait(false);
        var objects = response["state"]?["objects"]?.AsObject();
        return objects is null
            ? []
            : [.. objects.Select(kv => kv.Key)];
    }

    private Session Find(string id) =>
        _sessions.TryGetValue(id, out var session)
            ? session
            : throw new AblException(
                "not_found", $"no session '{id}'", hint: "session_list shows live sessions");

    private string ResolveCheckpoint(string reference) =>
        store.Resolve(reference)
        ?? throw new AblException(
            "not_found", $"no checkpoint matches '{reference}'",
            hint: "checkpoint_list shows hashes and tags");

    private SessionInfo Info(Session session) => new(
        session.Id,
        session.Project,
        session.State.ToString().ToLowerInvariant(),
        session.Head,
        session.Worker.Id,
        session.CreatedAt);

    private static string NewId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}
