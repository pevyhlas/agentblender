using Abl.Core.Lifecycle;
using Xunit;

namespace Abl.Integration.Tests;

public sealed partial class DaemonLifecycleTests
{
    [Theory(Timeout = 60_000)]
    [InlineData("config", false, "config.file")]
    [InlineData("config", true, "config.file")]
    [InlineData("state", false, "runtime.dir")]
    [InlineData("state", true, "runtime.dir")]
    [InlineData("store", false, "paths.store")]
    [InlineData("store", true, "paths.store")]
    [InlineData("lock", false, "daemon.port")]
    [InlineData("lock", true, "daemon.port")]
    public async Task IntentionalRunRefusalStopsBeforeLogRecordWorkerOrListener(string cause, bool launchd, string check)
    {
        var log = Path.Combine(PrivateDir("logs"), "daemon.log");
        File.WriteAllText(log, "previous log\n");
        File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(Store, Private);
        StoreLock? held = null;
        if (cause == "config")
            File.WriteAllText(Path.Combine(_root, "config.json"), "{\"version\":99}");
        if (cause == "state")
        {
            PrivateDir("state");
            Directory.CreateDirectory(Path.Combine(_root, "state", "agentblender"), Private | UnixFileMode.GroupRead);
        }
        if (cause == "store")
            File.SetUnixFileMode(Store, Private | UnixFileMode.GroupWrite);
        if (cause == "lock")
        {
            held = StoreLock.TryAcquire(Store, TimeSpan.Zero);
            Assert.NotNull(held);
            new DaemonState(Environment.ProcessId, _port, DaemonPaths.UrlFor(_port), "0.0.0",
                DateTimeOffset.UtcNow, "held", log, null).Write(Store);
        }
        var statePath = DaemonPaths.StateFile(Store);
        var stateBefore = File.Exists(statePath) ? File.ReadAllText(statePath) : null;
        try
        {
            var (code, stdout, stderr) = await RunAsync(["daemon", "run", "--log-file", log, .. launchd ? new[] { "--launchd" } : []]);

            Assert.Equal(launchd ? 0 : 78, code);
            Assert.Empty(stdout);
            Assert.Contains($"abl: {check}:", stderr);
            if (cause == "config")
                Assert.Contains("unsupported version 99 (this abl reads version 1)", stderr);
            else
                Assert.Contains("hint:", stderr);
            Assert.Equal(launchd, stderr.Contains("startup refused; launchd job stopped without retry", StringComparison.Ordinal));
            Assert.Equal("previous log\n", File.ReadAllText(log));
            Assert.False(File.Exists(log + ".1"));
            Assert.Equal(stateBefore, File.Exists(statePath) ? File.ReadAllText(statePath) : null);
            Assert.False(Stub.HasEnvironment);
            Assert.Equal(0, Stub.Connections);
            Assert.False(PortAnswers());
            Assert.Equal(cause == "lock", StoreLock.IsHeld(Store));
        }
        finally
        {
            held?.Dispose();
            File.SetUnixFileMode(Store, Private);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogOpenFailureRemainsRetryableInBothRunModes(bool launchd)
    {
        var log = Path.Combine(PrivateDir("logs"), "daemon.log");
        File.WriteAllText(log, "keep\n");
        File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var (code, _, stderr) = await RunAsync(["daemon", "run", "--log-file", log, .. launchd ? new[] { "--launchd" } : []]);
        Assert.Equal(1, code);
        Assert.Contains("cannot open the daemon log", stderr);
        Assert.DoesNotContain("job stopped without retry", stderr);
        Assert.Equal("keep\n", File.ReadAllText(log));
        Assert.False(StoreLock.IsHeld(Store));
        Assert.Null(DaemonState.Read(Store));
        Assert.False(Stub.HasEnvironment);
        Assert.False(PortAnswers());
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchdAdapterRequiresLogAndRejectsDetachment(bool detached)
    {
        var args = detached
            ? new[] { "daemon", "run", "--launchd", "--detached", "--log-file", Path.Combine(_root, "unused.log") }
            : ["daemon", "run", "--launchd"];
        var (code, _, stderr) = await RunAsync(args);
        Assert.Equal(2, code);
        Assert.Contains("needs --log-file FILE and cannot use --detached", stderr);
        Assert.False(Stub.HasEnvironment);
        Assert.Null(DaemonState.Read(Store));
    }
}
