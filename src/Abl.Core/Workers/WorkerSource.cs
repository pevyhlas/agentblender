using System.Security.Cryptography;
using System.Text;

namespace Abl.Core.Workers;

/// <summary>
/// The worker's Python modules as the host sees them on disk (008 doctor <c>worker.files</c>):
/// the same module list and digest the worker reports as <c>worker_source_sha256</c> in its
/// hello (<c>worker.py</c> <c>op_hello</c>), so doctor can say whether the worker that ran is
/// the one installed. A unit test keeps <see cref="Modules"/> equal to the worker's list.
/// </summary>
public static class WorkerSource
{
    /// <summary>The modules the worker digests, in its order.</summary>
    public static IReadOnlyList<string> Modules { get; } =
        ["worker.py", "fingerprint.py", "sheet.py", "blobs.py", "cycles_devices.py", "inspection.py"];

    /// <summary>The worker's per-file bound: a larger module fails its hello.</summary>
    public const int MaxModuleBytes = 1024 * 1024;

    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("abl.worker-source-observation.v1\n");

    /// <summary>The modules missing from <paramref name="workerDir"/> (empty when complete).</summary>
    public static IReadOnlyList<string> Missing(string workerDir) =>
        [.. Modules.Where(m => !File.Exists(Path.Combine(workerDir, m)))];

    /// <summary>
    /// The digest the worker's hello would report for <paramref name="workerDir"/>: SHA-256 over
    /// the domain line, then each module's name, a newline and the module's own SHA-256.
    /// </summary>
    /// <exception cref="IOException">A module is missing, unreadable or larger than <see cref="MaxModuleBytes"/>.</exception>
    public static string Digest(string workerDir) => Combine(ModuleHashes(workerDir));

    /// <summary>Each module's own SHA-256 in <paramref name="workerDir"/>, in <see cref="Modules"/> order.</summary>
    /// <exception cref="IOException">A module is missing, unreadable or larger than <see cref="MaxModuleBytes"/>.</exception>
    public static IReadOnlyList<(string Module, byte[] Sha256)> ModuleHashes(string workerDir)
    {
        var hashes = new List<(string, byte[])>(Modules.Count);
        foreach (var module in Modules)
        {
            var path = Path.Combine(workerDir, module);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException($"worker module {path} is missing", path);
            }

            if (info.Length > MaxModuleBytes)
            {
                throw new IOException($"worker module {path} exceeds {MaxModuleBytes / 1024} KiB");
            }

            hashes.Add((module, SHA256.HashData(File.ReadAllBytes(path))));
        }

        return hashes;
    }

    /// <summary>The worker digest over per-module SHA-256 values given in <see cref="Modules"/> order.</summary>
    public static string Combine(IEnumerable<(string Module, byte[] Sha256)> moduleHashes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        foreach (var (module, sha256) in moduleHashes)
        {
            hash.AppendData(Encoding.ASCII.GetBytes(module + "\n"));
            hash.AppendData(sha256);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
