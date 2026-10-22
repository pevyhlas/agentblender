namespace Abl.Core;

/// <summary>
/// The canonical public surface (docs/public-surface.md). MCP tools, REST routes,
/// and CLI verbs all enumerate THIS list; the parity contract test fails on drift (FR-009).
/// </summary>
public static class OpSurface
{
    public static readonly IReadOnlyList<string> All =
    [
        "session_open",
        "session_list",
        "session_close",
        "scene_state",
        "scene_inspect",
        "scene_exec",
        "scene_preview",
        "checkpoint_save",
        "checkpoint_list",
        "checkpoint_branch",
        "checkpoint_tag",
        "checkpoint_diff",
        "checkpoint_prune",
        "trajectory_freeze",
        "trajectory_export",
        "trajectory_import",
        "replay_run",
        "render_final",
        "batch_run",
        "delivery_export",
        "delivery_list",
        "delivery_verify",
        "daemon_status",
    ];
}
