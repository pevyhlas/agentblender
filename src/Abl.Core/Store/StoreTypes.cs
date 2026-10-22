using System.Text.Json.Nodes;

namespace Abl.Core.Store;

/// <summary>
/// One content-addressed .blend snapshot in the tree (D-03). Hash addresses the BYTES
/// (dedup, storage); Digest is the canonical scene-content hash the replay gate compares
/// (bytes embed process memory layout and are not cross-process deterministic — R-03).
/// </summary>
public sealed record CheckpointMeta(
    string Hash,
    string? Parent,
    string SessionId,
    long Bytes,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags,
    string? Digest = null,
    string? BlenderVersion = null);

/// <summary>One journal line in a session's trajectory (sessions/&lt;id&gt;/journal.jsonl).</summary>
public sealed record TrajectoryEntry(
    int Seq,
    string Op,
    string? Script,
    JsonObject? Params,
    string? Checkpoint,
    double DurationMs,
    DateTimeOffset At);

/// <summary>One frozen, self-contained op of a trajectory (script CONTENT, not a path).</summary>
public sealed record FrozenOp(string Script, JsonObject? Params);

/// <summary>
/// A winning trajectory frozen into a replayable artifact. The replay gate is equality
/// of the canonical scene DIGEST (R-03: .blend bytes are not cross-process
/// deterministic) — pixels are excluded. ExpectedFinal keeps the final checkpoint's
/// byte-hash as a store reference (prune protection, branching).
/// </summary>
public sealed record FrozenArtifact(
    string Name,
    string BaseCheckpoint,
    IReadOnlyList<FrozenOp> Ops,
    string ExpectedFinal,
    string ExpectedDigest,
    DateTimeOffset CreatedAt,
    string? BlenderVersion = null);

public sealed record PruneResult(int Deleted, IReadOnlyList<string> Refused);

/// <summary>
/// The on-disk schema of a bundle's artifact.json (feature 002). A bundle directory =
/// this file + base.blend (the base checkpoint object, verbatim — SHA-256 must equal
/// BaseCheckpoint, which is how git/LFS mangling is caught at import). Serialization is
/// deterministic (fixed member order, indented, LF) so unchanged re-exports diff clean.
/// </summary>
public sealed record ArtifactBundle(
    int FormatVersion,
    string Name,
    string BaseCheckpoint,
    IReadOnlyList<FrozenOp> Ops,
    string ExpectedFinal,
    string ExpectedDigest,
    string? BlenderVersion,
    DateTimeOffset CreatedAt);

/// <summary>Where an exported bundle landed.</summary>
public sealed record BundlePaths(string Directory, string ArtifactJson, string BaseBlend);
