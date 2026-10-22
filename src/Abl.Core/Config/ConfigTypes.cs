using System.Collections;
using System.Text.Json.Nodes;

namespace Abl.Core.Config;

/// <summary>Where a resolved key's value came from (008 FR-002, precedence order).</summary>
public enum ConfigSource
{
    Flag,
    Env,
    File,
    Discovered,
    Default,
}

/// <summary>One resolved key: its value and the source that won.</summary>
/// <param name="Key">Dotted key, e.g. <c>daemon.port</c>.</param>
/// <param name="Value">The canonical value (absolute path, number, boolean, list or null).</param>
/// <param name="Source">The precedence tier that supplied it.</param>
public sealed record ConfigValue(string Key, JsonNode? Value, ConfigSource Source)
{
    /// <summary>The file a secret was read from (<c>desktop.token</c> only).</summary>
    public string? File { get; init; }

    /// <summary>Why a value was refused while the rest of the config stays usable (token-file rule).</summary>
    public string? Error { get; init; }

    /// <summary>The value is a secret: never printed, never part of the digest.</summary>
    public bool Secret { get; init; }
}

/// <summary>
/// A configuration problem the daemon and CLI refuse to run with. <see cref="Check"/> is the
/// stable doctor check ID it maps to (<c>config.file</c> for the file; <c>config.value</c> for
/// a bad <c>ABL_*</c> variable or flag).
/// </summary>
public sealed class ConfigException(string check, string message, string? hint = null) : Exception(message)
{
    public string Check { get; } = check;

    public string? Hint { get; } = hint;
}

/// <summary>
/// A failing check carried as data (008 FR-003): the resolver's Blender verdict travels on
/// <see cref="AblOptions"/> so every spawn refuses with the same check ID, problem and remedy.
/// </summary>
/// <param name="Check">The stable doctor check ID (<c>blender.found</c>, <c>blender.version</c>).</param>
/// <param name="Problem">What is wrong.</param>
/// <param name="Hint">The remedy.</param>
public sealed record CheckFailure(string Check, string Problem, string? Hint)
{
    public ConfigException ToException() => new(Check, Problem, Hint);
}

/// <summary>
/// Everything the resolver reads, injected so tests are hermetic (plan M10): no test touches
/// the real <c>~/.config/agentblender</c>.
/// </summary>
public sealed record ConfigInputs
{
    /// <summary>The environment (only <c>ABL_*</c>, <c>OTEL_*</c> and <c>XDG_*</c>/<c>TMPDIR</c> are read).</summary>
    public required IReadOnlyDictionary<string, string> Env { get; init; }

    /// <summary>The user's home directory (expands <c>~</c>, anchors the defaults).</summary>
    public required string Home { get; init; }

    /// <summary>Command-line overrides by dotted key (<c>blender.path</c> → raw text), the top tier.</summary>
    public IReadOnlyDictionary<string, string> Flags { get; init; } = new Dictionary<string, string>();

    /// <summary>File-system access for the config file, its trust rule and the token file.</summary>
    public IConfigFileSystem FileSystem { get; init; } = UnixConfigFileSystem.Instance;

    /// <summary>False skips the config file entirely (tests that pin env-only inputs).</summary>
    public bool LoadFile { get; init; } = true;

    /// <summary>Picks the Blender search order, the socket limit and the <c>/tmp</c> fallback.</summary>
    public HostPlatform Platform { get; init; } = HostPlatform.Linux;

    /// <summary>The effective uid (names the <c>/tmp/abl-&lt;uid&gt;</c> fallback runtime dir).</summary>
    public uint UserId { get; init; }

    /// <summary>
    /// Where to look for Blender. Null (the default) neither probes an explicit path nor
    /// discovers one, so a CLI call that only needs the port never runs Blender; the daemon
    /// and <c>abl config show</c> pass a search (<see cref="WithBlenderSearch"/>).
    /// </summary>
    public BlenderSearch? Blender { get; init; }

    /// <summary>The current process: its environment, home directory, platform and uid.</summary>
    public static ConfigInputs FromProcess() => new()
    {
        Env = ProcessEnvironment(),
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        LoadFile = !OperatingSystem.IsWindows(), // the trust rule needs Unix ownership and modes
        Platform = OperatingSystem.IsMacOS() ? HostPlatform.MacOS : HostPlatform.Linux,
        UserId = OperatingSystem.IsWindows() ? 0 : UnixIdentity.EffectiveUserId,
    };

    /// <summary>These inputs plus the real Blender search for their platform, home and <c>PATH</c>.</summary>
    public ConfigInputs WithBlenderSearch() => this with { Blender = BlenderSearch.ForProcess(Platform, Home, Env) };

    private static Dictionary<string, string> ProcessEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                env[name] = value;
            }
        }

        return env;
    }
}
