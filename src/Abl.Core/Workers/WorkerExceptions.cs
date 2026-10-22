namespace Abl.Core.Workers;

/// <summary>
/// A worker failure. When it ended a <see cref="WorkerSupervisor.SpawnAsync"/>, the supervisor
/// tags it with typed facts, never spliced into the message: the <see cref="Stage"/> it failed
/// and, for a local worker, how the Blender process ended (<see cref="Exit"/>).
/// </summary>
public abstract class WorkerException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The spawn stage it failed, when it ended a spawn (<see cref="WorkerSpawnStage"/>).</summary>
    public string? Stage { get; internal set; }

    /// <summary>The Blender process's exit code and last output, when it ended a local spawn.</summary>
    public WorkerExit? Exit { get; internal set; }
}

/// <summary>An op the worker reported as failed (state already restored per FR-003).</summary>
public sealed class WorkerOpException(string kind, string message, string? workerTraceback)
    : WorkerException(message)
{
    public string Kind { get; } = kind;
    public string? WorkerTraceback { get; } = workerTraceback;
}

/// <summary>Host-enforced timeout breach; the worker gets killed and recycled.</summary>
public sealed class WorkerTimeoutException(string op, TimeSpan timeout)
    : WorkerException($"worker op '{op}' exceeded its {timeout.TotalSeconds:0}s timeout")
{
    public string Op { get; } = op;
}

/// <summary>The worker process died or the socket broke mid-conversation.</summary>
public sealed class WorkerDeadException(string message, Exception? inner = null)
    : WorkerException(message, inner);

/// <summary>
/// How a local Blender worker process ended a failed spawn (008 plan B4: "with the Blender
/// stderr tail"): its exit code when it exited on its own (null when the host had to kill it,
/// for example after a timeout) and its last stdout/stderr lines, raw and oldest first.
/// Consumers clean and bound them before they show them.
/// </summary>
public sealed record WorkerExit(int? ExitCode, IReadOnlyList<string> Output);

/// <summary>
/// Where a <see cref="WorkerSupervisor.SpawnAsync"/> failed (008 plan B4/B5), a typed fact the
/// supervisor tags on the exception it throws, never parsed from the message: doctor's
/// <c>worker.roundtrip</c> and <c>/healthz</c>'s <c>lastWorkerError</c> share it.
/// </summary>
public static class WorkerSpawnStage
{
    /// <summary>Refused before any socket is bound or process started (Blender, runtime dir).</summary>
    public const string Preflight = "preflight";

    /// <summary>The socket bind, the process start or the worker's connect.</summary>
    public const string Spawn = "spawn";

    /// <summary>The <c>hello</c> request, or the supervisor's check of its version.</summary>
    public const string Hello = "hello";

    /// <summary>The <c>warm</c> request (with the 64 px eevee prewarm render).</summary>
    public const string Warm = "warm";

    /// <summary>The stage a spawn failure carries, or null for an exception the supervisor did not tag.</summary>
    public static string? Of(Exception error) => (error as WorkerException)?.Stage;

    /// <summary>How the Blender process ended a failed spawn, or null when that is not known.</summary>
    public static WorkerExit? ExitOf(Exception error) => (error as WorkerException)?.Exit;

    /// <summary>Tags a worker exception with its stage and process exit (first tag of each wins) and returns it.</summary>
    internal static Exception Tag(Exception error, string stage, WorkerExit? exit = null)
    {
        if (error is WorkerException worker)
        {
            worker.Stage ??= stage;
            worker.Exit ??= exit;
        }

        return error;
    }
}
