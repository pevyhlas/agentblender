using System.Text.Json.Nodes;
using Abl.Core.Config;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 FR-002, AC-04..07 on the resolver with injected env and file system (plan M10): the
/// precedence matrix, strict-schema refusals, the trust rule (foreign owner through the
/// seam) and the token-file rule. Nothing here reads the real environment or disk.
/// </summary>
public class ConfigResolverTests
{
    private const string Home = "/home/u";
    private const string ConfigFile = "/home/u/.config/agentblender/config.json";

    [Fact]
    public void NoFileAndNoVariablesGiveDefaultsWithDefaultSources()
    {
        var config = Resolve(new FakeConfigFileSystem());

        Assert.False(config.FileLoaded);
        Assert.Equal(ConfigFile, config.FilePath);
        Assert.All(config.Values, v => Assert.Equal(ConfigSource.Default, v.Source));
        Assert.Equal(7811, config.Options.Port);
        Assert.Equal("/home/u/.local/share/agentblender/store", config.Options.StoreDir);
        Assert.Equal("/home/u/.local/share/agentblender/renders", config.Options.RendersDir);
        Assert.Equal("/home/u/.local/state/agentblender/run", config.Options.RuntimeDir);
        Assert.Null(config.Options.BlenderPath); // no built-in path: discovered, else unset (FR-003)
        Assert.Null(config.Blender); // no search in the inputs: nothing probed
        Assert.False(config.Options.TelemetryEnabled);
        Assert.Null(config.Options.DesktopToken);
    }

    public static TheoryData<string, string, string, string, string, string> PrecedenceKeys => new()
    {
        // key, env var, file JSON section+value, env text, flag text, then the expected canonical file value
        { "daemon.port", "ABL_PORT", "\"daemon\":{\"port\":7000}", "7100", "7200", "7000" },
        { "paths.store", "ABL_STORE", "\"paths\":{\"store\":\"~/file-store\"}", "/env-store", "/flag-store", "/home/u/file-store" },
        { "daemon.logLevel", "ABL_LOG_LEVEL", "\"daemon\":{\"logLevel\":\"warning\"}", "Debug", "error", "warning" },
        { "telemetry.enabled", "ABL_TELEMETRY", "\"telemetry\":{\"enabled\":true}", "0", "1", "true" },
    };

    [Theory]
    [MemberData(nameof(PrecedenceKeys))]
    public void FlagBeatsEnvBeatsFileBeatsDefault(
        string key, string envVar, string fileSection, string envText, string flagText, string fileValue)
    {
        var fs = new FakeConfigFileSystem().File(ConfigFile, $"{{\"version\":1,{fileSection}}}");
        var env = new Dictionary<string, string> { [envVar] = envText };
        var flags = new Dictionary<string, string> { [key] = flagText };

        var all = Resolve(fs, env, flags)[key];
        Assert.Equal(ConfigSource.Flag, all.Source);

        var noFlag = Resolve(fs, env)[key];
        Assert.Equal(ConfigSource.Env, noFlag.Source);

        var fileOnly = Resolve(fs)[key];
        Assert.Equal(ConfigSource.File, fileOnly.Source);
        Assert.Equal(fileValue, Text(fileOnly.Value));

        var none = Resolve(new FakeConfigFileSystem())[key];
        Assert.Equal(ConfigSource.Default, none.Source);
    }

    [Fact]
    public void EnvOverFileIsReportedAsEnvInTheJson()
    {
        // AC-04: a key set in the file and in its ABL_* variable uses the env value.
        var fs = new FakeConfigFileSystem().File(ConfigFile, """{"version":1,"daemon":{"port":7000}}""");
        var config = Resolve(fs, new Dictionary<string, string> { ["ABL_PORT"] = "7100" });

        Assert.Equal(7100, config.Options.Port);
        var json = config.ToJson();
        Assert.Equal(ConfigFile, json["file"]!.GetValue<string>());
        Assert.True(json["fileLoaded"]!.GetValue<bool>());
        Assert.Equal(7100, json["effective"]!["daemon.port"]!["value"]!.GetValue<int>());
        Assert.Equal("env", json["effective"]!["daemon.port"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public void FileValuesReachTheOptions()
    {
        var fs = new FakeConfigFileSystem().File(ConfigFile, """
            {
              "version": 1,
              "blender": { "path": "/Applications/Blender.app/Contents/MacOS/Blender", "allowUnsupported": true },
              "daemon": { "port": 7900, "logLevel": "warning" },
              "paths": { "store": "~/s", "renders": "/r/", "runtime": null, "workerDir": "/w" },
              "worker": { "envPassthrough": ["NVIDIA_DRIVER_CAPABILITIES"] },
              "telemetry": { "enabled": true, "otlpEndpoint": "http://127.0.0.1:4999", "tracesEndpoint": null },
              "desktop": { "endpoint": "192.0.2.2:7812", "tokenFile": null }
            }
            """);
        var options = Resolve(fs).Options;

        Assert.Equal("/Applications/Blender.app/Contents/MacOS/Blender", options.BlenderPath);
        Assert.True(options.AllowUnsupportedBlender);
        Assert.Equal(7900, options.Port);
        Assert.Equal("warning", options.LogLevel);
        Assert.Equal("/home/u/s", options.StoreDir);
        Assert.Equal("/r", options.RendersDir);
        Assert.Equal("/home/u/.local/state/agentblender/run", options.RuntimeDir); // null = not set here
        Assert.Equal("/w", options.WorkerDir);
        Assert.Equal(["NVIDIA_DRIVER_CAPABILITIES"], options.WorkerEnvPassthrough);
        Assert.True(options.TelemetryEnabled);
        Assert.Equal("http://127.0.0.1:4999", options.OtlpEndpoint);
        Assert.Null(options.TracesEndpoint);
        Assert.Equal("192.0.2.2:7812", options.DesktopEndpoint);
    }

    [Fact]
    public void EmptyVariablesCountAsUnset()
    {
        var config = Resolve(new FakeConfigFileSystem(), new Dictionary<string, string> { ["ABL_PORT"] = "", ["ABL_STORE"] = "" });
        Assert.Equal(ConfigSource.Default, config["daemon.port"].Source);
        Assert.Equal(ConfigSource.Default, config["paths.store"].Source);
    }

    [Fact]
    public void FileLocationFollowsAblConfigThenXdgConfigHome()
    {
        var fs = new FakeConfigFileSystem();
        Assert.Equal(("/etc/abl/c.json", ConfigSource.Env), AblConfig.LocateFile(Inputs(fs, new() { ["ABL_CONFIG"] = "/etc/abl/c.json" })));
        Assert.Equal(("/home/u/abl.json", ConfigSource.Env), AblConfig.LocateFile(Inputs(fs, new() { ["ABL_CONFIG"] = "~/abl.json" })));
        Assert.Equal(
            ("/x/agentblender/config.json", ConfigSource.Default),
            AblConfig.LocateFile(Inputs(fs, new() { ["XDG_CONFIG_HOME"] = "/x" })));
        Assert.Equal((ConfigFile, ConfigSource.Default), AblConfig.LocateFile(Inputs(fs, new() { ["XDG_CONFIG_HOME"] = "relative" })));
    }

    [Fact]
    public void RuntimeDefaultFollowsTheRuntimePathsRule()
    {
        // A5 through the resolver: an owned XDG_RUNTIME_DIR (Linux), else the state dir;
        // TMPDIR is no longer a candidate. RuntimePathsTests covers the rule itself.
        var owned = new FakeConfigFileSystem().Dir("/run/user/1000");
        Assert.Equal(
            "/run/user/1000/agentblender",
            Resolve(owned, new() { ["XDG_RUNTIME_DIR"] = "/run/user/1000", ["TMPDIR"] = "/t" }).Options.RuntimeDir);
        Assert.Equal(
            "/home/u/.local/state/agentblender/run",
            Resolve(new FakeConfigFileSystem(), new() { ["XDG_RUNTIME_DIR"] = "/run/user/1000", ["TMPDIR"] = "/t/" }).Options.RuntimeDir);
        Assert.Equal("/s/agentblender/run", Resolve(new FakeConfigFileSystem(), new() { ["XDG_STATE_HOME"] = "/s/" }).Options.RuntimeDir);
        Assert.Equal(ConfigSource.Env, Resolve(new FakeConfigFileSystem(), new() { ["ABL_RUNTIME_DIR"] = "/r" })["paths.runtime"].Source);
    }

    public static TheoryData<string, string> StrictSchemaViolations => new()
    {
        { """{"version":1,"daemon":{"port":7811},"extra":true}""", "unknown key 'extra'" },
        { """{"version":1,"daemon":{"prot":7811}}""", "unknown key 'daemon.prot'" },
        { """{"version":2}""", "unsupported version 2" },
        { """{"version":"1"}""", "unsupported version \"1\"" },
        { """{"daemon":{"port":7811}}""", "missing 'version'" },
        { "{\"version\":1,\n // a comment\n\"daemon\":{}}", "line 2" },
        { """{"version":1,"daemon":{"port":7811,}}""", "not strict JSON" },
        { """{"version":1,"daemon":{"port":1},"daemon":{"port":2}}""", "key 'daemon' appears more than once" },
        { """{"version":1,"daemon":{"port":1,"port":2}}""", "key 'daemon.port' appears more than once" },
        { """{"version":1,"daemon":{"port":"7811"}}""", "'daemon.port' must be an integer TCP port" },
        { """{"version":1,"daemon":{"port":70000}}""", "'daemon.port' must be an integer TCP port" },
        { """{"version":1,"daemon":[]}""", "'daemon' must be an object" },
        { """{"version":1,"paths":{"store":"relative/store"}}""", "'paths.store' must be an absolute path" },
        { """{"version":1,"blender":{"allowUnsupported":"yes"}}""", "'blender.allowUnsupported' must be true or false" },
        { """{"version":1,"daemon":{"logLevel":"loud"}}""", "'daemon.logLevel' must be one of" },
        { """{"version":1,"worker":{"envPassthrough":["PYTHONPATH"]}}""", "PYTHON* variables never reach workers" },
        { """{"version":1,"worker":{"envPassthrough":["A","A"]}}""", "lists A twice" },
        { """{"version":1,"worker":{"envPassthrough":["BAD-NAME"]}}""", "environment variable names" },
        { """{"version":1,"telemetry":{"otlpEndpoint":"ftp://x"}}""", "absolute http(s) URL" },
        { "[1]", "top level must be a JSON object" },
    };

    [Theory]
    [MemberData(nameof(StrictSchemaViolations))]
    public void StrictSchemaViolationsRefuseTheFileNamingFileAndKey(string content, string expected)
    {
        // AC-05: unknown key, unsupported version or a comment refuses, naming the file, the
        // key or position, and config.file.
        var error = Assert.Throws<ConfigException>(() => Resolve(new FakeConfigFileSystem().File(ConfigFile, content)));
        Assert.Equal("config.file", error.Check);
        Assert.Contains(ConfigFile, error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("ABL_PORT", "abc", "not a TCP port")]
    [InlineData("ABL_PORT", "0", "not a TCP port")]
    [InlineData("ABL_TELEMETRY", "yes", "not a boolean")]
    [InlineData("ABL_LOG_LEVEL", "2", "not a log level")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "localhost:4318", "absolute http(s) URL")]
    public void MalformedVariablesAreRefusedByName(string name, string value, string expected)
    {
        var error = Assert.Throws<ConfigException>(
            () => Resolve(new FakeConfigFileSystem(), new Dictionary<string, string> { [name] = value }));
        Assert.Equal("config.value", error.Check);
        Assert.StartsWith(name, error.Message);
        Assert.Contains(expected, error.Message);
    }

    public static TheoryData<string, string> UntrustedFiles => new()
    {
        { "world-writable file", "chmod go-w" },
        { "group-writable file", "writable by group or others (mode 0620)" },
        { "foreign-owned file", "owned by another user" },
        { "world-writable directory", "/home/u/.config/agentblender is writable by group or others (mode 0777)" },
        { "foreign-owned directory", "/home/u/.config/agentblender is owned by another user" },
        { "symlink to a writable target", "(symlink to /srv/shared/config.json) is writable" },
        { "symlink to a target in a writable directory", "/srv/shared is writable by group or others" },
        { "dangling symlink", "symlink to a missing target" },
    };

    [Theory]
    [MemberData(nameof(UntrustedFiles))]
    public void UntrustedConfigFilesAreRefusedBeforeTheyAreRead(string shape, string expected)
    {
        // AC-07 (trust rule, M1): refused under config.file, never parsed.
        const string Body = """{"version":1}""";
        const UnixFileMode Open = (UnixFileMode)0b111_111_111;
        var fs = shape switch
        {
            "world-writable file" => new FakeConfigFileSystem().File(ConfigFile, Body, FakeConfigFileSystem.Private | UnixFileMode.OtherWrite),
            "group-writable file" => new FakeConfigFileSystem().File(ConfigFile, Body, FakeConfigFileSystem.Private | UnixFileMode.GroupWrite),
            "foreign-owned file" => new FakeConfigFileSystem().File(ConfigFile, Body, owned: false),
            "world-writable directory" => new FakeConfigFileSystem().Dir("/home/u/.config/agentblender", Open).File(ConfigFile, Body),
            "foreign-owned directory" => new FakeConfigFileSystem().Dir("/home/u/.config/agentblender", owned: false).File(ConfigFile, Body),
            "symlink to a writable target" => new FakeConfigFileSystem()
                .Dir("/home/u/.config/agentblender")
                .File("/srv/shared/config.json", Body, FakeConfigFileSystem.Private | UnixFileMode.OtherWrite)
                .Link(ConfigFile, "/srv/shared/config.json"),
            "symlink to a target in a writable directory" => new FakeConfigFileSystem()
                .Dir("/home/u/.config/agentblender")
                .Dir("/srv/shared", Open)
                .File("/srv/shared/config.json", Body)
                .Link(ConfigFile, "/srv/shared/config.json"),
            "dangling symlink" => new FakeConfigFileSystem().Dir("/home/u/.config/agentblender").Link(ConfigFile, "/gone/config.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var error = Assert.Throws<ConfigException>(() => Resolve(fs));
        Assert.Equal("config.file", error.Check);
        Assert.Contains("refusing to load", error.Message);
        Assert.Contains(expected, error.Message);
        Assert.Empty(fs.Reads);
    }

    [Fact]
    public void ReadableByOthersIsFineForTheConfigFile()
    {
        // The file holds no inline secret, so only writability and ownership matter.
        var fs = new FakeConfigFileSystem().File(
            ConfigFile, """{"version":1}""", FakeConfigFileSystem.Private | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.True(Resolve(fs).FileLoaded);
    }

    [Fact]
    public void SymlinkToATrustedTargetIsLoaded()
    {
        var fs = new FakeConfigFileSystem()
            .Dir("/home/u/.config/agentblender")
            .File("/home/u/dotfiles/abl.json", """{"version":1,"daemon":{"port":7333}}""")
            .Link(ConfigFile, "/home/u/dotfiles/abl.json");
        Assert.Equal(7333, Resolve(fs).Options.Port);
    }

    [Fact]
    public void EnvTokenWinsOverTheTokenFileWhichIsNotRead()
    {
        var fs = new FakeConfigFileSystem()
            .File(ConfigFile, """{"version":1,"desktop":{"endpoint":"192.0.2.2:7812","tokenFile":"~/.config/agentblender/desktop.token"}}""")
            .File("/home/u/.config/agentblender/desktop.token", "file-secret\n");
        var config = Resolve(fs, new() { ["ABL_DESKTOP_TOKEN"] = "env-secret" });

        Assert.Equal("env-secret", config.Options.DesktopToken);
        Assert.Equal(ConfigSource.Env, config[AblConfig.TokenKey].Source);
        Assert.DoesNotContain("/home/u/.config/agentblender/desktop.token", fs.Reads);
        AssertNoSecret(config, "env-secret");
    }

    [Fact]
    public void TrustedTokenFileSuppliesTheTokenRedacted()
    {
        var fs = new FakeConfigFileSystem()
            .File(ConfigFile, """{"version":1,"desktop":{"endpoint":"192.0.2.2:7812","tokenFile":"~/.config/agentblender/desktop.token"}}""")
            .File("/home/u/.config/agentblender/desktop.token", "file-secret\n");
        var config = Resolve(fs);

        Assert.Equal("file-secret", config.Options.DesktopToken);
        var token = config.ToJson()["effective"]![AblConfig.TokenKey]!;
        Assert.Equal(AblConfig.Redacted, token["value"]!.GetValue<string>());
        Assert.Equal("file", token["source"]!.GetValue<string>());
        Assert.Equal("/home/u/.config/agentblender/desktop.token", token["file"]!.GetValue<string>());
        AssertNoSecret(config, "file-secret");
    }

    [Fact]
    public void DesktopTierIsUnsupportedOnMacOS()
    {
        // 008 FR-004 (M9b): the endpoint and token stay reported, the tier stays off.
        var fs = new FakeConfigFileSystem()
            .File(ConfigFile, """{"version":1,"desktop":{"endpoint":"192.0.2.2:7812"}}""");
        var env = new Dictionary<string, string> { ["ABL_DESKTOP_TOKEN"] = "env-secret" };
        var mac = AblConfig.Resolve(Inputs(fs, env) with { Platform = HostPlatform.MacOS });
        var linux = AblConfig.Resolve(Inputs(fs, env));

        Assert.Null(mac.Options.DesktopEndpoint);
        Assert.Null(mac.Options.DesktopToken);
        Assert.False(new Workers.DesktopTier(mac.Options, Microsoft.Extensions.Logging.Abstractions.NullLogger<Workers.DesktopTier>.Instance).IsConfigured);
        var endpoint = mac[AblConfig.DesktopEndpointKey];
        Assert.Equal("192.0.2.2:7812", endpoint.Value!.GetValue<string>());
        Assert.Equal(AblConfig.DesktopUnsupported, endpoint.Error);
        Assert.Contains("unsupported on macOS", mac.ToJson()["effective"]!["desktop.endpoint"]!["error"]!.GetValue<string>());
        AssertNoSecret(mac, "env-secret");

        Assert.Equal("192.0.2.2:7812", linux.Options.DesktopEndpoint);
        Assert.Null(linux[AblConfig.DesktopEndpointKey].Error);
    }

    [Theory]
    [InlineData(0b110_100_000, false, "accessible by group or others (mode 0640)")]
    [InlineData(0b110_000_100, false, "accessible by group or others (mode 0604)")]
    [InlineData(0b110_000_000, true, "owned by another user")]
    public void UntrustedTokenFileRefusesOnlyTheDesktopTier(int mode, bool foreign, string expected)
    {
        // AC-07: a token file readable by group/others or owned by another user refuses the
        // desktop tier; the rest of the config (and local rendering) stays usable.
        var fs = new FakeConfigFileSystem()
            .File(ConfigFile, """{"version":1,"desktop":{"endpoint":"192.0.2.2:7812","tokenFile":"/home/u/.config/agentblender/desktop.token"}}""")
            .File("/home/u/.config/agentblender/desktop.token", "file-secret", (UnixFileMode)mode, owned: !foreign);
        var config = Resolve(fs);

        Assert.Null(config.Options.DesktopToken);
        Assert.False(new Workers.DesktopTier(config.Options, Microsoft.Extensions.Logging.Abstractions.NullLogger<Workers.DesktopTier>.Instance).IsConfigured);
        var token = config[AblConfig.TokenKey];
        Assert.Null(token.Value);
        Assert.Contains(expected, token.Error);
        Assert.Contains("desktop tier stays off", token.Error);
        Assert.DoesNotContain("/home/u/.config/agentblender/desktop.token", fs.Reads);
    }

    [Fact]
    public void EmptyTokenFileIsRefused()
    {
        var fs = new FakeConfigFileSystem()
            .File(ConfigFile, """{"version":1,"desktop":{"tokenFile":"/home/u/.config/agentblender/desktop.token"}}""")
            .File("/home/u/.config/agentblender/desktop.token", " \n");
        var config = Resolve(fs);
        Assert.Null(config.Options.DesktopToken);
        Assert.Contains("is empty", config[AblConfig.TokenKey].Error);
    }

    [Fact]
    public void DigestIgnoresWhitespaceAndSecretsButTracksEffectiveValues()
    {
        // M6/AC-28 groundwork: the digest is over the resolved map, not the file bytes.
        var compact = Resolve(new FakeConfigFileSystem().File(ConfigFile, """{"version":1,"daemon":{"port":7000}}"""));
        var spaced = Resolve(new FakeConfigFileSystem().File(ConfigFile, "{\n  \"version\": 1,\n  \"daemon\": { \"port\": 7000 }\n}\n"));
        Assert.Equal(compact.EffectiveDigest, spaced.EffectiveDigest);
        Assert.StartsWith("sha256:", compact.EffectiveDigest);

        // Values, not sources: the same port from env is the same effective config; a
        // different one (shell vs service env drift) is not.
        var sameFromEnv = Resolve(
            new FakeConfigFileSystem().File(ConfigFile, """{"version":1,"daemon":{"port":7000}}"""), new() { ["ABL_PORT"] = "7000" });
        Assert.Equal(compact.EffectiveDigest, sameFromEnv.EffectiveDigest);
        var drifted = Resolve(
            new FakeConfigFileSystem().File(ConfigFile, """{"version":1,"daemon":{"port":7000}}"""), new() { ["ABL_PORT"] = "7001" });
        Assert.NotEqual(compact.EffectiveDigest, drifted.EffectiveDigest);

        var tokenA = Resolve(new FakeConfigFileSystem(), new() { ["ABL_DESKTOP_TOKEN"] = "a" });
        var tokenB = Resolve(new FakeConfigFileSystem(), new() { ["ABL_DESKTOP_TOKEN"] = "b" });
        Assert.Equal(tokenA.EffectiveDigest, tokenB.EffectiveDigest);
    }

    [Fact]
    public void ResolutionIsDeterministicAndKeysAreOrdered()
    {
        var fs = new FakeConfigFileSystem().File(ConfigFile, """{"version":1,"daemon":{"port":7000}}""");
        var first = Resolve(fs).ToJson().ToJsonString();
        var second = Resolve(fs).ToJson().ToJsonString();
        Assert.Equal(first, second);

        var keys = Resolve(fs).Values.Select(v => v.Key).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal([.. AblConfig.FileKeys.Append(AblConfig.TokenKey).Order(StringComparer.Ordinal)], keys);
    }

    [Fact]
    public void LoadFileFalseNeverTouchesTheFileSystem()
    {
        var fs = new FakeConfigFileSystem().File(ConfigFile, "not json at all");
        var config = AblConfig.Resolve(Inputs(fs, []) with { LoadFile = false });
        Assert.Null(config.FilePath);
        Assert.False(config.FileLoaded);
        Assert.Empty(fs.Reads);
    }

    private static void AssertNoSecret(AblConfig config, string secret)
    {
        Assert.DoesNotContain(secret, config.ToJson().ToJsonString());
        Assert.DoesNotContain(secret, config.Options.ToString());
        Assert.DoesNotContain(secret, config.EffectiveDigest);
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node!.ToJsonString();

    private static AblConfig Resolve(
        FakeConfigFileSystem fs, Dictionary<string, string>? env = null, Dictionary<string, string>? flags = null) =>
        AblConfig.Resolve(Inputs(fs, env ?? [], flags));

    private static ConfigInputs Inputs(FakeConfigFileSystem fs, Dictionary<string, string> env, Dictionary<string, string>? flags = null) =>
        new() { Env = env, Home = Home, FileSystem = fs, Flags = flags ?? [] };

    [Fact]
    public void AbsolutePathVariablesResolveRelativeValuesAsTheResolverDoes()
    {
        // 2a round-3 eng-arch LOW-3: `daemon start` hands its child (cwd: the state dir) these
        // values, so they must name what the resolver reads in the caller's cwd, by its own rule.
        var env = new Dictionary<string, string?>
        {
            ["ABL_STORE"] = "rel/store",
            ["ABL_RENDERS"] = "~/renders",
            ["ABL_RUNTIME_DIR"] = "/abs/run",
            ["ABL_CONFIG"] = "~",
            ["ABL_PORT"] = "7999",
            ["ABL_BLENDER"] = "",
        };
        var cwd = Environment.CurrentDirectory;

        var rewritten = AblConfig.AbsolutePathVariables(env, cwd);

        var (name, value) = Assert.Single(rewritten);
        Assert.Equal("ABL_STORE", name);
        Assert.Equal(Path.GetFullPath("rel/store", cwd), value);
        var fs = new FakeConfigFileSystem();
        Assert.Equal(
            Resolve(fs, new() { ["ABL_STORE"] = "rel/store" }).Options.StoreDir,
            Resolve(fs, new() { ["ABL_STORE"] = value }).Options.StoreDir);
        Assert.Equal("/elsewhere/rel/store", Assert.Single(AblConfig.AbsolutePathVariables(env, "/elsewhere")).Value);
    }
}
