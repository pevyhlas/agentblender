using System.Security.Cryptography;
using System.Text;

namespace Abl.Core.Lifecycle;

/// <summary>
/// Where the daemon keeps its lifecycle files (008 FR-007, plan B3 and review L-c). The store
/// holds the single-instance lock (<c>daemon.lock</c>) and the instance record
/// (<c>daemon.json</c>), so other CLI calls find the daemon by resolving the store. The state
/// dir (<c>$XDG_STATE_HOME/agentblender</c>, default <c>~/.local/state/agentblender</c>) is the
/// daemon's content root and working directory and holds the log, named per store so two
/// daemons on two stores never share or rotate one log.
/// </summary>
public static class DaemonPaths
{
    /// <summary>The single-instance lock file name inside the store.</summary>
    public const string LockFileName = "daemon.lock";

    /// <summary>The instance record file name inside the store.</summary>
    public const string StateFileName = "daemon.json";

    /// <summary><c>$XDG_STATE_HOME/agentblender</c> (an absolute value only), else <c>~/.local/state/agentblender</c>.</summary>
    public static string StateDir(string home, IReadOnlyDictionary<string, string> env)
    {
        var root = env.TryGetValue("XDG_STATE_HOME", out var xdg) && xdg.Length > 0 && Path.IsPathRooted(xdg)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(xdg))
            : Path.Combine(home, ".local", "state");
        return Path.Combine(root, "agentblender");
    }

    public static string LockFile(string storeDir) => Path.Combine(storeDir, LockFileName);

    public static string StateFile(string storeDir) => Path.Combine(storeDir, StateFileName);

    /// <summary>The daemon's base URL on <paramref name="port"/>: always IPv4 loopback (FR-013).</summary>
    public static string UrlFor(int port) => $"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The store path in the one form that identifies a store: absolute, normalised, without a
    /// trailing separator. Two store paths name the same store when these forms are equal (the
    /// log name below, and the service handoff of <c>start</c>).
    /// </summary>
    public static string StoreIdentity(string storeDir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDir));

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name the same store (<see cref="StoreIdentity"/>).</summary>
    public static bool SameStore(string a, string b) => string.Equals(StoreIdentity(a), StoreIdentity(b), StringComparison.Ordinal);

    /// <summary><c>&lt;state&gt;/daemon-&lt;12 hex of SHA-256(store path)&gt;.log</c>: one log per store (L-c).</summary>
    public static string LogFile(string stateDir, string storeDir)
    {
        var store = StoreIdentity(storeDir);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(store)));
        return Path.Combine(stateDir, $"daemon-{digest[..12]}.log");
    }
}
