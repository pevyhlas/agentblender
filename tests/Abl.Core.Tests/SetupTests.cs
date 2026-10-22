using System.Text.Json.Nodes;
using Abl.Cli.Host;
using Abl.Core.Config;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T050/T054 (FR-006, AC-15, AC-16): <c>abl setup</c> against a real temp config
/// directory (<c>ABL_CONFIG</c>), fake <c>blender</c> scripts and a temp search root. Nothing
/// here touches the real config file or Blender.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SetupTests : IDisposable
{
    private readonly string _root;

    public SetupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "abl-setup-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Home);
        File.SetUnixFileMode(_root, (UnixFileMode)0b111_000_000);
        File.SetUnixFileMode(Home, (UnixFileMode)0b111_101_101);
        Directory.CreateDirectory(SysRoot);
    }

    private string Home => Path.Combine(_root, "home");

    private string SysRoot => Path.Combine(_root, "sys");

    private string ConfigDir => Path.Combine(_root, "cfg");

    private string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AC15_ASecondRunReportsUnchangedAndLeavesTheFileByteIdentical()
    {
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");

        var (code, json, _) = Run("--blender", blender, "--port", "7900", "--json");
        Assert.Equal(0, code);
        Assert.True(json["changed"]!.GetValue<bool>());
        Assert.True(json["written"]!.GetValue<bool>());
        var first = File.ReadAllBytes(ConfigPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ConfigPath));
        Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(ConfigDir));
        Assert.Equal(
            $"{{\n  \"version\": 1,\n  \"blender\": {{\n    \"path\": \"{blender}\"\n  }},\n  \"daemon\": {{\n    \"port\": 7900\n  }}\n}}\n",
            File.ReadAllText(ConfigPath));

        foreach (var again in new[] { new[] { "--json" }, ["--blender", blender, "--port", "7900", "--json"] })
        {
            var (secondCode, second, _) = Run(again);
            Assert.Equal(0, secondCode);
            Assert.False(second["changed"]!.GetValue<bool>());
            Assert.False(second["written"]!.GetValue<bool>());
            Assert.Equal(first, File.ReadAllBytes(ConfigPath));
        }

        Assert.Equal([ConfigPath], Directory.EnumerateFiles(ConfigDir)); // no temp file left behind
    }

    [Fact]
    public void AC15_AHandWrittenFileWithTheSameValuesIsNotRewritten()
    {
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");
        WriteConfig($"{{ \"version\": 1, \"blender\": {{ \"path\": \"{blender}\" }} }}");
        var before = File.ReadAllBytes(ConfigPath);

        var (code, json, _) = Run("--blender", blender, "--json");

        Assert.Equal(0, code);
        Assert.False(json["changed"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void AC16_AnExistingValidValueIsKeptAndTheDifferenceReported()
    {
        var pinned = FakeBlender(Path.Combine(_root, "pinned", "blender"), "5.2.3");
        var discoverable = FakeBlender(Path.Combine(SysRoot, "usr", "bin", "blender"), "5.2.1");
        WriteConfig($$$"""{"version":1,"blender":{"path":"{{{pinned}}}"}}""");
        var before = File.ReadAllBytes(ConfigPath);

        var (code, json, _) = Run("--json");

        Assert.Equal(0, code);
        var key = Key(json, AblConfig.BlenderKey);
        Assert.Equal("kept", key["action"]!.GetValue<string>());
        Assert.Equal(pinned, key["value"]!.GetValue<string>());
        Assert.Equal($"discovery would choose {discoverable}", key["note"]!.GetValue<string>());
        Assert.Equal("warn", CheckOf(json, "blender.version")["status"]!.GetValue<string>()); // 5.2.3, not the reference
        Assert.False(json["changed"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void AC16_AnInvalidExistingValueFailsThatKeyWithTheBlenderAndResetRemedy()
    {
        var deleted = Path.Combine(_root, "deleted", "blender");
        WriteConfig($$$"""{"version":1,"blender":{"path":"{{{deleted}}}"},"daemon":{"port":7000}}""");
        var before = File.ReadAllBytes(ConfigPath);

        var (code, json, _) = Run("--port", "7001", "--json");

        Assert.Equal(1, code);
        var key = Key(json, AblConfig.BlenderKey);
        Assert.Equal("invalid", key["action"]!.GetValue<string>());
        Assert.Contains("blender.found", key["note"]!.GetValue<string>());
        Assert.Contains("abl setup --blender <path>", key["note"]!.GetValue<string>());
        Assert.Contains("abl setup --reset", key["note"]!.GetValue<string>());
        var found = CheckOf(json, "blender.found");
        Assert.Equal("fail", found["status"]!.GetValue<string>());
        Assert.Contains("--reset", found["remedy"]!.GetValue<string>());
        Assert.False(json["written"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllBytes(ConfigPath)); // nothing written while a key fails
    }

    [Fact]
    public void AnExplicitBlenderReplacesAnInvalidValueAndKeepsTheOtherKeys()
    {
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");
        WriteConfig($$$"""{"version":1,"blender":{"path":"{{{Path.Combine(_root, "deleted", "blender")}}}"},"telemetry":{"enabled":true}}""");

        var (code, json, _) = Run("--blender", blender, "--json");

        Assert.Equal(0, code);
        var key = Key(json, AblConfig.BlenderKey);
        Assert.Equal("set", key["action"]!.GetValue<string>());
        Assert.EndsWith(Path.Combine("deleted", "blender"), key["previous"]!.GetValue<string>());
        var file = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal(blender, file["blender"]!["path"]!.GetValue<string>());
        Assert.True(file["telemetry"]!["enabled"]!.GetValue<bool>()); // merged, not replaced
    }

    [Fact]
    public void ResetRewritesTheFileFromDiscoveryAndDefaults()
    {
        var discovered = FakeBlender(Path.Combine(SysRoot, "usr", "bin", "blender"), "5.2.1");
        WriteConfig("""{"version":1,"blender":{"path":"/gone/blender"},"telemetry":{"enabled":true}}""");

        var (code, json, _) = Run("--reset", "--json");

        Assert.Equal(0, code);
        Assert.True(json["written"]!.GetValue<bool>());
        Assert.Equal(
            $"{{\n  \"version\": 1,\n  \"blender\": {{\n    \"path\": \"{discovered}\"\n  }}\n}}\n",
            File.ReadAllText(ConfigPath));

        var (_, again, _) = Run("--reset", "--json");
        Assert.False(again["changed"]!.GetValue<bool>()); // AC-15 holds for --reset too
    }

    [Fact]
    public void ResetRepairsAFileThatDoesNotParse()
    {
        FakeBlender(Path.Combine(SysRoot, "usr", "bin", "blender"), "5.2.1");
        WriteConfig("{ \"version\": 1, // comment\n}");

        var (refused, _, _) = Run("--json");
        Assert.Equal(1, refused);

        var (code, _, _) = Run("--reset", "--json");
        Assert.Equal(0, code);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(ConfigPath))!["blender"]);
    }

    [Fact]
    public void DryRunReportsTheChangeAndWritesNothing()
    {
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");

        var (code, json, _) = Run("--blender", blender, "--dry-run", "--json");

        Assert.Equal(0, code);
        Assert.True(json["changed"]!.GetValue<bool>());
        Assert.False(json["written"]!.GetValue<bool>());
        Assert.False(Directory.Exists(ConfigDir));
    }

    [Fact]
    public void NoBlenderFailsWithTheSetupRemedyAndWritesNothing()
    {
        var (code, json, _) = Run("--json");

        Assert.Equal(1, code);
        var found = CheckOf(json, "blender.found");
        Assert.Equal("fail", found["status"]!.GetValue<string>());
        Assert.Contains("abl setup --blender <path>", found["remedy"]!.GetValue<string>());
        Assert.Contains(SupportedBlender.DownloadPage, found["remedy"]!.GetValue<string>());
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData("--runtime-dir", "runtime.dir")]
    [InlineData("--store", "paths.store")]
    [InlineData("--renders", "paths.renders")]
    public void AFailingPathCheckBlocksTheWrite(string flag, string check)
    {
        // T050: setup runs doctor's path checks on the values it would write, and a failing one
        // writes nothing: a symlinked runtime dir, a store open to others, renders on a file.
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");
        string value;
        switch (flag)
        {
            case "--runtime-dir":
                var real = Path.Combine(_root, "rt-real");
                Directory.CreateDirectory(real, (UnixFileMode)0b111_000_000);
                value = Path.Combine(_root, "rt-link");
                Directory.CreateSymbolicLink(value, real);
                break;
            case "--store":
                value = Path.Combine(_root, "open-store");
                Directory.CreateDirectory(value);
                File.SetUnixFileMode(value, (UnixFileMode)0b111_111_111);
                break;
            default:
                value = Path.Combine(_root, "renders-file");
                File.WriteAllText(value, "not a directory");
                break;
        }

        var (code, json, _) = Run("--blender", blender, flag, value, "--json");

        Assert.Equal(1, code);
        Assert.Equal("fail", CheckOf(json, check)["status"]!.GetValue<string>());
        Assert.Equal("pass", CheckOf(json, "blender.version")["status"]!.GetValue<string>()); // only the path check fails
        Assert.False(json["written"]!.GetValue<bool>());
        Assert.False(File.Exists(ConfigPath));
        Assert.False(Directory.Exists(ConfigDir) && Directory.EnumerateFileSystemEntries(ConfigDir).Any());
    }

    [Fact]
    public void ASymlinkedConfigIsWrittenThroughAndStaysALink()
    {
        // A dotfiles-managed config: the link survives and its target holds the new text.
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");
        var dotfiles = Path.Combine(_root, "dotfiles");
        Directory.CreateDirectory(dotfiles, (UnixFileMode)0b111_000_000);
        var target = Path.Combine(dotfiles, "config.json");
        File.WriteAllText(target, """{"version":1,"daemon":{"port":7901}}""");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(ConfigDir, (UnixFileMode)0b111_000_000);
        File.CreateSymbolicLink(ConfigPath, target);

        var (code, json, _) = Run("--blender", blender, "--json");

        Assert.Equal(0, code);
        Assert.True(json["written"]!.GetValue<bool>());
        Assert.NotNull(new FileInfo(ConfigPath).LinkTarget);
        Assert.Contains(blender, File.ReadAllText(target));
        Assert.Contains("7901", File.ReadAllText(target));
    }

    [Fact]
    public void ResetRefusesAConfigDirectoryOthersCanWrite()
    {
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");
        WriteConfig("""{"version":1}""");
        File.SetUnixFileMode(ConfigDir, (UnixFileMode)0b111_111_000);

        var (code, json, _) = Run("--reset", "--blender", blender, "--json");

        Assert.Equal(1, code);
        var file = CheckOf(json, "config.file");
        Assert.Equal("fail", file["status"]!.GetValue<string>());
        // Setup's own rule, not the resolver's file refusal a moment later.
        Assert.StartsWith("config directory ", file["summary"]!.GetValue<string>());
        Assert.Contains("is writable by group or others", file["summary"]!.GetValue<string>());
        Assert.False(json["written"]!.GetValue<bool>());
        Assert.Equal("""{"version":1}""", File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("--port", "seventy")]
    [InlineData("--port", "70000")]
    [InlineData("--colour", "blue")]
    public void ABadFlagIsAUsageError(string flag, string value)
    {
        var (code, _, stderr) = RunText(flag, value);

        Assert.Equal(2, code);
        Assert.StartsWith("abl: ", stderr);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void AnEnvironmentOverrideIsReportedAsAWarning()
    {
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");

        var (code, json, stderr) = Run(new Dictionary<string, string> { ["ABL_PORT"] = "7400" }, "--blender", blender, "--json");

        Assert.Equal(0, code);
        Assert.Contains("ABL_PORT is set in this shell and overrides daemon.port", stderr);
        Assert.Single(json["warnings"]!.AsArray());
    }

    [Fact]
    public void TextOutputNamesTheFileTheKeysAndTheChecks()
    {
        var blender = FakeBlender(Path.Combine(_root, "b", "blender"), "5.2.1");

        var (code, stdout, _) = RunText("--blender", blender, "--port", "7900");

        Assert.Equal(0, code);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"config {ConfigPath}: written", lines[0]);
        Assert.StartsWith("  set       blender.path   " + blender, lines[1]);
        Assert.StartsWith("  set       daemon.port    7900", lines[2]);
        Assert.Contains(lines, l => l.StartsWith("  pass  blender.version", StringComparison.Ordinal));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private (int Code, JsonObject Json, string Stderr) Run(params string[] args) => Run([], args);

    private (int Code, JsonObject Json, string Stderr) Run(Dictionary<string, string> env, params string[] args)
    {
        var (code, stdout, stderr) = RunText(env, args);
        return (code, JsonNode.Parse(stdout)!.AsObject(), stderr);
    }

    private (int Code, string Stdout, string Stderr) RunText(params string[] args) => RunText([], args);

    private (int Code, string Stdout, string Stderr) RunText(Dictionary<string, string> env, params string[] args)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ABL_CONFIG"] = ConfigPath,
            ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
        };
        foreach (var (key, value) in env)
        {
            merged[key] = value;
        }

        var inputs = new ConfigInputs
        {
            Env = merged,
            Home = Home,
            Platform = HostPlatform.Linux,
            UserId = UnixIdentity.EffectiveUserId,
            Blender = new BlenderSearch { Platform = HostPlatform.Linux, Home = Home, SystemRoot = SysRoot, Spotlight = _ => [] },
        };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Setup.Run(args, inputs, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private void WriteConfig(string text)
    {
        Directory.CreateDirectory(ConfigDir, (UnixFileMode)0b111_000_000);
        File.WriteAllText(ConfigPath, text);
        File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static JsonObject Key(JsonObject json, string key) =>
        Assert.Single(json["keys"]!.AsArray(), k => k!["key"]!.GetValue<string>() == key)!.AsObject();

    private static JsonObject CheckOf(JsonObject json, string id) =>
        Assert.Single(json["checks"]!.AsArray(), c => c!["id"]!.GetValue<string>() == id)!.AsObject();

    private string FakeBlender(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"#!/bin/sh\nprintf 'Blender {version}\\n\\tbuild hash: 0123456789ab\\n'\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);

        // Discovery refuses group/other-writable dirs; do not depend on the runner's umask.
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.StartsWith(_root, StringComparison.Ordinal) && dir != _root; dir = Path.GetDirectoryName(dir))
        {
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
        }

        return path;
    }
}
