using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Abl.Core.Config;

/// <summary>
/// Unix identity primitives (008 T010, N1; T013). .NET has no managed effective-uid,
/// file-owner or ACL API, so this is the one place that reaches libc through
/// <c>LibraryImport</c>: <c>geteuid()</c>, the owner uid (<see cref="OwnerOf"/>) and the
/// macOS ACL read (<see cref="HasAllowAcl"/>). Ownership is always read from the uid, never
/// proved by a mode-changing operation. <see cref="UnixConfigFileSystem"/> collects these
/// primitives into the shared <see cref="PathFacts"/> used by path-trust policies (008 T021).
/// </summary>
/// <remarks>
/// Running AgentBlender as root is not a supported configuration. <see cref="OwnerOf"/> uses
/// <c>statx(2)</c> on Linux, whose layout is the same on every architecture, and the
/// 64-bit-inode <c>stat(2)</c> on macOS, whose layout is the same on arm64 and x64. On macOS
/// <see cref="HasAllowAcl"/> reads extended ACLs, which the mode bits do not show. The
/// Phase 2 process calls (<c>dup2</c>, <c>setsid</c>, <c>kill</c>) are in
/// <c>UnixIdentity.Process.cs</c> (plan Complexity Tracking).
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static partial class UnixIdentity
{
    private const string Libc = "libc";

    static UnixIdentity()
    {
        // "libc" is not a loadable file name on glibc (libc.so is a linker script) and
        // differs on macOS (libSystem). Every libc symbol is already in the process's
        // global scope, so resolve it from the main program handle instead.
        NativeLibrary.SetDllImportResolver(typeof(UnixIdentity).Assembly, Resolve);
    }

    /// <summary>The effective user id of this process.</summary>
    public static uint EffectiveUserId => geteuid();

    /// <summary>
    /// True when the path's owner uid equals the effective uid, including on read-only
    /// filesystems. Does not change permission bits or metadata.
    /// </summary>
    /// <exception cref="ArgumentException">The final component is a symlink.</exception>
    public static bool IsOwnedByCurrentUser(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new ArgumentException($"'{path}' is a symlink; resolve it before proving ownership", nameof(path));
        }

        return OwnerOf(path) == EffectiveUserId;
    }

    /// <summary>The owner uid of <paramref name="path"/>, following symlinks (as the mode check does).</summary>
    /// <exception cref="IOException">The path cannot be inspected.</exception>
    public static uint OwnerOf(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            // struct stat with 64-bit inodes: dev_t (4), mode_t (2), nlink_t (2), ino_t (8), uid_t at 16.
            Span<byte> buffer = stackalloc byte[MacStatSize];
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? stat_inode64(path, ref MemoryMarshal.GetReference(buffer))
                : stat(path, ref MemoryMarshal.GetReference(buffer));
            ThrowOnError(result, "stat", path);
            return BitConverter.ToUInt32(buffer[MacStatUidOffset..]);
        }

        // struct statx: stx_mask (4), stx_blksize (4), stx_attributes (8), stx_nlink (4), stx_uid at 20.
        Span<byte> statxBuffer = stackalloc byte[StatxSize];
        ThrowOnError(statx(AtFdCwd, path, 0, StatxUid, ref MemoryMarshal.GetReference(statxBuffer)), "statx", path);
        if ((BitConverter.ToUInt32(statxBuffer) & StatxUid) == 0)
        {
            throw new IOException($"statx did not report the owner of '{path}'");
        }

        return BitConverter.ToUInt32(statxBuffer[StatxUidOffset..]);
    }

    /// <summary>
    /// True when <paramref name="path"/>, following symlinks, is a regular file: not a directory,
    /// FIFO, socket or device, so opening it neither blocks nor reads without end.
    /// </summary>
    /// <exception cref="IOException">The path cannot be inspected.</exception>
    public static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            // struct stat with 64-bit inodes: dev_t (4), then mode_t st_mode at 4.
            Span<byte> buffer = stackalloc byte[MacStatSize];
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? stat_inode64(path, ref MemoryMarshal.GetReference(buffer))
                : stat(path, ref MemoryMarshal.GetReference(buffer));
            ThrowOnError(result, "stat", path);
            return (BitConverter.ToUInt16(buffer[MacStatModeOffset..]) & FileTypeMask) == RegularFileType;
        }

        // struct statx: ..., stx_uid (4) at 20, stx_gid (4) at 24, stx_mode (2) at 28.
        Span<byte> statxBuffer = stackalloc byte[StatxSize];
        ThrowOnError(statx(AtFdCwd, path, 0, StatxType, ref MemoryMarshal.GetReference(statxBuffer)), "statx", path);
        if ((BitConverter.ToUInt32(statxBuffer) & StatxType) == 0)
        {
            throw new IOException($"statx did not report the file type of '{path}'");
        }

        return (BitConverter.ToUInt16(statxBuffer[StatxModeOffset..]) & FileTypeMask) == RegularFileType;
    }

    /// <summary>
    /// macOS: true when <paramref name="path"/> (followed, as the mode check is) carries an
    /// extended ACL with any <c>allow</c> entry. Such an entry can grant others write access
    /// that the mode bits do not show, so the discovery trust rule fails closed on any of
    /// them; the stock home ACLs are <c>deny</c> entries only. Linux POSIX ACLs need no check:
    /// their mask shows in the group bits of the mode.
    /// </summary>
    /// <exception cref="IOException">The ACL cannot be read.</exception>
    [SupportedOSPlatform("macos")]
    public static bool HasAllowAcl(string path)
    {
        var acl = acl_get_file(path, AclTypeExtended);
        if (acl == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ENoEnt
                ? false // no extended ACL (the path itself was inspected before this call)
                : throw new IOException($"cannot read the ACL of '{path}': {Marshal.GetPInvokeErrorMessage(error)}");
        }

        try
        {
            for (var id = AclFirstEntry; acl_get_entry(acl, id, out var entry) == 0; id = AclNextEntry)
            {
                if (acl_get_tag_type(entry, out var tag) != 0 || tag == AclExtendedAllow)
                {
                    return true; // an unreadable entry fails closed too
                }
            }

            return false;
        }
        finally
        {
            _ = acl_free(acl);
        }
    }

    private static void ThrowOnError(int result, string call, string path)
    {
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw error == ENoEnt
                ? new FileNotFoundException($"{call} found no file at '{path}'", path)
                : new IOException($"{call} failed for '{path}': {Marshal.GetPInvokeErrorMessage(error)}");
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Libc ? NativeLibrary.GetMainProgramHandle() : IntPtr.Zero;

    private const int MacStatSize = 144;
    private const int MacStatUidOffset = 16;
    private const int MacStatModeOffset = 4;
    private const int StatxSize = 256;
    private const int StatxUidOffset = 20;
    private const uint StatxUid = 0x8;
    private const uint StatxType = 0x1;
    private const int StatxModeOffset = 28;
    private const int FileTypeMask = 0xF000;
    private const int RegularFileType = 0x8000;
    private const int AtFdCwd = -100;
    private const int ENoEnt = 2;
    private const int AclTypeExtended = 0x100;
    private const int AclFirstEntry = 0;
    private const int AclNextEntry = -1;
    private const int AclExtendedAllow = 1;

#pragma warning disable SA1300, IDE1006 // libc symbol name
    [LibraryImport(Libc)]
    private static partial uint geteuid();

    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int stat(string path, ref byte buffer);

    [LibraryImport(Libc, EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int stat_inode64(string path, ref byte buffer);

    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr acl_get_file(string path, int type);

    [LibraryImport(Libc)]
    private static partial int acl_get_entry(IntPtr acl, int entryId, out IntPtr entry);

    [LibraryImport(Libc)]
    private static partial int acl_get_tag_type(IntPtr entry, out int tag);

    [LibraryImport(Libc)]
    private static partial int acl_free(IntPtr obj);
#pragma warning restore SA1300, IDE1006
}
