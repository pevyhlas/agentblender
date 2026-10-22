using System.Text.Json.Nodes;
using Abl.Cli.Host;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T030–T034 building blocks, hermetic: the N2 self command, the lifecycle file
/// locations (L-c), the store lock (M2, L-d), the instance record, the detached daemon's
/// environment allowlist (A6), <c>lastWorkerError</c> bounding (B5), log rotation and tail
/// (N3), and the signal primitives. The process-level proofs are
/// <c>Abl.Integration.Tests.DaemonLifecycleTests</c>.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class DaemonLifecycleUnitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-lcu-" + Guid.NewGuid().ToString("N"));

    public DaemonLifecycleUnitTests() =>
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData("/opt/homebrew/opt/dotnet/libexec/dotnet")]
    [InlineData("/home/u/.dotnet/dotnet.exe")]
    public void UnderTheDotnetMuxerTheEntryAssemblyFollows(string muxer)
    {
        // N2: `dotnet abl.dll daemon start` must re-spawn `dotnet abl.dll daemon run`, not `dotnet daemon run`.
        var (program, prefix) = SelfCommand.Resolve(muxer, "/opt/abl/abl.dll");

        Assert.Equal(muxer, program);
        Assert.Equal(["/opt/abl/abl.dll"], prefix);
    }

    [Fact]
    public void AnApphostRunsAsItself()
    {
        var (program, prefix) = SelfCommand.Resolve("/home/u/.dotnet/tools/abl", "/home/u/.dotnet/tools/.store/abl.dll");

        Assert.Equal("/home/u/.dotnet/tools/abl", program);
        Assert.Empty(prefix);
    }

    [Theory]
    [InlineData(null, "/opt/abl/abl.dll")]
    [InlineData("/usr/bin/dotnet", null)]
    [InlineData("/usr/bin/dotnet", "")]
    public void AnUnknownSelfIsRefused(string? processPath, string? entryAssembly) =>
        Assert.Throws<InvalidOperationException>(() => SelfCommand.Resolve(processPath, entryAssembly));

    [Fact]
    public void TheCurrentSelfCommandNamesARunnableProgram()
    {
        var (program, prefix) = SelfCommand.Current();

        Assert.True(File.Exists(program), program);
        Assert.All(prefix, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void StateDirFollowsAnAbsoluteXdgStateHomeOnly()
    {
        Assert.Equal("/x/state/agentblender", DaemonPaths.StateDir("/home/u", new Dictionary<string, string> { ["XDG_STATE_HOME"] = "/x/state/" }));
        Assert.Equal("/home/u/.local/state/agentblender", DaemonPaths.StateDir("/home/u", new Dictionary<string, string> { ["XDG_STATE_HOME"] = "rel" }));
        Assert.Equal("/home/u/.local/state/agentblender", DaemonPaths.StateDir("/home/u", new Dictionary<string, string>()));
    }

    [Fact]
    public void TheLogIsNamedPerStoreAndTheLockAndRecordLiveInTheStore()
    {
        // L-c: two stores sharing a state dir never share (or rotate) one log.
        var a = DaemonPaths.LogFile("/s/agentblender", "/data/store-a");
        var b = DaemonPaths.LogFile("/s/agentblender", "/data/store-b");

        Assert.NotEqual(a, b);
        Assert.Equal(a, DaemonPaths.LogFile("/s/agentblender", "/data/store-a/"));
        Assert.Matches(@"^/s/agentblender/daemon-[0-9a-f]{12}\.log$", a);
        Assert.Equal("/data/store-a/daemon.lock", DaemonPaths.LockFile("/data/store-a"));
        Assert.Equal("/data/store-a/daemon.json", DaemonPaths.StateFile("/data/store-a"));
    }

    [Fact]
    public void TheStoreLockIsExclusiveUntilReleased()
    {
        using (var held = StoreLock.TryAcquire(_root, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(held.Path));
            Assert.True(StoreLock.IsHeld(_root));

            // L-d: a second taker retries (here ~0.3 s) and then gives up.
            var started = DateTime.UtcNow;
            Assert.Null(StoreLock.TryAcquire(_root, TimeSpan.FromMilliseconds(300)));
            Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(300));
        }

        Assert.False(StoreLock.IsHeld(_root));
        using var again = StoreLock.TryAcquire(_root, TimeSpan.Zero);
        Assert.NotNull(again);
    }

    [Fact]
    public void TwoProbesNeverSeeEachOtherAsTheHolder()
    {
        // 2a round-2 eng-code L-1: a probe takes LOCK_SH, so an overlapping probe (held open
        // here) is not reported as a daemon, while the daemon's exclusive lock still conflicts.
        Assert.False(StoreLock.IsHeld(_root)); // creates the lock file
        using (new FileStream(DaemonPaths.LockFile(_root), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(StoreLock.IsHeld(_root));
            Assert.Null(StoreLock.TryAcquire(_root, TimeSpan.Zero)); // a probe in progress delays a daemon (it retries)
        }

        using var held = StoreLock.TryAcquire(_root, TimeSpan.Zero);
        Assert.NotNull(held);
        Assert.True(StoreLock.IsHeld(_root));
    }

    [Fact]
    public async Task TheLockIsTakenWhenItIsReleasedDuringTheRetry()
    {
        var held = StoreLock.TryAcquire(_root, TimeSpan.Zero)!;
        var release = Task.Run(async () =>
        {
            await Task.Delay(300);
            held.Dispose();
        });

        using var taken = StoreLock.TryAcquire(_root, TimeSpan.FromSeconds(5));
        Assert.NotNull(taken);
        await release;
    }

    [Fact]
    public void TheInstanceRecordRoundTripsPrivately()
    {
        var state = new DaemonState(4242, 7811, "http://127.0.0.1:7811", "0.1.0-dev", DateTimeOffset.Parse("2026-10-21T09:00:00Z"), "sha256:ab", "/s/daemon-0123456789ab.log", 4242);

        state.Write(_root);

        Assert.Equal(state, DaemonState.Read(_root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(DaemonPaths.StateFile(_root)));
        Assert.Equal(["daemon.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
        var json = JsonNode.Parse(File.ReadAllText(DaemonPaths.StateFile(_root)))!.AsObject();
        Assert.Equal(["pid", "port", "url", "version", "startedAt", "effectiveConfigDigest", "logFile", "processGroup"], json.Select(p => p.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[]")]
    public void AnUnreadableRecordIsNoRecord(string content)
    {
        File.WriteAllText(DaemonPaths.StateFile(_root), content);

        Assert.Null(DaemonState.Read(_root));
    }

    [Fact]
    public void TheRecordIsNeverWrittenThroughALinkAtItsTempPath()
    {
        // 2a round-2 eng-test L-3 (round-1 eng-security LOW-3): a symlink planted at the temp
        // path is removed, and its target is untouched.
        var target = Path.Combine(_root, "elsewhere");
        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink($"{DaemonPaths.StateFile(_root)}.tmp-{Environment.ProcessId}", target);
        var state = new DaemonState(4242, 8765, "http://127.0.0.1:8765", "1.0.0", DateTimeOffset.UtcNow, "abc", null, null);

        state.Write(_root);

        Assert.Equal("keep", File.ReadAllText(target));
        Assert.Equal(state, DaemonState.Read(_root));
        Assert.Null(new FileInfo(DaemonPaths.StateFile(_root)).LinkTarget);
    }

    [Fact]
    public void ARecordThatIsALinkOrTooLargeIsNoRecord()
    {
        // 2a round-2 eng-security LOW-1: the read is bounded and never follows a link.
        var real = Path.Combine(_root, "real.json");
        new DaemonState(4242, 8765, "http://127.0.0.1:8765", "1.0.0", DateTimeOffset.UtcNow, "abc", null, null).Write(_root);
        File.Move(DaemonPaths.StateFile(_root), real);
        File.CreateSymbolicLink(DaemonPaths.StateFile(_root), real);
        Assert.Null(DaemonState.Read(_root));

        File.Delete(DaemonPaths.StateFile(_root));
        File.WriteAllText(DaemonPaths.StateFile(_root), "{\"pid\": 1" + new string(' ', DaemonState.MaxBytes) + "}");
        Assert.Null(DaemonState.Read(_root));
    }

    [Fact]
    public void ThePsTableParsesAndYieldsEveryDescendant()
    {
        // Round-2 eng-code M-1: a daemon without its own group is swept by its descendants.
        var table = ProcessTree.Parse(
            "    1     0 /sbin/init\n" +
            "  100     1 dotnet /opt/abl/abl.dll daemon run\n" +
            "  101   100 /bin/sh /opt/fake-blender --background -- /run/abl/w-0a1b2c3d.sock\n" +
            "  102   101 sleep 300\n" +
            "  103   100 blender --background --python worker.py -- /run/abl/w-ffffffff.sock\n" +
            "  200     1 unrelated\n" +
            "  201   200 child of unrelated\n" +
            "not a row\n");

        Assert.Equal(7, table.Count);
        Assert.Equal("dotnet /opt/abl/abl.dll daemon run", table[1].Args);
        Assert.Equal([101, 103, 102], ProcessTree.DescendantsOf(table, 100).Select(r => r.Pid));
        Assert.Empty(ProcessTree.DescendantsOf(table, 102));

        var host = ProcessTree.Snapshot();
        Assert.Null(host.Unreadable);
        Assert.Contains(host.Rows!, r => r.Pid == Environment.ProcessId);
    }

    [Fact]
    public void TheSnapshotNeverStartsAProgramByARelativeName()
    {
        // 2a round-3: Process.Start resolves a slash-free name through the current directory
        // and PATH, so a planted `ps` there would run as the user. Only an absolute ps runs.
        Assert.All(ProcessTree.PsPaths, p => Assert.True(Path.IsPathFullyQualified(p), p));

        var planted = Path.Combine(_root, "ps");
        File.WriteAllText(planted, "#!/bin/sh\nexit 0\n");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, planted);
        Assert.True(File.Exists(relative), "the relative name resolves from the current directory");
        var started = new List<string>();
        (int, string)? Record(string path, IReadOnlyList<string> args)
        {
            started.Add(path);
            return (0, $"{Environment.ProcessId} 1 self\n");
        }

        var table = ProcessTree.Snapshot(new ProcessTree.Source(null, ["ps", relative, "./ps", "/nonexistent/ps"], Record, Environment.ProcessId));

        Assert.Empty(started);
        Assert.Null(table.Rows);
        Assert.Equal("the process table cannot be read (no ps at /nonexistent/ps)", table.Unreadable);
        Assert.Empty(table.DescendantsOf(1));

        // The same file by its absolute path is what runs.
        Assert.NotNull(ProcessTree.Snapshot(new ProcessTree.Source(null, ["ps", planted], Record, Environment.ProcessId)).Rows);
        Assert.Equal([planted], started);
    }

    public static TheoryData<int, string, string> UnreadableTables => new()
    {
        { 1, "  7 1 x\n", "exited with code 1" },
        { 0, "ps: unknown option -- ww\n", "output does not list this process" }, // a ps without POSIX -o
        { 0, "  7 1 x\n", "output does not list this process" }, // a table without the caller is not one
        { -1, "", "could not be started" },
    };

    [Theory]
    [MemberData(nameof(UnreadableTables))]
    public void AnUnreadableTableIsUnknownNeverEmpty(int exitCode, string output, string reason)
    {
        // 2a round-3: an empty table would read as "no workers", so `stop` would claim AC-20.
        var ps = Path.Combine(_root, "ps");
        File.WriteAllText(ps, "");
        var table = ProcessTree.Snapshot(new ProcessTree.Source(
            null, [ps], (_, _) => exitCode < 0 ? null : (exitCode, output), Self: 4242));

        Assert.Null(table.Rows);
        Assert.Contains($"{ps} {reason}", table.Unreadable);
        Assert.Empty(table.DescendantsOf(1));

        // `stop` reports this for a daemon without its own group; a grouped one is checked by its group.
        Assert.Equal(table.Unreadable, table.UncheckedFor(processGroup: null));
        Assert.Null(table.UncheckedFor(processGroup: 4242));
        Assert.Null(ProcessTree.Snapshot().UncheckedFor(processGroup: null));
    }

    [Fact]
    public void ProcIsReadWithoutAnyProgramAndFallsBackToPsWithoutTheCaller()
    {
        // 2a round-3: on Linux the table is the kernel's /proc, so no ps is needed there.
        var proc = Path.Combine(_root, "proc");
        void Process(string pid, string stat, string? cmdline)
        {
            var dir = Directory.CreateDirectory(Path.Combine(proc, pid)).FullName;
            File.WriteAllText(Path.Combine(dir, "stat"), stat);
            if (cmdline is not null)
            {
                File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline);
            }
        }

        Process("4242", "4242 (dotnet) S 1 4242 4242 0 -1", "dotnet\0/opt/abl/abl.dll\0daemon\0run\0");
        Process("4300", "4300 (fake (blen) der) S 4242 4242 4242 0 -1", "/bin/sh\0/opt/fake-blender\0--\0/run/abl/w-0a1b2c3d.sock\0");
        Process("4301", "4301 (blender) Z 4242 4242 4242 0 -1", "");
        Process("4302", "4302 (sleep) S 4300 4242 4242 0 -1", "sleep\0300\0");
        Process("4400", "4400 (gone) S 4242", null); // exited between the listing and the read
        Process("4401", "garbage", "x\0");
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        var started = new List<string>();
        (int, string)? Record(string path, IReadOnlyList<string> args)
        {
            started.Add(path);
            return (0, "  4242 1 dotnet from ps\n  4243 4242 child from ps\n");
        }

        var table = ProcessTree.Snapshot(new ProcessTree.Source(proc, ["/bin/sh"], Record, Self: 4242));

        Assert.Empty(started);
        Assert.Null(table.Unreadable);
        Assert.Equal(
            ["4300 4242 /bin/sh /opt/fake-blender -- /run/abl/w-0a1b2c3d.sock", "4301 4242 [blender] <defunct>", "4302 4300 sleep 300"],
            table.DescendantsOf(4242).Select(r => $"{r.Pid} {r.ParentPid} {r.Args}").Order(StringComparer.Ordinal));

        // A /proc that does not list the caller (not mounted, or another pid namespace) is
        // not a table: ps at its absolute path is read instead.
        var other = ProcessTree.Snapshot(new ProcessTree.Source(proc, ["/bin/sh"], Record, Self: 4243));
        Assert.Equal(["/bin/sh"], started);
        Assert.Equal([4243], other.DescendantsOf(4242).Select(r => r.Pid));

        var neither = ProcessTree.Snapshot(new ProcessTree.Source(proc, ["/nonexistent/ps"], Record, Self: 9));
        Assert.Equal($"the process table cannot be read ({proc} does not list this process; no ps at /nonexistent/ps)", neither.Unreadable);
    }

    [Theory]
    [InlineData("HOME", true)]
    [InlineData("PATH", true)]
    [InlineData("DOTNET_ROOT", true)]
    [InlineData("ABL_PORT", true)]
    [InlineData("ABL_DESKTOP_TOKEN", true)] // a configuration input (FR-002 precedence)
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", true)]
    [InlineData("XDG_STATE_HOME", true)]
    [InlineData("Logging__LogLevel__Default", true)]
    [InlineData("LC_ALL", true)]
    [InlineData("ANTHROPIC_API_KEY", false)]
    [InlineData("OPENAI_API_KEY", false)]
    [InlineData("GITHUB_TOKEN", false)]
    [InlineData("LD_PRELOAD", false)]
    [InlineData("DOTNET_STARTUP_HOOKS", false)]
    [InlineData("ASPNETCORE_URLS", false)]
    [InlineData("Kestrel__Endpoints__Http__Url", false)]
    [InlineData("abl_port", false)]
    public void TheDetachedDaemonKeepsOnlyItsInputs(string name, bool kept)
    {
        // Plan A6: agent API keys and stray variables never reach the daemon.
        Assert.Equal(kept, ChildEnvironment.AllowsForDaemon(name));
    }

    [Theory]
    [InlineData("CUDA_VISIBLE_DEVICES", true)] // listed: reaches workers through `daemon start` (AC-14)
    [InlineData("OCIO", true)]
    [InlineData("HOME", true)] // the A6 list still applies
    [InlineData("NVIDIA_DRIVER_CAPABILITIES", false)] // not listed
    [InlineData("ANTHROPIC_API_KEY", false)]
    [InlineData("PYTHONPATH", false)] // listed, but PYTHON* never passes
    [InlineData("DOTNET_STARTUP_HOOKS", false)] // listed, but it would steer the daemon's runtime
    [InlineData("ASPNETCORE_URLS", false)]
    [InlineData("aspnetcore_hostingstartupassemblies", false)]
    [InlineData("KESTREL__ENDPOINTS__HTTP__URL", false)]
    [InlineData("COREHOST_TRACE", false)]
    [InlineData("URLS", false)] // unprefixed ASP.NET host keys (round-2 eng-code L-3)
    [InlineData("http_ports", false)]
    [InlineData("HTTPS_PORTS", false)]
    [InlineData("AllowedHosts", false)]
    [InlineData("LD_LIBRARY_PATH", true)] // a loader variable listed for workers reaches the daemon deliberately (plan A6)
    public void TheDetachedDaemonAlsoKeepsTheWorkerPassthroughNames(string name, bool kept)
    {
        string[] passthrough =
            ["CUDA_VISIBLE_DEVICES", "OCIO", "PYTHONPATH", "DOTNET_STARTUP_HOOKS", "ASPNETCORE_URLS",
             "aspnetcore_hostingstartupassemblies", "KESTREL__ENDPOINTS__HTTP__URL", "COREHOST_TRACE",
             "URLS", "http_ports", "HTTPS_PORTS", "AllowedHosts", "LD_LIBRARY_PATH"];

        Assert.Equal(kept, ChildEnvironment.AllowsForDetachedDaemon(name, passthrough));
    }

    [Fact]
    public async Task TheDaemonHostRefusesAConfigResolvedWithoutABlenderSearch()
    {
        // T030 (1b eng-arch LOW-A, eng-test L-2): the daemon never spawns an unchecked Blender.
        var config = AblConfig.Resolve(new ConfigInputs { Env = new Dictionary<string, string>(), Home = "/home/u", FileSystem = new FakeConfigFileSystem() });
        Assert.Null(config.Blender);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Abl.Daemon.DaemonHost.RunAsync(
            config, new DaemonInstance(Environment.ProcessId, DateTimeOffset.UtcNow, _root, null, 7811, config.EffectiveDigest, Detached: false), CancellationToken.None));
        Assert.Contains("WithBlenderSearch", error.Message);
    }

    [Fact]
    public void LastWorkerErrorIsBoundedCleanedAndCarriesTheConfigCheck()
    {
        var at = DateTimeOffset.Parse("2026-10-21T09:00:00Z");
        var refusal = new WorkerDeadException("runtime.dir: bad\nmode", new ConfigException("runtime.dir", "bad mode", "fix it"));
        var plain = new WorkerDeadException(new string('x', 900) + "\u001b[31m");

        var configError = WorkerError.From("spawn", refusal, at);
        var plainError = WorkerError.From("spawn", plain, at);

        Assert.Equal(new WorkerError("spawn", "runtime.dir: bad mode", "runtime.dir", at), configError);
        Assert.Null(plainError.Check);
        Assert.Equal(WorkerError.MaxMessageLength, plainError.Message.Length);
        Assert.DoesNotContain(plainError.Message, char.IsControl);
    }

    [Fact]
    public void ALogOverTheLimitIsRotatedKeepingOnePreviousFile()
    {
        var log = Path.Combine(_root, "daemon.log");
        File.WriteAllText(log + ".1", "older");
        using (var big = File.Create(log))
        {
            big.SetLength(DaemonLog.RotateBytes + 1);
        }

        DaemonLog.Rotate(log);

        Assert.False(File.Exists(log));
        Assert.Equal(DaemonLog.RotateBytes + 1, new FileInfo(log + ".1").Length);

        File.WriteAllText(log, "small");
        DaemonLog.Rotate(log);
        Assert.Equal("small", File.ReadAllText(log)); // at or under the limit: kept
    }

    [Fact]
    public void TheTailIsBoundedAndCleaned()
    {
        var log = Path.Combine(_root, "daemon.log");
        File.WriteAllText(log, string.Concat(Enumerable.Range(1, 30).Select(i => $"line {i}\n")) + "esc \u001b[31mred\ttab\r\n");

        var tail = DaemonLog.Tail(log, 3);

        Assert.Equal(["line 29", "line 30", "esc ?[31mred\ttab"], tail);
    }

    [Fact]
    public void LogsReadsTheRecordedLogOnlyWhenItIsARegularFileTheUserOwns()
    {
        // 008 T021 (2a round-4 eng-security LOW-2, the reader half), built with 009 B1: the
        // record's logFile is read only when it is a regular file the user owns; a symlink,
        // FIFO, directory or missing file falls back to the derived log with a note.
        var derived = Path.Combine(_root, "derived.log");
        var recorded = Path.Combine(_root, "recorded.log");
        File.WriteAllText(recorded, "recorded");

        Assert.Equal((recorded, (string?)null), DaemonLog.Readable(recorded, derived));
        Assert.Equal((derived, (string?)null), DaemonLog.Readable(null, derived));
        Assert.Equal((derived, (string?)null), DaemonLog.Readable(derived, derived));
        Assert.True(DaemonLog.IsOwnRegularFile(recorded));

        var link = Path.Combine(_root, "link.log");
        File.CreateSymbolicLink(link, recorded);
        var fifo = Path.Combine(_root, "fifo.log");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [fifo]))
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        foreach (var refused in new[] { link, fifo, "/dev/null", _root, Path.Combine(_root, "missing.log") })
        {
            Assert.False(DaemonLog.IsOwnRegularFile(refused), refused);
            var (path, note) = DaemonLog.Readable(refused, derived);
            Assert.Equal(derived, path);
            Assert.Contains(refused, note);
            Assert.Contains("not a regular file you own", note);
        }
    }

    [Fact]
    public void TheTailOfALargeLogSkipsThePartialFirstLine()
    {
        var log = Path.Combine(_root, "daemon.log");
        File.WriteAllText(log, new string('a', 300 * 1024) + "\nlast\n");

        Assert.Equal(["last"], DaemonLog.Tail(log, 5));
    }

    [Fact]
    public void SignalsReportExistenceAndRefuseBroadcasts()
    {
        Assert.True(UnixIdentity.ProcessExists(Environment.ProcessId));
        Assert.False(UnixIdentity.ProcessExists(int.MaxValue));
        Assert.False(UnixIdentity.GroupExists(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnixIdentity.Signal(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnixIdentity.Signal(-1, 0));
    }

    [Fact]
    public async Task DisposingThePoolCancelsAndAwaitsASpawnInFlight()
    {
        // Round-4 eng-test L-2 (AC-20): the shutdown token reaches the spawn in flight, so a
        // connecting worker is killed before the daemon exits, and that is no worker error.
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var options = new AblOptions { RuntimeDir = _root, PoolFloor = 1 };
        var pool = new WorkerPool(
            options,
            new WorkerSupervisor(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerSupervisor>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerPool>.Instance)
        {
            Spawner = async ct =>
            {
                started.SetResult(ct);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("not reached");
                }
                finally
                {
                    finished = true;
                }
            },
        };

        pool.Replenish();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(token.IsCancellationRequested, "the spawn in flight never saw the shutdown");
        Assert.True(finished, "disposal returned before the spawn in flight ended");
        Assert.Null(pool.LastWorkerError);
    }
}
