using System.Diagnostics;
using Abl.Core.Config;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T010/T021: the read-only libc identity primitives, plus the
/// config and token trust rules on the real file system (modes and symlinks a test can make
/// for real; a foreign owner is simulated in <see cref="ConfigResolverTests"/>).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class UnixIdentityTests : IDisposable
{
    private readonly string _root;

    public UnixIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "abl-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root, FakeConfigFileSystem.PrivateDir);
    }

    [Fact]
    public async Task EffectiveUserIdMatchesIdU()
    {
        var start = new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true };
        using var id = Process.Start(start)!;
        var output = await id.StandardOutput.ReadToEndAsync();
        await id.WaitForExitAsync();
        Assert.Equal(uint.Parse(output.Trim()), UnixIdentity.EffectiveUserId);
    }

    [Fact]
    public void OwnFileIsOwnedAndTheProofLeavesItsModeAlone()
    {
        var file = Path.Combine(_root, "mine");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead);

        Assert.True(UnixIdentity.IsOwnedByCurrentUser(file));
        Assert.True(UnixIdentity.IsOwnedByCurrentUser(_root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
    }

    [Fact]
    public void RootOwnedPathIsNotOwned()
    {
        if (UnixIdentity.EffectiveUserId == 0) return; // root owns this directory

        // "/" is root-owned everywhere; on macOS it is the sealed, read-only system volume,
        // which must be inspected without attempting chmod.
        Assert.False(UnixIdentity.IsOwnedByCurrentUser("/"));
    }

    [RootOwnedTmpFact]
    public void RootOwnedDirectoryOnAWritableFileSystemIsNotOwned()
    {
        // Ownership comes from the uid even on a writable filesystem.
        Assert.False(UnixIdentity.IsOwnedByCurrentUser(RootOwnedTmpFactAttribute.Tmp));
    }

    [ReadOnlyImageFact]
    public void OwnFileOnAReadOnlyFileSystemIsOwned()
    {
        // hdiutil attaches
        // a read-only image as an unprivileged user; its files show as owned by that user.
        var source = Path.Combine(_root, "image-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "mine"), "x");
        var image = Path.Combine(_root, "ro.dmg");
        var mount = Path.Combine(_root, "mnt");
        Hdiutil("create", "-quiet", "-srcfolder", source, "-format", "UDRO", image);
        Hdiutil("attach", "-quiet", "-readonly", "-nobrowse", "-mountpoint", mount, image);
        try
        {
            var file = Path.Combine(mount, "mine");
            var refused = Assert.Throws<IOException>(() => File.SetUnixFileMode(file, File.GetUnixFileMode(file)));
            Assert.Equal(30, refused.HResult); // EROFS; ownership inspection itself is read-only
            Assert.Equal(UnixIdentity.EffectiveUserId, UnixIdentity.OwnerOf(file));
            Assert.True(UnixIdentity.IsOwnedByCurrentUser(file));
        }
        finally
        {
            Hdiutil("detach", "-quiet", "-force", mount);
        }
    }

    [Fact]
    public void SymlinkFinalComponentIsRefusedInsteadOfProvingItsTarget()
    {
        var link = Path.Combine(_root, "link");
        File.CreateSymbolicLink(link, "/");
        Assert.Throws<ArgumentException>(() => UnixIdentity.IsOwnedByCurrentUser(link));
    }

    [Fact]
    public void RealDiskTrustRuleAcceptsAPrivateFileAndRefusesWritableOnes()
    {
        var fs = UnixConfigFileSystem.Instance;
        var file = Path.Combine(_root, "config.json");
        File.WriteAllText(file, "{}");
        File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100); // 0644: readable is fine
        Assert.Null(ConfigTrust.CheckConfigFile(fs, file));

        File.SetUnixFileMode(file, (UnixFileMode)0b110_110_100); // 0664
        Assert.Contains("writable by group or others (mode 0664)", ConfigTrust.CheckConfigFile(fs, file));

        File.SetUnixFileMode(file, FakeConfigFileSystem.Private);
        File.SetUnixFileMode(_root, (UnixFileMode)0b111_111_111); // 0777 directory
        Assert.Contains($"{_root} is writable by group or others (mode 0777)", ConfigTrust.CheckConfigFile(fs, file));
        File.SetUnixFileMode(_root, FakeConfigFileSystem.PrivateDir);
    }

    [Fact]
    public void RealDiskSymlinkIsJudgedAtItsTarget()
    {
        var fs = UnixConfigFileSystem.Instance;
        var shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(shared, FakeConfigFileSystem.PrivateDir);
        var target = Path.Combine(shared, "config.json");
        File.WriteAllText(target, "{}");
        File.SetUnixFileMode(target, FakeConfigFileSystem.Private);
        var link = Path.Combine(_root, "config.json");
        File.CreateSymbolicLink(link, target);

        Assert.Null(ConfigTrust.CheckConfigFile(fs, link));
        File.SetUnixFileMode(target, (UnixFileMode)0b110_110_110);
        Assert.Contains("(symlink to " + target + ") is writable by group or others", ConfigTrust.CheckConfigFile(fs, link));

        File.SetUnixFileMode(target, FakeConfigFileSystem.Private);
        File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111);
        Assert.Contains($"{shared} is writable by group or others", ConfigTrust.CheckConfigFile(fs, link));
        File.SetUnixFileMode(shared, FakeConfigFileSystem.PrivateDir);

        File.Delete(target);
        Assert.Contains("symlink to a missing target", ConfigTrust.CheckConfigFile(fs, link));
    }

    [Fact]
    public void RealDiskTokenFileMustBePrivate()
    {
        var fs = UnixConfigFileSystem.Instance;
        var token = Path.Combine(_root, "desktop.token");
        File.WriteAllText(token, "secret");
        File.SetUnixFileMode(token, FakeConfigFileSystem.Private);
        Assert.Null(ConfigTrust.CheckTokenFile(fs, token));

        File.SetUnixFileMode(token, (UnixFileMode)0b110_100_000); // 0640
        Assert.Contains("accessible by group or others (mode 0640); run: chmod 600", ConfigTrust.CheckTokenFile(fs, token));
        Assert.Contains("does not exist", ConfigTrust.CheckTokenFile(fs, token + ".missing"));
    }

    [Fact]
    public void OwnerOfReadsTheOwnerUidAndFollowsSymlinks()
    {
        // Every trust rule now shares the discovery rule's uid distinction.
        var file = Path.Combine(_root, "mine");
        File.WriteAllText(file, "x");
        Assert.Equal(UnixIdentity.EffectiveUserId, UnixIdentity.OwnerOf(file));
        Assert.Equal(UnixIdentity.EffectiveUserId, UnixIdentity.OwnerOf(_root));
        Assert.Equal(0u, UnixIdentity.OwnerOf("/"));

        var link = Path.Combine(_root, "to-root");
        File.CreateSymbolicLink(link, "/");
        Assert.Equal(0u, UnixIdentity.OwnerOf(link));
        Assert.Throws<FileNotFoundException>(() => UnixIdentity.OwnerOf(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void HasAllowAclSeesAnAllowEntryButNotADenyOnlyAcl()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return; // macOS extended ACLs; Linux POSIX ACLs show in the mode's group bits
        }

        var dir = Path.Combine(_root, "shared");
        Directory.CreateDirectory(dir);
        Assert.False(UnixIdentity.HasAllowAcl(dir));

        ChmodAcl("group:everyone deny delete", dir); // the stock home ACL
        Assert.False(UnixIdentity.HasAllowAcl(dir));
        Assert.False(UnixConfigFileSystem.Instance.Inspect(dir)!.HasAllowAcl);

        ChmodAcl("group:everyone allow add_file,add_subdirectory,delete_child", dir);
        Assert.True(UnixIdentity.HasAllowAcl(dir));
        Assert.True(UnixConfigFileSystem.Instance.Inspect(dir)!.HasAllowAcl);
        Assert.NotNull(ConfigTrust.AllowAclProblem(UnixConfigFileSystem.Instance.Inspect(dir)!));
    }

    /// <summary>macOS <c>chmod +a</c>: adds one ACL entry.</summary>
    internal static void ChmodAcl(string entry, string path)
    {
        var start = new ProcessStartInfo("/bin/chmod") { ArgumentList = { "+a", entry, path }, RedirectStandardError = true };
        using var chmod = Process.Start(start)!;
        var error = chmod.StandardError.ReadToEnd();
        chmod.WaitForExit();
        Assert.True(chmod.ExitCode == 0, $"chmod +a failed: {error}");
    }

    private static void Hdiutil(params string[] arguments)
    {
        var start = new ProcessStartInfo(ReadOnlyImageFactAttribute.HdiutilPath) { RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var hdiutil = Process.Start(start)!;
        var error = hdiutil.StandardError.ReadToEnd();
        hdiutil.WaitForExit();
        Assert.True(hdiutil.ExitCode == 0, $"hdiutil {arguments[0]} failed: {error}");
    }

    public void Dispose()
    {
        try
        {
            File.SetUnixFileMode(_root, FakeConfigFileSystem.PrivateDir);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Runs when the temp root (<c>/tmp</c>, <c>/private/tmp</c> on macOS) is root-owned and the test is not root.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class RootOwnedTmpFactAttribute : FactAttribute
{
    public static string Tmp => OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp";

    public RootOwnedTmpFactAttribute()
    {
        if (UnixIdentity.EffectiveUserId == 0)
        {
            Skip = "root owns the root-owned temp directory";
        }
        else if (!Directory.Exists(Tmp) || new DirectoryInfo(Tmp).LinkTarget is not null || UnixIdentity.OwnerOf(Tmp) != 0)
        {
            Skip = $"{Tmp} is not a root-owned directory on this host";
        }
    }
}

/// <summary>Runs on macOS, where <c>hdiutil</c> attaches a read-only image without privileges.</summary>
public sealed class ReadOnlyImageFactAttribute : FactAttribute
{
    public const string HdiutilPath = "/usr/bin/hdiutil";

    public ReadOnlyImageFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(HdiutilPath))
        {
            Skip = "needs macOS hdiutil to attach a read-only image (no unprivileged read-only mount on Linux)";
        }
    }
}
