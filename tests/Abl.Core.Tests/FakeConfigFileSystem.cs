using Abl.Core.Config;

namespace Abl.Core.Tests;

/// <summary>
/// In-memory <see cref="IConfigFileSystem"/> for hermetic resolver tests: modes, foreign
/// owners and symlinks without touching the real disk (a test cannot chown for real).
/// </summary>
internal sealed class FakeConfigFileSystem : IConfigFileSystem
{
    public const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PrivateDir = Private | UnixFileMode.UserExecute;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public List<string> Reads { get; } = [];

    public FakeConfigFileSystem Dir(string path, UnixFileMode mode = PrivateDir, bool owned = true, bool allowAcl = false)
    {
        _entries[path] = new Entry(null, true, mode, owned, null, allowAcl);
        return this;
    }

    /// <summary>Adds a file (and a private, owned parent directory unless one exists).</summary>
    public FakeConfigFileSystem File(string path, string content, UnixFileMode mode = Private, bool owned = true, bool allowAcl = false)
    {
        _entries[path] = new Entry(content, false, mode, owned, null, allowAcl);
        var parent = Path.GetDirectoryName(path)!;
        if (!_entries.ContainsKey(parent))
        {
            Dir(parent);
        }

        return this;
    }

    public FakeConfigFileSystem Link(string path, string target)
    {
        _entries[path] = new Entry(null, false, 0, true, target, false);
        return this;
    }

    public PathFacts? Inspect(string path)
    {
        if (!_entries.TryGetValue(path, out var entry))
        {
            return null;
        }

        if (entry.LinkTarget is null)
        {
            return Facts(path, false, path, entry);
        }

        var target = entry.LinkTarget;
        while (_entries.TryGetValue(target, out var next) && next.LinkTarget is not null)
        {
            target = next.LinkTarget;
        }

        return _entries.TryGetValue(target, out var final)
            ? Facts(path, true, target, final)
            : new PathFacts(path, true, target, false, false, 0, null, 1000, false, false);
    }

    public string ReadAllText(string path)
    {
        Reads.Add(path);
        var facts = Inspect(path) ?? throw new FileNotFoundException(path);
        return _entries[facts.Target].Content ?? throw new IOException($"{path} is a directory");
    }

    private static PathFacts Facts(string path, bool link, string target, Entry entry) =>
        new(path, link, target, true, entry.IsDirectory, entry.Mode, entry.Owned ? 1000u : 1001u,
            1000, entry.AllowAcl, !entry.IsDirectory);

    private sealed record Entry(string? Content, bool IsDirectory, UnixFileMode Mode, bool Owned, string? LinkTarget, bool AllowAcl);
}
