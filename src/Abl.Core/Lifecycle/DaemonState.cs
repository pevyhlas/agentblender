using System.Text.Json;
using System.Text.Json.Serialization;

namespace Abl.Core.Lifecycle;

/// <summary>
/// The instance record <c>&lt;store&gt;/daemon.json</c> (008 FR-007, plan M2), written 0600
/// by the daemon as soon as it holds the store lock. It names the instance for <c>start</c>,
/// <c>stop</c>, <c>status</c> and <c>logs</c>; it is kept after the daemon exits so
/// <c>status</c> can report <c>lastPid</c>. Liveness is never read from the file alone: the
/// CLI confirms <see cref="Pid"/> against <c>/healthz</c>.
/// </summary>
/// <param name="Pid">The daemon's process id.</param>
/// <param name="Port">The loopback port.</param>
/// <param name="Url">The base URL (<c>http://127.0.0.1:&lt;port&gt;</c>).</param>
/// <param name="Version">The daemon's version.</param>
/// <param name="StartedAt">When the daemon took the lock.</param>
/// <param name="EffectiveConfigDigest">The resolver's digest of the effective configuration.</param>
/// <param name="LogFile">The log the daemon writes to, or null when it logs to its console.</param>
/// <param name="ProcessGroup">The daemon's own process group (its pid after <c>setsid</c>), or null when it shares one.</param>
public sealed record DaemonState(
    int Pid,
    int Port,
    string Url,
    string Version,
    DateTimeOffset StartedAt,
    string EffectiveConfigDigest,
    string? LogFile,
    int? ProcessGroup)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The largest record <see cref="Read"/> accepts; a real one is a few hundred bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// The record in <paramref name="storeDir"/>, or null when it is missing, unreadable, a
    /// symlink or larger than <see cref="MaxBytes"/> (2a round-2 eng-security LOW-1: the read is
    /// bounded and never follows a link planted at the record's path).
    /// </summary>
    public static DaemonState? Read(string storeDir)
    {
        var path = DaemonPaths.StateFile(storeDir);
        try
        {
            if (new FileInfo(path) is not { Exists: true, LinkTarget: null, Length: <= MaxBytes })
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[MaxBytes + 1];
            var length = 0;
            while (length < buffer.Length && stream.Read(buffer, length, buffer.Length - length) is > 0 and var read)
            {
                length += read;
            }

            return length > MaxBytes ? null : JsonSerializer.Deserialize<DaemonState>(buffer.AsSpan(0, length), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the record atomically (temp file in the store, 0600, rename).</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Write(string storeDir)
    {
        var path = DaemonPaths.StateFile(storeDir);
        var temp = $"{path}.tmp-{Environment.ProcessId}";

        // A stale temp file (or a symlink planted there) is removed, never written through.
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            JsonSerializer.Serialize(stream, this, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
