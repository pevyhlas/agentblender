using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Abl.Core.Config;

/// <summary>
/// Unix process control for the daemon lifecycle (008 T032–T034, plan B3): <c>dup2</c> to
/// point fds 0–2 at the log and <c>/dev/null</c>, <c>setsid</c> to leave the agent's session
/// and process group, and <c>kill</c> for graceful, group and liveness signals. .NET has no
/// API for these (plan Complexity Tracking); they share <see cref="UnixIdentity"/>'s libc
/// resolver.
/// </summary>
public static partial class UnixIdentity
{
    /// <summary><c>SIGKILL</c> (the same number on Linux and macOS).</summary>
    public const int SigKill = 9;

    /// <summary><c>SIGTERM</c> (the same number on Linux and macOS).</summary>
    public const int SigTerm = 15;

    /// <summary><c>SIGSTOP</c> (19 on Linux, 17 on macOS and the BSDs).</summary>
    public static int SigStop => OperatingSystem.IsLinux() ? 19 : 17;

    private const int ESrch = 3;

    /// <summary>Makes <paramref name="target"/> (0, 1 or 2) refer to the open file of <paramref name="source"/>.</summary>
    /// <exception cref="IOException">dup2 failed.</exception>
    public static void Duplicate(SafeFileHandle source, int target)
    {
        var added = false;
        source.DangerousAddRef(ref added);
        try
        {
            int result;
            do
            {
                result = dup2((int)source.DangerousGetHandle(), target);
            }
            while (result < 0 && Marshal.GetLastPInvokeError() == EIntr);

            if (result < 0)
            {
                throw new IOException($"dup2 onto fd {target} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            }
        }
        finally
        {
            if (added)
            {
                source.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// <c>setsid()</c>: this process becomes the leader of a new session and process group
    /// (its pid becomes its pgid), detached from any controlling terminal.
    /// </summary>
    /// <exception cref="IOException">setsid failed (the process already leads a group).</exception>
    public static void CreateSession()
    {
        if (setsid() < 0)
        {
            throw new IOException($"setsid failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }
    }

    /// <summary>
    /// Sends <paramref name="signal"/> to process <paramref name="pid"/>, or to every process in
    /// group <c>-pid</c> when <paramref name="pid"/> is negative. Signal 0 only checks existence.
    /// </summary>
    /// <returns>False when no such process or group exists (<c>ESRCH</c>).</returns>
    /// <exception cref="IOException">Any other failure (for example <c>EPERM</c>).</exception>
    public static bool Signal(int pid, int signal)
    {
        if (pid is 0 or -1)
        {
            throw new ArgumentOutOfRangeException(nameof(pid), pid, "0 and -1 address more than one process group");
        }

        if (kill(pid, signal) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ESrch
            ? false
            : throw new IOException($"kill({pid}, {signal}) failed: {Marshal.GetPInvokeErrorMessage(error)}");
    }

    /// <summary>True while a process with this pid exists.</summary>
    public static bool ProcessExists(int pid) => Signal(pid, 0);

    /// <summary>True while any process is in process group <paramref name="pgid"/>.</summary>
    public static bool GroupExists(int pgid) => Signal(-pgid, 0);

    private const int EIntr = 4;

#pragma warning disable SA1300, IDE1006 // libc symbol name
    [LibraryImport(Libc, SetLastError = true)]
    private static partial int dup2(int oldfd, int newfd);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int setsid();

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int kill(int pid, int sig);
#pragma warning restore SA1300, IDE1006
}
