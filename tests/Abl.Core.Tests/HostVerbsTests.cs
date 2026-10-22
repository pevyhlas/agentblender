using System.Text.Json.Nodes;
using Abl.Cli;
using Abl.Core.Config;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 plan B1 and T017: host-only verbs are disjoint from the op verbs, and
/// <c>abl config show|path</c> report the resolver's view (hermetic inputs).
/// </summary>
public class HostVerbsTests
{
    private const string ConfigFile = "/home/u/.config/agentblender/config.json";

    [Fact]
    public void HostVerbsAreDisjointFromOpVerbsAndNameNoOp()
    {
        Assert.Empty(HostVerbs.Verbs.Intersect(VerbMap.Verbs.Keys));
        var opNames = OpSurface.All.ToHashSet();
        Assert.DoesNotContain(HostVerbs.Verbs, v => opNames.Contains($"{v.Noun}_{v.Verb}"));
    }

    [Fact]
    public void DaemonStatusIsAnOpVerbAndTheServiceVerbsAreHostVerbs()
    {
        // T036: `abl daemon status` is the CLI form of the daemon_status op (VerbMap); the
        // lifecycle and service verbs stay host-only.
        Assert.Equal("daemon_status", VerbMap.Verbs[("daemon", "status")]);
        Assert.Contains(("daemon", "install-service"), HostVerbs.Verbs);
        Assert.Contains(("daemon", "uninstall-service"), HostVerbs.Verbs);
        Assert.DoesNotContain(("daemon", "status"), HostVerbs.Verbs);
    }

    [Fact]
    public void OpVerbsNeverCallTheDaemonHostInProcess()
    {
        // Plan B1: op verbs always post to /api/ops; only the daemon lifecycle verbs (`run`
        // hosts the daemon) name Abl.Daemon in the CLI.
        var naming = CliSources().Where(f => File.ReadAllText(f).Contains("Abl.Daemon", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(CliDir, f))
            .ToList();
        Assert.Equal(["Host/DaemonVerbs.cs"], naming);
    }

    [Fact]
    public void HostVerbsReadThroughApiOpsButNeverMutate()
    {
        // Plan B1: the only ops a host verb may post are read ops, and the only place in the
        // host verbs that names /api/ops is the guarded reader (DaemonLifecycle.ReadOpAsync).
        Assert.Equal(new HashSet<string> { "session_list", "daemon_status" }, HostVerbs.ReadOps.ToHashSet());
        Assert.Subset(OpSurface.All.ToHashSet(), HostVerbs.ReadOps.ToHashSet());

        var routes = CliSources()
            .Where(f => !f.EndsWith("Program.cs", StringComparison.Ordinal)) // the op verbs' own dispatch
            .SelectMany(f => File.ReadAllLines(f).Select(l => (File: Path.GetRelativePath(CliDir, f), Line: l)))
            .Where(l => l.Line.Contains("/api/ops/", StringComparison.Ordinal) && !l.Line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();
        var route = Assert.Single(routes);
        Assert.Equal("Host/DaemonLifecycle.cs", route.File);
        Assert.Contains("url + \"/api/ops/\" + op", route.Line);
    }

    private static string CliDir
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "agentblender.slnx")))
                {
                    return Path.Combine(d.FullName, "src", "Abl.Cli");
                }
            }

            throw new DirectoryNotFoundException("repository root (agentblender.slnx) not found above the test dir");
        }
    }

    private static IEnumerable<string> CliSources() =>
        Directory.EnumerateFiles(CliDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    [Fact]
    public void ConfigShowJsonReportsEachKeyWithItsSource()
    {
        var fs = new FakeConfigFileSystem().File(ConfigFile, """{"version":1,"daemon":{"port":7000},"paths":{"store":"~/s"}}""");
        var (code, stdout, stderr) = Run(("config", "show"), ["--json"], fs, new() { ["ABL_PORT"] = "7100", ["ABL_DESKTOP_TOKEN"] = "s3cret" });

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal(ConfigFile, json["file"]!.GetValue<string>());
        Assert.Equal("env", json["effective"]!["daemon.port"]!["source"]!.GetValue<string>());
        Assert.Equal(7100, json["effective"]!["daemon.port"]!["value"]!.GetValue<int>());
        Assert.Equal("file", json["effective"]!["paths.store"]!["source"]!.GetValue<string>());
        Assert.Equal("/home/u/s", json["effective"]!["paths.store"]!["value"]!.GetValue<string>());
        Assert.Equal("<redacted>", json["effective"]!["desktop.token"]!["value"]!.GetValue<string>());
        Assert.DoesNotContain("s3cret", stdout);
        Assert.StartsWith("sha256:", json["effectiveDigest"]!.GetValue<string>());
    }

    [Fact]
    public void ConfigShowTextIsOneLinePerKeyAndRedacts()
    {
        var (code, stdout, _) = Run(("config", "show"), [], new FakeConfigFileSystem(), new() { ["ABL_DESKTOP_TOKEN"] = "s3cret" });

        Assert.Equal(0, code);
        Assert.Contains($"config {ConfigFile} (not present, location from default)", stdout);
        Assert.Matches(@"daemon\.port\s+7811  \(default\)", stdout);
        Assert.Matches(@"desktop\.token\s+<redacted>  \(env\)", stdout);
        Assert.DoesNotContain("s3cret", stdout);
        Assert.Contains("effective digest sha256:", stdout);
    }

    [Fact]
    public void ConfigShowFailsWithTheCheckIdOnAnInvalidFile()
    {
        var fs = new FakeConfigFileSystem().File(ConfigFile, """{"version":1,"daemon":{"prot":1}}""");
        var (code, stdout, stderr) = Run(("config", "show"), ["--json"], fs, []);

        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("config.file", error["check"]!.GetValue<string>());
        Assert.Contains("unknown key 'daemon.prot'", error["message"]!.GetValue<string>());
        Assert.StartsWith($"abl: config.file: config file {ConfigFile}: unknown key 'daemon.prot'", stderr);
    }

    [Fact]
    public void ConfigShowWarnsOnARefusedTokenFileButSucceeds()
    {
        var fs = new FakeConfigFileSystem()
            .File(ConfigFile, """{"version":1,"desktop":{"tokenFile":"/home/u/t"}}""")
            .File("/home/u/t", "secret", (UnixFileMode)0b110_100_100);
        var (code, stdout, stderr) = Run(("config", "show"), [], fs, []);

        Assert.Equal(0, code);
        Assert.Matches(@"desktop\.token\s+-  \(file /home/u/t\)", stdout);
        Assert.Contains("abl: warning: desktop.token: token file /home/u/t is accessible by group or others", stderr);
    }

    [Fact]
    public void ConfigPathWorksEvenWhenTheFileIsInvalid()
    {
        var fs = new FakeConfigFileSystem().File("/etc/abl.json", "{ not json");
        var (code, stdout, _) = Run(("config", "path"), ["--json"], fs, new() { ["ABL_CONFIG"] = "/etc/abl.json" });

        Assert.Equal(0, code);
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal("/etc/abl.json", json["path"]!.GetValue<string>());
        Assert.Equal("env", json["source"]!.GetValue<string>());
        Assert.True(json["exists"]!.GetValue<bool>());

        var (_, plain, _) = Run(("config", "path"), [], new FakeConfigFileSystem(), []);
        Assert.Equal(ConfigFile + Environment.NewLine, plain);
    }

    [Fact]
    public void UnknownArgumentsAreUsageErrors()
    {
        var (code, _, stderr) = Run(("config", "show"), ["--port", "1"], new FakeConfigFileSystem(), []);
        Assert.Equal(2, code);
        Assert.Contains("takes only --json", stderr);
    }

    private static (int Code, string Stdout, string Stderr) Run(
        (string, string) verb, string[] rest, FakeConfigFileSystem fs, Dictionary<string, string> env)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = HostVerbs.Run(verb, rest, new ConfigInputs { Env = env, Home = "/home/u", FileSystem = fs }, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
