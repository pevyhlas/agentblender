using System.Diagnostics;
using Abl.Core.Telemetry;

namespace Abl.Core.Rendering;

/// <summary>
/// Single-slot GPU lease (FR-011): one render on the host GPU at a time. Callers pick
/// wait (bounded), cpu_fallback, or fail; the policy lives in the render ops — this type
/// only owns the slot.
/// </summary>
public sealed class GpuLease(string tier = "local")
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _attempts;

    /// <summary>How many acquisitions were attempted (tests: a refused request makes none).</summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Returns a hold, or null when the slot stayed busy past maxWait.</summary>
    public async Task<Hold?> TryAcquireAsync(TimeSpan maxWait, CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        var watch = Stopwatch.StartNew();
        var outcome = "cancelled";
        try
        {
            var acquired = await _slot.WaitAsync(maxWait, ct).ConfigureAwait(false);
            outcome = acquired ? "acquired" : "timeout";
            return acquired ? new Hold(_slot) : null;
        }
        finally
        {
            AblTelemetry.RecordLeaseWait(tier, watch.Elapsed.TotalMilliseconds, outcome);
        }
    }

    public sealed class Hold(SemaphoreSlim slot) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slot.Release();
            }
        }
    }
}
