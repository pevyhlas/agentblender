using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;

namespace Abl.Cli.Host;

/// <summary>
/// <c>abl setup [--json] [--dry-run] [--reset] [--blender PATH] [--port N] [--store PATH]
/// [--renders PATH] [--runtime-dir PATH]</c> (008 FR-006, plan B2): non-interactive and
/// idempotent. It discovers and validates Blender, resolves and checks the paths, and merges
/// the result into the config file atomically (temp file in the same directory, fsync,
/// rename, 0600; the directory is created 0700). It pins <c>blender.path</c> and the keys given
/// by flag; every other key in the file is kept as written. It never downloads anything (O-8).
/// </summary>
/// <remarks>
/// An existing valid value is kept and a difference from what setup would choose is reported
/// (AC-16); an existing invalid value fails that key with a <c>--blender</c> / <c>--reset</c>
/// remedy and nothing is written. <c>--reset</c> rewrites the file from discovered values,
/// defaults and flags. A second run with the same inputs reports <c>changed: false</c> and
/// leaves the file byte-identical (AC-15): a file is rewritten only when a value changes.
/// Exit codes: 0 ok, 1 failed, 2 usage.
/// </remarks>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static class Setup
{
    /// <summary>The explicit-value flags and the keys they set.</summary>
    public static IReadOnlyDictionary<string, string> Flags { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["--blender"] = AblConfig.BlenderKey,
        ["--port"] = "daemon.port",
        ["--store"] = "paths.store",
        ["--renders"] = "paths.renders",
        ["--runtime-dir"] = "paths.runtime",
    };

    private static readonly Dictionary<string, string> EnvOverrides = new(StringComparer.Ordinal)
    {
        [AblConfig.BlenderKey] = "ABL_BLENDER",
        ["daemon.port"] = "ABL_PORT",
        ["paths.store"] = "ABL_STORE",
        ["paths.renders"] = "ABL_RENDERS",
        ["paths.runtime"] = "ABL_RUNTIME_DIR",
    };

    private const string InvalidRemedy = "abl setup --blender <path>, or abl setup --reset to discover Blender again";

    private const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>One key's outcome: <c>set</c>, <c>unchanged</c>, <c>kept</c> or <c>invalid</c>.</summary>
    private sealed record KeyReport(string Key, string Action, JsonNode? Value, JsonNode? Previous = null, string? Note = null)
    {
        public JsonObject ToJson()
        {
            var json = new JsonObject { ["key"] = Key, ["action"] = Action, ["value"] = Value?.DeepClone() };
            if (Previous is not null)
            {
                json["previous"] = Previous.DeepClone();
            }

            if (Note is not null)
            {
                json["note"] = Note;
            }

            return json;
        }
    }

    /// <summary>Runs setup with <paramref name="inputs"/>, which must carry a Blender search.</summary>
    public static int Run(IReadOnlyList<string> rest, ConfigInputs inputs, TextWriter stdout, TextWriter stderr)
    {
        bool json = false, dryRun = false, reset = false;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--reset":
                    reset = true;
                    break;
                case var flag when Flags.TryGetValue(flag, out var key) && i + 1 < rest.Count:
                    flags[key] = rest[++i];
                    break;
                default:
                    stderr.WriteLine($"abl: setup takes --json, --dry-run, --reset and {string.Join(", ", Flags.Keys.Select(f => f + " VALUE"))} (got '{rest[i]}')");
                    return 2;
            }
        }

        var search = inputs.Blender ?? throw new ArgumentException("setup needs a Blender search (ConfigInputs.WithBlenderSearch)", nameof(inputs));
        var (path, _) = AblConfig.LocateFile(inputs);
        var fs = inputs.FileSystem;
        var facts = fs.Inspect(path);

        // Canonical values: each flag alone, and the file alone (no environment), so neither is
        // confused with what the shell would override.
        var canonical = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, raw) in flags)
        {
            try
            {
                canonical[key] = AblConfig.Resolve(inputs with
                {
                    Env = new Dictionary<string, string>(),
                    Flags = new Dictionary<string, string> { [key] = raw },
                    LoadFile = false,
                    Blender = null,
                })[key].Value;
            }
            catch (ConfigException error)
            {
                stderr.WriteLine($"abl: {error.Message}");
                return 2;
            }
        }

        var fileOnly = inputs with { Env = new Dictionary<string, string> { ["ABL_CONFIG"] = path }, Flags = new Dictionary<string, string>(), Blender = null };
        var written = new Dictionary<string, JsonNode?>(StringComparer.Ordinal); // the file as written (raw nodes)
        var fromFile = new Dictionary<string, JsonNode?>(StringComparer.Ordinal); // canonical file values
        var checks = new List<DoctorCheck>();
        var failed = false;
        if (facts is not null && !reset)
        {
            try
            {
                var resolved = AblConfig.Resolve(fileOnly);
                foreach (var value in resolved.Values.Where(v => v.Source == ConfigSource.File))
                {
                    fromFile[value.Key] = value.Value;
                }

                Flatten(fs.ReadAllText(path), written);
            }
            catch (ConfigException error)
            {
                return Report(stdout, stderr, json, path, dryRun, reset, false, false, [],
                    [DoctorCheck.Fail(error.Check, error.Message, "fix it, or rewrite the file with `abl setup --reset`")], []);
            }
        }
        else if (facts is not null && DirectoryProblem(fs, Path.GetDirectoryName(facts.IsSymlink ? facts.Target : path)!) is { } untrusted)
        {
            return Report(stdout, stderr, json, path, dryRun, reset, false, false, [],
                [DoctorCheck.Fail("config.file", untrusted, "fix the directory's owner or mode, or point ABL_CONFIG at a file you own")], []);
        }

        var previousFile = new Dictionary<string, JsonNode?>(written, StringComparer.Ordinal);
        var keys = new List<KeyReport>();

        // Explicit values other than Blender.
        foreach (var (key, value) in canonical.Where(c => c.Key != AblConfig.BlenderKey).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var previous = fromFile.GetValueOrDefault(key);
            if (JsonNode.DeepEquals(previous, value))
            {
                keys.Add(new KeyReport(key, "unchanged", value));
                continue;
            }

            written[key] = value?.DeepClone();
            keys.Add(new KeyReport(key, "set", value, previous));
        }

        // Blender: the flag, else the file's valid value, else discovery.
        var allow = fromFile.GetValueOrDefault("blender.allowUnsupported")?.GetValue<bool>() ?? false;
        BlenderResolution blender;
        if (canonical.TryGetValue(AblConfig.BlenderKey, out var explicitBlender))
        {
            blender = BlenderDiscovery.ProbeExplicit(explicitBlender!.GetValue<string>(), ConfigSource.Flag, allow, search);
            if (blender.Check is null)
            {
                var previous = fromFile.GetValueOrDefault(AblConfig.BlenderKey);
                if (JsonNode.DeepEquals(previous, explicitBlender))
                {
                    keys.Add(new KeyReport(AblConfig.BlenderKey, "unchanged", explicitBlender));
                }
                else
                {
                    written[AblConfig.BlenderKey] = explicitBlender.DeepClone();
                    keys.Add(new KeyReport(AblConfig.BlenderKey, "set", explicitBlender, previous));
                }
            }
        }
        else if (fromFile.GetValueOrDefault(AblConfig.BlenderKey) is { } existing)
        {
            blender = BlenderDiscovery.ProbeExplicit(existing.GetValue<string>(), ConfigSource.File, allow, search);
            if (blender.Check is not null)
            {
                failed = true;
                keys.Add(new KeyReport(AblConfig.BlenderKey, "invalid", existing, Note: $"{blender.Check}: {blender.Problem}; remedy: {InvalidRemedy}"));
            }
            else
            {
                var discovered = BlenderDiscovery.Discover(allow, search);
                keys.Add(new KeyReport(
                    AblConfig.BlenderKey,
                    "kept",
                    existing,
                    Note: discovered.Path is { } other && other != existing.GetValue<string>() ? $"discovery would choose {other}" : null));
            }
        }
        else
        {
            blender = BlenderDiscovery.Discover(allow, search);
            if (blender.Path is { } chosen && blender.Check is null)
            {
                written[AblConfig.BlenderKey] = chosen;
                keys.Add(new KeyReport(AblConfig.BlenderKey, "set", chosen));
            }
        }

        var (found, version) = HostChecks.Blender(blender);
        checks.Add(blender.Check == "blender.found" && blender.Source == ConfigSource.File
            ? found with { Remedy = InvalidRemedy }
            : found);
        if (version is not null)
        {
            checks.Add(version);
        }

        foreach (var key in fromFile.Keys.Except(keys.Select(k => k.Key)).Where(EnvOverrides.ContainsKey).Order(StringComparer.Ordinal))
        {
            keys.Add(new KeyReport(key, "kept", fromFile[key]));
        }

        var warnings = EnvOverrides
            .Where(o => inputs.Env.GetValueOrDefault(o.Value) is { Length: > 0 })
            .Select(o => $"{o.Value} is set in this shell and overrides {o.Key} from the file here; a service reads the file only")
            .ToList();

        // The paths as the daemon will resolve them with the new file (this shell's env still wins).
        var text = Serialize(written);
        try
        {
            var effective = AblConfig.Resolve(inputs with { FileSystem = new OverlayFileSystem(fs, path, text), Blender = null }).Options;
            checks.Add(HostChecks.RuntimeDir(effective.RuntimeDir, fs));
            checks.Add(HostChecks.SocketPath(effective.RuntimeDir, effective.Platform, fs, probe: false));
            checks.Add(HostChecks.DataDir("paths.store", effective.StoreDir, fs, HostChecks.FreeBytes));
            checks.Add(HostChecks.DataDir("paths.renders", effective.RendersDir, fs, HostChecks.FreeBytes));
        }
        catch (ConfigException error)
        {
            checks.Add(DoctorCheck.Fail(error.Check, error.Message, error.Check == "config.value" ? "fix or unset the variable the message names" : "fix the value"));
        }

        failed |= checks.Any(c => c.Status == CheckStatus.Fail);
        var changed = facts is null ? true : reset ? !SameText(fs, path, written) : !SameValues(previousFile, written);
        var wrote = false;
        if (!failed && changed && !dryRun)
        {
            try
            {
                Write(facts is { IsSymlink: true } link ? link.Target : path, text);
                wrote = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                checks.Add(DoctorCheck.Fail("config.file", $"cannot write {path}: {ex.Message}", "fix the directory's permissions, or point ABL_CONFIG elsewhere"));
                failed = true;
            }
        }

        return Report(stdout, stderr, json, path, dryRun, reset, changed, wrote, keys, checks, warnings, failed);
    }

    private static int Report(
        TextWriter stdout, TextWriter stderr, bool json, string path, bool dryRun, bool reset, bool changed, bool wrote,
        IReadOnlyList<KeyReport> keys, IReadOnlyList<DoctorCheck> checks, IReadOnlyList<string> warnings, bool failed = true)
    {
        failed |= checks.Any(c => c.Status == CheckStatus.Fail);
        keys = [.. keys.OrderBy(k => AblConfig.FileKeys.ToList().IndexOf(k.Key))]; // schema order
        if (json)
        {
            stdout.WriteLine(new JsonObject
            {
                ["tool"] = "abl",
                ["version"] = AblVersion.Current,
                ["file"] = path,
                ["dryRun"] = dryRun,
                ["reset"] = reset,
                ["ok"] = !failed,
                ["changed"] = changed,
                ["written"] = wrote,
                ["keys"] = new JsonArray([.. keys.Select(k => (JsonNode)k.ToJson())]),
                ["checks"] = new JsonArray([.. checks.Select(c => (JsonNode)c.ToJson())]),
                ["warnings"] = new JsonArray([.. warnings.Select(w => (JsonNode)w)]),
            }.ToJsonString());
        }
        else
        {
            var outcome = failed ? "not written (setup failed)"
                : !changed ? "unchanged"
                : dryRun ? "would be written (dry run: nothing written)"
                : "written";
            stdout.WriteLine(DaemonLog.Clean($"config {path}: {outcome}"));
            foreach (var key in keys)
            {
                var value = key.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : key.Value?.ToJsonString() ?? "-";
                var previous = key.Previous is null ? "" : $" (was {(key.Previous is JsonValue p && p.TryGetValue<string>(out var ps) ? ps : key.Previous.ToJsonString())})";
                stdout.WriteLine(DaemonLog.Clean($"  {key.Action,-9} {key.Key,-14} {value}{previous}{(key.Note is null ? "" : "; " + key.Note)}"));
            }

            foreach (var line in new DoctorReport(checks).TextLines())
            {
                stdout.WriteLine("  " + line);
            }
        }

        foreach (var warning in warnings)
        {
            stderr.WriteLine($"abl: warning: {warning}");
        }

        return failed ? 1 : 0;
    }

    /// <summary>The file's keys as written (dotted key → node).</summary>
    private static void Flatten(string text, Dictionary<string, JsonNode?> into)
    {
        if (JsonNode.Parse(text) is not JsonObject root)
        {
            return;
        }

        foreach (var (section, node) in root)
        {
            if (section == "version" || node is not JsonObject inner)
            {
                continue;
            }

            foreach (var (name, value) in inner)
            {
                into[section + "." + name] = value?.DeepClone();
            }
        }
    }

    /// <summary>The file text: <c>version</c> first, then sections and keys in schema order, two-space indent, final newline.</summary>
    internal static string Serialize(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var root = new JsonObject { ["version"] = AblConfig.SupportedVersion };
        foreach (var key in AblConfig.FileKeys.Where(values.ContainsKey))
        {
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            var section = key[..dot];
            if (root[section] is not JsonObject inner)
            {
                root[section] = inner = new JsonObject();
            }

            inner[key[(dot + 1)..]] = values[key]?.DeepClone();
        }

        return root.ToJsonString(Indented) + "\n";
    }

    private static bool SameValues(IReadOnlyDictionary<string, JsonNode?> a, IReadOnlyDictionary<string, JsonNode?> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && JsonNode.DeepEquals(p.Value, other));

    /// <summary>A reset whose result has the same values as the file: no rewrite (AC-15 holds for <c>--reset</c> too).</summary>
    private static bool SameText(IConfigFileSystem fs, string path, IReadOnlyDictionary<string, JsonNode?> values)
    {
        var current = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        try
        {
            Flatten(fs.ReadAllText(path), current);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return SameValues(current, values);
    }

    /// <summary>Temp file in the target's directory (0600, created exclusively), fsync, rename (plan B2).</summary>
    private static void Write(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, PrivateDir);
        }

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = PrivateFile,
            }))
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temp, PrivateFile);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The config directory rule for <c>--reset</c>, which replaces the file without reading it.</summary>
    private static string? DirectoryProblem(IConfigFileSystem fs, string directory)
    {
        var facts = fs.Inspect(directory);
        if (facts is not { TargetExists: true, IsDirectory: true })
        {
            return $"config directory {directory} is not a directory";
        }

        if (!facts.OwnedByCurrentUser)
        {
            return $"config directory {directory} is owned by another user";
        }

        return (facts.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0
            ? $"config directory {directory} is writable by group or others; run: chmod go-w {facts.Target}"
            : null;
    }

    /// <summary>The file system as the daemon will see it after the write: the new text at the config path.</summary>
    private sealed class OverlayFileSystem(IConfigFileSystem inner, string path, string text) : IConfigFileSystem
    {
        private readonly string _directory = Path.GetDirectoryName(path)!;

        public PathFacts? Inspect(string asked) =>
            asked == path ? new PathFacts(asked, false, asked, true, false, PrivateFile, true)
            : inner.Inspect(asked) ?? (asked == _directory ? new PathFacts(asked, false, asked, true, true, PrivateDir, true) : null);

        public string ReadAllText(string asked) => asked == path ? text : inner.ReadAllText(asked);
    }
}
