namespace Abl.Core.Lifecycle;

/// <summary>
/// The single-instance guarantee (008 FR-007, plan M2): the daemon holds an exclusive lock on
/// <c>&lt;store&gt;/daemon.lock</c> for its lifetime, so two daemons on one store are impossible
/// whatever port or runtime dir each is given. .NET takes an advisory <c>flock</c> for
/// <see cref="FileShare.None"/> on Unix; the descriptor is close-on-exec, so workers never
/// inherit it, and the kernel drops it when the daemon dies however it dies.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class StoreLock : IDisposable
{
    private readonly FileStream _stream;

    private StoreLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>The lock file.</summary>
    public string Path { get; }

    /// <summary>
    /// Takes the lock, retrying for <paramref name="retry"/> (about 2 s, review L-d) so a
    /// preflight probe that holds it for a moment does not fail a daemon starting alongside.
    /// </summary>
    /// <returns>The held lock, or null when another process still holds it.</returns>
    public static StoreLock? TryAcquire(string storeDir, TimeSpan retry)
    {
        var path = DaemonPaths.LockFile(storeDir);
        var deadline = DateTime.UtcNow + retry;
        while (true)
        {
            if (TryOpen(path, exclusive: true) is { } stream)
            {
                return new StoreLock(path, stream);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// True when another process holds the store lock. The probe takes a shared lock for a
    /// moment (<see cref="FileShare.Read"/> on a read-only open is <c>LOCK_SH</c>), so two
    /// probes never see each other as the holder, while a daemon's exclusive lock still
    /// conflicts (2a round-2 eng-code L-1). A daemon that starts during a probe retries.
    /// </summary>
    public static bool IsHeld(string storeDir)
    {
        using var stream = TryOpen(DaemonPaths.LockFile(storeDir), exclusive: false);
        return stream is null;
    }

    public void Dispose() => _stream.Dispose();

    private static FileStream? TryOpen(string path, bool exclusive)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                Share = exclusive ? FileShare.None : FileShare.Read,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch (IOException) when (File.Exists(path))
        {
            return null; // held: .NET reports the flock conflict as an IOException
        }
    }
}
