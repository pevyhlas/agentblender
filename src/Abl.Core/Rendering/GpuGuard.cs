using Abl.Core.Config;
using Abl.Core.Workers;

namespace Abl.Core.Rendering;

/// <summary>The request paths that can send <c>cycles_gpu</c> to a worker (009 FR-003, plan D6).</summary>
public enum GpuPath
{
    RenderFinal,
    ScenePreview,
    BatchRun,
    DeliveryExport,
}

/// <summary>What <see cref="GpuGuard.Decide"/> chose for one request.</summary>
public enum GpuDecisionKind
{
    /// <summary>Send the request as asked.</summary>
    Render,

    /// <summary>Render <c>cycles_cpu</c> without the GPU lease (<c>render_final</c> + <c>lease=cpu_fallback</c>).</summary>
    RenderCpuFallback,

    /// <summary>Fail with <c>gpu_unavailable</c> and <see cref="GpuDecision.Hint"/>; no render, no lease.</summary>
    Refuse,
}

public readonly record struct GpuDecision(GpuDecisionKind Kind, string? Hint = null);

/// <summary>
/// The single host predicate for <c>cycles_gpu</c> (009 FR-003, plan D6 and "Host guard
/// matrix"). It runs before any <c>render</c> request and before the GPU lease, so a
/// refusal never waits for the lease. <see cref="HintFor"/> is the only source of
/// <c>gpu_unavailable</c> hint text, for the preflight refusal and for a worker-raised
/// <c>gpu_unavailable</c> alike. A hint never names a parameter its path does not accept
/// (only <c>render_final</c> takes <c>lease</c>) and never a remedy the request already used
/// (a <c>cpu_fallback</c> request never receives <c>gpu_unavailable</c>).
/// </summary>
public static class GpuGuard
{
    /// <summary>The <c>render_final</c> hint (lease <c>wait</c> or <c>fail</c>).</summary>
    public const string RenderFinalHint = "use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback";

    /// <summary>The hint for the paths without a <c>lease</c> parameter.</summary>
    public const string EngineHint = "pass engine=cycles_cpu or engine=eevee";

    /// <summary>Appended to every hint on a macOS (arm64) host: the likely cause and where to diagnose it.</summary>
    public const string MacSuffix = "; on macOS a daemon started inside an agent sandbox cannot see Metal, see abl doctor gpu.backend";

    /// <summary>Whether the capability has a GPU backend <c>cycles_gpu</c> can use.</summary>
    public static bool HasBackend(GpuCapability gpu) => gpu.Backend is "optix" or "metal";

    /// <summary>The predicate: <paramref name="engine"/> may be sent to a worker with <paramref name="gpu"/> as asked.</summary>
    public static bool Allows(GpuCapability gpu, string engine) => engine != "cycles_gpu" || HasBackend(gpu);

    /// <summary>
    /// Decide one request. <paramref name="leaseMode"/> is null on the paths that take no
    /// <c>lease</c>; only <see cref="GpuPath.RenderFinal"/> with <c>cpu_fallback</c> falls
    /// back to the CPU instead of refusing.
    /// </summary>
    public static GpuDecision Decide(GpuCapability gpu, string engine, string? leaseMode, GpuPath path, HostPlatform host)
    {
        if (Allows(gpu, engine))
        {
            return new GpuDecision(GpuDecisionKind.Render);
        }

        return path == GpuPath.RenderFinal && leaseMode == "cpu_fallback"
            ? new GpuDecision(GpuDecisionKind.RenderCpuFallback)
            : new GpuDecision(GpuDecisionKind.Refuse, HintFor(path, host));
    }

    /// <summary>The exact <c>gpu_unavailable</c> hint for <paramref name="path"/> on <paramref name="host"/>.</summary>
    public static string HintFor(GpuPath path, HostPlatform host) =>
        (path == GpuPath.RenderFinal ? RenderFinalHint : EngineHint) + (host == HostPlatform.MacOS ? MacSuffix : "");

    /// <summary>The preflight refusal for a <see cref="GpuDecisionKind.Refuse"/> decision.</summary>
    public static AblException Unavailable(GpuDecision decision, WorkerTier tier) => new(
        "gpu_unavailable",
        $"cycles_gpu needs a GPU backend, and the {tier.ToString().ToLowerInvariant()} worker reports no GPU backend",
        hint: decision.Hint);
}
