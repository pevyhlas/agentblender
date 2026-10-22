using Abl.Core;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class TimeoutRecycleTests
{
    [Fact]
    public async Task RunawayExecIsKilledAtTheTimeoutAndTheSessionStaysRecoverable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"abl-timeout-{Guid.NewGuid():N}");
        var options = TestOptions.FromEnvironment() with
        {
            StoreDir = Path.Combine(root, "store"),
            RendersDir = Path.Combine(root, "renders"),
            PoolFloor = 0,
            ExecTimeout = TimeSpan.FromSeconds(3), // host-enforced breach point
        };
        if (!File.Exists(options.BlenderPath))
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var store = new CheckpointStore(options.StoreDir);
        var supervisor = new WorkerSupervisor(options, NullLogger<WorkerSupervisor>.Instance);
        await using var pool = new WorkerPool(options, supervisor, NullLogger<WorkerPool>.Instance);
        await using var sessions = new SessionService(
            options, pool, store, new GpuLease(),
            new DesktopTier(options, NullLogger<DesktopTier>.Instance), NullLogger<SessionService>.Instance);

        var ct = CancellationToken.None;
        var session = await sessions.OpenAsync("timeout-test", ct);
        var head = session.Head;

        var error = await Assert.ThrowsAsync<AblException>(
            () => sessions.ExecAsync(session.Id, "import time\ntime.sleep(60)", null, ct));
        Assert.Equal("timeout", error.Kind);
        Assert.Equal(head, error.LastCheckpoint);

        // the session is dead (worker killed), and the recovery move works
        var followUp = await Assert.ThrowsAsync<AblException>(
            () => sessions.StateAsync(session.Id, "delta", ct));
        Assert.Equal("worker_died", followUp.Kind);
        var recovered = await sessions.BranchAsync(head, "timeout-recovered", ct);
        Assert.Equal(head, recovered.Head);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
