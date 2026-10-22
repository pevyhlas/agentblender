using System.Text.Json.Serialization;

namespace Abl.Core.Workers;

/// <summary>One GPU device a worker's hello reported (009 FR-001): a bounded, cleaned name and its Cycles type.</summary>
public sealed record GpuDevice(string Name, string Type);

/// <summary>
/// The GPU capability a worker reported in its hello (009 FR-001, plan D2): the Cycles
/// backend (<c>optix</c>, <c>metal</c> or <c>none</c>), its devices, and the MetalRT
/// preference (<c>null</c> unless the backend is Metal). Read by
/// <see cref="Rendering.GpuGuard"/>; validated by <see cref="WorkerReplies"/> before it is built.
/// </summary>
public sealed record GpuCapability(string Backend, IReadOnlyList<GpuDevice> Devices, string? MetalRt)
{
    /// <summary>No GPU backend: the state of a handle before its hello, and of a GPU-less host.</summary>
    public static GpuCapability None { get; } = new("none", [], null);
}

/// <summary>
/// The device AB configured and checked immediately before a Cycles render (009 FR-005,
/// plan D4 b): not proven execution. <see cref="Fallback"/> is <c>gpu_busy</c> or
/// <c>gpu_unavailable</c> when a <c>cpu_fallback</c> request rendered on the CPU instead
/// (plan D7), and is omitted when null. <see cref="Name"/> is null for the CPU.
/// </summary>
public sealed record RenderDevice(
    string Backend,
    // Always written, "name": null for the CPU, even under a serializer that omits nulls
    // (the MCP structured result), so every surface carries the same object.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fallback = null)
{
    /// <summary>
    /// The text form used by the hand-built surfaces (MCP <c>batch_run</c> block, CLI):
    /// <c>metal (Apple M…)</c>, <c>cpu</c>, or <c>cpu (fallback gpu_unavailable)</c>.
    /// </summary>
    public static string Describe(string backend, string? name, string? fallback) =>
        backend
        + (name is null ? "" : $" ({name})")
        + (fallback is null ? "" : $" (fallback {fallback})");

    public override string ToString() => Describe(Backend, Name, Fallback);
}
