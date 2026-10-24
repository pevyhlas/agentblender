using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Abl.Cli.Host;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>008/T021 shared read-only facts and the distinct policies consuming them.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class PathTrustTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("abl-trust-").FullName;

    [Fact]
    public void OwnershipIsUidEqualityIncludingRootAndMissingTargets()
    {
        var own = new FakeConfigFileSystem().File("/cfg/file", "{}").Inspect("/cfg/file")!;
        Assert.True(own.OwnedByCurrentUser);
        Assert.False((own with { OwnerUserId = 0 }).OwnedByCurrentUser);
        Assert.True((own with { OwnerUserId = 0, EffectiveUserId = 0 }).OwnedByCurrentUser);
        Assert.False((own with { OwnerUserId = null }).OwnedByCurrentUser);
        Assert.False((own with { TargetExists = false }).OwnedByCurrentUser);
    }

    [Fact]
    public void InspectionPreservesModeContentsAndLinuxChangeTime()
    {
        var file = Path.Combine(_root, "private");
        File.WriteAllText(file, "unchanged");
        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        var before = OperatingSystem.IsLinux() ? ChangeTime(file) : null;

        var facts = UnixConfigFileSystem.Instance.Inspect(file)!;
        Assert.True(UnixIdentity.IsOwnedByCurrentUser(file));

        Assert.Equal(UnixIdentity.EffectiveUserId, facts.OwnerUserId);
        Assert.True(facts.OwnedByCurrentUser);
        Assert.True(facts.IsRegularFile);
        Assert.False(facts.IsDirectory);
        Assert.Equal(UnixFileMode.UserRead, facts.Mode);
        Assert.Equal("unchanged", File.ReadAllText(file));
        if (before is not null) Assert.Equal(before, ChangeTime(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void StateFinalSymlinkIsRefusedWithoutChangingTargetOrCreatingStore(string suffix)
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target, RuntimePaths.DirMode);
        var link = Path.Combine(_root, "state-link");
        Directory.CreateSymbolicLink(link, target);
        var store = Path.Combine(_root, "store");

        var error = DaemonLifecycle.PrepareDirs(link + suffix, store);

        Assert.Equal("runtime.dir", error?.Check);
        Assert.Contains("is a symlink", error!.Message);
        Assert.Equal(RuntimePaths.DirMode, File.GetUnixFileMode(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public void UnsafeExistingStateIsNotAutomaticallyRepaired()
    {
        var state = Path.Combine(_root, "state");
        Directory.CreateDirectory(state);
        var mode = RuntimePaths.DirMode | UnixFileMode.GroupRead;
        File.SetUnixFileMode(state, mode);
        var store = Path.Combine(_root, "store");

        Assert.Contains("must be 0700", DaemonLifecycle.PrepareDirs(state, store)!.Message);
        Assert.Equal(mode, File.GetUnixFileMode(state));
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public void PrivateStateAndStoreAreCreatedAndParentAliasesRemainUsable()
    {
        var parent = Path.Combine(_root, "parent");
        Directory.CreateDirectory(parent, RuntimePaths.DirMode);
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, parent);
        var state = Path.Combine(alias, "state");
        var store = Path.Combine(_root, "store");

        Assert.Null(DaemonLifecycle.PrepareDirs(state, store));
        Assert.Equal(RuntimePaths.DirMode, File.GetUnixFileMode(state));
        Assert.Null(DaemonLifecycle.PrepareDirs(state, store));
    }

    [Fact]
    public void RuntimeStateStoreAndDoctorRejectAllowAclFacts()
    {
        var fs = new FakeConfigFileSystem().Dir("/private", allowAcl: true);
        Assert.Contains("ACL with an allow entry", RuntimePaths.CheckDir("/private", fs));
        Assert.Contains("ACL with an allow entry", RuntimePaths.CheckDir("/private", fs, "state"));
        Assert.Equal("paths.store", DaemonLifecycle.UntrustedStore("/private", fs)?.Check);
        Assert.Equal(CheckStatus.Fail, HostChecks.DataDir("paths.store", "/private", fs, _ => null).Status);
        Assert.Null(DaemonLifecycle.UntrustedStore("/private", new FakeConfigFileSystem().Dir("/private")));
        Assert.NotNull(DaemonLifecycle.UntrustedStore("/private", new FakeConfigFileSystem().Dir("/private", owned: false)));
    }

    [Fact]
    public void SetupResetRefusesAnAllowAclDirectoryBeforeReadingOrProbing()
    {
        const string path = "/cfg/config.json";
        var fs = new FakeConfigFileSystem().File(path, "unread-invalid-json").Dir("/cfg", allowAcl: true);
        var inputs = new ConfigInputs
        {
            Home = "/home/u", Platform = HostPlatform.Linux, UserId = 1000,
            Env = new Dictionary<string, string> { ["ABL_CONFIG"] = path }, FileSystem = fs,
            Blender = new BlenderSearch
            {
                Platform = HostPlatform.Linux, Home = "/home/u",
                Probe = (_, _) => throw new InvalidOperationException("must not probe"),
            },
        };
        var output = new StringWriter();

        Assert.Equal(1, Setup.Run(["--reset", "--json"], inputs, output, new StringWriter()));
        Assert.Contains("ACL with an allow entry", output.ToString());
        Assert.Contains("config.file", output.ToString());
        Assert.Empty(fs.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceInstallationRefusesUnsafeStateBeforeWritingOrCallingTheManager(bool symlink)
    {
        var home = Path.Combine(_root, "home");
        var state = DaemonPaths.StateDir(home, new Dictionary<string, string>());
        Directory.CreateDirectory(Path.GetDirectoryName(state)!, RuntimePaths.DirMode);
        var target = symlink ? Path.Combine(_root, "target") : state;
        var mode = symlink ? RuntimePaths.DirMode : RuntimePaths.DirMode | UnixFileMode.GroupRead;
        Directory.CreateDirectory(target, mode);
        if (symlink) Directory.CreateSymbolicLink(state, target);
        var inputs = new ConfigInputs
        {
            Home = home, Platform = HostPlatform.Linux, UserId = UnixIdentity.EffectiveUserId,
            Env = new Dictionary<string, string>(),
        };
        var unit = Path.Combine(_root, "units", "agentblender.service");
        var services = new SystemdUserService(unit, (_, _) => throw new InvalidOperationException("must not call service manager"));
        using var output = new StringWriter();

        var code = DaemonVerbs.InstallService(["--json"], output, new StringWriter(), inputs, services);

        Assert.Equal(1, code);
        Assert.Equal("runtime.dir", JsonNode.Parse(output.ToString())!["error"]!["check"]!.GetValue<string>());
        Assert.Contains(symlink ? "is a symlink" : "must be 0700", output.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Equal(mode, File.GetUnixFileMode(target));
        Assert.False(Directory.Exists(Path.GetDirectoryName(unit)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfigInspectionFailuresRemainStructuredAndNeverReadOrProbe(bool parent, bool denied)
    {
        const string path = "/cfg/config.json";
        var inner = new FakeConfigFileSystem().File(path, "unread");
        var inputs = new ConfigInputs
        {
            Home = "/home/u", Platform = HostPlatform.Linux, UserId = 1000,
            Env = new Dictionary<string, string> { ["ABL_CONFIG"] = path },
            FileSystem = new FailingInspection(inner, parent ? "/cfg" : path, denied),
            Blender = new BlenderSearch
            {
                Home = "/home/u", Platform = HostPlatform.Linux,
                Probe = (_, _) => throw new InvalidOperationException("must not probe"),
            },
        };

        var error = Assert.Throws<ConfigException>(() => AblConfig.Resolve(inputs));
        Assert.Equal("config.file", error.Check);
        Assert.Contains("cannot inspect", error.Message);
        Assert.Contains("permissions/ACLs", error.Hint);

        var host = new DoctorHost(inputs, _root, null, (_, _) => throw new InvalidOperationException("must not spawn"));
        using var output = new StringWriter();
        Assert.Equal(1, await Doctor.MainAsync(["--json", "--check", "config.file"], host, output, new StringWriter()));
        var report = JsonNode.Parse(output.ToString())!;
        Assert.Equal("doctor.v1", report["schema"]!.GetValue<string>());
        Assert.Equal("config.file", report["checks"]![0]!["id"]!.GetValue<string>());
        Assert.Contains("cannot inspect", report["checks"]![0]!["summary"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(report["checks"]![0]!["remedy"]!.GetValue<string>()));

        foreach (var reset in new[] { false, true })
        {
            using var setup = new StringWriter();
            Assert.Equal(1, Setup.Run(reset ? ["--json", "--reset"] : ["--json"], inputs, setup, new StringWriter()));
            var check = JsonNode.Parse(setup.ToString())!["checks"]![0]!;
            Assert.Equal("config.file", check["id"]!.GetValue<string>());
            Assert.Contains("cannot inspect", check["summary"]!.GetValue<string>());
            Assert.False(string.IsNullOrEmpty(check["remedy"]!.GetValue<string>()));
        }
        Assert.Empty(inner.Reads);
    }

    [Fact]
    public void LogAclRefusalNamesTheActualProblemAndRecovery()
    {
        var fs = new FakeConfigFileSystem().File("/logs/daemon.log", "not-read", allowAcl: true);
        var problem = DaemonLog.ReadProblem("/logs/daemon.log", fs);
        Assert.Contains("ACL with an allow entry", problem);
        Assert.Contains("chmod -N", problem);
        Assert.DoesNotContain("not a regular file", problem);
        var (path, note) = DaemonLog.Readable("/logs/daemon.log", "/logs/fallback.log", fs);
        Assert.Equal("/logs/fallback.log", path);
        Assert.Contains(problem!, note);
        Assert.Empty(fs.Reads);
    }

    private sealed class FailingInspection(IConfigFileSystem inner, string refused, bool denied) : IConfigFileSystem
    {
        public PathFacts? Inspect(string path) => path == refused
            ? throw (denied ? new UnauthorizedAccessException("metadata denied") : new IOException("ACL read failed"))
            : inner.Inspect(path);

        public string ReadAllText(string path) => throw new InvalidOperationException("must not read");
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/state/")]
    [InlineData("/")]
    public void RuntimeFallbackUsesTheDaemonStateLocation(string xdg)
    {
        var env = new Dictionary<string, string> { ["XDG_STATE_HOME"] = xdg };
        var expected = Path.Combine(DaemonPaths.StateDir("/home/u", env), "run");
        Assert.Equal(expected, RuntimePaths.StateRunDir("/home/u", xdg));
        Assert.Equal(expected, RuntimePaths.Default(HostPlatform.MacOS, "/home/u", 1000, env, new FakeConfigFileSystem()));
    }

    [Fact]
    public void SpecialFilesAreClassifiedWithoutOpeningAndRefusedByReaders()
    {
        var fifo = Path.Combine(_root, "fifo");
        using (var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!)
        {
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
        }
        var socketPath = Path.Combine(_root, "socket");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        foreach (var path in new[] { fifo, socketPath, "/dev/null" })
        {
            var facts = UnixConfigFileSystem.Instance.Inspect(path)!;
            Assert.True(facts.TargetExists);
            Assert.False(facts.IsRegularFile);
            Assert.False(facts.IsDirectory);
            Assert.Contains("not a regular file", ConfigTrust.CheckConfigFile(UnixConfigFileSystem.Instance, path));
            Assert.False(DaemonLog.IsOwnRegularFile(path));
        }
    }

    [Fact]
    public void DanglingLinkFactsDoNotInventAnOwner()
    {
        var link = Path.Combine(_root, "dangling");
        File.CreateSymbolicLink(link, Path.Combine(_root, "missing"));
        var facts = UnixConfigFileSystem.Instance.Inspect(link)!;
        Assert.True(facts.IsSymlink);
        Assert.False(facts.TargetExists);
        Assert.Null(facts.OwnerUserId);
        Assert.False(facts.OwnedByCurrentUser);
    }

    private static string ChangeTime(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("stat")
        {
            ArgumentList = { "-c", "%z", "--", path }, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000));
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
