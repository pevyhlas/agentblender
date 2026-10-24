using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;

namespace Abl.Cli.Host;

/// <summary>
/// The checks <c>abl doctor</c> and <c>abl setup</c> share (008 FR-006, FR-009): the Blender
/// verdict, the runtime dir and its socket paths, and the store and renders directories. They
/// read the host and never write it, except the socket bind/unlink probe in a runtime dir
/// that already passed its own check (L-e).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static class HostChecks
{
    /// <summary>Doctor and setup warn when a data volume has less free space than this (plan B2, B4).</summary>
    public const long LowSpaceBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The <c>blender.found</c> remedy (AC-24): the setup command and the official download page.</summary>
    public const string BlenderFoundRemedy =
        "abl setup --blender <path> (Blender 5.2 LTS: " + SupportedBlender.DownloadPage + ")";

    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private const UnixFileMode GroupOrOtherAny =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary><c>blender.found</c> and <c>blender.version</c> from the resolver's Blender verdict.</summary>
    public static (DoctorCheck Found, DoctorCheck? Version) Blender(BlenderResolution blender)
    {
        var detail = new JsonObject
        {
            ["path"] = blender.Path,
            ["source"] = AblConfig.SourceName(blender.Source),
            ["version"] = blender.Version?.ToString(),
            ["buildHash"] = blender.BuildHash,
            ["alternatives"] = blender.ToJson()["alternatives"]!.DeepClone(),
        };
        if (blender.Check == "blender.found")
        {
            return (DoctorCheck.Fail("blender.found", blender.Problem ?? "no Blender found", BlenderFoundRemedy, detail), null);
        }

        var path = blender.Path ?? blender.Alternatives.FirstOrDefault(a => a.Probe.Version is not null)?.Path ?? "?";
        var others = blender.Alternatives.Where(a => a.Path != path).ToList();
        var found = DoctorCheck.Pass(
            "blender.found",
            (blender.Path is null ? $"found Blender at {path}" : $"Blender at {path} ({AblConfig.SourceName(blender.Source)})") +
                (others.Count == 0 ? "" : "; also found " + string.Join(", ", others.Select(a => $"{a.Path} ({a.Probe.Version?.ToString() ?? a.Probe.Error ?? "?"})"))),
            detail);
        if (blender.Check == "blender.version")
        {
            return (found, DoctorCheck.Fail("blender.version", blender.Problem ?? "unsupported Blender", blender.Hint ?? SupportedBlender.Remedy));
        }

        var version = blender.Version?.ToString() ?? "unknown";
        var versionDetail = new JsonObject
        {
            ["version"] = blender.Version?.ToString(),
            ["support"] = blender.Support is { } s ? SupportedBlender.Name(s) : null,
            ["supportedLine"] = SupportedBlender.Line,
            ["reference"] = SupportedBlender.Reference.ToString(),
            ["allowUnsupported"] = blender.UnsupportedAllowed,
        };
        var check = blender.Support switch
        {
            BlenderSupport.Reference => DoctorCheck.Pass("blender.version", $"Blender {version} (reference)", versionDetail),
            BlenderSupport.Supported => DoctorCheck.Warn(
                "blender.version",
                $"Blender {version} (reference {SupportedBlender.Reference})",
                $"bundles made with {SupportedBlender.Reference} may report a version mismatch on replay; install {SupportedBlender.Reference} to match exactly",
                versionDetail),
            BlenderSupport.Unsupported => DoctorCheck.Warn(
                "blender.version",
                $"Blender {version} is outside {SupportedBlender.Line}; used because blender.allowUnsupported is set",
                $"install Blender 5.2 LTS ({SupportedBlender.DownloadPage})",
                versionDetail),
            _ => DoctorCheck.Warn(
                "blender.version",
                "the Blender version is unknown; used because blender.allowUnsupported is set",
                $"install Blender 5.2 LTS ({SupportedBlender.DownloadPage})",
                versionDetail),
        };
        return (found, check);
    }

    /// <summary><c>runtime.dir</c>: the final component is a real, user-owned 0700 directory, or not created yet.</summary>
    public static DoctorCheck RuntimeDir(string runtimeDir, IConfigFileSystem fs)
    {
        var remedy = $"remove {runtimeDir} and retry, or set paths.runtime (ABL_RUNTIME_DIR) to a private directory";
        var detail = new JsonObject { ["path"] = runtimeDir };
        if (fs.Inspect(runtimeDir) is null)
        {
            detail["exists"] = false;
            return DoctorCheck.Pass("runtime.dir", $"{runtimeDir} does not exist yet; the first worker spawn creates it 0700", detail);
        }

        detail["exists"] = true;
        return RuntimePaths.CheckDir(runtimeDir, fs) is { } problem
            ? DoctorCheck.Fail("runtime.dir", problem, remedy, detail)
            : DoctorCheck.Pass("runtime.dir", $"{runtimeDir} is a private directory (0700)", detail);
    }

    /// <summary>
    /// <c>runtime.socket-path</c>: the longest worker socket fits <c>sun_path</c>, and, when the
    /// runtime dir exists and passes its check, a real bind and unlink there succeed (L-e).
    /// </summary>
    public static DoctorCheck SocketPath(string runtimeDir, HostPlatform platform, IConfigFileSystem fs, bool probe)
    {
        var length = RuntimePaths.LongestSocketPathBytes(runtimeDir);
        var limit = RuntimePaths.MaxSocketPathBytes(platform);
        var detail = new JsonObject { ["longestBytes"] = length, ["limitBytes"] = limit };
        if (RuntimePaths.CheckSocketLength(runtimeDir, platform) is { } tooLong)
        {
            return DoctorCheck.Fail("runtime.socket-path", tooLong, "set a shorter paths.runtime (ABL_RUNTIME_DIR)", detail);
        }

        var summary = string.Create(CultureInfo.InvariantCulture, $"longest socket path {length} of {limit} bytes");
        if (!probe || fs.Inspect(runtimeDir) is null || RuntimePaths.CheckDir(runtimeDir, fs) is not null)
        {
            detail["bindProbe"] = "not run";
            return DoctorCheck.Pass("runtime.socket-path", summary, detail);
        }

        // Same length as the longest worker socket; the "d-" prefix never matches a worker
        // socket name, so the stop sweep ignores it.
        var socketPath = Path.Combine(runtimeDir, "d-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            detail["bindProbe"] = "ok";
            return DoctorCheck.Pass("runtime.socket-path", summary + "; a socket binds there", detail);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            detail["bindProbe"] = "failed";
            return DoctorCheck.Fail(
                "runtime.socket-path",
                $"cannot bind a socket in {runtimeDir}: {ex.Message}",
                "set paths.runtime (ABL_RUNTIME_DIR) to a private directory on a local file system",
                detail);
        }
        finally
        {
            try
            {
                File.Delete(socketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// <c>paths.store</c> / <c>paths.renders</c>: owned by the user, writable, not writable by
    /// others, 0700 (warn otherwise), and at least <see cref="LowSpaceBytes"/> free (warn).
    /// </summary>
    public static DoctorCheck DataDir(string id, string path, IConfigFileSystem fs, Func<string, long?> freeBytes)
    {
        var key = id; // paths.store or paths.renders: the check ID is the config key
        var variable = id == "paths.store" ? "ABL_STORE" : "ABL_RENDERS";
        var detail = new JsonObject { ["path"] = path };
        var free = freeBytes(path);
        detail["freeBytes"] = free;
        var space = free is { } bytes ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB free") : "free space unknown";
        var facts = fs.Inspect(path);
        string summary;
        if (facts is null)
        {
            detail["exists"] = false;
            summary = $"{path} does not exist yet; it is created on first use ({space})";
        }
        else
        {
            detail["exists"] = true;
            detail["mode"] = Octal(facts.Mode);
            if (!facts.TargetExists || !facts.IsDirectory)
            {
                return DoctorCheck.Fail(id, $"{path} is not a directory", $"remove it or set {key} ({variable}) to a directory", detail);
            }

            if (!facts.OwnedByCurrentUser)
            {
                return DoctorCheck.Fail(id, $"{path} is owned by another user", $"use a directory you own: set {key} ({variable})", detail);
            }

            if ((facts.Mode & GroupOrOtherWrite) != 0)
            {
                return DoctorCheck.Fail(id, $"{path} is writable by group or others (mode {Octal(facts.Mode)})", $"chmod 700 {facts.Target}", detail);
            }

            if (ConfigTrust.AllowAclProblem(facts) is { } aclProblem)
            {
                return DoctorCheck.Fail(id, aclProblem, $"remove the allow ACL (chmod -N {facts.Target}) or choose another directory", detail);
            }

            if ((facts.Mode & UnixFileMode.UserWrite) == 0)
            {
                return DoctorCheck.Fail(id, $"{path} is not writable (mode {Octal(facts.Mode)})", $"chmod 700 {facts.Target}", detail);
            }

            if ((facts.Mode & GroupOrOtherAny) != 0)
            {
                return DoctorCheck.Warn(id, $"{path} is readable by group or others (mode {Octal(facts.Mode)}; expected 0700)", $"chmod 700 {facts.Target}", detail);
            }

            summary = $"{path} (0700, {space})";
        }

        return free is { } available && available < LowSpaceBytes
            ? DoctorCheck.Warn(id, $"only {space} on the volume of {path}", $"free disk space, or set {key} ({variable}) to a larger volume", detail)
            : DoctorCheck.Pass(id, summary, detail);
    }

    /// <summary>Free bytes on the volume that holds <paramref name="path"/> (or its nearest existing parent), or null.</summary>
    public static long? FreeBytes(string path)
    {
        for (var dir = path; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    return new DriveInfo(dir).AvailableFreeSpace;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(4, '0');
}
