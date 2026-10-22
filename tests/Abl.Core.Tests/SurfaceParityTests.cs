using System.Reflection;
using Abl.Cli;
using Abl.Core;
using Abl.Daemon.Api;
using Abl.Daemon.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>FR-009: every surface exposes exactly the canonical op set — no drift, ever.</summary>
public class SurfaceParityTests
{
    [Fact]
    public void McpToolNamesMatchTheCanonicalSurface()
    {
        var toolNames = typeof(AblTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet();
        Assert.Equal(OpSurface.All.ToHashSet(), toolNames);
    }

    [Fact]
    public void CliVerbsMatchTheCanonicalSurface()
    {
        Assert.Equal(OpSurface.All.ToHashSet(), VerbMap.Verbs.Values.ToHashSet());
    }

    [Fact]
    public void RestHandlerTableIsTheCanonicalSurface()
    {
        // Ops derives from the REST handler dictionary's keys, so this fails whenever an
        // op is added to OpSurface without a REST handler (or vice versa) — it must never
        // regress to comparing OpSurface against itself.
        Assert.Equal(OpSurface.All.ToHashSet(), ApiEndpoints.Ops.ToHashSet());
        Assert.Equal(OpSurface.All.Count, ApiEndpoints.Ops.Count);
    }

    [Fact]
    public void EveryMcpToolHasADescription()
    {
        var undocumented = typeof(AblTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Where(m => m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is null)
            .Select(m => m.Name)
            .ToList();
        Assert.Empty(undocumented);
    }

    /// <summary>
    /// 009 T012 (plan "Agent-facing text"): a fresh agent reads the tool descriptions, not the
    /// README, so the GPU device contract is stated where it calls the tools.
    /// </summary>
    [Theory]
    [InlineData("render_final", null, "the result's `device` reports the device AB configured")]
    [InlineData("render_final", null, "about a minute while Metal compiles its kernels; do not cancel it as hung")]
    [InlineData("render_final", null, "`abl doctor --check gpu.render` primes the cache")]
    [InlineData("render_final", "lease", "cpu_fallback renders cycles_cpu when the GPU is busy or the host has no usable GPU")]
    [InlineData("batch_run", null, "for Cycles engines the batch's `device`")]
    [InlineData("delivery_export", null, "Renders with cycles_gpu by default; on a host without a GPU pass engine=cycles_cpu")]
    public void GpuDeviceTextReachesTheAgent(string tool, string? parameter, string expected)
    {
        var method = typeof(AblTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);
        ICustomAttributeProvider target = parameter is null
            ? method
            : method.GetParameters().Single(p => p.Name == parameter);
        var description = target.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;
        Assert.Contains(expected, description, StringComparison.Ordinal);
    }
}
