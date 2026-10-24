using System.Globalization;
using System.Text;

namespace Abl.Core.Config;

/// <summary>
/// The worker socket rendezvous directory (008 FR-005, plan A5): user-owned first, the shared
/// <c>/tmp</c> last. The default is <c>$XDG_RUNTIME_DIR/agentblender</c> (Linux, when the user
/// owns that dir), then <c>$XDG_STATE_HOME/agentblender/run</c>, and only when both would make
/// a socket path too long, <c>/tmp/abl-&lt;uid&gt;</c> (<c>/private/tmp</c> on macOS). Before
/// every spawn the final component must be a real directory (not a symlink), owned by the
/// user, mode 0700, and the longest socket path must fit <c>sun_path</c>. Symlinks in parent
/// components (<c>/tmp → /private/tmp</c>) are allowed.
/// </summary>
public static class RuntimePaths
{
    /// <summary><c>sizeof(sockaddr_un.sun_path)</c> on macOS.</summary>
    public const int MacSunPath = 104;

    /// <summary><c>sizeof(sockaddr_un.sun_path)</c> on Linux.</summary>
    public const int LinuxSunPath = 108;

    /// <summary>The longest socket file name the supervisor creates (<see cref="SocketFileName"/>).</summary>
    public const string LongestSocketName = "w-00000000.sock";

    /// <summary>The only mode a runtime dir may have.</summary>
    public const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The mode of a bound socket file.</summary>
    public const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode PermissionBits = (UnixFileMode)0b111_111_111;

    /// <summary>The socket file name for a worker id (8 hex characters).</summary>
    public static string SocketFileName(string id) => $"w-{id}.sock";

    /// <summary>
    /// True when <paramref name="name"/> is a file name <see cref="SocketFileName"/> makes for a
    /// worker id (<c>w-</c>, 8 lowercase hex characters, <c>.sock</c>): its inverse, so a reader of
    /// worker sockets (the <c>stop</c> sweep) follows this one rule (2a round-3 eng-arch LOW-3).
    /// </summary>
    public static bool IsSocketFileName(string name) =>
        name.Length == LongestSocketName.Length
        && name.StartsWith("w-", StringComparison.Ordinal)
        && name.EndsWith(".sock", StringComparison.Ordinal)
        && name[2..^5].All(char.IsAsciiHexDigitLower);

    /// <summary>The longest path a socket may have: <c>sun_path</c> minus the NUL byte (103 / 107).</summary>
    public static int MaxSocketPathBytes(HostPlatform platform) =>
        (platform == HostPlatform.MacOS ? MacSunPath : LinuxSunPath) - 1;

    /// <summary>The byte length of the longest socket path under <paramref name="runtimeDir"/>.</summary>
    public static int LongestSocketPathBytes(string runtimeDir) =>
        Encoding.UTF8.GetByteCount(Path.Combine(runtimeDir, LongestSocketName));

    /// <summary>Null when every socket path under the dir fits, otherwise the <c>runtime.socket-path</c> reason.</summary>
    public static string? CheckSocketLength(string runtimeDir, HostPlatform platform)
    {
        var length = LongestSocketPathBytes(runtimeDir);
        var limit = MaxSocketPathBytes(platform);
        return length <= limit
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"socket path {Path.Combine(runtimeDir, LongestSocketName)} is {length} bytes; the " +
                $"{(platform == HostPlatform.MacOS ? "macOS" : "Linux")} limit is {limit} (sun_path {limit + 1} minus the NUL byte)");
    }

    /// <summary>The default runtime dir (plan A5 order), chosen without creating anything.</summary>
    public static string Default(
        HostPlatform platform, string home, uint userId, IReadOnlyDictionary<string, string> env, IConfigFileSystem fs)
    {
        if (platform == HostPlatform.Linux
            && Lookup(env, "XDG_RUNTIME_DIR") is { } xdgRuntime
            && Path.IsPathRooted(xdgRuntime)
            && fs.Inspect(Path.TrimEndingDirectorySeparator(xdgRuntime)) is { TargetExists: true, IsDirectory: true, OwnedByCurrentUser: true })
        {
            var runtime = Path.Combine(Normalize(xdgRuntime), "agentblender");
            if (CheckSocketLength(runtime, platform) is null)
            {
                return runtime;
            }
        }

        var state = Path.Combine(Lifecycle.DaemonPaths.StateDir(home, env), "run");
        if (CheckSocketLength(state, platform) is null)
        {
            return state;
        }

        return Fallback(platform, userId);
    }

    /// <summary><c>$XDG_STATE_HOME/agentblender/run</c> (default <c>~/.local/state/agentblender/run</c>).</summary>
    public static string StateRunDir(string home, string? xdgStateHome) =>
        Path.Combine(Lifecycle.DaemonPaths.StateDir(home,
            new Dictionary<string, string> { ["XDG_STATE_HOME"] = xdgStateHome ?? "" }), "run");

    /// <summary>The last-resort dir, used only when the user-owned ones are too long.</summary>
    public static string Fallback(HostPlatform platform, uint userId) =>
        (platform == HostPlatform.MacOS ? "/private/tmp/abl-" : "/tmp/abl-") + userId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The per-spawn check (AC-12, AC-13): the socket length first (nothing is created for a dir
    /// that cannot work), then the dir is created 0700 when missing, and its final component
    /// must be a real directory owned by the user with mode 0700.
    /// </summary>
    /// <exception cref="ConfigException"><c>runtime.socket-path</c> or <c>runtime.dir</c>.</exception>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public static void Prepare(string runtimeDir, HostPlatform platform, IConfigFileSystem fs)
    {
        if (CheckSocketLength(runtimeDir, platform) is { } tooLong)
        {
            throw new ConfigException("runtime.socket-path", tooLong, "set a shorter paths.runtime (ABL_RUNTIME_DIR)");
        }

        var hint = $"remove {runtimeDir} and retry, or set paths.runtime (ABL_RUNTIME_DIR) to a private directory";
        string? problem;
        try
        {
            if (fs.Inspect(runtimeDir) is null)
            {
                Directory.CreateDirectory(runtimeDir, DirMode);

                // The umask may have cleared bits (chmod never widens past 0700). chmod follows
                // links, so it runs only on a real, owned directory: a symlink planted between
                // the check and the create is left alone for CheckDir to refuse.
                if (fs.Inspect(runtimeDir) is { IsSymlink: false, IsDirectory: true, OwnedByCurrentUser: true, HasAllowAcl: false })
                {
                    File.SetUnixFileMode(runtimeDir, DirMode);
                }
            }

            problem = CheckDir(runtimeDir, fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A parent that cannot be created or traversed still names the check (T036, T051).
            problem = $"cannot prepare runtime dir {runtimeDir}: {ex.Message}";
        }

        if (problem is not null)
        {
            throw new ConfigException("runtime.dir", problem, hint);
        }
    }

    /// <summary>Null when the final component is a usable runtime dir, otherwise the <c>runtime.dir</c> reason.</summary>
    public static string? CheckDir(string runtimeDir, IConfigFileSystem fs, string kind = "runtime")
    {
        var facts = fs.Inspect(runtimeDir);
        if (facts is null)
        {
            return $"{kind} dir {runtimeDir} does not exist";
        }

        if (facts.IsSymlink)
        {
            return $"{kind} dir {runtimeDir} is a symlink (to {facts.Target}); its final component must be a real directory";
        }

        if (!facts.TargetExists || !facts.IsDirectory)
        {
            return $"{kind} dir {runtimeDir} is not a directory";
        }

        if (!facts.OwnedByCurrentUser)
        {
            return $"{kind} dir {runtimeDir} is owned by another user";
        }

        if ((facts.Mode & PermissionBits) != DirMode)
        {
            return $"{kind} dir {runtimeDir} has mode {Convert.ToString((int)(facts.Mode & PermissionBits), 8).PadLeft(4, '0')}; it must be 0700";
        }

        return ConfigTrust.AllowAclProblem(facts);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) is { Length: > 0 } full ? full : "/";

    private static string? Lookup(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}
