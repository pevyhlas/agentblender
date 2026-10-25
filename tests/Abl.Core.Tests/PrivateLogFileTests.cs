using System.Diagnostics;
using System.Text;
using Abl.Cli.Host;
using Abl.Core.Config;
using Xunit;

namespace Abl.Core.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class PrivateLogFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-pl-" + Guid.NewGuid().ToString("N"));
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public PrivateLogFileTests() => Directory.CreateDirectory(_root, Private | UnixFileMode.UserExecute);
    public void Dispose() => Directory.Delete(_root, true);
    private string PathOf(string name) => Path.Combine(_root, name);

    private void Write(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, Private);
    }

    [Fact]
    public void CreateAppendAndRotatePreservePrivateBytesAndModes()
    {
        var path = PathOf("new/daemon.log");
        using (var handle = PrivateLogFile.OpenAppend(path))
        using (var stream = new FileStream(handle, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes("first\n"));
        }

        Assert.Equal(Private, File.GetUnixFileMode(path));
        Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        using (var handle = PrivateLogFile.OpenAppend(path))
        using (var stream = new FileStream(handle, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes("second\n"));
        }

        Assert.Equal("first\nsecond\n", File.ReadAllText(path));
        using (var handle = PrivateLogFile.OpenAppend(path, rotateBytes: 3))
        {
            Assert.Equal(0, RandomAccess.GetLength(handle));
        }

        Assert.Equal("first\nsecond\n", File.ReadAllText(path + ".1"));
        Assert.Equal(Private, File.GetUnixFileMode(path + ".1"));
        Assert.Equal(Private, File.GetUnixFileMode(path));
    }

    [Theory]
    [InlineData("link")]
    [InlineData("dangling")]
    [InlineData("fifo")]
    [InlineData("directory")]
    [InlineData("hardlink")]
    [InlineData("wide")]
    public void UnsafeActiveAndPreviousEntriesRefuseWithoutRepair(string kind)
    {
        var sentinel = PathOf("sentinel");
        Write(sentinel, "untouched secret");
        var path = PathOf("log");
        Plant(path, kind, sentinel);
        var mode = File.GetUnixFileMode(sentinel);
        Assert.ThrowsAny<IOException>(() => PrivateLogFile.OpenRead(path));
        Assert.ThrowsAny<IOException>(() => PrivateLogFile.OpenAppend(path, 1));
        Assert.Equal("untouched secret", File.ReadAllText(sentinel));
        Assert.Equal(mode, File.GetUnixFileMode(sentinel));
        Assert.False(File.Exists(path + ".1"));
        if (kind == "directory") { Directory.Delete(path); }
        else { File.Delete(path); }
        // The .1 entry is inspected before the active name or bytes change.
        Write(path, "active retained");
        Plant(path + ".1", kind, sentinel);
        Assert.ThrowsAny<IOException>(() => PrivateLogFile.OpenAppend(path, 1));
        Assert.Equal("active retained", File.ReadAllText(path));
        Assert.Equal(Private, File.GetUnixFileMode(path));
        Assert.Equal("untouched secret", File.ReadAllText(sentinel));
    }

    [Fact]
    public void WideAncestorAndArbitrarySymlinkAreRefusedBeforeCreation()
    {
        var wide = PathOf("wide");
        Directory.CreateDirectory(wide);
        File.SetUnixFileMode(wide, (UnixFileMode)0x1ff);
        Assert.Throws<IOException>(() => PrivateLogFile.OpenAppend(Path.Combine(wide, "new/log")));
        Assert.False(Directory.Exists(Path.Combine(wide, "new")));
        Assert.Equal((UnixFileMode)0x1ff, File.GetUnixFileMode(wide));
        var link = PathOf("alias");
        Directory.CreateSymbolicLink(link, _root);
        Assert.Throws<IOException>(() => PrivateLogFile.OpenAppend(Path.Combine(link, "log")));
        Assert.False(File.Exists(PathOf("log")));
    }

    [Fact]
    public void OpenedParentSurvivesAncestorSubstitution()
    {
        var parent = PathOf("parent");
        var moved = PathOf("moved");
        Directory.CreateDirectory(parent, Private | UnixFileMode.UserExecute);
        var path = Path.Combine(parent, "log");
        Write(path, "original");
        using var handle = PrivateLogFile.Open(path, write: false, rotateBytes: null, afterDirectory: visited =>
        {
            if (visited == parent)
            {
                Directory.Move(parent, moved);
                Directory.CreateDirectory(parent, Private | UnixFileMode.UserExecute);
                Write(path, "substitute");
            }
        });
        var bytes = new byte[8];
        Assert.Equal(8, RandomAccess.Read(handle, bytes, 0));
        Assert.Equal("original", Encoding.UTF8.GetString(bytes));
        Assert.Equal("substitute", File.ReadAllText(path));
    }

    [Fact]
    public void ChangedLeafIdentityRefusesBeforeWriting()
    {
        var path = PathOf("log");
        Write(path, "original");
        Assert.Throws<IOException>(() => PrivateLogFile.Open(path, true, null, beforeFileOpen: () =>
        {
            File.Move(path, path + ".saved");
            Write(path, "substitute");
        }));
        Assert.Equal("original", File.ReadAllText(path + ".saved"));
        Assert.Equal("substitute", File.ReadAllText(path));
    }

    [Fact]
    public void ReplacementFailureRetainsRotatedBytesWithoutFollowingPlantedLink()
    {
        var path = PathOf("log");
        var sentinel = PathOf("sentinel");
        Write(path, "old bytes");
        Write(sentinel, "secret");
        Assert.Throws<IOException>(() => PrivateLogFile.Open(path, true, 1,
            beforeReplacement: () => File.CreateSymbolicLink(path, sentinel)));
        Assert.Equal("old bytes", File.ReadAllText(path + ".1"));
        Assert.Equal("secret", File.ReadAllText(sentinel));
        Assert.Equal(Private, File.GetUnixFileMode(sentinel));
    }

    [Fact]
    public void ReadersNeverCreateAndEveryTailValidatesItsActualOpen()
    {
        var missing = PathOf("missing/log");
        Assert.Throws<FileNotFoundException>(() => DaemonLog.Tail(missing, 10));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        var path = PathOf("derived.log");
        File.WriteAllText(path, "sensitive");
        File.SetUnixFileMode(path, Private | UnixFileMode.OtherRead);
        Assert.Throws<IOException>(() => DaemonLog.Tail(path, 10));
        Assert.Contains("log tail refused", Assert.Single(DaemonLog.FailureTail(path, 10)));
        Assert.DoesNotContain("sensitive", string.Join('\n', DaemonLog.FailureTail(path, 10)));
        Assert.Equal("sensitive", File.ReadAllText(path));
    }

    [Fact]
    public void CapturedTailWindowIgnoresGrowthAndToleratesTruncation()
    {
        var path = PathOf("log");
        Write(path, "first\nlast\n");
        var tail = DaemonLog.Tail(path, int.MaxValue, _ => File.AppendAllText(path, new string('x', 2_000_000)));
        Assert.Equal(["first", "last"], tail);
        Write(path, "first\nlast\n");
        Assert.Empty(DaemonLog.Tail(path, 10, _ => File.WriteAllText(path, "")));
        Write(path, string.Concat(Enumerable.Repeat("line\n", 1100)));
        Assert.Equal(1000, DaemonLog.Tail(path, int.MaxValue).Count);
    }

    [Fact]
    public void DarwinAllowAclRefusesReadAndRotationWithoutChangingBytes()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        var path = PathOf("log");
        Write(path, "retained");
        Run("chmod", "+a", $"user:{UnixIdentity.EffectiveUserId} allow read", path);
        Assert.Throws<IOException>(() => PrivateLogFile.OpenRead(path));
        Assert.Throws<IOException>(() => PrivateLogFile.OpenAppend(path, 1));
        Assert.Equal("retained", File.ReadAllText(path));
        Assert.True(UnixIdentity.HasAllowAcl(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialDupAndDirectoryFailuresRestoreEveryOriginalDescriptor(bool failDirectory)
    {
        // Real dup2/fcntl over isolated descriptors, leaving the test runner's 0/1/2 alone.
        using var input = File.OpenHandle(PathOf("input"), FileMode.Create, FileAccess.ReadWrite);
        using var output = File.OpenHandle(PathOf("output"), FileMode.Create, FileAccess.ReadWrite);
        using var error = File.OpenHandle(PathOf("error"), FileMode.Create, FileAccess.ReadWrite);
        RandomAccess.Write(input, "input"u8, 0);
        var targets = ((int)input.DangerousGetHandle(), (int)output.DangerousGetHandle(), (int)error.DangerousGetHandle());
        var injected = false;
        Assert.Throws<IOException>(() => DaemonLog.Redirect(PathOf("log"), true, _root, targets,
            (handle, fd) =>
            {
                if (!failDirectory && fd == targets.Item3 && !injected)
                {
                    injected = true;
                    throw new IOException("injected partial dup failure");
                }

                UnixIdentity.Duplicate(handle, fd);
            }, _ => throw new IOException("injected cwd failure")));
        RandomAccess.Write(output, "out"u8, 0);
        RandomAccess.Write(error, "err"u8, 0);
        var data = new byte[5];
        Assert.Equal(5, RandomAccess.Read(input, data, 0));
        Assert.Equal("input", Encoding.UTF8.GetString(data));
        Assert.Equal("out", File.ReadAllText(PathOf("output")));
        Assert.Equal("err", File.ReadAllText(PathOf("error")));
        Assert.Empty(File.ReadAllBytes(PathOf("log")));
    }

    private void Plant(string path, string kind, string sentinel)
    {
        switch (kind)
        {
            case "link": File.CreateSymbolicLink(path, sentinel); break;
            case "dangling": File.CreateSymbolicLink(path, PathOf("absent")); break;
            case "fifo": Run("mkfifo", path); break;
            case "directory": Directory.CreateDirectory(path); break;
            case "hardlink": Run("ln", sentinel, path); break;
            case "wide": Write(path, "wide bytes"); File.SetUnixFileMode(path, Private | UnixFileMode.OtherRead); break;
        }
    }

    private static void Run(string command, params string[] args)
    {
        using var process = Process.Start(command, args)!;
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, process.ExitCode);
    }
}
