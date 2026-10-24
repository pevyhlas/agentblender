using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Abl.Core.Config;

/// <summary>
/// The configuration resolver (008 FR-002, plan A1–A3). Every key resolves to
/// <c>{value, source}</c> by precedence <b>flag &gt; <c>ABL_*</c> env &gt; file &gt;
/// discovered &gt; default</b>. The file is one strict JSON document (<c>version: 1</c>, no
/// comments, no unknown or repeated keys) at <c>$ABL_CONFIG</c>, else
/// <c>$XDG_CONFIG_HOME/agentblender/config.json</c>, else
/// <c>~/.config/agentblender/config.json</c>; it is refused unless the
/// <see cref="ConfigTrust"/> rule holds. The result is deterministic for the same inputs.
/// </summary>
public sealed partial class AblConfig
{
    public const int SupportedVersion = 1;
    public const string TokenKey = "desktop.token";
    public const string BlenderKey = "blender.path";
    public const string DesktopEndpointKey = "desktop.endpoint";

    /// <summary>The <c>desktop.endpoint</c> error on macOS, where the desktop tier is unsupported.</summary>
    public const string DesktopUnsupported = "unsupported on macOS: the desktop tier stays off (the desktop launcher serves a Linux host only)";
    public const string Redacted = "<redacted>";

    /// <summary>Larger files are refused: the schema is a few hundred bytes.</summary>
    public const int MaxFileBytes = 64 * 1024;

    private static readonly string[] LogLevels = ["trace", "debug", "information", "warning", "error", "critical", "none"];

    private static readonly Spec[] Specs =
    [
        new("blender.path", Kind.Path, "ABL_BLENDER", _ => null), // discovered (A4), else unset
        new("blender.allowUnsupported", Kind.Bool, null, _ => false),
        new("daemon.port", Kind.Port, "ABL_PORT", _ => AblDefaults.Port),
        new("daemon.logLevel", Kind.LogLevel, "ABL_LOG_LEVEL", _ => AblDefaults.LogLevel),
        new("paths.store", Kind.Path, "ABL_STORE", d => AblDefaults.StoreDir(d.Home)),
        new("paths.renders", Kind.Path, "ABL_RENDERS", d => AblDefaults.RendersDir(d.Home)),
        new("paths.runtime", Kind.Path, "ABL_RUNTIME_DIR", d => RuntimePaths.Default(
            d.Inputs.Platform, d.Inputs.Home, d.Inputs.UserId, d.Inputs.Env, d.Inputs.FileSystem)),
        new("paths.workerDir", Kind.Path, "ABL_WORKER_DIR", _ => AblDefaults.WorkerDir()),
        new("worker.envPassthrough", Kind.NameList, null, _ => new JsonArray()),
        new("telemetry.enabled", Kind.Bool, "ABL_TELEMETRY", _ => false),
        new("telemetry.otlpEndpoint", Kind.Url, "OTEL_EXPORTER_OTLP_ENDPOINT", _ => AblDefaults.OtlpEndpoint),
        new("telemetry.tracesEndpoint", Kind.Url, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", _ => null),
        new("desktop.endpoint", Kind.Text, "ABL_DESKTOP_ENDPOINT", _ => null),
        new("desktop.tokenFile", Kind.Path, null, _ => null),
    ];

    private readonly SortedDictionary<string, ConfigValue> _values;

    private AblConfig(
        string? filePath,
        ConfigSource fileSource,
        bool fileLoaded,
        SortedDictionary<string, ConfigValue> values,
        BlenderResolution? blender,
        AblOptions options)
    {
        FilePath = filePath;
        FileSource = fileSource;
        FileLoaded = fileLoaded;
        _values = values;
        Blender = blender;
        Options = options;
        EffectiveDigest = ComputeDigest(values.Values);
    }

    private enum Kind
    {
        Path,
        Port,
        Bool,
        LogLevel,
        NameList,
        Url,
        Text,
    }

    /// <summary>Every dotted key the file may set, in schema order.</summary>
    public static IReadOnlyList<string> FileKeys { get; } = [.. Specs.Select(s => s.Key)];

    /// <summary>
    /// The environment variables that name a path (<c>ABL_CONFIG</c> and every path key's
    /// variable). A relative value resolves against the cwd.
    /// </summary>
    public static IReadOnlyList<string> PathVariables { get; } =
        ["ABL_CONFIG", .. Specs.Where(s => s.Kind == Kind.Path && s.EnvVar is not null).Select(s => s.EnvVar!)];

    /// <summary>
    /// The <see cref="PathVariables"/> set in <paramref name="env"/> whose value depends on the
    /// cwd, each made absolute against <paramref name="cwd"/>: the same rule the resolver reads
    /// them by, so a child given these values in another cwd resolves the same paths (2a round-3
    /// eng-arch LOW-3). A value that is rooted, <c>~</c> or starts with <c>~/</c> is left out.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> AbsolutePathVariables(IDictionary<string, string?> env, string cwd) =>
        [.. PathVariables
            .Select(name => (Name: name, Value: env.TryGetValue(name, out var value) ? value : null))
            .Where(v => !string.IsNullOrEmpty(v.Value) && !IsCwdIndependent(v.Value))
            .Select(v => KeyValuePair.Create(v.Name, Path.GetFullPath(v.Value!, cwd)))];

    /// <summary>Where the config file is (or would be); null when file loading was disabled.</summary>
    public string? FilePath { get; }

    /// <summary><c>env</c> when <c>ABL_CONFIG</c> named the file, otherwise <c>default</c>.</summary>
    public ConfigSource FileSource { get; }

    /// <summary>The file existed, passed the trust rule and was parsed.</summary>
    public bool FileLoaded { get; }

    /// <summary>Resolved keys in ordinal key order, including <c>desktop.token</c>.</summary>
    public IReadOnlyCollection<ConfigValue> Values => _values.Values;

    /// <summary>SHA-256 over the canonical resolved map; secrets contribute their source only (plan M6).</summary>
    public string EffectiveDigest { get; }

    /// <summary>The options the daemon runs with.</summary>
    public AblOptions Options { get; }

    /// <summary>
    /// How Blender was chosen and what its probe found (008 FR-003); null when the inputs had no
    /// <see cref="ConfigInputs.Blender"/> search. A failing check is not fatal here: it is
    /// reported (doctor, <c>config show</c>, the daemon log) and the spawn fails with it.
    /// </summary>
    public BlenderResolution? Blender { get; }

    public ConfigValue this[string key] => _values[key];

    /// <summary>Where the config file is looked for, and which tier chose that location.</summary>
    public static (string Path, ConfigSource Source) LocateFile(ConfigInputs inputs)
    {
        if (Lookup(inputs.Env, "ABL_CONFIG") is { } named)
        {
            return (ExpandPath(named, inputs.Home), ConfigSource.Env);
        }

        var configHome = Lookup(inputs.Env, "XDG_CONFIG_HOME") is { } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(inputs.Home, ".config");
        return (Path.GetFullPath(Path.Combine(configHome, "agentblender", "config.json")), ConfigSource.Default);
    }

    /// <summary>Resolves every key from the inputs.</summary>
    /// <exception cref="ConfigException">The file is untrusted or invalid, or a variable or flag is malformed.</exception>
    public static AblConfig Resolve(ConfigInputs inputs)
    {
        foreach (var flag in inputs.Flags.Keys)
        {
            if (!Specs.Any(s => s.Key == flag))
            {
                throw new ArgumentException($"no config key '{flag}'", nameof(inputs));
            }
        }

        string? filePath = null;
        var fileSource = ConfigSource.Default;
        var file = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var fileLoaded = false;
        if (inputs.LoadFile)
        {
            (filePath, fileSource) = LocateFile(inputs);
            fileLoaded = LoadFile(inputs, filePath, file);
        }

        var defaults = new DefaultsContext(inputs);
        var values = new SortedDictionary<string, ConfigValue>(StringComparer.Ordinal);
        foreach (var spec in Specs)
        {
            values[spec.Key] = ResolveKey(spec, inputs, file, filePath, defaults);
        }

        BlenderResolution? blender = null;
        if (inputs.Blender is { } search)
        {
            (values[BlenderKey], blender) = ResolveBlender(values[BlenderKey], values["blender.allowUnsupported"], search);
        }

        var (token, tokenValue) = ResolveToken(inputs, values["desktop.tokenFile"]);
        values[TokenKey] = token;
        if (inputs.Platform == HostPlatform.MacOS && values[DesktopEndpointKey].Value is not null)
        {
            // 008 FR-004 (plan M9b): the desktop launcher (D-15) serves the Linux host only.
            values[DesktopEndpointKey] = values[DesktopEndpointKey] with { Error = DesktopUnsupported };
        }

        return new AblConfig(filePath, fileSource, fileLoaded, values, blender, BuildOptions(values, tokenValue, blender, inputs.Platform));
    }

    /// <summary>
    /// The resolved configuration as JSON: <c>{file, fileLoaded, effectiveDigest, effective}</c>,
    /// with every secret redacted. This is the <c>config</c> object of <c>daemon_status</c>.
    /// </summary>
    public JsonObject ToJson()
    {
        var effective = new JsonObject();
        foreach (var value in _values.Values)
        {
            var entry = new JsonObject
            {
                ["value"] = value.Secret ? (value.Value is null ? null : Redacted) : value.Value?.DeepClone(),
                ["source"] = SourceName(value.Source),
            };
            if (value.File is not null)
            {
                entry["file"] = value.File;
            }

            if (value.Error is not null)
            {
                entry["error"] = value.Error;
            }

            effective[value.Key] = entry;
        }

        var json = new JsonObject
        {
            ["file"] = FilePath,
            ["fileLoaded"] = FileLoaded,
            ["effectiveDigest"] = EffectiveDigest,
            ["effective"] = effective,
        };
        if (Blender is not null)
        {
            json["blender"] = Blender.ToJson();
        }

        return json;
    }

    /// <summary>The lowercase wire name of a source.</summary>
    public static string SourceName(ConfigSource source) => source switch
    {
        ConfigSource.Flag => "flag",
        ConfigSource.Env => "env",
        ConfigSource.File => "file",
        ConfigSource.Discovered => "discovered",
        _ => "default",
    };

    private static bool LoadFile(ConfigInputs inputs, string path, Dictionary<string, JsonElement> file)
    {
        try
        {
            if (inputs.FileSystem.Inspect(path) is null)
            {
                return false; // no file: discovered values and defaults apply (AC-06)
            }

            if (ConfigTrust.CheckConfigFile(inputs.FileSystem, path) is { } distrust)
            {
                throw FileError(path, $"refusing to load: {distrust}",
                    "the config file chooses which programs the daemon runs, so only you may be able to change it");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileError(path, $"cannot inspect: {ex.Message}",
                "check the file and directory permissions/ACLs, or choose another config file with ABL_CONFIG");
        }

        string text;
        try
        {
            text = inputs.FileSystem.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileError(path, $"cannot read: {ex.Message}");
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
        {
            throw FileError(path, $"larger than {MaxFileBytes / 1024} KiB");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 8,
            });
        }
        catch (JsonException ex)
        {
            throw FileError(
                path,
                $"not strict JSON at line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1} " +
                "(comments and trailing commas are not allowed)");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw FileError(path, "the top level must be a JSON object");
            }

            var sections = Specs.Select(s => s.Key[..s.Key.IndexOf('.', StringComparison.Ordinal)]).ToHashSet();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var version = false;
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw FileError(path, $"key '{property.Name}' appears more than once");
                }

                if (property.Name == "version")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var v)
                        || v != SupportedVersion)
                    {
                        throw FileError(path, $"unsupported version {property.Value.GetRawText()} (this abl reads version {SupportedVersion})");
                    }

                    version = true;
                    continue;
                }

                if (!sections.Contains(property.Name))
                {
                    throw FileError(path, $"unknown key '{property.Name}'");
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw FileError(path, $"'{property.Name}' must be an object");
                }

                var inner = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in property.Value.EnumerateObject())
                {
                    var key = property.Name + "." + entry.Name;
                    if (!inner.Add(entry.Name))
                    {
                        throw FileError(path, $"key '{key}' appears more than once");
                    }

                    if (!Specs.Any(s => s.Key == key))
                    {
                        throw FileError(path, $"unknown key '{key}'");
                    }

                    file[key] = entry.Value.Clone();
                }
            }

            if (!version)
            {
                throw FileError(path, $"missing 'version' (expected {SupportedVersion})");
            }
        }

        return true;
    }

    private static ConfigValue ResolveKey(
        Spec spec, ConfigInputs inputs, Dictionary<string, JsonElement> file, string? filePath, DefaultsContext defaults)
    {
        if (inputs.Flags.TryGetValue(spec.Key, out var flag))
        {
            return new ConfigValue(spec.Key, ParseText(spec, flag, inputs.Home, $"flag for {spec.Key}"), ConfigSource.Flag);
        }

        if (spec.EnvVar is not null && Lookup(inputs.Env, spec.EnvVar) is { } env)
        {
            return new ConfigValue(spec.Key, ParseText(spec, env, inputs.Home, spec.EnvVar), ConfigSource.Env);
        }

        if (file.TryGetValue(spec.Key, out var element) && element.ValueKind != JsonValueKind.Null)
        {
            return new ConfigValue(spec.Key, ParseFileValue(spec, element, inputs.Home, filePath!), ConfigSource.File);
        }

        return new ConfigValue(spec.Key, spec.Default(defaults), ConfigSource.Default);
    }

    private static JsonNode? ParseText(Spec spec, string raw, string home, string origin)
    {
        var text = raw.Trim();
        switch (spec.Kind)
        {
            case Kind.Path:
                return ExpandPath(text, home);
            case Kind.Port:
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
                {
                    return port;
                }

                throw ValueError(origin, $"'{text}' is not a TCP port (1-65535)");
            case Kind.Bool:
                return text.ToLowerInvariant() switch
                {
                    "1" or "true" => true,
                    "0" or "false" => false,
                    _ => throw ValueError(origin, $"'{text}' is not a boolean (1, 0, true or false)"),
                };
            case Kind.LogLevel:
                return LogLevels.Contains(text.ToLowerInvariant())
                    ? text.ToLowerInvariant()
                    : throw ValueError(origin, $"'{text}' is not a log level ({string.Join(", ", LogLevels)})");
            case Kind.Url:
                return IsHttpUrl(text) ? text : throw ValueError(origin, "is not an absolute http(s) URL");
            case Kind.Text:
                return text;
            default:
                throw ValueError(origin, "cannot be set from text");
        }
    }

    private static JsonNode? ParseFileValue(Spec spec, JsonElement element, string home, string path)
    {
        JsonNode? Fail(string problem) => throw FileError(path, $"'{spec.Key}' {problem}");
        switch (spec.Kind)
        {
            case Kind.Path:
                if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 } p)
                {
                    return Fail("must be a path string");
                }

                if (!IsCwdIndependent(p))
                {
                    return Fail("must be an absolute path or start with ~/");
                }

                return ExpandPath(p, home);
            case Kind.Port:
                return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var port) && port is >= 1 and <= 65535
                    ? port
                    : Fail("must be an integer TCP port (1-65535)");
            case Kind.Bool:
                return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? element.GetBoolean()
                    : Fail("must be true or false");
            case Kind.LogLevel:
                return element.ValueKind == JsonValueKind.String && LogLevels.Contains(element.GetString())
                    ? element.GetString()
                    : Fail($"must be one of {string.Join(", ", LogLevels)}");
            case Kind.NameList:
                if (element.ValueKind != JsonValueKind.Array)
                {
                    return Fail("must be an array of environment variable names");
                }

                var names = new JsonArray();
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name || !EnvName().IsMatch(name))
                    {
                        return Fail("must contain only environment variable names ([A-Za-z_][A-Za-z0-9_]*)");
                    }

                    if (name.StartsWith("PYTHON", StringComparison.Ordinal))
                    {
                        return Fail($"cannot pass {name}: PYTHON* variables never reach workers");
                    }

                    if (!unique.Add(name))
                    {
                        return Fail($"lists {name} twice");
                    }

                    names.Add(name);
                }

                return names;
            case Kind.Url:
                return element.ValueKind == JsonValueKind.String && IsHttpUrl(element.GetString()!)
                    ? element.GetString()
                    : Fail("must be an absolute http(s) URL");
            case Kind.Text:
                return element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text
                    ? text
                    : Fail("must be a non-empty string");
            default:
                return Fail("is not supported");
        }
    }

    /// <summary>
    /// An explicit <c>blender.path</c> is probed and never falls through to discovery; an unset
    /// one is discovered (source <c>discovered</c>). A problem stays on the key as its error.
    /// </summary>
    private static (ConfigValue Value, BlenderResolution Resolution) ResolveBlender(
        ConfigValue configured, ConfigValue allowUnsupported, BlenderSearch search)
    {
        var allow = allowUnsupported.Value!.GetValue<bool>();
        var resolution = configured.Value?.GetValue<string>() is { } path
            ? BlenderDiscovery.ProbeExplicit(path, configured.Source, allow, search)
            : BlenderDiscovery.Discover(allow, search);
        var value = resolution.Path is { } chosen && resolution.Source == ConfigSource.Discovered
            ? new ConfigValue(BlenderKey, chosen, ConfigSource.Discovered)
            : configured;
        return (value with { Error = resolution.Check is null ? null : $"{resolution.Check}: {resolution.Problem}" }, resolution);
    }

    /// <summary>
    /// <c>ABL_DESKTOP_TOKEN</c> wins over <c>desktop.tokenFile</c> (plan L6). A token file that
    /// fails the trust rule refuses the desktop tier (the value stays null, with the reason)
    /// rather than the whole daemon, which keeps rendering locally.
    /// </summary>
    private static (ConfigValue Value, string? Token) ResolveToken(ConfigInputs inputs, ConfigValue tokenFile)
    {
        if (Lookup(inputs.Env, "ABL_DESKTOP_TOKEN") is { } envToken)
        {
            return (new ConfigValue(TokenKey, Redacted, ConfigSource.Env) { Secret = true }, envToken);
        }

        if (tokenFile.Value?.GetValue<string>() is not { } path)
        {
            return (new ConfigValue(TokenKey, null, ConfigSource.Default) { Secret = true }, null);
        }

        var problem = ConfigTrust.CheckTokenFile(inputs.FileSystem, path);
        string? token = null;
        if (problem is null)
        {
            try
            {
                token = inputs.FileSystem.ReadAllText(path).Trim();
                if (token.Length == 0)
                {
                    problem = $"token file {path} is empty";
                    token = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"cannot read token file {path}: {ex.Message}";
            }
        }

        var value = new ConfigValue(TokenKey, token is null ? null : Redacted, tokenFile.Source)
        {
            Secret = true,
            File = path,
            Error = problem is null ? null : problem + "; the desktop tier stays off",
        };
        return (value, token);
    }

    private static AblOptions BuildOptions(
        SortedDictionary<string, ConfigValue> v, string? token, BlenderResolution? blender, HostPlatform platform)
    {
        string Str(string key) => v[key].Value!.GetValue<string>();
        string? OptionalStr(string key) => v[key].Value?.GetValue<string>();
        bool Bool(string key) => v[key].Value!.GetValue<bool>();
        return new AblOptions
        {
            BlenderPath = OptionalStr(BlenderKey),
            BlenderProblem = blender is { Check: { } check }
                ? new CheckFailure(check, blender.Problem ?? check, blender.Hint)
                : null,
            ProbedBlenderVersion = blender?.Version,
            Platform = platform,
            AllowUnsupportedBlender = Bool("blender.allowUnsupported"),
            Port = v["daemon.port"].Value!.GetValue<int>(),
            LogLevel = Str("daemon.logLevel"),
            StoreDir = Str("paths.store"),
            RendersDir = Str("paths.renders"),
            RuntimeDir = Str("paths.runtime"),
            WorkerDir = Str("paths.workerDir"),
            WorkerEnvPassthrough = [.. v["worker.envPassthrough"].Value!.AsArray().Select(n => n!.GetValue<string>())],
            TelemetryEnabled = Bool("telemetry.enabled"),
            OtlpEndpoint = Str("telemetry.otlpEndpoint"),
            TracesEndpoint = OptionalStr("telemetry.tracesEndpoint"),
            DesktopEndpoint = platform == HostPlatform.MacOS ? null : OptionalStr(DesktopEndpointKey),
            DesktopToken = platform == HostPlatform.MacOS ? null : token,
        };
    }

    private static string ComputeDigest(IEnumerable<ConfigValue> values)
    {
        var canonical = new JsonObject();
        foreach (var value in values)
        {
            canonical[value.Key] = value.Secret
                ? new JsonObject { ["source"] = SourceName(value.Source), ["file"] = value.File, ["set"] = value.Value is not null }
                : value.Value?.DeepClone();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString()));
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>A path that <see cref="ExpandPath"/> resolves without the cwd: rooted, <c>~</c> or <c>~/…</c>.</summary>
    private static bool IsCwdIndependent(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || Path.IsPathRooted(path);

    /// <summary>Expands <c>~</c> and makes the path absolute (relative to the cwd for env and flags).</summary>
    private static string ExpandPath(string path, string home)
    {
        var expanded = path == "~" ? home
            : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..])
            : path;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)) is { Length: > 0 } full ? full : "/";
    }

    private static bool IsHttpUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string? Lookup(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    private static ConfigException FileError(string path, string problem, string? hint = null) =>
        new("config.file", $"config file {path}: {problem}", hint);

    private static ConfigException ValueError(string origin, string problem) =>
        new("config.value", $"{origin} {problem}");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvName();

    private sealed record Spec(string Key, Kind Kind, string? EnvVar, Func<DefaultsContext, JsonNode?> Default);

    private sealed record DefaultsContext(ConfigInputs Inputs)
    {
        public string Home => Inputs.Home;
    }
}
