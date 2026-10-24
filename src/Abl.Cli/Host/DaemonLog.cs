using System.Text;
using Abl.Core.Config;

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

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Rotation first, then open, then the descriptors (review L-b step order).</summary>
    public static void Redirect(string logFile, bool detached, string stateDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!, PrivateDir);
        Rotate(logFile);
        using (var log = new FileStream(logFile, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            UnixCreateMode = PrivateFile,
        }))
        {
            // The create mode covers a new log only; an existing, wider one is narrowed too.
            File.SetUnixFileMode(log.SafeFileHandle, PrivateFile);
            UnixIdentity.Duplicate(log.SafeFileHandle, 1);
            UnixIdentity.Duplicate(log.SafeFileHandle, 2);
        }

        if (!detached)
        {
            return;
        }

        using (var devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read))
        {
            UnixIdentity.Duplicate(devNull, 0);
        }

        Directory.SetCurrentDirectory(stateDir);
    }

    /// <summary>Renames a log over <see cref="RotateBytes"/> to <c>X.1</c> (replacing the previous one).</summary>
    public static void Rotate(string logFile)
    {
        var info = new FileInfo(logFile);
        if (info.Exists && info.Length > RotateBytes)
        {
            File.Move(logFile, logFile + ".1", overwrite: true);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a regular file the current user owns: not a
    /// symlink, FIFO, socket or device, so reading it neither follows a planted link nor blocks
    /// (008 T021, 2a round-4 eng-security LOW-2, the reader half).
    /// </summary>
    public static bool IsOwnRegularFile(string path)
    {
        try
        {
            return UnixConfigFileSystem.Instance.Inspect(path) is
                { TargetExists: true, IsSymlink: false, IsRegularFile: true, OwnedByCurrentUser: true, HasAllowAcl: false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The log <c>abl daemon logs</c> reads: the record's <paramref name="recorded"/> log only
    /// when it is <see cref="IsOwnRegularFile">a regular file the user owns</see>, else the
    /// <paramref name="derived"/> per-store log, with a note naming the refused path.
    /// </summary>
    public static (string Path, string? Note) Readable(string? recorded, string derived) =>
        recorded is null || recorded == derived || IsOwnRegularFile(recorded)
            ? (recorded ?? derived, null)
            : (derived, $"the daemon record names {recorded}, which is missing or not a regular file you own; reading {derived}");

    /// <summary>The last <paramref name="lines"/> lines of the log, control characters (but tabs) replaced.</summary>
    public static IReadOnlyList<string> Tail(string logFile, int lines)
    {
        const int window = 256 * 1024;
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - window);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer);
        var all = text.Split('\n');
        var skipFirst = start > 0 ? 1 : 0; // the first line of a window may be partial
        var trimmed = all.Length > 0 && all[^1].Length == 0 ? all[..^1] : all;
        return [.. trimmed.Skip(skipFirst).TakeLast(lines).Select(Clean)];
    }

    /// <summary>The line with control characters (but tabs) replaced, safe to print to a terminal.</summary>
    public static string Clean(string line) =>
        new([.. line.TrimEnd('\r').Select(c => char.IsControl(c) && c != '\t' ? '?' : c)]);
}
