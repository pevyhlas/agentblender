using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Abl.Core.Rendering;
using Abl.Core.Store;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging;

namespace Abl.Core.Delivery;

public sealed record DeliveryExportResult(string PayloadDir, string DescriptorPath, IReadOnlyList<string> Images,
    string ReplaySourceDir, string ContactSheet, string Name, int Items);
public sealed record DeliveryVerifyResult(bool Pass, int Items, IReadOnlyList<string> Problems, string? Hint)
{
    public bool PixelsDecoded => false;
    public bool Admitted => false;
    public bool ProvenanceVerified => false;
    public bool SourceReplayVerified => false;
}
public sealed record DeliveryListItem(string File, int Width, int Height, string Sha256, string SceneDigest, string Recipe);
public sealed record DeliveryListResult(string Name, string Profile, string? BlenderVersion, IReadOnlyList<DeliveryListItem> Items);

/// <summary>One frozen still, staged as a complete fresh generation; never replaces selected output.</summary>
public sealed class DeliveryService(AblOptions options, WorkerPool pool, CheckpointStore store,
    GpuLease gpuLease, ILogger<DeliveryService> logger)
{
    public const string DescriptorName = "delivery.json"; // Producer receipt OUTSIDE the strict payload.
    public const string PayloadDirName = "payload";
    public const string ReplaySourceDirName = "replay-source";
    public const string ContactSheetName = "contact-sheet.png";
    public const long MaxImageBytes = 64L * 1024 * 1024;

    public async Task<DeliveryExportResult> ExportAsync(string name, string destDir, IReadOnlyList<JsonObject> paramTable,
        string engine, int res, int? samples, int seed, int columns, CancellationToken ct)
    {
        try { return await ExportCoreAsync(name, destDir, paramTable, engine, res, samples, seed, columns, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AblException("io_error", "delivery source/output is unreadable or publication failed",
                hint: "choose a writable output parent or restore the source; prior generations are not replaced");
        }
        catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped, GpuPath.DeliveryExport, options.Platform)) { throw mapped; }
    }

    private async Task<DeliveryExportResult> ExportCoreAsync(string name, string destDir, IReadOnlyList<JsonObject> paramTable,
        string engine, int res, int? samples, int seed, int columns, CancellationToken ct)
    {
        if (!DeliveryPayload.Id(name) || paramTable is null || paramTable.Count > 1 || paramTable.Any(row => row is null) ||
            engine is not ("eevee" or "cycles_cpu" or "cycles_gpu") || res is < 1 or > 4096 || samples is < 1 or > 4096 || seed < 0 || columns != 1 && columns != 3)
            throw new AblException("bad_request", "first delivery profile supports one still (zero/one param row), lowercase asset id, res1..4096 and Eevee/Cycles");
        var overlay = paramTable.Count == 0 ? new JsonObject() : paramTable[0].DeepClone().AsObject();
        if (Encoding.UTF8.GetByteCount(overlay.ToJsonString()) > 65536) throw new AblException("bad_request", "parameter row exceeds 64 KiB");
        var artifact = store.GetFrozen(name) ?? throw new AblException("not_found", "frozen artifact not found");
        var parent = Path.GetFullPath(destDir);
        DeliveryPayload.RefuseLinks(parent);
        Directory.CreateDirectory(parent);
        DeliveryPayload.RefuseLinks(parent);
        var generation = name + "-" + Guid.NewGuid().ToString("N");
        var stage = Path.Combine(parent, ".stage-" + generation);
        var completed = Path.Combine(parent, generation);
        WorkerHandle? worker = null;
        try
        {
            worker = await pool.TakeAsync(ct).ConfigureAwait(false);
            // 009 FR-003: delivery defaults to cycles_gpu; refuse it on a worker without a GPU
            // backend before staging and before the GPU lease.
            var decision = GpuGuard.Decide(worker.Gpu, engine, null, GpuPath.DeliveryExport, options.Platform);
            if (decision.Kind == GpuDecisionKind.Refuse) throw GpuGuard.Unavailable(decision, WorkerTier.Local);
            Directory.CreateDirectory(stage);
            var payload = Path.Combine(stage, PayloadDirName);
            Directory.CreateDirectory(payload);
            var replay = store.ExportBundle(name, Path.Combine(stage, ReplaySourceDirName));
            var recipeBytes = DeliveryPayload.ReadBounded(replay.ArtifactJson, 8 * 1024 * 1024);
            await worker.RequestAsync("load", new() { ["path"] = store.ObjectPath(artifact.BaseCheckpoint) }, options.ExecTimeout, ct);
            foreach (var op in artifact.Ops)
            {
                var merged = op.Params?.DeepClone().AsObject() ?? new JsonObject();
                foreach (var (key, value) in overlay) merged[key] = value?.DeepClone();
                await worker.RequestAsync("exec", new() { ["source"] = op.Script, ["params"] = merged }, options.ExecTimeout, ct);
            }
            var hello = await worker.RequestAsync("hello", null, options.ExecTimeout, ct);
            var producingVersion = hello["blender_version"] is JsonValue helloVersion && helloVersion.TryGetValue<string>(out var version)
                ? version : throw new AblException("worker_error", "producing worker hello has no Blender version");
            if (artifact.BlenderVersion != producingVersion)
                throw new AblException("bad_request", "Blender version differs from frozen source; requalify the source first");
            var blenderPolicy = DeliveryBlenderPolicy.Capture(producingVersion, options);
            var temporaryBlend = store.TmpBlendPath();
            string digest;
            try
            {
                var saved = await worker.RequestAsync("save", new() { ["path"] = temporaryBlend }, options.ExecTimeout, ct);
                digest = saved["digest"]!.GetValue<string>();
            }
            finally { if (File.Exists(temporaryBlend)) File.Delete(temporaryBlend); }
            if (overlay.Count == 0 && digest != artifact.ExpectedDigest)
                throw new AblException("exec_failed", "frozen base replay digest drifted within declared R3 coverage");
            var rawImage = Path.Combine(stage, "raw.png");
            JsonObject rendered;
            using (await gpuLease.TryAcquireAsync(options.RenderTimeout, ct) ?? throw new AblException("gpu_busy", "GPU lease busy"))
                rendered = await worker.RequestAsync("render", new()
                {
                    ["engine"] = engine,
                    ["res"] = res,
                    ["samples"] = samples ?? 16,
                    ["seed"] = seed,
                    ["out"] = rawImage,
                    ["profile"] = true,
                }, options.RenderTimeout, ct);
            var rawBytes = DeliveryPayload.ReadBounded(rawImage, MaxImageBytes);
            var png = PngReader.Normalize(rawBytes);
            var header = PngReader.Parse(png);
            var imagePath = Path.Combine(payload, AgdStillDescriptor.ImageName);
            await File.WriteAllBytesAsync(imagePath, png, ct);
            File.Delete(rawImage);
            var item = new DeliveryItem(AgdStillDescriptor.ImageName, png.LongLength, AgdStillDescriptor.Hash(png), artifact.BaseCheckpoint,
                digest, new(name, overlay), new(engine, res, samples ?? 16, seed, "srgb", "straight")
                {
                    EffectiveSettings = rendered["settings"]?.DeepClone().AsObject()
                        ?? throw new AblException("worker_error", "render profile returned no effective settings"),
                }, new(header.Width, header.Height));
            var build = typeof(DeliveryService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var hostHash = AgdStillDescriptor.Hash(DeliveryPayload.ReadBounded(typeof(DeliveryService).Assembly.Location, 16 * 1024 * 1024));
            var snapshot = new JsonObject
            {
                ["profile"] = "abl.still-recipe-snapshot.v1",
                ["frozenArtifact"] = JsonNode.Parse(recipeBytes),
                ["overlay"] = overlay.DeepClone(),
                ["render"] = item.Render.EffectiveSettings!.DeepClone(),
                ["renderDeclaration"] = System.Text.Json.JsonSerializer.SerializeToNode(item.Render, System.Text.Json.JsonSerializerOptions.Web),
                ["producerBuild"] = build,
                ["blenderVersion"] = producingVersion,
                ["blenderPolicy"] = System.Text.Json.JsonSerializer.SerializeToNode(blenderPolicy),
                ["blenderBuildHash"] = hello["blender_build_hash"]!.DeepClone(),
                ["hostAssemblySha256"] = hostHash,
                ["workerSourceSha256"] = hello["worker_source_sha256"]!.DeepClone(),
            };
            var snapshotBytes = Encoding.UTF8.GetBytes(snapshot.ToJsonString());
            if (snapshotBytes.Length > 8 * 1024 * 1024) throw new AblException("bad_request", "recipe snapshot exceeds 8 MiB");
            await File.WriteAllBytesAsync(Path.Combine(stage, "recipe-snapshot.json"), snapshotBytes, ct);
            var recipeHash = AgdStillDescriptor.Hash(snapshotBytes);
            var metadata = AgdStillDescriptor.Serialize(name, item, build, recipeHash);
            await File.WriteAllTextAsync(Path.Combine(payload, AgdStillDescriptor.FileName), metadata, ct);
            var producer = new DeliveryProducer("agentblender", producingVersion)
            {
                BlenderPolicy = blenderPolicy,
                Build = build,
                BlenderBuildHash = hello["blender_build_hash"]!.GetValue<string>(),
                HostAssemblySha256 = hostHash,
                WorkerSourceSha256 = hello["worker_source_sha256"]!.GetValue<string>(),
            };
            var receipt = new DeliveryDescriptor(2, name, producer, AgdStillDescriptor.Profile, artifact.CreatedAt, [item])
            {
                DescriptorSha256 = AgdStillDescriptor.Hash(Encoding.UTF8.GetBytes(metadata)),
                RecipeSnapshotSha256 = recipeHash,
            };
            await worker.RequestAsync("contact_sheet", new()
            {
                ["inputs"] = new JsonArray(imagePath),
                ["labels"] = new JsonArray(name),
                ["columns"] = 1,
                ["cell"] = Math.Min(res, 256),
                ["out"] = Path.Combine(stage, ContactSheetName),
            }, options.RenderTimeout, ct);
            await File.WriteAllTextAsync(Path.Combine(stage, DescriptorName), DeliveryJson.Serialize(receipt), ct);
            var verified = DeliveryPayload.Verify(stage);
            if (!verified.Pass) throw new AblException("worker_error", string.Join("; ", verified.Problems));
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, completed); // Complete visibility last; never replaces an existing generation.
            logger.LogInformation("exported still {Name}", name);
            return new(Path.Combine(completed, PayloadDirName), Path.Combine(completed, PayloadDirName, AgdStillDescriptor.FileName),
                [Path.Combine(completed, PayloadDirName, AgdStillDescriptor.ImageName)], Path.Combine(completed, ReplaySourceDirName),
                Path.Combine(completed, ContactSheetName), name, 1);
        }
        catch (Exception ex) when (WorkerErrors.TryMap(ex, out var mapped, GpuPath.DeliveryExport, options.Platform)) { throw mapped; }
        finally
        {
            try
            {
                if (worker is not null) { await worker.ShutdownAsync(); await worker.DisposeAsync(); pool.Replenish(); }
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
    }

    public DeliveryVerifyResult Verify(string directory) => DeliveryPayload.Verify(directory);
    public DeliveryListResult List(string directory) => DeliveryPayload.List(directory);
}
