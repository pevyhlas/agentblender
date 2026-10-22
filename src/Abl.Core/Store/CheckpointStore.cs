using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Abl.Core.Store;

/// <summary>
/// Content-addressed .blend checkpoint tree + per-session trajectory journals + frozen
/// artifacts, all plain files (D-03):
/// <code>
/// store/objects/&lt;sha256&gt;.blend   store/meta/&lt;sha256&gt;.json
/// store/frozen/&lt;name&gt;.json       store/sessions/&lt;id&gt;/journal.jsonl
/// store/sessions/&lt;id&gt;/scripts/&lt;seq&gt;.py     store/tmp/
/// </code>
/// Addressing is SHA-256 of the file bytes — identical saves are byte-identical (P4.I-02),
/// so ingest deduplicates for free.
/// </summary>
public sealed class CheckpointStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly Lock _metaLock = new();

    public CheckpointStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows())
        {
            // Agent work product (exec scripts, journals, .blend content) — not
            // world-readable on a shared host; every Unix, macOS included (008 FR-005).
            File.SetUnixFileMode(
                _root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var sub in new[] { "objects", "meta", "frozen", "sessions", "tmp" })
        {
            Directory.CreateDirectory(Path.Combine(_root, sub));
        }
    }

    public string TmpBlendPath() => Path.Combine(_root, "tmp", $"{Guid.NewGuid():N}.blend");

    public string ObjectPath(string hash) => Path.Combine(_root, "objects", $"{Validate(hash)}.blend");

    public string SessionScriptPath(string sessionId, int seq)
    {
        var dir = Path.Combine(_root, "sessions", Validate(sessionId), "scripts");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{seq:D4}.py");
    }

    /// <summary>Hash + move a worker-saved .blend into the tree. Returns the hash.</summary>
    public string Ingest(
        string tmpPath, string? parent, string sessionId,
        IReadOnlyList<string>? tags = null, string? digest = null, string? blenderVersion = null)
    {
        string hash;
        using (var stream = File.OpenRead(tmpPath))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        var target = ObjectPath(hash);
        lock (_metaLock)
        {
            if (File.Exists(target))
            {
                File.Delete(tmpPath); // dedup: same bytes already stored
            }
            else
            {
                File.Move(tmpPath, target);
            }

            var existing = Get(hash);
            if (existing is null)
            {
                WriteMeta(new CheckpointMeta(
                    hash, parent, sessionId, new FileInfo(target).Length,
                    DateTimeOffset.UtcNow, tags ?? [], digest, blenderVersion));
            }
            else if (tags is { Count: > 0 })
            {
                WriteMeta(existing with { Tags = [.. existing.Tags.Union(tags)] });
            }
        }

        return hash;
    }

    public CheckpointMeta? Get(string hash)
    {
        var path = MetaPath(Validate(hash));
        return File.Exists(path)
            ? JsonSerializer.Deserialize<CheckpointMeta>(File.ReadAllText(path), Json)
            : null;
    }

    /// <summary>Resolve a (possibly abbreviated) hash or a tag to a full hash.</summary>
    public string? Resolve(string reference)
    {
        if (reference.Length == 64 && Get(reference) is not null)
        {
            return reference;
        }

        var all = List();
        var byPrefix = all.Where(m => m.Hash.StartsWith(reference, StringComparison.Ordinal)).ToList();
        if (byPrefix.Count == 1)
        {
            return byPrefix[0].Hash;
        }

        var byTag = all.Where(m => m.Tags.Contains(reference)).ToList();
        return byTag.Count == 1 ? byTag[0].Hash : null;
    }

    public IReadOnlyList<CheckpointMeta> List(string? sessionId = null)
    {
        var metas = Directory.EnumerateFiles(Path.Combine(_root, "meta"), "*.json")
            .Select(p => JsonSerializer.Deserialize<CheckpointMeta>(File.ReadAllText(p), Json)!)
            .Where(m => sessionId is null || m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAt)
            .ToList();
        return metas;
    }

    public void AddTags(string hash, IReadOnlyList<string> tags)
    {
        lock (_metaLock)
        {
            var meta = Get(hash) ?? throw new AblException("not_found", $"checkpoint {hash} not found");
            WriteMeta(meta with { Tags = [.. meta.Tags.Union(tags)] });
        }
    }

    /// <summary>
    /// Delete unreferenced checkpoints. Caller-supplied refs (live session heads) and
    /// frozen-artifact base/final hashes are ALWAYS refused; tagged checkpoints are
    /// refused unless force. Meta files of deleted nodes are removed entirely — the tree
    /// stays reconstructable from survivors.
    /// </summary>
    public PruneResult Prune(IReadOnlySet<string> protectedHashes, bool force = false)
    {
        lock (_metaLock)
        {
            var keep = new HashSet<string>(protectedHashes, StringComparer.Ordinal);
            foreach (var artifact in ListFrozen())
            {
                keep.Add(artifact.BaseCheckpoint);
                keep.Add(artifact.ExpectedFinal);
            }

            var refused = new List<string>();
            var deleted = 0;
            foreach (var meta in List())
            {
                if (keep.Contains(meta.Hash))
                {
                    refused.Add(meta.Hash);
                    continue;
                }

                if (meta.Tags.Count > 0 && !force)
                {
                    refused.Add(meta.Hash);
                    continue;
                }

                File.Delete(ObjectPath(meta.Hash));
                File.Delete(MetaPath(meta.Hash));
                deleted++;
            }

            return new PruneResult(deleted, refused);
        }
    }

    // --- trajectory journal ---

    public void AppendJournal(string sessionId, TrajectoryEntry entry)
    {
        var dir = Path.Combine(_root, "sessions", Validate(sessionId));
        Directory.CreateDirectory(dir);
        File.AppendAllText(
            Path.Combine(dir, "journal.jsonl"),
            JsonSerializer.Serialize(entry, JsonlOptions) + "\n");
    }

    public IReadOnlyList<TrajectoryEntry> ReadJournal(string sessionId)
    {
        var path = Path.Combine(_root, "sessions", Validate(sessionId), "journal.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        return [.. File.ReadLines(path)
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<TrajectoryEntry>(l, JsonlOptions)!)];
    }

    // --- frozen artifacts ---

    public void SaveFrozen(FrozenArtifact artifact)
    {
        File.WriteAllText(
            Path.Combine(_root, "frozen", $"{Validate(artifact.Name)}.json"),
            JsonSerializer.Serialize(artifact, Json));
    }

    public FrozenArtifact? GetFrozen(string name)
    {
        var path = Path.Combine(_root, "frozen", $"{Validate(name)}.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<FrozenArtifact>(File.ReadAllText(path), Json)
            : null;
    }

    public IReadOnlyList<FrozenArtifact> ListFrozen() =>
        [.. Directory.EnumerateFiles(Path.Combine(_root, "frozen"), "*.json")
            .Select(p => JsonSerializer.Deserialize<FrozenArtifact>(File.ReadAllText(p), Json)!)];

    // --- git-portable bundles (feature 002) ---

    private const int BundleFormatVersion = 1;

    // artifact.json is script text and small metadata; anything bigger is not a bundle
    // this tool produced. Gates the read so a repo-borne multi-GB file can't OOM the daemon.
    private const long MaxArtifactJsonBytes = 4 * 1024 * 1024;

    // Deterministic bundle serialization (FR-002): fixed member order (the record),
    // indented, LF on every platform — unchanged re-exports must be byte-identical.
    // Relaxed escaping: the file is reviewed by humans in git diffs and never lands in an
    // HTML context, so script text keeps its literal quotes and non-ASCII.
    private static readonly JsonSerializerOptions BundleJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Export a frozen artifact as a repo-committable bundle: &lt;dest&gt;/&lt;name&gt;/artifact.json
    /// + base.blend (the base checkpoint object, verbatim). Timestamps come from the
    /// artifact — never wall-clock — so re-exports are byte-identical (FR-002).
    /// </summary>
    public BundlePaths ExportBundle(string name, string destDir)
    {
        lock (_metaLock)
        {
            var artifact = GetFrozen(name)
                ?? throw new AblException("not_found", $"no frozen artifact '{name}'");
            var objectPath = ObjectPath(artifact.BaseCheckpoint);
            if (!File.Exists(objectPath))
            {
                throw new AblException(
                    "bad_reference", $"base checkpoint {Short(artifact.BaseCheckpoint)} is missing from the store");
            }

            var dir = Path.Combine(destDir, Validate(name));
            Directory.CreateDirectory(dir);
            var jsonPath = Path.Combine(dir, "artifact.json");
            var blendPath = Path.Combine(dir, "base.blend");
            RefuseSymlink(dir);
            RefuseSymlink(jsonPath);
            RefuseSymlink(blendPath);
            var bundle = new ArtifactBundle(
                BundleFormatVersion, artifact.Name, artifact.BaseCheckpoint, artifact.Ops,
                artifact.ExpectedFinal, artifact.ExpectedDigest, artifact.BlenderVersion,
                artifact.CreatedAt);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(bundle, BundleJson) + "\n");
            File.Copy(objectPath, blendPath, overwrite: true);
            return new BundlePaths(dir, jsonPath, blendPath);
        }
    }

    /// <summary>
    /// Import a bundle directory: ALL verification happens before any store mutation
    /// (FR-003) — well-formed artifact.json with a known format version, path-safe name,
    /// and SHA-256(base.blend) equal to the recorded base checkpoint hash (the integrity
    /// gate that catches git/LFS text-mangling). Idempotent per FR-004.
    /// </summary>
    public FrozenArtifact ImportBundle(string bundleDir)
    {
        var jsonPath = Path.Combine(bundleDir, "artifact.json");
        var blendPath = Path.Combine(bundleDir, "base.blend");
        if (!File.Exists(jsonPath) || !File.Exists(blendPath))
        {
            throw new AblException(
                "bad_reference", $"'{bundleDir}' is not a bundle",
                hint: "a bundle directory contains artifact.json + base.blend (trajectory_export makes one)");
        }

        // Bundle entries are plain files — a symlinked entry would let a hostile repo
        // read (hash, parse) arbitrary daemon-readable files instead of bundle content.
        RefuseSymlink(jsonPath);
        RefuseSymlink(blendPath);
        if (new FileInfo(jsonPath).Length > MaxArtifactJsonBytes)
        {
            throw new AblException(
                "bad_reference", $"artifact.json exceeds the {MaxArtifactJsonBytes / (1024 * 1024)} MB import cap");
        }

        ArtifactBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ArtifactBundle>(File.ReadAllText(jsonPath), Json);
        }
        catch (JsonException ex)
        {
            throw new AblException("bad_reference", $"artifact.json is not valid JSON: {ex.Message}");
        }

        if (bundle is null || bundle.FormatVersion != BundleFormatVersion)
        {
            throw new AblException(
                "bad_reference",
                $"unsupported bundle format version '{bundle?.FormatVersion.ToString() ?? "?"}' (this build reads {BundleFormatVersion})");
        }

        if (bundle.Ops is not { Count: > 0 } || string.IsNullOrEmpty(bundle.ExpectedDigest)
            || string.IsNullOrEmpty(bundle.ExpectedFinal) || string.IsNullOrEmpty(bundle.BaseCheckpoint)
            || string.IsNullOrEmpty(bundle.Name))
        {
            throw new AblException(
                "bad_reference",
                "artifact.json is missing ops, name, baseCheckpoint, expectedFinal or expectedDigest");
        }

        Validate(bundle.Name);
        if (bundle.BaseCheckpoint.Length != 64 || !bundle.BaseCheckpoint.All(char.IsAsciiHexDigitLower))
        {
            throw new AblException(
                "bad_reference", "baseCheckpoint is not a lowercase SHA-256 hex hash");
        }

        string hash;
        using (var stream = File.OpenRead(blendPath))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        if (hash != bundle.BaseCheckpoint)
        {
            throw new AblException(
                "bad_reference",
                $"base.blend hashes to {Short(hash)} but the bundle records {Short(bundle.BaseCheckpoint)}",
                hint: "the .blend was altered in transit — check the repo's gitattributes (*.blend -text) and LFS smudge");
        }

        var artifact = new FrozenArtifact(
            bundle.Name, bundle.BaseCheckpoint, bundle.Ops, bundle.ExpectedFinal,
            bundle.ExpectedDigest, bundle.CreatedAt, bundle.BlenderVersion);

        lock (_metaLock)
        {
            var existing = GetFrozen(bundle.Name);
            if (existing is not null)
            {
                if (JsonSerializer.Serialize(existing, Json) == JsonSerializer.Serialize(artifact, Json))
                {
                    return existing; // FR-004: identical content — a clean no-op
                }

                throw new AblException(
                    "bad_reference", $"a different frozen artifact named '{bundle.Name}' already exists",
                    hint: "no silent overwrite — rename one of them, or remove the local artifact first");
            }

            var target = ObjectPath(bundle.BaseCheckpoint);
            if (!File.Exists(target))
            {
                // Land bytes the way Ingest does: stage, re-hash the staged copy, then an
                // atomic move — an interrupted copy can never register wrong bytes under
                // this hash. (Content-addressed: dedupes by construction.)
                var staged = TmpBlendPath();
                try
                {
                    File.Copy(blendPath, staged);
                    string stagedHash;
                    using (var stream = File.OpenRead(staged))
                    {
                        stagedHash = Convert.ToHexStringLower(SHA256.HashData(stream));
                    }

                    if (stagedHash != bundle.BaseCheckpoint)
                    {
                        throw new AblException(
                            "bad_reference",
                            $"base.blend changed mid-import (staged copy hashes to {Short(stagedHash)})");
                    }

                    File.Move(staged, target);
                }
                finally
                {
                    if (File.Exists(staged))
                    {
                        File.Delete(staged);
                    }
                }
            }

            if (Get(bundle.BaseCheckpoint) is null)
            {
                WriteMeta(new CheckpointMeta(
                    bundle.BaseCheckpoint, null, "import", new FileInfo(target).Length,
                    bundle.CreatedAt, ["imported"], null, bundle.BlenderVersion));
            }

            SaveFrozen(artifact);
        }

        return artifact;
    }

    private static readonly JsonSerializerOptions JsonlOptions = new(JsonSerializerDefaults.Web);

    private string MetaPath(string hash) => Path.Combine(_root, "meta", $"{hash}.json");

    private void WriteMeta(CheckpointMeta meta) =>
        File.WriteAllText(MetaPath(meta.Hash), JsonSerializer.Serialize(meta, Json));

    /// <summary>Abbreviate a hash for error messages without assuming its length.</summary>
    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    /// <summary>
    /// Bundle paths must be regular files/directories: a symlink is either a hostile
    /// bundle entry (turns import into a read oracle on daemon-readable files) or a
    /// pre-planted write-through trap in an export destination. LinkTarget is lstat —
    /// it also catches dangling links that Exists() reports as absent.
    /// </summary>
    private static void RefuseSymlink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new AblException(
                "bad_reference", $"'{path}' is a symlink — bundle entries must be regular files");
        }
    }

    /// <summary>Path-safety gate: ids/hashes/names used in file paths (security anchor).</summary>
    private static string Validate(string value)
    {
        if (value.Length is 0 or > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
        {
            throw new AblException("bad_reference", $"invalid store reference '{value}'");
        }

        if (value.StartsWith('.'))
        {
            throw new AblException("bad_reference", $"invalid store reference '{value}'");
        }

        return value;
    }
}
