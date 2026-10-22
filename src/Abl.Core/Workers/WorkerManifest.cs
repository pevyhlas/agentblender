using System.Globalization;

namespace Abl.Core.Workers;

/// <summary>
/// The package manifest of <c>worker/</c> (008 FR-014, AC-32): written at build time next to
/// <c>abl.dll</c> as <see cref="FileName"/>, one <c>&lt;sha256&gt;  &lt;module&gt;</c> line per
/// <c>worker/*.py</c> (the <c>sha256sum</c> format, so <c>sha256sum -c</c> can check it from
/// inside <c>worker/</c>), and packed into the <c>agentblender</c> tool package with it. Doctor's
/// <c>worker.files</c> proves the resolved <c>worker/</c> is the packaged one with it.
/// </summary>
public sealed class WorkerManifest
{
    /// <summary>The manifest's file name in the package directory.</summary>
    public const string FileName = "worker-manifest.sha256";

    /// <summary>A manifest larger than this is not one (a handful of 80-byte lines).</summary>
    private const int MaxBytes = 64 * 1024;

    private WorkerManifest(IReadOnlyDictionary<string, string> modules) => Modules = modules;

    /// <summary>Module file name to its lower-case hex SHA-256.</summary>
    public IReadOnlyDictionary<string, string> Modules { get; }

    /// <summary>The manifest's path in <paramref name="packageDirectory"/>.</summary>
    public static string PathIn(string packageDirectory) => Path.Combine(packageDirectory, FileName);

    /// <summary>The manifest in <paramref name="packageDirectory"/>, or null when there is none.</summary>
    /// <exception cref="InvalidDataException">The file is not a manifest, or lacks one of <see cref="WorkerSource.Modules"/>.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static WorkerManifest? Read(string packageDirectory)
    {
        var path = PathIn(packageDirectory);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        if (info.Length > MaxBytes)
        {
            throw new InvalidDataException($"{path} is larger than {MaxBytes / 1024} KiB, so it is not a worker manifest");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Parses manifest text; <paramref name="source"/> names it in errors.</summary>
    /// <exception cref="InvalidDataException">A line is malformed or repeated, or a worker module is not listed.</exception>
    public static WorkerManifest Parse(string text, string source)
    {
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            // `<64 hex>  <name>`: the name is a plain file name, never a path.
            var name = line.Length > 66 ? line[66..] : "";
            if (line.Length <= 66 || line[64] != ' ' || line[65] != ' ' || !IsHex(line.AsSpan(0, 64))
                || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or ".." || name.Any(char.IsControl))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{source} line {number} is not `<sha256>  <module>`"));
            }

            if (!modules.TryAdd(name, line[..64].ToLowerInvariant()))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{source} line {number} repeats {name}"));
            }
        }

        if (WorkerSource.Modules.Where(m => !modules.ContainsKey(m)).ToList() is { Count: > 0 } missing)
        {
            throw new InvalidDataException($"{source} does not list {string.Join(", ", missing)}");
        }

        return new WorkerManifest(modules);
    }

    /// <summary>The worker digest the packaged modules give (<see cref="WorkerSource.Digest"/>'s construction).</summary>
    public string Digest() =>
        WorkerSource.Combine(WorkerSource.Modules.Select(m => (m, Convert.FromHexString(Modules[m]))));

    /// <summary>The worker modules whose hash in <paramref name="workerDir"/> differs from the manifest, in <see cref="WorkerSource.Modules"/> order.</summary>
    /// <exception cref="IOException">A module is missing, unreadable or too large.</exception>
    public IReadOnlyList<string> Changed(string workerDir) =>
        [.. WorkerSource.ModuleHashes(workerDir)
            .Where(h => !string.Equals(Convert.ToHexStringLower(h.Sha256), Modules[h.Module], StringComparison.Ordinal))
            .Select(h => h.Module)];

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
