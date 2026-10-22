using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Abl.Core.Workers;

/// <summary>
/// Keeps a floor of pre-warmed Ready workers so session opens never pay the cold start
/// (Principle 3). Replenishes in the background after every take.
/// </summary>
public sealed class WorkerPool(AblOptions options, WorkerSupervisor supervisor, ILogger<WorkerPool> logger)
    : IAsyncDisposable
{
    private readonly ConcurrentQueue<WorkerHandle> _ready = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _fill = Task.CompletedTask;
    private int _filling;
    private volatile bool _disposed;

    public int ReadyCount => _ready.Count;

    /// <summary>
    /// The last failed background spawn (008 FR-010, plan B5), or null. <c>abl daemon start</c>
    /// fails on it instead of waiting out its timeout (AC-18); a later spawn does not clear it.
    /// </summary>
    public WorkerError? LastWorkerError { get; private set; }

    /// <summary>
    /// Test seam: when set, workers come from this factory instead of
    /// <see cref="WorkerSupervisor.SpawnAsync"/> (unit tests drive scripted fake workers).
    /// </summary>
    internal Func<CancellationToken, Task<WorkerHandle>>? Spawner { get; init; }

    /// <summary>
    /// When the last spawn delivered a ready worker (008 T036: <c>daemon_status</c> shows it
    /// next to <see cref="LastWorkerError"/>), or null before the first.
    /// </summary>
    public DateTimeOffset? LastSpawnAt { get; private set; }

    /// <summary>
    /// The GPU capability from the hello of the last ready local worker and when it was seen
    /// (008 B5, 009 T020), or null before any. A desktop worker's GPU is not reported here.
    /// </summary>
    public (GpuCapability Gpu, DateTimeOffset At)? LastLocalGpu { get; private set; }

    private async Task<WorkerHandle> SpawnAsync(CancellationToken ct)
    {
        var handle = await (Spawner?.Invoke(ct) ?? supervisor.SpawnAsync(prewarm: true, ct)).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        LastSpawnAt = now;
        if (handle.Tier == WorkerTier.Local)
        {
            LastLocalGpu = (handle.Gpu, now);
        }

        return handle;
    }

    public async Task<WorkerHandle> TakeAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_ready.TryDequeue(out var handle))
        {
            if (handle.IsAlive)
            {
                Replenish();
                return handle;
            }

            await handle.DisposeAsync().ConfigureAwait(false);
        }

        Replenish();
        return await SpawnAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Fire-and-forget top-up back to the pool floor.</summary>
    public void Replenish()
    {
        if (_disposed || Interlocked.Exchange(ref _filling, 1) == 1)
        {
            return;
        }

        var token = _shutdown.Token;
        _fill = Task.Run(async () =>
        {
            try
            {
                while (!_disposed && _ready.Count < options.PoolFloor)
                {
                    var handle = await SpawnAsync(token).ConfigureAwait(false);
                    if (_disposed)
                    {
                        await handle.DisposeAsync().ConfigureAwait(false); // ready after the drain
                        return;
                    }

                    _ready.Enqueue(handle);
                }
            }
            catch (Exception ex) when (token.IsCancellationRequested)
            {
                logger.LogDebug(ex, "pool replenish cancelled by shutdown");
            }
            catch (Exception ex)
            {
                LastWorkerError = WorkerError.From(WorkerSpawnStage.Of(ex) ?? WorkerSpawnStage.Spawn, ex, DateTimeOffset.UtcNow);
                logger.LogWarning(ex, "pool replenish failed; next take spawns directly");
            }
            finally
            {
                Interlocked.Exchange(ref _filling, 0);
            }
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The in-flight spawn's catch kills its process tree and deletes its socket.
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _fill.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        while (_ready.TryDequeue(out var handle))
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        // _shutdown is not disposed: a late Replenish may still read its token (it has no timer).
    }
}

/// <summary>
/// A worker failure as reported on <c>/healthz</c> (008 plan B5): the stage (the supervisor's typed
/// <see cref="WorkerSpawnStage"/>, the vocabulary doctor's <c>worker.roundtrip</c> uses), a bounded message
/// (500 characters, control characters removed) and when it happened. <see cref="Check"/> is
/// the doctor check ID of a configuration refusal, read from the <see cref="Config.ConfigException"/>
/// inside the <see cref="WorkerDeadException"/>, never parsed from the message.
/// </summary>
public sealed record WorkerError(string Stage, string Message, string? Check, DateTimeOffset At)
{
    public const int MaxMessageLength = 500;

    public static WorkerError From(string stage, Exception error, DateTimeOffset at)
    {
        var text = new string([.. error.Message.Select(c => char.IsControl(c) ? ' ' : c)]);
        return new WorkerError(
            stage,
            text.Length > MaxMessageLength ? text[..MaxMessageLength] : text,
            (error.InnerException as Config.ConfigException)?.Check,
            at);
    }
}
