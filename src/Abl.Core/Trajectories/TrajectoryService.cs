using System.Text.Json.Nodes;
using Abl.Core.Rendering;
using Abl.Core.Store;
using Abl.Core.Workers;
using Abl.Core.Telemetry;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Abl.Core.Trajectories;

public sealed record ReplayResult(
    bool Pass,
    string FinalDigest,
    string ExpectedDigest,
    double DurationMs,
    string? ArtifactBlenderVersion = null,
    string? WorkerBlenderVersion = null);

/// <summary>
/// A batch: per-variant renders on disk plus one contact sheet. <see cref="Device"/> is the
/// device AB configured for the batch's Cycles renders (009 FR-005), omitted for other engines.
/// </summary>
public sealed record BatchResult(IReadOnlyList<string> Renders, string ContactSheet, string Tier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FallbackReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RenderDevice? Device = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackHint => FallbackReason == "worker_upgrade_required" ? WorkerTransfers.UpgradeHint : null;
}

public sealed record ExportResult(
    string Directory, string ArtifactJson, string BaseBlend, string Name, int Ops);

public sealed record ImportResult(
    string Name, int Ops, string BaseCheckpoint, string ExpectedDigest, string? BlenderVersion);

/// <summary>
/// Freeze a winning trajectory into a self-contained artifact, replay it on a fresh
/// worker with the canonical scene-digest gate (FR-007, R-03 — .blend bytes
/// are not cross-process comparable), and run parametrized batches with
/// one-contact-sheet verification (FR-008).
/// </summary>
public sealed class TrajectoryService(
    AblOptions options,
    WorkerPool pool,
    CheckpointStore store,
    GpuLease gpuLease,
    DesktopTier desktop,
    ILogger<TrajectoryService> logger)
{
    public FrozenArtifact Freeze(string sessionId, string name, int? fromSeq, int? toSeq)
    {
        var journal = store.ReadJournal(sessionId);
        if (journal.Count == 0)
        {
            throw new AblException("not_found", $"session '{sessionId}' has no journal");
        }

        var execs = journal
            .Where(e => e.Op == "exec" && e.Script is not null && e.Checkpoint is not null)
            .Where(e => (fromSeq is null || e.Seq >= fromSeq) && (toSeq is null || e.Seq <= toSeq))
            .OrderBy(e => e.Seq)
            .ToList();
        if (execs.Count == 0)
        {
            throw new AblException(
                "bad_request", $"no exec ops in range for session '{sessionId}'",
                hint: "the journal only freezes executed scripts; run scene_exec first");
        }

        var baseEntry = journal
            .Where(e => e.Seq < execs[0].Seq && e.Checkpoint is not null)
            .OrderBy(e => e.Seq)
            .LastOrDefault()
            ?? throw new AblException("bad_request", "no base checkpoint precedes the range");

        var finalMeta = store.Get(execs[^1].Checkpoint!);
        var expectedDigest = finalMeta?.Digest
            ?? throw new AblException(
                "bad_request", "the final checkpoint carries no scene digest",
                hint: "checkpoints saved by older workers lack digests; re-run the trajectory");

        var artifact = new FrozenArtifact(
            name,
            baseEntry.Checkpoint!,
            [.. execs.Select(e => new FrozenOp(e.Script!, e.Params))],
            execs[^1].Checkpoint!,
            expectedDigest,
            DateTimeOffset.UtcNow,
            finalMeta.BlenderVersion);
        store.SaveFrozen(artifact);
        logger.LogInformation(
            "froze {Name}: {Ops} ops, base {Base}, expected digest {Digest}",
            name, artifact.Ops.Count, artifact.BaseCheckpoint[..12], artifact.ExpectedDigest[..12]);
        return artifact;
    }

    /// <summary>Export a frozen artifact as a git-committable bundle (feature 002).</summary>
    public ExportResult Export(string name, string destDir)
    {
        var paths = store.ExportBundle(name, destDir);
        var artifact = store.GetFrozen(name)!;
        logger.LogInformation("exported bundle {Name} → {Dir}", name, paths.Directory);
        return new ExportResult(
            paths.Directory, paths.ArtifactJson, paths.BaseBlend, artifact.Name, artifact.Ops.Count);
    }

    /// <summary>Import a bundle directory into the store, integrity-verified (feature 002).</summary>
    public ImportResult Import(string bundleDir)
    {
        var artifact = store.ImportBundle(bundleDir);
        logger.LogInformation(
            "imported bundle {Name}: {Ops} ops, base {Base}", artifact.Name,
            artifact.Ops.Count, artifact.BaseCheckpoint[..12]);
        return new ImportResult(
            artifact.Name, artifact.Ops.Count, artifact.BaseCheckpoint,
            artifact.ExpectedDigest, artifact.BlenderVersion);
    }

    /// <summary>
    /// Replay on a FRESH worker; PASS = canonical scene-digest equality (R-03 gate —
    /// .blend bytes embed process memory layout and are excluded, like pixels).
    /// </summary>
    public async Task<ReplayResult> ReplayAsync(string name, CancellationToken ct)
    {
        var artifact = store.GetFrozen(name)
            ?? throw new AblException("not_found", $"no frozen artifact '{name}'");
        var started = DateTimeOffset.UtcNow;
        var worker = await pool.TakeAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(worker, artifact.BaseCheckpoint, ct).ConfigureAwait(false);
            foreach (var op in artifact.Ops)
            {
                // Replay stays on the local tier: it is the R-03 digest gate, not a render, and
                // digest authority lives on the host (the desktop renders pixels only).
                await ExecOpAsync(worker, op.Script, op.Params, WorkerTier.Local, ct).ConfigureAwait(false);
            }

            var tmp = store.TmpBlendPath();
            var save = await worker
                .RequestAsync("save", new JsonObject { ["path"] = tmp }, options.ExecTimeout, ct)
                .ConfigureAwait(false);
            File.Delete(tmp);
            var finalDigest = save["digest"]?.GetValue<string>() ?? "";

            // FR-005: surface a Blender-version mismatch (imported bundles may come from
            // another machine) — informational; digest equality stays the sole arbiter.
            var mismatch = artifact.BlenderVersion is not null
                && worker.BlenderVersion is not null
                && artifact.BlenderVersion != worker.BlenderVersion;
            return new ReplayResult(
                finalDigest == artifact.ExpectedDigest, finalDigest, artifact.ExpectedDigest,
                (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                mismatch ? artifact.BlenderVersion : null,
                mismatch ? worker.BlenderVersion : null);
        }
        // Transient worker: convert its failures here, inside the telemetry choke point,
        // so the op's outcome records the real kind rather than a raw-exception "error".
        catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped))
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
    /// Parametrized production over a frozen artifact: per-variant params overlay each
    /// op's frozen params; renders land on disk and come back as ONE contact sheet.
    /// </summary>
    public async Task<BatchResult> BatchAsync(
        string name,
        IReadOnlyList<JsonObject> paramTable,
        int res,
        int columns,
        string engine,
        CancellationToken ct)
    {
        var artifact = store.GetFrozen(name)
            ?? throw new AblException("not_found", $"no frozen artifact '{name}'");
        if (paramTable.Count == 0)
        {
            throw new AblException("bad_request", "batch_run needs at least one param row");
        }

        // Created by RunBatchAsync, so a gpu_unavailable refusal leaves no empty directory.
        var outDir = Path.Combine(
            options.RendersDir, "batch", $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        // FR-004 (T005b): prefer the desktop for this heavy fan-out; a bringup failure falls
        // back to the host's local worker (SC-002). Batch runs on a transient worker either
        // way — no session state is ever at risk.
        string? fallback = null;
        var routed = await TryBatchOnDesktopAsync(artifact, paramTable, res, columns, engine, outDir,
                reason => fallback = reason, ct)
            .ConfigureAwait(false);
        if (routed is not null)
        {
            return routed;
        }

        var worker = await pool.TakeAsync(ct).ConfigureAwait(false);
        try
        {
            // 009 FR-003: refuse cycles_gpu on a worker without a GPU backend before any
            // variant runs and before the GPU lease.
            var decision = GpuGuard.Decide(worker.Gpu, engine, null, GpuPath.BatchRun, options.Platform);
            if (decision.Kind == GpuDecisionKind.Refuse)
            {
                throw GpuGuard.Unavailable(decision, WorkerTier.Local);
            }

            var local = await RunBatchAsync(
                    worker, artifact, paramTable, res, columns, engine, outDir, gpuLease, WorkerTier.Local,
                    remoteBasePath: null, ct)
                .ConfigureAwait(false);
            return local with { FallbackReason = fallback };
        }
        // Transient worker: convert failures here (inside the telemetry choke point) so the
        // outcome records the real kind — a bad engine reads bad_request, not "error".
        catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped, GpuPath.BatchRun, options.Platform))
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
    /// Attempt the batch on the desktop tier (feature 003 / T005b). Returns the result on
    /// success, or <c>null</c> when the desktop is unavailable — not configured, not reachable,
    /// or it failed during BRINGUP (connect, token, warm) — so the caller runs the batch
    /// locally (SC-002 transparent fallback). A failure of an op that actually ran on the
    /// desktop is surfaced, attributed to the tier. The remote worker is transient, so no
    /// session state is ever involved either way.
    /// </summary>
    private async Task<BatchResult?> TryBatchOnDesktopAsync(
        FrozenArtifact artifact, IReadOnlyList<JsonObject> paramTable, int res, int columns,
        string engine, string outDir, Action<string> fallback, CancellationToken ct)
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
            // BRINGUP — connect + auth + warm + SHIP THE BASE. Any failure here (including a
            // stalled or missing base-checkpoint ship — the largest single transfer) means the
            // desktop is unavailable → fall back to local. Shipping state to prime the worker
            // is setup, not one of artifact.Ops: no pixel has been rendered yet, so a local
            // retry costs nothing extra. This mirrors render_final's bringup exactly (SC-002).
            // Only a genuine caller-cancellation escapes.
            string remoteBasePath;
            try
            {
                worker = await desktop.ConnectWorkerAsync(ct).ConfigureAwait(false);
                Sessions.SessionService.RequireDesktopGpu(worker, engine);
                remoteBasePath = await ShipBaseAsync(worker, artifact, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                fallback(ex is AblException { Kind: "worker_upgrade_required" }
                    ? "worker_upgrade_required" : "desktop_bringup_failed");
                logger.LogWarning(ex, "desktop bringup failed; running the batch on the local tier");
                return null;
            }

            // RENDER — the ops run on the desktop worker. Surface their failures, attributed to
            // the tier so they are actionable and not mistaken for a local-GPU fault.
            try
            {
                return await RunBatchAsync(
                        worker, artifact, paramTable, res, columns, engine, outDir,
                        desktop.GpuLease, WorkerTier.Desktop, remoteBasePath, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AblException ex) // gpu_busy on the desktop lease, a bad op, etc.
            {
                throw DesktopAttributed(ex);
            }
            // A legacy oversized pixel frame has a precise upgrade action, not a generic death.
            catch (Exception ex) when (WorkerErrors.TryLegacyFrameLimit(ex, worker.SupportsBlobChunks, out var upgrade))
            {
                throw DesktopAttributed(upgrade);
            }
            catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped, GpuPath.BatchRun, options.Platform))
            {
                throw DesktopAttributed(mapped);
            }
            // Defense-in-depth: an unexpected host-side fault finishing the desktop batch
            // (malformed shipped pixels, a disk write error) becomes a structured error, never
            // an empty-body 500.
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                throw DesktopAttributed(new AblException("worker_died", $"desktop batch failed: {ex.Message}"));
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

    /// <summary>
    /// Ship the frozen base checkpoint to the remote worker ONCE and keep it there, returning
    /// the worker-side path so the batch can reset to it per variant with a cheap local `load`
    /// instead of re-shipping the bytes N times. Called during BRINGUP — a ship failure
    /// (missing base, a stalled transfer) is a "desktop unavailable" signal that falls back to
    /// the local tier, never a surfaced desktop op fault.
    /// </summary>
    private async Task<string> ShipBaseAsync(WorkerHandle worker, FrozenArtifact artifact, CancellationToken ct)
    {
        if (!File.Exists(store.ObjectPath(artifact.BaseCheckpoint)))
        {
            throw new AblException(
                "not_found", $"checkpoint {artifact.BaseCheckpoint[..12]} is missing from the store",
                hint: "frozen bases are prune-protected — was force used?");
        }

        return await WorkerTransfers.LoadAsync(worker, store.ObjectPath(artifact.BaseCheckpoint), true, options.ExecTimeout, ct)
            .ConfigureAwait(false)
            ?? throw new WorkerOpException("worker_error", "remote load_bytes returned no path", null);
    }

    /// <summary>
    /// The per-variant production loop, shared by the local and desktop tiers. On the desktop
    /// the base has already been shipped and kept during bringup (<paramref name="remoteBasePath"/>
    /// is its worker-side path); each variant resets to it with a cheap local `load`, each op's
    /// script is shipped by source, each variant's pixels ride back inline, and the contact
    /// sheet is composed on the worker and shipped back — the host never sees the worker's
    /// filesystem. Locally <paramref name="remoteBasePath"/> is null and everything stays
    /// path-based, exactly as before.
    /// </summary>
    private async Task<BatchResult> RunBatchAsync(
        WorkerHandle worker, FrozenArtifact artifact, IReadOnlyList<JsonObject> paramTable,
        int res, int columns, string engine, string outDir, GpuLease lease, WorkerTier tier,
        string? remoteBasePath, CancellationToken ct)
    {
        var remote = tier == WorkerTier.Desktop;
        AblTelemetry.SetTier(tier);
        Directory.CreateDirectory(outDir);
        RenderDevice? device = null;

        var renders = new List<string>();      // host-side vNN.png — what the caller keeps
        var labels = new List<string>();
        var remoteInputs = new List<string>();  // worker-side paths, inputs to the remote sheet
        for (var i = 0; i < paramTable.Count; i++)
        {
            if (remote)
            {
                await worker.RequestAsync(
                        "load", new JsonObject { ["path"] = remoteBasePath }, options.ExecTimeout, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await LoadAsync(worker, artifact.BaseCheckpoint, ct).ConfigureAwait(false);
            }

            foreach (var op in artifact.Ops)
            {
                await ExecOpAsync(worker, op.Script, Merge(op.Params, paramTable[i]), tier, ct)
                    .ConfigureAwait(false);
            }

            var outPath = Path.Combine(outDir, $"v{i:D2}.png");
            using (await lease.TryAcquireAsync(options.RenderTimeout, ct).ConfigureAwait(false)
                ?? throw new AblException("gpu_busy", "the GPU lease never freed within the render timeout"))
            {
                var request = new JsonObject
                {
                    ["engine"] = engine,
                    ["res"] = res,
                    ["out"] = outPath,
                    ["return_bytes"] = remote && !worker.SupportsBlobChunks,
                    ["return_blob"] = remote && worker.SupportsBlobChunks,
                    ["keep"] = remote,
                };
                var response = await worker.RequestAsync("render", request, options.RenderTimeout, ct)
                    .ConfigureAwait(false);
                // One worker and one engine: every variant reports the same configured device.
                if (response["device"] is JsonObject reported)
                {
                    device = new RenderDevice(reported["backend"]!.GetValue<string>(), reported["name"]?.GetValue<string>());
                }

                if (remote)
                {
                    await WorkerTransfers.WritePixelsAsync(worker, response, outPath, options.ExecTimeout, ct).ConfigureAwait(false);
                    remoteInputs.Add(
                        response["out"]?.GetValue<string>()
                        ?? throw new WorkerOpException("worker_error", "remote render returned no path", null));
                }
            }

            renders.Add(outPath);
            labels.Add($"v{i} {Compact(paramTable[i])}");
        }

        var sheetPath = Path.Combine(outDir, "sheet.png");
        var sheetRequest = new JsonObject
        {
            ["inputs"] = new JsonArray([.. (remote ? remoteInputs : renders).Select(p => (JsonNode)p)]),
            ["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)l)]),
            ["columns"] = columns,
            ["cell"] = Math.Min(res, 256),
            ["out"] = sheetPath,
            ["return_bytes"] = remote && !worker.SupportsBlobChunks,
            ["return_blob"] = remote && worker.SupportsBlobChunks,
        };
        var sheetResponse = await worker.RequestAsync("contact_sheet", sheetRequest, options.RenderTimeout, ct)
            .ConfigureAwait(false);
        if (remote)
        {
            await WorkerTransfers.WritePixelsAsync(worker, sheetResponse, sheetPath, options.ExecTimeout, ct).ConfigureAwait(false);
        }

        return new BatchResult(renders, sheetPath, tier.ToString().ToLowerInvariant(), Device: device);
    }

    private static AblException DesktopAttributed(AblException ex) => new(
        ex.Kind, $"{ex.Message} [desktop tier]", ex.Hint, ex.LastCheckpoint, ex.WorkerTraceback);

    private async Task LoadAsync(WorkerHandle worker, string hash, CancellationToken ct)
    {
        if (!File.Exists(store.ObjectPath(hash)))
        {
            throw new AblException(
                "not_found", $"checkpoint {hash[..12]} is missing from the store",
                hint: "it may have been pruned; frozen bases are prune-protected — was force used?");
        }

        await worker.RequestAsync(
                "load", new JsonObject { ["path"] = store.ObjectPath(hash) }, options.ExecTimeout, ct)
            .ConfigureAwait(false);
    }

    private async Task ExecOpAsync(
        WorkerHandle worker, string script, JsonObject? parameters, WorkerTier tier, CancellationToken ct)
    {
        // exec-by-source on the desktop (T005b): the remote worker cannot read the host's tmp
        // script file, so ship the source inline. Locally we keep writing a tmp file and
        // passing its path (the worker compiles it under that path for readable tracebacks).
        if (tier == WorkerTier.Desktop)
        {
            await worker.RequestAsync("exec", new JsonObject
            {
                ["source"] = script,
                ["params"] = parameters?.DeepClone(),
            }, options.ExecTimeout, ct).ConfigureAwait(false);
            return;
        }

        var scriptPath = Path.Combine(options.StoreDir, "tmp", $"{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);
        try
        {
            // Fail-fast: no recover_to — replay/batch workers are fresh and discarded on failure.
            await worker.RequestAsync("exec", new JsonObject
            {
                ["script"] = scriptPath,
                ["params"] = parameters?.DeepClone(),
            }, options.ExecTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static JsonObject Merge(JsonObject? frozen, JsonObject overlay)
    {
        var merged = frozen?.DeepClone().AsObject() ?? [];
        foreach (var (key, value) in overlay)
        {
            merged[key] = value?.DeepClone();
        }

        return merged;
    }

    private static string Compact(JsonObject parameters) =>
        string.Join(" ", parameters.Select(kv => $"{kv.Key}:{kv.Value?.ToJsonString().Trim('"')}"));
}
