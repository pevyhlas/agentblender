using System.Diagnostics;
using Abl.Core.Config;

namespace Abl.Core.Lifecycle;

/// <summary>
/// The process operations the daemon lifecycle verbs use (008 T035, 2a eng-arch LOW-1):
/// signals, liveness, start times and the process table. <c>start</c>, <c>stop</c> and
/// <c>status</c> reach the host only through it, so a fake can stand in for a daemon, its
/// workers and an unreadable process table in unit tests.
/// </summary>
public interface IProcessControl
{
    /// <summary>
    /// Sends <paramref name="signal"/> to process <paramref name="pid"/>, or to every process
    /// in group <c>-pid</c> when it is negative.
    /// </summary>
    /// <returns>False when no such process or group exists.</returns>
    /// <exception cref="IOException">Any other failure (for example <c>EPERM</c>).</exception>
    bool Signal(int pid, int signal);

    /// <summary>True while a process with this pid exists.</summary>
    bool Exists(int pid);

    /// <summary>True while any process is in process group <paramref name="pgid"/>.</summary>
    bool GroupExists(int pgid);

    /// <summary>When <paramref name="pid"/> started (UTC), or null when it cannot be read.</summary>
    DateTimeOffset? StartTime(int pid);

    /// <summary>The host's process table, or <see cref="ProcessTree.Table.Unknown"/> with the reason.</summary>
    ProcessTree.Table Snapshot();

    /// <summary>This process's pid: a verb never signals the process tree it runs in.</summary>
    int CallerPid { get; }

    /// <summary>This process's process group: a verb never signals the group it runs in.</summary>
    int CallerGroup { get; }
}

/// <summary>The real host: <c>kill(2)</c>, the runtime's process start time and <see cref="ProcessTree"/>.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class UnixProcessControl : IProcessControl
{
    public static UnixProcessControl Instance { get; } = new();

    private UnixProcessControl()
    {
    }

    public bool Signal(int pid, int signal) => UnixIdentity.Signal(pid, signal);

    public bool Exists(int pid) => UnixIdentity.ProcessExists(pid);

    public bool GroupExists(int pgid) => UnixIdentity.GroupExists(pgid);

    public DateTimeOffset? StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public ProcessTree.Table Snapshot() => ProcessTree.Snapshot();

    public int CallerPid => Environment.ProcessId;

    public int CallerGroup => UnixIdentity.ProcessGroup;
}
