namespace Abl.Core.Config;

/// <summary>
/// What the trust rules need to know about one path, read without following the final
/// component first (<c>lstat</c> semantics): whether it is a symlink, and the facts of what
/// it finally resolves to.
/// </summary>
/// <param name="Path">The path as asked.</param>
/// <param name="IsSymlink">The final component of <paramref name="Path"/> is a symlink.</param>
/// <param name="Target">The fully resolved target (equal to <paramref name="Path"/> when not a link).</param>
/// <param name="TargetExists">The target exists (false for a dangling link).</param>
/// <param name="IsDirectory">The target is a directory.</param>
/// <param name="Mode">The target's permission bits.</param>
/// <param name="OwnedByCurrentUser">The current user owns the target.</param>
public sealed record PathFacts(
    string Path,
    bool IsSymlink,
    string Target,
    bool TargetExists,
    bool IsDirectory,
    UnixFileMode Mode,
    bool OwnedByCurrentUser);

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

        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is null && Directory.Exists(path))
        {
            info = new DirectoryInfo(path);
        }

        var isLink = info.LinkTarget is not null;
        if (!isLink && !info.Exists)
        {
            return null;
        }

        var target = path;
        if (isLink)
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            target = resolved?.FullName ?? path;
            if (resolved is null || !(File.Exists(target) || Directory.Exists(target)))
            {
                return new PathFacts(path, true, target, false, false, 0, false);
            }
        }

        var isDirectory = Directory.Exists(target);
        return new PathFacts(
            path,
            isLink,
            target,
            true,
            isDirectory,
            File.GetUnixFileMode(target),
            UnixIdentity.IsOwnedByCurrentUser(target));
    }

    public string ReadAllText(string path) => File.ReadAllText(path);
}
