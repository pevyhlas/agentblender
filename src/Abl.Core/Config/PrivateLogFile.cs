using Microsoft.Win32.SafeHandles;

namespace Abl.Core.Config;

/// <summary>Directory-anchored private log IO. Existing unsafe entries are refused, never repaired.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static class PrivateLogFile
{
    /// <summary>Open a verified private regular file for reading, without creating any entry.</summary>
    public static SafeFileHandle OpenRead(string path) => Open(path, write: false, rotateBytes: null);

    /// <summary>Open for append, rotating a large validated log to .1 before exclusive replacement creation.</summary>
    public static SafeFileHandle OpenAppend(string path, long? rotateBytes = null) => Open(path, write: true, rotateBytes);

    // Local seams run only in tests, never as process-global callbacks.
    internal static SafeFileHandle Open(string path, bool write, long? rotateBytes,
        Action<string>? afterDirectory = null, Action? beforeFileOpen = null, Action? beforeReplacement = null,
        Func<UnixIdentity.OpenedFacts, UnixIdentity.OpenedFacts>? openedFacts = null)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Private logs require Linux or macOS");
        }

        var full = Normalize(path);
        var name = Path.GetFileName(full);
        if (name.Length == 0 || full.Length > 4096)
        {
            throw new IOException("log path must name a bounded file path");
        }

        using var parent = OpenParent(Path.GetDirectoryName(full)!, write, afterDirectory);
        var entry = UnixIdentity.InspectAt(parent, name);
        if (entry is not null)
        {
            ValidateFile(entry, full);
        }

        beforeFileOpen?.Invoke();
        var handle = UnixIdentity.OpenLogAt(parent, name, write, create: write && entry is null);
        try
        {
            var facts = UnixIdentity.InspectHandle(handle);
            facts = openedFacts?.Invoke(facts) ?? facts;
            ValidateFile(facts, full);
            if (entry is not null && !entry.SameObject(facts))
            {
                throw new IOException($"log entry changed while opening {full}; select a stable private log path");
            }

            if (write && rotateBytes is { } limit && facts.Size > limit)
            {
                var previous = UnixIdentity.InspectAt(parent, name + ".1");
                if (previous is not null)
                {
                    ValidateFile(previous, full + ".1");
                    using var old = UnixIdentity.OpenLogAt(parent, name + ".1", write: false, create: false);
                    var oldFacts = UnixIdentity.InspectHandle(old);
                    ValidateFile(oldFacts, full + ".1");
                    if (!previous.SameObject(oldFacts))
                    {
                        throw new IOException("previous log changed while opening; rotation refused");
                    }
                }

                var current = UnixIdentity.InspectAt(parent, name);
                if (current is null || !facts.SameObject(current))
                {
                    throw new IOException("active log changed before rotation; rotation refused");
                }

                UnixIdentity.RenameAt(parent, name, name + ".1");
                handle.Dispose();
                beforeReplacement?.Invoke();
                handle = UnixIdentity.OpenLogAt(parent, name, write: true, create: true);
                ValidateFile(UnixIdentity.InspectHandle(handle), full);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenParent(string path, bool create, Action<string>? afterDirectory)
    {
        var current = UnixIdentity.OpenRoot();
        try
        {
            ValidateDirectory(UnixIdentity.InspectHandle(current), "/");
            var traversed = "";
            foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                traversed += "/" + component;
                SafeFileHandle next;
                try
                {
                    next = UnixIdentity.OpenDirectoryAt(current, component);
                }
                catch (FileNotFoundException) when (create)
                {
                    UnixIdentity.CreateDirectoryAt(current, component);
                    next = UnixIdentity.OpenDirectoryAt(current, component);
                }

                current.Dispose();
                current = next;
                ValidateDirectory(UnixIdentity.InspectHandle(current), traversed);
                afterDirectory?.Invoke(traversed);
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void ValidateDirectory(UnixIdentity.OpenedFacts facts, string path)
    {
        var stickyRoot = facts.Uid == 0 && (facts.Mode & 0x200) != 0;
        if (!facts.IsDirectory || (facts.Uid != 0 && facts.Uid != UnixIdentity.EffectiveUserId)
            || (!stickyRoot && (facts.Mode & 0x12) != 0) || facts.AllowAcl)
        {
            throw new IOException($"unsafe log directory {path}: require root/current-user ownership, no group/other write and no allow ACL; choose a private directory");
        }
    }

    private static void ValidateFile(UnixIdentity.OpenedFacts facts, string path)
    {
        if (!facts.IsRegular || facts.Uid != UnixIdentity.EffectiveUserId || (facts.Mode & 0x3f) != 0 || facts.Links != 1 || facts.AllowAcl)
        {
            throw new IOException($"unsafe log {path}: require a regular file you own, private mode (0600), one link and no allow ACL; choose a new private log path");
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS())
        {
            return full;
        }

        // Only known root-managed aliases, with the exact stock target. The subsequent fd walk
        // verifies / and every target directory. Arbitrary symlinks remain O_NOFOLLOW refusals.
        foreach (var alias in new[] { "/tmp", "/var", "/etc" })
        {
            if (full.StartsWith(alias + "/", StringComparison.Ordinal))
            {
                using var root = UnixIdentity.OpenRoot();
                var facts = UnixIdentity.InspectHandle(root);
                if (facts.Uid != 0 || (facts.Mode & 0x12) != 0 || facts.AllowAcl)
                {
                    throw new IOException("system alias parent is not trusted");
                }

                var target = new DirectoryInfo(alias).LinkTarget;
                if (target == "private" + alias || target == "/private" + alias)
                {
                    return "/private" + full;
                }
            }
        }

        return full;
    }
}
