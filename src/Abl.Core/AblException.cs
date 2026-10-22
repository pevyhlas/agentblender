namespace Abl.Core;

/// <summary>
/// The structured error every surface returns: {kind, message, hint?, lastCheckpoint?}
/// (FR-010). A dead worker's error carries the last checkpoint so the recovery move
/// (checkpoint_branch) is one call.
/// </summary>
public sealed class AblException(
    string kind,
    string message,
    string? hint = null,
    string? lastCheckpoint = null,
    string? workerTraceback = null)
    : Exception(message)
{
    public string Kind { get; } = kind;
    public string? Hint { get; } = hint;
    public string? LastCheckpoint { get; } = lastCheckpoint;
    public string? WorkerTraceback { get; } = workerTraceback;
}
