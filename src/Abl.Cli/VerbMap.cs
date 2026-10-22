namespace Abl.Cli;

/// <summary>
/// CLI verb → public op mapping. Values must equal OpSurface.All — the parity contract
/// test enforces it (FR-009).
/// </summary>
public static class VerbMap
{
    public static readonly IReadOnlyDictionary<(string, string), string> Verbs =
        new Dictionary<(string, string), string>
        {
            [("session", "open")] = "session_open",
            [("session", "list")] = "session_list",
            [("session", "close")] = "session_close",
            [("scene", "state")] = "scene_state",
            [("scene", "inspect")] = "scene_inspect",
            [("scene", "exec")] = "scene_exec",
            [("scene", "preview")] = "scene_preview",
            [("checkpoint", "save")] = "checkpoint_save",
            [("checkpoint", "list")] = "checkpoint_list",
            [("checkpoint", "branch")] = "checkpoint_branch",
            [("checkpoint", "tag")] = "checkpoint_tag",
            [("checkpoint", "diff")] = "checkpoint_diff",
            [("checkpoint", "prune")] = "checkpoint_prune",
            [("trajectory", "freeze")] = "trajectory_freeze",
            [("trajectory", "export")] = "trajectory_export",
            [("trajectory", "import")] = "trajectory_import",
            [("replay", "run")] = "replay_run",
            [("render", "final")] = "render_final",
            [("batch", "run")] = "batch_run",
            [("delivery", "export")] = "delivery_export",
            [("delivery", "list")] = "delivery_list",
            [("delivery", "verify")] = "delivery_verify",

            // 008 FR-010: the CLI form of the daemon_status op adds its `local` object and
            // exits 3 when no daemon runs (Host/DaemonVerbs).
            [("daemon", "status")] = "daemon_status",
        };
}
