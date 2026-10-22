using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Abl.Core.Workers;

// Busy-serialization is a SESSION concern (Session.EnterOp) — workers only spawn, warm,
// serve, die.
public enum WorkerState
{
    Spawning,
    Warming,
    Ready,
    Dead,
}

/// <summary>A live Blender worker plus its protocol connection.</summary>
public sealed class WorkerHandle : IAsyncDisposable
{
    // A REMOTE worker (feature 003) has no local process — the desktop launcher owns
    // blender.exe; the host holds only the connection. Killing a remote worker closes the
    // socket, which the launcher observes and reaps its child. So _process is nullable.
    private readonly Process? _process;
    private readonly WorkerConnection _connection;
    private readonly Queue<string> _recentOutput = new();
    private readonly Lock _outputLock = new();
    private volatile bool _killedByHost;

    /// <summary>How long a worker whose socket broke may take to exit on its own before it is killed.</summary>
    private static readonly TimeSpan DeadExitGrace = TimeSpan.FromSeconds(1);

    /// <summary>Local worker: owns a Blender process whose stdout/stderr we capture.</summary>
    internal WorkerHandle(string id, Process process, WorkerConnection connection)
    {
        Id = id;
        _process = process;
        _connection = connection;
        Tier = WorkerTier.Local;
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>Remote worker: a connection to a launcher-owned Blender (no local process).</summary>
    internal WorkerHandle(string id, WorkerConnection connection, WorkerTier tier)
    {
        Id = id;
        _process = null;
        _connection = connection;
        Tier = tier;
    }

    public string Id { get; }

    /// <summary>Blender process id, or -1 for a remote worker (the process lives on the desktop).</summary>
    public int ProcessId => _process?.Id ?? -1;

    /// <summary>Which hardware tier this worker runs on (feature 003).</summary>
    public WorkerTier Tier { get; }

    public WorkerState State { get; internal set; } = WorkerState.Spawning;

    /// <summary>The running Blender's version string, captured from hello (provenance).</summary>
    public string? BlenderVersion { get; internal set; }
    public bool SupportsBlobChunks { get; internal set; }

    /// <summary>The running Blender's build hash from hello (doctor <c>worker.roundtrip</c>).</summary>
    public string? BlenderBuildHash { get; internal set; }

    /// <summary>The worker's own digest of its modules from hello (<see cref="WorkerSource"/>).</summary>
    public string? WorkerSourceSha256 { get; internal set; }

    /// <summary>
    /// The GPU capability from the worker's own hello (009 T004), read by
    /// <see cref="Rendering.GpuGuard"/>. <see cref="GpuCapability.None"/> until the hello.
    /// </summary>
    public GpuCapability Gpu { get; internal set; } = GpuCapability.None;

    /// <summary>
    /// Record what a validated hello reports, the same way on every tier (the local
    /// supervisor and the desktop tier).
    /// </summary>
    internal void ApplyHello(JsonObject hello)
    {
        BlenderVersion = hello["blender_version"]?.GetValue<string>();
        SupportsBlobChunks = hello["capabilities"]?["blob_chunks"]?.GetValue<bool>() == true;
        BlenderBuildHash = hello["blender_build_hash"] is JsonValue hash && hash.TryGetValue<string>(out var h) ? h : null;
        WorkerSourceSha256 = hello["worker_source_sha256"] is JsonValue digest && digest.TryGetValue<string>(out var d) ? d : null;
        Gpu = WorkerReplies.Gpu(hello);
    }

    // Recycle-policy inputs. 001 recycles on timeout/crash only; op-count/age-based
    // recycling is a recorded carry-item (tasks.md T007).
    public DateTimeOffset SpawnedAt { get; } = DateTimeOffset.UtcNow;
    public int OpCount { get; private set; }

    public bool IsAlive =>
        State is not WorkerState.Dead && (_process is null || !_process.HasExited) && !_connection.Poisoned;

    /// <summary>Last stdout/stderr lines — diagnostic context for dead-worker errors.</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_outputLock)
            {
                return [.. _recentOutput];
            }
        }
    }

    public async Task<JsonObject> RequestAsync(
        string op, JsonObject? fields, TimeSpan timeout, CancellationToken ct)
    {
        OpCount++;
        try
        {
            return await _connection.RequestAsync(op, fields, timeout, ct).ConfigureAwait(false);
        }
        catch (WorkerTimeoutException)
        {
            await KillAsync().ConfigureAwait(false);
            throw;
        }
        catch (WorkerDeadException)
        {
            // A broken socket usually means Blender is going away (a native crash): give it a
            // moment to exit on its own, so the exit code a failed spawn reports is its own.
            await KillAsync(DeadExitGrace).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled request may leave an unread response on the wire. Never reuse it.
            await KillAsync().ConfigureAwait(false);
            _connection.Dispose();
            throw;
        }
    }

    /// <summary>Best-effort graceful quit, then kill.</summary>
    public async Task ShutdownAsync()
    {
        if (IsAlive)
        {
            try
            {
                await _connection.RequestAsync("quit", null, TimeSpan.FromSeconds(3), CancellationToken.None)
                    .ConfigureAwait(false);
                _process?.WaitForExit(2000);
            }
            catch (Exception ex) when (ex is WorkerTimeoutException or WorkerDeadException or WorkerOpException)
            {
                // fall through to kill
            }
        }

        await KillAsync().ConfigureAwait(false);
    }

    public Task KillAsync() => KillAsync(TimeSpan.Zero);

    /// <summary>Kills the process unless it exits on its own within <paramref name="grace"/>.</summary>
    private async Task KillAsync(TimeSpan grace)
    {
        State = WorkerState.Dead;
        // Remote worker: no local process to kill. Disposing the connection (below) closes
        // the socket, which the launcher observes to reap its blender.exe child.
        if (_process is null)
        {
            return;
        }

        try
        {
            if (grace > TimeSpan.Zero && !_process.HasExited)
            {
                await WaitBoundedAsync(_process, grace).ConfigureAwait(false);
            }

            if (!_process.HasExited)
            {
                _killedByHost = true;
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000); // reap — a long-lived daemon must not leak zombies
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    /// <summary>
    /// Ends a worker whose spawn failed and reports how its local process ended (008 plan B4):
    /// the exit code when it exited on its own, and its last output once the output events
    /// have flushed. The flush wait is bounded, so a grandchild that holds the pipes open
    /// cannot stall the report. Disposes the handle; null for a remote worker.
    /// </summary>
    internal async Task<WorkerExit?> EndAsync(TimeSpan flush)
    {
        await KillAsync().ConfigureAwait(false);
        WorkerExit? exit = null;
        if (_process is not null)
        {
            await WaitBoundedAsync(_process, flush).ConfigureAwait(false);
            int? code = null;
            try
            {
                code = !_killedByHost && _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
            }

            exit = new WorkerExit(code, RecentOutput);
        }

        await DisposeAsync().ConfigureAwait(false);
        return exit;
    }

    /// <summary>Waits at most <paramref name="limit"/> for the exit and the end of its redirected output.</summary>
    private static async Task WaitBoundedAsync(Process process, TimeSpan limit)
    {
        using var wait = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync().ConfigureAwait(false);
        _connection.Dispose();
        _process?.Dispose();
    }

    private void Remember(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_outputLock)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > 40)
            {
                _recentOutput.Dequeue();
            }
        }
    }
}
