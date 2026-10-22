namespace Abl.Core;

/// <summary>
/// The agent-facing guidance the MCP server sends on initialize (008 FR-013, plan D3, D-09):
/// when AgentBlender helps, and when AgentGraphicDesign is the right tool. Kept within
/// <see cref="MaxInstructionWords"/>; <c>docs/agent-guide.md</c> and the README say the same.
/// Motion and family export are not offered.
/// </summary>
public static class AgentGuidance
{
    /// <summary>The word budget of <see cref="ServerInstructions"/> (FR-013).</summary>
    public const int MaxInstructionWords = 250;

    /// <summary>
    /// The MCP server <c>instructions</c>. The Metal clause is 009's agent-facing text: the
    /// compile half from 008 T053, the <c>abl doctor --check gpu.render</c> half from 009 T023.
    /// </summary>
    public const string ServerInstructions =
        "AgentBlender renders 3D scenes in warm headless Blender sessions. Reach for it when form, " +
        "light, material or camera matter: 3D product/hero renders, lighting and material studies, " +
        "camera/angle exploration, and parametric variant sweeps: freeze a trajectory, then " +
        "`batch_run` a params table and judge one contact sheet. Start with `session_open`, iterate " +
        "with `scene_exec` + 512 px `scene_preview`, read state as deltas, checkpoint before " +
        "branching. Deliver finished stills with `delivery_export` (one still per generation) and " +
        "hand them to AgentGraphicDesign as raster input. Do NOT use AgentBlender for flat 2D work: " +
        "vector logos/marks, wordmarks and text outlines, pixel art, icon and platform kits " +
        "(ICO/ICNS/web) belong to AgentGraphicDesign (`agd`). Final renders go to disk, never into " +
        "context. The first Metal render (`cycles_gpu` on a Mac) compiles kernels (~1 min; do not " +
        "cancel it); `abl doctor --check gpu.render` primes the cache. Run `daemon_status` if " +
        "something looks misconfigured.";
}
