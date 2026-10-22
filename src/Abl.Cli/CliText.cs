using System.Text.Json.Nodes;
using Abl.Core.Workers;

namespace Abl.Cli;

/// <summary>
/// The CLI's hand-built text lines that other surfaces must match (009 FR-003, FR-005), kept
/// out of the top-level program so the surface-parity tests can render them from a REST
/// payload exactly as <c>abl</c> prints them.
/// </summary>
public static class CliText
{
    /// <summary>
    /// The <c>render_final</c> / <c>batch_run</c> device line (009 FR-005): <c>device metal (…)</c>,
    /// <c>device cpu</c> for a requested CPU render, <c>device cpu (fallback gpu_unavailable)</c>
    /// for a CPU fallback; null when the payload has no <c>device</c> (non-Cycles engines).
    /// </summary>
    public static string? DeviceLine(JsonObject payload) =>
        payload["device"] is JsonObject device
            ? "device " + RenderDevice.Describe(
                device["backend"]!.GetValue<string>(), device["name"]?.GetValue<string>(), device["fallback"]?.GetValue<string>())
            : null;

    /// <summary>
    /// The line an op verb prints when nothing answers at <paramref name="url"/> (008 FR-013,
    /// plan D3; golden-tested, AC-31): how to start the daemon and how to diagnose it.
    /// </summary>
    public static string Unreachable(string? url) =>
        $"abl: cannot reach the daemon at {url} — start it with 'abl daemon start' (diagnose: 'abl doctor')";

    /// <summary>The stderr lines for a REST error envelope: kind and message, then hint, checkpoint and traceback.</summary>
    public static IEnumerable<string> ErrorLines(JsonObject payload)
    {
        yield return $"abl: {payload["kind"]}: {payload["message"]}";
        if (payload["hint"] is { } hint)
        {
            yield return $"  hint: {hint}";
        }

        if (payload["lastCheckpoint"] is { } last)
        {
            yield return $"  lastCheckpoint: {last}";
        }

        if (payload["traceback"] is { } traceback)
        {
            yield return traceback.GetValue<string>();
        }
    }
}
