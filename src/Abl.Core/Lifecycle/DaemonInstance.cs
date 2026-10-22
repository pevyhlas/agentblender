using System.Globalization;

namespace Abl.Core.Lifecycle;

/// <summary>
/// The running daemon's identity (008 FR-007, FR-010; 2a eng-arch LOW-2, LOW-3): built once
/// by <c>abl daemon run</c> when it holds the store lock, and the single source of the
/// instance record (<see cref="ToState"/>), <c>/healthz</c> and <c>daemon_status</c>.
/// </summary>
/// <param name="Pid">This process.</param>
/// <param name="StartedAt">When the store lock was taken.</param>
/// <param name="StateDir">The content root and working directory (008 FR-007).</param>
/// <param name="LogFile">The log fds 1/2 point at, or null when the daemon logs to its console.</param>
/// <param name="Port">The loopback port.</param>
/// <param name="EffectiveConfigDigest">The resolver's digest of the configuration it serves.</param>
/// <param name="Detached">True after <c>setsid</c>: the daemon leads its own process group.</param>
public sealed record DaemonInstance(
    int Pid,
    DateTimeOffset StartedAt,
    string StateDir,
    string? LogFile,
    int Port,
    string EffectiveConfigDigest,
    bool Detached)
{
    /// <summary>The daemon's version.</summary>
    public string Version => AblVersion.Current;

    /// <summary>The base URL, always loopback (FR-013).</summary>
    public string Url => DaemonPaths.UrlFor(Port);

    /// <summary>The instance record <c>daemon.json</c> for this daemon.</summary>
    public DaemonState ToState() =>
        new(Pid, Port, Url, Version, StartedAt, EffectiveConfigDigest, LogFile, Detached ? Pid : null);

    /// <summary><see cref="StartedAt"/> as written on every surface (round-trip format).</summary>
    public string StartedAtText => StartedAt.ToString("O", CultureInfo.InvariantCulture);
}
