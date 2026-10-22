using System.Text.Json.Nodes;
using Abl.Core.Config;

namespace Abl.Cli;

/// <summary>
/// Host-only verbs (008 plan B1): they act on this machine (configuration, setup, doctor, the
/// daemon lifecycle and its user service, the MCP client snippet) and must work while the
/// daemon is down, and they have no MCP tool. They may read through <c>/api/ops</c> when a daemon
/// answers, only the <see cref="ReadOps"/> (for example <c>daemon stop</c> lists sessions),
/// and never mutate through it. Disjoint from <see cref="VerbMap"/>, which maps op verbs
/// one-to-one onto the public op surface (<c>abl daemon status</c> is the CLI form of the
/// <c>daemon_status</c> op); contract tests enforce both.
/// </summary>
public static class HostVerbs
{
    public static readonly IReadOnlySet<(string Noun, string Verb)> Verbs = new HashSet<(string, string)>
    {
        ("config", "show"),
        ("config", "path"),
        ("daemon", "run"),
        ("daemon", "start"),
        ("daemon", "stop"),
        ("daemon", "logs"),
        ("daemon", "install-service"),
        ("daemon", "uninstall-service"),
        ("mcp", "config"),
    };

    /// <summary>The one-word host commands: <c>abl setup</c> (FR-006) and <c>abl doctor</c> (FR-009).</summary>
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal) { "setup", "doctor" };

    /// <summary>The only ops a host verb may post to <c>/api/ops</c>: they read and never mutate (plan B1).</summary>
    public static readonly IReadOnlySet<string> ReadOps = new HashSet<string>(StringComparer.Ordinal)
    {
        "session_list",
        "daemon_status",
    };

    /// <summary>
    /// Runs a <c>config</c> or <c>mcp</c> host verb. Exit codes: 0 ok, 1 failed, 2 usage. The
    /// <c>daemon</c> verbs run through <see cref="Host.DaemonVerbs"/>, which must reach the
    /// console lazily.
    /// </summary>
    public static int Run(
        (string Noun, string Verb) verb, IReadOnlyList<string> rest, ConfigInputs inputs, TextWriter stdout, TextWriter stderr)
    {
        if (verb == ("mcp", "config"))
        {
            return Host.McpConfig.Run(rest, inputs, stdout, stderr);
        }

        var json = false;
        foreach (var arg in rest)
        {
            if (arg == "--json")
            {
                json = true;
                continue;
            }

            stderr.WriteLine($"abl: {verb.Noun} {verb.Verb} takes only --json (got '{arg}')");
            return 2;
        }

        return verb switch
        {
            ("config", "show") => ConfigShow(inputs, json, stdout, stderr),
            ("config", "path") => ConfigPath(inputs, json, stdout),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "not a host verb"),
        };
    }

    /// <summary><c>abl config show</c>: every effective key with its source; secrets redacted (FR-002).</summary>
    private static int ConfigShow(ConfigInputs inputs, bool json, TextWriter stdout, TextWriter stderr)
    {
        AblConfig config;
        try
        {
            config = AblConfig.Resolve(inputs);
        }
        catch (ConfigException error)
        {
            if (json)
            {
                stdout.WriteLine(new JsonObject
                {
                    ["error"] = new JsonObject { ["check"] = error.Check, ["message"] = error.Message, ["hint"] = error.Hint },
                }.ToJsonString());
            }

            WriteError(stderr, error);
            return 1;
        }

        if (json)
        {
            stdout.WriteLine(config.ToJson().ToJsonString());
            return 0;
        }

        stdout.WriteLine($"config {config.FilePath ?? "(not read)"} ({(config.FileLoaded ? "loaded" : "not present")}, " +
            $"location from {AblConfig.SourceName(config.FileSource)})");
        var width = config.Values.Max(v => v.Key.Length) + 2;
        foreach (var value in config.Values)
        {
            var shown = value.Value is null ? "-"
                : value.Secret ? AblConfig.Redacted
                : value.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text
                : value.Value.ToJsonString();
            var origin = value.File is null ? AblConfig.SourceName(value.Source) : $"{AblConfig.SourceName(value.Source)} {value.File}";
            stdout.WriteLine($"{value.Key.PadRight(width)}{shown}  ({origin})");
            if (value.Error is not null)
            {
                stderr.WriteLine($"abl: warning: {value.Key}: {value.Error}");
            }
        }

        stdout.WriteLine($"effective digest {config.EffectiveDigest}");
        return 0;
    }

    /// <summary><c>abl config path</c>: where the file is looked for; works even when it is invalid.</summary>
    private static int ConfigPath(ConfigInputs inputs, bool json, TextWriter stdout)
    {
        var (path, source) = AblConfig.LocateFile(inputs);
        var exists = inputs.FileSystem.Inspect(path) is not null;
        stdout.WriteLine(json
            ? new JsonObject { ["path"] = path, ["source"] = AblConfig.SourceName(source), ["exists"] = exists }.ToJsonString()
            : path);
        return 0;
    }

    public static void WriteError(TextWriter stderr, ConfigException error)
    {
        stderr.WriteLine($"abl: {error.Check}: {error.Message}");
        if (error.Hint is not null)
        {
            stderr.WriteLine($"  hint: {error.Hint}");
        }
    }
}
