using Abl.Core.Config;
using Abl.Core.Rendering;

namespace Abl.Core.Workers;

/// <summary>
/// Turns a worker exception into the structured <see cref="AblException"/> envelope for
/// the <em>transient-worker</em> paths — render-from-checkpoint, replay, batch — and as
/// the last-resort backstop at the REST and MCP surfaces so no worker failure ever leaves
/// the daemon as a non-structured (empty-body 500) response.
///
/// Session-owned workers do NOT use this: their failures recover through
/// <c>SessionService.MapSessionFault</c>, which also kills+recycles the session and hands
/// back the one-call recovery (FR-010). Both honor <see cref="OpKind"/>.
/// </summary>
public static class WorkerErrors
{
    /// <summary>
    /// The worker-chosen error kinds the host trusts: a caller mistake (<c>bad_request</c>)
    /// and a failed device check (<c>gpu_unavailable</c>, 009 FR-003). Every other worker
    /// kind (a raw Python exception type name) collapses to <c>exec_failed</c> so a worker
    /// can neither expand the host's error-kind vocabulary nor inflate telemetry-label
    /// cardinality: the set stays fixed and closed.
    /// </summary>
    public const string WorkerBadRequest = "bad_request";

    /// <summary>The worker kind for a failed <c>cycles_gpu</c> device check (009 plan D4).</summary>
    public const string WorkerGpuUnavailable = "gpu_unavailable";

    /// <summary>Host error kind for a worker OP failure (state already restored).</summary>
    public static string OpKind(string workerKind) => workerKind switch
    {
        WorkerBadRequest => "bad_request",
        WorkerGpuUnavailable => "gpu_unavailable",
        _ => "exec_failed",
    };

    public static bool TryMap(Exception ex, out AblException mapped) => TryMap(ex, out mapped, null);

    /// <summary>
    /// <see cref="TryMap(Exception, out AblException)"/> on a guarded path (009 plan D6): a
    /// worker-raised <c>gpu_unavailable</c> carries <see cref="GpuGuard.HintFor"/> for
    /// <paramref name="path"/>, the same hint as the preflight refusal.
    /// </summary>
    public static bool TryMap(Exception ex, out AblException mapped, GpuPath path, HostPlatform host) =>
        TryMap(ex, out mapped, GpuGuard.HintFor(path, host));

    private static bool TryMap(Exception ex, out AblException mapped, string? gpuHint)
    {
        // A transient worker is disposed and replenished by its caller, so its death
        // leaves no session state behind — the move is simply to retry.
        const string transientHint =
            "retry; this ran on a transient worker, so no session state is affected";
        mapped = ex switch
        {
            WorkerOpException op => new AblException(
                OpKind(op.Kind), op.Message,
                hint: OpKind(op.Kind) == WorkerGpuUnavailable ? gpuHint : null,
                workerTraceback: op.WorkerTraceback),
            WorkerTimeoutException t => new AblException("timeout", t.Message, hint: transientHint),
            // A configuration refusal (008 T036, 1b eng-code L1): retrying cannot help, so the
            // hint is the check's own remedy, never the transient one.
            WorkerDeadException { InnerException: ConfigException refused } d => new AblException(
                "worker_died", d.Message, hint: refused.Hint ?? $"fix {refused.Check} (see `abl config show`)"),
            WorkerDeadException d => new AblException("worker_died", d.Message, hint: transientHint),
            _ => null!,
        };
        return mapped is not null;
    }

    public static bool TryLegacyFrameLimit(Exception ex, bool supportsChunks, out AblException mapped)
    {
        mapped = !supportsChunks && ex is WorkerDeadException { InnerException: InvalidDataException data }
            && data.Message.Contains("exceeds", StringComparison.Ordinal)
            ? new AblException("worker_upgrade_required", "legacy desktop pixels exceeded the single-frame limit",
                hint: "update the desktop launcher together with its bundled worker/ to enable chunked transfers")
            : null!;
        return mapped is not null;
    }
}
