using System.Net.Sockets;
using Abl.Core.Config;
using Abl.Core.Store;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T018b, FR-005 / AC-12, AC-13: socket-length arithmetic at the macOS (103/104/105) and
/// Linux (107/108/109) boundaries, the default runtime-dir order, and the per-spawn
/// final-component rule (symlink, foreign owner and wrong mode refused; a parent symlink
/// allowed) on the real disk and through the injected file system.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class RuntimePathsTests : IDisposable
{
    private readonly string _root;

    public RuntimePathsTests()
    {
        // Short on purpose: the real-disk cases must fit sun_path themselves.
        _root = Path.Combine(Path.GetTempPath(), "abl-rp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root, RuntimePaths.DirMode);
    }

    [Theory]
    [InlineData(HostPlatform.MacOS, 103, true)]
    [InlineData(HostPlatform.MacOS, 104, false)]
    [InlineData(HostPlatform.MacOS, 105, false)]
    [InlineData(HostPlatform.Linux, 107, true)]
    [InlineData(HostPlatform.Linux, 108, false)]
    [InlineData(HostPlatform.Linux, 109, false)]
    public void SocketPathMustFitSunPathMinusTheNul(HostPlatform platform, int socketPathBytes, bool fits)
    {
        var dir = DirForSocketLength(socketPathBytes);
        Assert.Equal(socketPathBytes, RuntimePaths.LongestSocketPathBytes(dir));

        var problem = RuntimePaths.CheckSocketLength(dir, platform);
        if (fits)
        {
            Assert.Null(problem);
            return;
        }

        // AC-12: the length and the limit are named.
        var limit = platform == HostPlatform.MacOS ? 103 : 107;
        Assert.Contains($"is {socketPathBytes} bytes; the {(platform == HostPlatform.MacOS ? "macOS" : "Linux")} limit is {limit}", problem);
        var error = Assert.Throws<ConfigException>(() => RuntimePaths.Prepare(dir, platform, UnixConfigFileSystem.Instance));
        Assert.Equal("runtime.socket-path", error.Check);
        Assert.False(Directory.Exists(dir)); // refused before anything is created
    }

    [Fact]
    public void TheLimitMatchesWhatThisOsSocketLayerAccepts()
    {
        // Cross-check of the arithmetic against .NET's own sun_path bound on this host.
        var platform = OperatingSystem.IsMacOS() ? HostPlatform.MacOS : HostPlatform.Linux;
        var max = RuntimePaths.MaxSocketPathBytes(platform);
        _ = new UnixDomainSocketEndPoint(Path.Combine(DirForSocketLength(max), RuntimePaths.LongestSocketName));
        Assert.ThrowsAny<ArgumentException>(
            () => new UnixDomainSocketEndPoint(Path.Combine(DirForSocketLength(max + 1), RuntimePaths.LongestSocketName)));
    }

    [Fact]
    public void DefaultOrderIsOwnedXdgRuntimeDirThenStateDirThenTmp()
    {
        var fs = new FakeConfigFileSystem().Dir("/run/user/1000");
        var env = new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = "/run/user/1000/" };

        Assert.Equal("/run/user/1000/agentblender", RuntimePaths.Default(HostPlatform.Linux, "/home/u", 1000, env, fs));

        // Not owned (or not a dir, or relative): the state dir.
        var foreign = new FakeConfigFileSystem().Dir("/run/user/1000", owned: false);
        Assert.Equal("/home/u/.local/state/agentblender/run", RuntimePaths.Default(HostPlatform.Linux, "/home/u", 1000, env, foreign));
        Assert.Equal(
            "/home/u/.local/state/agentblender/run",
            RuntimePaths.Default(HostPlatform.Linux, "/home/u", 1000, Env("XDG_RUNTIME_DIR", "run/user"), fs));

        // AC-13: macOS never uses XDG_RUNTIME_DIR; it gets the user-owned state dir.
        Assert.Equal("/Users/u/.local/state/agentblender/run", RuntimePaths.Default(HostPlatform.MacOS, "/Users/u", 501, env, fs));
        Assert.Equal(
            "/state/agentblender/run",
            RuntimePaths.Default(HostPlatform.MacOS, "/Users/u", 501, Env("XDG_STATE_HOME", "/state"), fs));

        // /tmp only when both user-owned choices are too long.
        var longHome = "/home/" + new string('h', 80);
        Assert.Equal("/tmp/abl-1000", RuntimePaths.Default(HostPlatform.Linux, longHome, 1000, Env(), fs));
        Assert.Equal("/private/tmp/abl-501", RuntimePaths.Default(HostPlatform.MacOS, longHome, 501, Env(), fs));
        Assert.Equal(
            "/run/user/1000/agentblender",
            RuntimePaths.Default(HostPlatform.Linux, longHome, 1000, env, fs)); // a short runtime dir still wins
    }

    [Fact]
    public void MissingDirIsCreatedPrivateAndThenAccepted()
    {
        var dir = Path.Combine(_root, "state", "run");
        RuntimePaths.Prepare(dir, Platform, UnixConfigFileSystem.Instance);

        Assert.Equal(RuntimePaths.DirMode, File.GetUnixFileMode(dir));
        RuntimePaths.Prepare(dir, Platform, UnixConfigFileSystem.Instance); // idempotent
    }

    [Fact]
    public void AnUncreatableDirFailsRuntimeDirNotARawIoError()
    {
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "");

        var refused = Assert.Throws<ConfigException>(
            () => RuntimePaths.Prepare(Path.Combine(file, "run"), Platform, UnixConfigFileSystem.Instance));

        Assert.Equal("runtime.dir", refused.Check);
        Assert.StartsWith($"cannot prepare runtime dir {Path.Combine(file, "run")}", refused.Message);
    }

    [Fact]
    public void FinalComponentSymlinkIsRefused()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real, RuntimePaths.DirMode);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);

        var error = Assert.Throws<ConfigException>(() => RuntimePaths.Prepare(link, Platform, UnixConfigFileSystem.Instance));
        Assert.Equal("runtime.dir", error.Check);
        Assert.Contains($"{link} is a symlink (to {real})", error.Message);
        Assert.Contains($"remove {link}", error.Hint);
    }

    [Fact]
    public void ParentSymlinkIsAllowed()
    {
        // /tmp → /private/tmp style: only the final component must be real.
        var real = Path.Combine(_root, "realparent");
        Directory.CreateDirectory(real, RuntimePaths.DirMode);
        var parentLink = Path.Combine(_root, "p");
        Directory.CreateSymbolicLink(parentLink, real);

        var dir = Path.Combine(parentLink, "run");
        RuntimePaths.Prepare(dir, Platform, UnixConfigFileSystem.Instance);
        Assert.True(Directory.Exists(Path.Combine(real, "run")));
    }

    [Fact]
    public void WrongModeAndForeignOwnerAndFilesAreRefused()
    {
        var open = Path.Combine(_root, "open");
        Directory.CreateDirectory(open);
        File.SetUnixFileMode(open, (UnixFileMode)0b111_101_101); // 0755
        var error = Assert.Throws<ConfigException>(() => RuntimePaths.Prepare(open, Platform, UnixConfigFileSystem.Instance));
        Assert.Equal("runtime.dir", error.Check);
        Assert.Contains("has mode 0755; it must be 0700", error.Message);

        var file = Path.Combine(_root, "file");
        File.WriteAllText(file, "");
        Assert.Contains("is not a directory", RuntimePaths.CheckDir(file, UnixConfigFileSystem.Instance));

        // A foreign owner cannot be made for real by a test: the injected seam simulates it.
        var foreign = new FakeConfigFileSystem().Dir("/tmp/abl-1000", owned: false);
        var refused = Assert.Throws<ConfigException>(() => RuntimePaths.Prepare("/tmp/abl-1000", HostPlatform.Linux, foreign));
        Assert.Contains("/tmp/abl-1000 is owned by another user", refused.Message);
    }

    [Fact]
    public async Task SpawnRefusesABadRuntimeDirBeforeStartingBlender()
    {
        // AC-12/13 at the spawn: the fake Blender would leave a marker if it ever ran.
        var marker = Path.Combine(_root, "ran");
        var blender = Path.Combine(_root, "blender");
        File.WriteAllText(blender, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(blender, RuntimePaths.DirMode);
        var real = Path.Combine(_root, "rt-real");
        Directory.CreateDirectory(real, RuntimePaths.DirMode);
        var link = Path.Combine(_root, "rt-link");
        Directory.CreateSymbolicLink(link, real);

        var symlinked = Supervisor(new AblOptions { BlenderPath = blender, RuntimeDir = link });
        var error = await Assert.ThrowsAsync<WorkerDeadException>(() => symlinked.SpawnAsync(prewarm: false, CancellationToken.None));
        Assert.StartsWith("runtime.dir: ", error.Message);
        Assert.Equal(WorkerSpawnStage.Preflight, error.Stage);
        Assert.IsType<ConfigException>(error.InnerException);

        var tooLong = Supervisor(new AblOptions { BlenderPath = blender, RuntimeDir = DirForSocketLength(200) });
        error = await Assert.ThrowsAsync<WorkerDeadException>(() => tooLong.SpawnAsync(prewarm: false, CancellationToken.None));
        Assert.StartsWith("runtime.socket-path: ", error.Message);

        var noBlender = Supervisor(new AblOptions { RuntimeDir = Path.Combine(_root, "rt") });
        error = await Assert.ThrowsAsync<WorkerDeadException>(() => noBlender.SpawnAsync(prewarm: false, CancellationToken.None));
        Assert.StartsWith("blender.found: ", error.Message);

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void CheckpointStoreRootIsPrivateOnEveryUnix()
    {
        var store = Path.Combine(_root, "store");
        _ = new CheckpointStore(store);
        Assert.Equal(RuntimePaths.DirMode, File.GetUnixFileMode(store));
    }

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

    private static Dictionary<string, string> Env(params string[] pairs) =>
        pairs.Chunk(2).ToDictionary(p => p[0], p => p[1]);

    private static HostPlatform Platform => OperatingSystem.IsMacOS() ? HostPlatform.MacOS : HostPlatform.Linux;

    private static WorkerSupervisor Supervisor(AblOptions options) => new(options with { Platform = Platform }, NullLogger<WorkerSupervisor>.Instance);

    /// <summary>A dir under the test root whose longest socket path is exactly <paramref name="bytes"/> long.</summary>
    private string DirForSocketLength(int bytes)
    {
        var fixedPart = RuntimePaths.LongestSocketPathBytes(Path.Combine(_root, "d"));
        return Path.Combine(_root, "d" + new string('x', bytes - fixedPart));
    }

    [Theory]
    [InlineData("w-0a1b2c3d.sock", true)]
    [InlineData("w-0A1B2C3D.sock", false)]
    [InlineData("w-0a1b2c3.sock", false)]
    [InlineData("w-0a1b2c3d4.sock", false)]
    [InlineData("w-0a1b2c3g.sock", false)]
    [InlineData("x-0a1b2c3d.sock", false)]
    [InlineData("w-0a1b2c3d.socket", false)]
    [InlineData("w-.sock", false)]
    public void IsSocketFileNameIsTheInverseOfSocketFileName(string name, bool isSocket)
    {
        // 2a round-3 eng-arch LOW-3: the stop sweep reads worker sockets by this one rule.
        Assert.Equal(isSocket, RuntimePaths.IsSocketFileName(name));
        Assert.True(RuntimePaths.IsSocketFileName(RuntimePaths.SocketFileName(Guid.NewGuid().ToString("N")[..8])));
    }
}
