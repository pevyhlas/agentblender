using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Abl.Core.Config;

public static partial class UnixIdentity
{
    internal sealed record OpenedFacts(uint Uid, ushort Mode, ulong Device, ulong Inode, uint Links, long Size, bool AllowAcl)
    {
        public bool IsDirectory => (Mode & FileTypeMask) == 0x4000;
        public bool IsRegular => (Mode & FileTypeMask) == RegularFileType;
        public bool SameObject(OpenedFacts other) => Device == other.Device && Inode == other.Inode;
    }

    private static int CloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int NoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int DirectoryFlag => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
    private static int NonBlocking => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    internal static SafeFileHandle OpenRoot() => Handle(open("/", CloseOnExec | DirectoryFlag | NoFollow, 0), "/");

    internal static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string name) =>
        Handle(openat(parent, name, CloseOnExec | DirectoryFlag | NoFollow, 0), name);

    internal static void CreateDirectoryAt(SafeFileHandle parent, string name)
    {
        if (mkdirat(parent, name, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
        {
            ThrowOnError(-1, "mkdirat", name);
        }
    }

    internal static SafeFileHandle OpenLogAt(SafeFileHandle parent, string name, bool write, bool create)
    {
        var flags = CloseOnExec | NoFollow | NonBlocking;
        if (write)
        {
            flags |= 1 | (OperatingSystem.IsMacOS() ? 0x8 : 0x400); // O_WRONLY | O_APPEND
        }

        if (create)
        {
            flags |= OperatingSystem.IsMacOS() ? 0xa00 : 0xc0; // O_CREAT | O_EXCL
        }

        return Handle(openat(parent, name, flags, 0x180), name); // 0600, never truncation
    }

    internal static void RenameAt(SafeFileHandle parent, string from, string to) =>
        ThrowOnError(renameat(parent, from, parent, to), "renameat", from);

    internal static OpenedFacts? InspectAt(SafeFileHandle parent, string name)
    {
        try
        {
            return Facts(parent, name, descriptor: false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal static OpenedFacts InspectHandle(SafeFileHandle handle) => Facts(handle, "", descriptor: true);

    private static OpenedFacts Facts(SafeFileHandle handle, string name, bool descriptor)
    {
        if (OperatingSystem.IsMacOS())
        {
            Span<byte> buffer = stackalloc byte[MacStatSize];
            var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            var result = descriptor
                ? x64 ? fstat_inode64(handle, ref MemoryMarshal.GetReference(buffer)) : fstat(handle, ref MemoryMarshal.GetReference(buffer))
                : x64 ? fstatat_inode64(handle, name, ref MemoryMarshal.GetReference(buffer), 0x20) : fstatat(handle, name, ref MemoryMarshal.GetReference(buffer), 0x20);
            ThrowOnError(result, descriptor ? "fstat" : "fstatat", name);
            return new(BitConverter.ToUInt32(buffer[16..]), BitConverter.ToUInt16(buffer[4..]),
                BitConverter.ToUInt32(buffer), BitConverter.ToUInt64(buffer[8..]), BitConverter.ToUInt16(buffer[6..]),
                BitConverter.ToInt64(buffer[96..]), descriptor && HasAllowAcl(handle));
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Private logs require Linux or macOS");
        }

        Span<byte> statBuffer = stackalloc byte[StatxSize];
        const uint mask = 0x30f; // type, mode, nlink, uid, ino, size
        ThrowOnError(statx_handle(handle, name, descriptor ? 0x1000 : 0x100, mask, ref MemoryMarshal.GetReference(statBuffer)), "statx", name);
        if ((BitConverter.ToUInt32(statBuffer) & mask) != mask)
        {
            throw new IOException("statx omitted required private-log facts");
        }

        return new(BitConverter.ToUInt32(statBuffer[20..]), BitConverter.ToUInt16(statBuffer[28..]),
            ((ulong)BitConverter.ToUInt32(statBuffer[136..]) << 32) | BitConverter.ToUInt32(statBuffer[140..]),
            BitConverter.ToUInt64(statBuffer[32..]), BitConverter.ToUInt32(statBuffer[16..]),
            checked((long)BitConverter.ToUInt64(statBuffer[40..])), false);
    }

    private static bool HasAllowAcl(SafeFileHandle handle)
    {
        var acl = acl_get_fd_np(handle, AclTypeExtended);
        if (acl == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            // Darwin also uses ENOENT for an absent FILESEC_ACL property. This handle
            // has already passed fstat and remains owned throughout the call: no path
            // lookup can confuse an absent ACL with a missing or substituted file.
            if (error == ENoEnt)
            {
                return false;
            }

            throw new IOException($"cannot read opened-file ACL: {Marshal.GetPInvokeErrorMessage(error)}");
        }

        try
        {
            for (var id = AclFirstEntry; ; id = AclNextEntry)
            {
                if (acl_get_entry_checked(acl, id, out var entry) != 0)
                {
                    if (Marshal.GetLastPInvokeError() == 22) // Darwin end-of-entries is -1/EINVAL.
                    {
                        return false;
                    }

                    throw new IOException("cannot enumerate opened-file ACL");
                }

                if (acl_get_tag_type(entry, out var tag) != 0)
                {
                    throw new IOException("cannot inspect opened-file ACL entry");
                }

                if (tag == AclExtendedAllow)
                {
                    return true;
                }
            }
        }
        finally
        {
            _ = acl_free(acl);
        }
    }

    /// <summary>Save a standard descriptor with close-on-exec before changing redirection.</summary>
    public static SafeFileHandle SaveDescriptor(int descriptor) =>
        Handle(fcntl(descriptor, OperatingSystem.IsMacOS() ? 67 : 1030, 3), "standard descriptor");

    private static SafeFileHandle Handle(int fd, string name)
    {
        if (fd < 0)
        {
            ThrowOnError(-1, "open", name);
        }

        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

#pragma warning disable SA1300, IDE1006 // libc symbol names
    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags, int mode);
    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int openat(SafeFileHandle parent, string name, int flags, int mode);
    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int mkdirat(SafeFileHandle parent, string name, int mode);
    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int renameat(SafeFileHandle from, string name, SafeFileHandle to, string target);
    [LibraryImport(Libc, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int statx_handle(SafeFileHandle fd, string path, int flags, uint mask, ref byte buffer);
    [LibraryImport(Libc, SetLastError = true)]
    private static partial int fstat(SafeFileHandle fd, ref byte buffer);
    [LibraryImport(Libc, EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int fstat_inode64(SafeFileHandle fd, ref byte buffer);
    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int fstatat(SafeFileHandle fd, string path, ref byte buffer, int flags);
    [LibraryImport(Libc, EntryPoint = "fstatat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int fstatat_inode64(SafeFileHandle fd, string path, ref byte buffer, int flags);
    [LibraryImport(Libc, SetLastError = true)]
    private static partial IntPtr acl_get_fd_np(SafeFileHandle fd, int type);
    [LibraryImport(Libc, EntryPoint = "acl_get_entry", SetLastError = true)]
    private static partial int acl_get_entry_checked(IntPtr acl, int entryId, out IntPtr entry);
    [LibraryImport(Libc, SetLastError = true)]
    private static partial int fcntl(int fd, int command, int minimum);
#pragma warning restore SA1300, IDE1006
}
