using Abl.Core;
using Abl.Core.Delivery;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// Real-Blender test host: temp store, pool floor 0 (spawn per take, no background
/// spawns), the repo worker/ scripts, the binary at $ABL_BLENDER.
/// </summary>
public sealed class BlenderFixture : IAsyncLifetime
{
    public AblOptions Options { get; private set; } = null!;
    public CheckpointStore Store { get; private set; } = null!;
    public WorkerPool Pool { get; private set; } = null!;
    public SessionService Sessions { get; private set; } = null!;
    public TrajectoryService Trajectories { get; private set; } = null!;
    public DeliveryService Deliveries { get; private set; } = null!;
    public string Root { get; private set; } = null!;

    public bool BlenderAvailable => File.Exists(Options.BlenderPath);

    public Task InitializeAsync()
    {
        Root = Path.Combine(Path.GetTempPath(), $"abl-itest-{Guid.NewGuid():N}");
        Options = TestOptions.FromEnvironment() with
        {
            StoreDir = Path.Combine(Root, "store"),
            RendersDir = Path.Combine(Root, "renders"),
            PoolFloor = 0,
        };
        Store = new CheckpointStore(Options.StoreDir);
        var supervisor = new WorkerSupervisor(Options, NullLogger<WorkerSupervisor>.Instance);
        Pool = new WorkerPool(Options, supervisor, NullLogger<WorkerPool>.Instance);
        var lease = new GpuLease();
        var desktop = new DesktopTier(Options, NullLogger<DesktopTier>.Instance); // unconfigured: all-local
        Sessions = new SessionService(Options, Pool, Store, lease, desktop, NullLogger<SessionService>.Instance);
        Trajectories = new TrajectoryService(
            Options, Pool, Store, lease, desktop, NullLogger<TrajectoryService>.Instance);
        Deliveries = new DeliveryService(
            Options, Pool, Store, lease, NullLogger<DeliveryService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Sessions.DisposeAsync();
        await Pool.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

[CollectionDefinition("blender", DisableParallelization = true)]
public sealed class BlenderCollection : ICollectionFixture<BlenderFixture>;
