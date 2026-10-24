namespace Abl.Core.Config;

/// <summary>
/// What the trust rules need to know about one path, read without following the final
/// component first (<c>lstat</c> semantics): whether it is a symlink, and the facts of what
/// it finally resolves to.
/// </summary>
/// <param name="Path">The path as asked.</param>
/// <param name="IsSymlink">The final component of <paramref name="Path"/> is a symlink.</param>
/// <param name="Target">The final link target, or the normalized path when not a link.</param>
/// <param name="TargetExists">The target exists (false for a dangling link).</param>
/// <param name="IsDirectory">The target is a directory.</param>
/// <param name="Mode">The target's permission bits.</param>
/// <param name="OwnerUserId">The target's owner uid, or null for a missing target.</param>
/// <param name="EffectiveUserId">The inspecting process's effective uid.</param>
/// <param name="HasAllowAcl">The target has a macOS extended allow ACL entry.</param>
/// <param name="IsRegularFile">The target is a regular file, not a directory, FIFO, socket or device.</param>
public sealed record PathFacts(
    string Path,
    bool IsSymlink,
    string Target,
    bool TargetExists,
    bool IsDirectory,
    UnixFileMode Mode,
    uint? OwnerUserId,
    uint EffectiveUserId,
    bool HasAllowAcl,
    bool IsRegularFile)
{
    public bool OwnedByCurrentUser => TargetExists && OwnerUserId == EffectiveUserId;
}

/// <summary>
/// The file-system seam of the config resolver (plan M10): tests inject a fake to simulate a
/// foreign owner, which a test process cannot create for real.
/// </summary>
public interface IConfigFileSystem
{
    /// <summary>Facts about <paramref name="path"/>, or null when nothing (not even a link) is there.</summary>
    PathFacts? Inspect(string path);

    /// <summary>Reads a whole UTF-8 text file.</summary>
    string ReadAllText(string path);
}

/// <summary>The real file system (Unix only: modes and ownership have no Windows meaning).</summary>
public sealed class UnixConfigFileSystem : IConfigFileSystem
{
    public static readonly UnixConfigFileSystem Instance = new();

    public PathFacts? Inspect(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("config trust checks need a Unix host");
        }

        var asked = path;
        // A trailing separator must not hide a linked final directory.
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is null && Directory.Exists(path))
        {
            info = new DirectoryInfo(path);
        }

        var isLink = info.LinkTarget is not null;
        if (!isLink)
        {
            try
            {
                _ = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }

        var target = path;
        if (isLink)
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            target = resolved?.FullName ?? path;
            try
            {
                _ = File.GetAttributes(target);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new PathFacts(asked, true, target, false, false, 0, null, UnixIdentity.EffectiveUserId, false, false);
            }
        }

        var isDirectory = Directory.Exists(target);
        return new PathFacts(
            asked,
            isLink,
            target,
            true,
            isDirectory,
            File.GetUnixFileMode(target),
            UnixIdentity.OwnerOf(target),
            UnixIdentity.EffectiveUserId,
            OperatingSystem.IsMacOS() && UnixIdentity.HasAllowAcl(target),
            UnixIdentity.IsRegularFile(target));
    }

    public string ReadAllText(string path) => File.ReadAllText(path);
}
