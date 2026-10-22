using System.Runtime.Versioning;

namespace Abl.Core.Config;

/// <summary>
/// Reads a file that may come from an untrusted tree (a cloned repository's <c>.mcp.json</c>, a
/// worker module) without trusting its size: the target, followed through symlinks, must be a
/// regular file (a FIFO would block the open, a device read without end), and the read stops
/// one byte past the bound, so a link to <c>/dev/zero</c>, a huge file or a file that grows
/// after the check cannot exhaust memory.
/// </summary>
[UnsupportedOSPlatform("windows")]
public static class BoundedFile
{
    /// <summary>The file's bytes, or null when it holds more than <paramref name="maxBytes"/>.</summary>
    /// <exception cref="IOException">The target is not a regular file, or cannot be read.</exception>
    public static byte[]? Read(string path, int maxBytes)
    {
        if (!UnixIdentity.IsRegularFile(path))
        {
            throw new IOException($"{path} is not a regular file");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.SequentialScan);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total > maxBytes ? null : buffer[..total];
    }
}
