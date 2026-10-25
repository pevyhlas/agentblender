using System.Text;
using Abl.Core.Config;
using Microsoft.Win32.SafeHandles;

namespace Abl.Cli.Host;

/// <summary>
/// The one log rule for every mode (008 review N3, plan B3): <c>abl daemon run --log-file X</c>
/// rotates X, opens it 0600 and points fds 1 and 2 at it, so the console logger, the runtime
/// and native stderr all land in the file; services pass <c>--log-file</c> too, so
/// <c>abl daemon logs</c> always reads the file. <c>--detached</c> also points fd 0 at
/// <c>/dev/null</c> and changes into the state dir; its <c>setsid()</c> runs just before the
/// redirect, so a refusal still reaches the caller. This runs before
/// the first console write: <see cref="Console.Out"/> duplicates fd 1 when first used.
/// </summary>
/// <remarks>
/// The runtime itself keeps close-on-exec duplicates of the original fds 0–2 from its
/// start-up, which managed code cannot safely close; nothing writes to them afterwards. That
/// is why <c>daemon start</c> gives the child fresh pipes and never waits for their EOF: the
/// caller's own pipes never reach the daemon (AC-17). A non-close-on-exec fd the caller leaks
/// into <c>start</c> is left to the macOS qualification (review L-a, AC-34).
/// </remarks>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static class DaemonLog
{
    /// <summary>A log above this size is rotated at start, keeping one previous file (<c>X.1</c>).</summary>
    public const long RotateBytes = 10 * 1024 * 1024;

    /// <summary>The most lines <c>abl daemon logs</c> prints.</summary>
    public const int MaxTailLines = 1000;

    /// <summary>Rotation first, then open, then the descriptors (review L-b step order).</summary>
    public static void Redirect(string logFile, bool detached, string stateDir) =>
        Redirect(logFile, detached, stateDir, (0, 1, 2), UnixIdentity.Duplicate, Directory.SetCurrentDirectory);

    internal static void Redirect(string logFile, bool detached, string stateDir,
        (int Input, int Output, int Error) targets, Action<SafeFileHandle, int> duplicate, Action<string> changeDirectory)
    {
        using var log = PrivateLogFile.OpenAppend(logFile, RotateBytes);
        using var devNull = detached ? File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read) : null;
        using var savedOut = UnixIdentity.SaveDescriptor(targets.Output);
        using var savedError = UnixIdentity.SaveDescriptor(targets.Error);
        using var savedIn = detached ? UnixIdentity.SaveDescriptor(targets.Input) : null;
        try
        {
            duplicate(log, targets.Output);
            duplicate(log, targets.Error);
            if (detached)
            {
                duplicate(devNull!, targets.Input);
                changeDirectory(stateDir);
            }
        }
        catch
        {
            // Try every restoration even if one fails. Startup still fails; never continue with
            // partial redirection. These close-on-exec saved handles outlive all dup operations.
            try { duplicate(savedOut, targets.Output); }
            finally
            {
                try { duplicate(savedError, targets.Error); }
                finally
                {
                    if (savedIn is not null)
                    {
                        duplicate(savedIn, targets.Input);
                    }
                }
            }

            throw;
        }
    }

    /// <summary>Renames a log over <see cref="RotateBytes"/> to <c>X.1</c> (replacing the previous one).</summary>
    public static void Rotate(string logFile)
    {
        using var log = PrivateLogFile.OpenAppend(logFile, RotateBytes);
    }

    /// <summary>
    /// True when <paramref name="path"/> is a regular file the current user owns: not a
    /// symlink, FIFO, socket or device, so reading it neither follows a planted link nor blocks
    /// (008 T021, 2a round-4 eng-security LOW-2, the reader half).
    /// </summary>
    public static bool IsOwnRegularFile(string path) => ReadProblem(path) is null;

    /// <summary>The existing reader predicate's actionable refusal, without opening the log.</summary>
    internal static string? ReadProblem(string path, IConfigFileSystem? fs = null)
    {
        try
        {
            if (fs is null)
            {
                using var file = PrivateLogFile.OpenRead(path);
                return null;
            }

            var facts = (fs ?? UnixConfigFileSystem.Instance).Inspect(path);
            if (facts is not { TargetExists: true, IsSymlink: false, IsRegularFile: true, OwnedByCurrentUser: true })
            {
                return $"{path} is missing or not a regular file you own; select a regular log file you own without a final symlink";
            }

            return ConfigTrust.AllowAclProblem(facts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"cannot inspect log {path}: {ex.Message}; check its permissions/ACLs or select another log path";
        }
    }

    /// <summary>
    /// The log <c>abl daemon logs</c> reads: the record's <paramref name="recorded"/> log only
    /// when it is <see cref="IsOwnRegularFile">a regular file the user owns</see>, else the
    /// <paramref name="derived"/> per-store log, with a note naming the refused path.
    /// </summary>
    public static (string Path, string? Note) Readable(string? recorded, string derived) =>
        Readable(recorded, derived, null);

    internal static (string Path, string? Note) Readable(string? recorded, string derived, IConfigFileSystem? fs)
    {
        if (recorded is null || recorded == derived || ReadProblem(recorded, fs) is not { } problem)
        {
            return (recorded ?? derived, null);
        }

        return (derived, $"the daemon record names {recorded}: {problem}; reading {derived}");
    }

    /// <summary>The last <paramref name="lines"/> lines of the log, control characters (but tabs) replaced.</summary>
    public static IReadOnlyList<string> Tail(string logFile, int lines) => Tail(logFile, lines, null);

    internal static IReadOnlyList<string> Tail(string logFile, int lines, Action<long>? afterLength)
    {
        const int window = 256 * 1024;
        using var handle = PrivateLogFile.OpenRead(logFile);
        var length = RandomAccess.GetLength(handle);
        afterLength?.Invoke(length);
        var start = Math.Max(0, length - window);
        var buffer = new byte[(int)(length - start)];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = RandomAccess.Read(handle, buffer.AsSpan(read), start + read);
            if (count == 0)
            {
                break; // truncated since the one captured length; growth stays outside this window
            }

            read += count;
        }

        var text = Encoding.UTF8.GetString(buffer.AsSpan(0, read));
        var all = text.Split('\n');
        var skipFirst = start > 0 ? 1 : 0; // the first line of a window may be partial
        var trimmed = all.Length > 0 && all[^1].Length == 0 ? all[..^1] : all;
        return [.. trimmed.Skip(skipFirst).TakeLast(Math.Clamp(lines, 0, MaxTailLines)).Select(Clean)];
    }

    /// <summary>Startup diagnostics must preserve the original error even when its log is unsafe.</summary>
    internal static IReadOnlyList<string> FailureTail(string logFile, int lines)
    {
        try { return Tail(logFile, lines); }
        catch (FileNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [Clean($"log tail refused: {ex.Message}; select a private regular log file in a trusted directory")];
        }
    }

    /// <summary>The line with control characters (but tabs) replaced, safe to print to a terminal.</summary>
    public static string Clean(string line) =>
        new([.. line.TrimEnd('\r').Select(c => char.IsControl(c) && c != '\t' ? '?' : c)]);
}
