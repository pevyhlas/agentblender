namespace Abl.Core;

/// <summary>
/// Daemon-wide configuration: the immutable result of <see cref="Config.AblConfig"/>
/// resolution (008 FR-002). It reads no environment variable itself; the initial values are
/// <see cref="AblDefaults"/>, so a directly constructed instance (tests) is hermetic.
/// </summary>
public sealed record AblOptions
{
    /// <summary>The Blender binary (explicit or discovered, 008 FR-003); null when none was found.</summary>
    public string? BlenderPath { get; init; }

    /// <summary>
    /// The resolver's failing Blender check (<c>blender.found</c> / <c>blender.version</c>), or
    /// null when Blender was not refused or was never checked (options resolved without a
    /// Blender search, or built by hand: only the worker hello then gates the spawn). When
    /// set, every spawn refuses with it before it binds a socket or starts a process, even
    /// when <see cref="BlenderPath"/> keeps an explicit path.
    /// </summary>
    public Config.CheckFailure? BlenderProblem { get; init; }

    /// <summary>What the resolver's <c>blender --version</c> probe found; the worker hello must agree.</summary>
    public Config.BlenderVersion? ProbedBlenderVersion { get; init; }

    /// <summary>Lets a Blender outside the supported line run (recorded in the config report).</summary>
    public bool AllowUnsupportedBlender { get; init; }

    /// <summary>Picks the socket length limit for the per-spawn runtime-dir check.</summary>
    public Config.HostPlatform Platform { get; init; } = OperatingSystem.IsMacOS() ? Config.HostPlatform.MacOS : Config.HostPlatform.Linux;

    /// <summary>Directory holding worker.py (the only bpy-importing code, D-10).</summary>
    public string WorkerDir { get; init; } = AblDefaults.WorkerDir();

    /// <summary>Socket rendezvous dir (008 FR-005, <see cref="Config.RuntimePaths"/>): checked before every spawn.</summary>
    public string RuntimeDir { get; init; } = Config.RuntimePaths.StateRunDir(AblDefaults.Home, null);

    /// <summary>Checkpoint store root (objects/meta/frozen/sessions, D-03).</summary>
    public string StoreDir { get; init; } = AblDefaults.StoreDir(AblDefaults.Home);

    /// <summary>Default output root for final renders (FR-005: disk-only).</summary>
    public string RendersDir { get; init; } = AblDefaults.RendersDir(AblDefaults.Home);

    /// <summary>Loopback port of the daemon (FR-013: always bound to 127.0.0.1).</summary>
    public int Port { get; init; } = AblDefaults.Port;

    /// <summary>Minimum level for the <c>Abl.*</c> log categories.</summary>
    public string LogLevel { get; init; } = AblDefaults.LogLevel;

    /// <summary>Extra variable names allowed into workers (008 T015 applies the allowlist).</summary>
    public IReadOnlyList<string> WorkerEnvPassthrough { get; init; } = [];

    /// <summary>FR-012: telemetry export is off unless enabled.</summary>
    public bool TelemetryEnabled { get; init; }

    /// <summary>OTLP http/protobuf base endpoint (metrics at /v1/metrics).</summary>
    public string OtlpEndpoint { get; init; } = AblDefaults.OtlpEndpoint;

    /// <summary>Explicit traces endpoint; null means <see cref="OtlpEndpoint"/> + /v1/traces.</summary>
    public string? TracesEndpoint { get; init; }

    /// <summary>Ready workers kept pre-warmed ahead of demand.</summary>
    public int PoolFloor { get; init; } = 1;

    public TimeSpan SpawnTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ExecTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Bounded wait before the lease policy (cpu_fallback/fail) kicks in (FR-011).</summary>
    public TimeSpan GpuLeaseWait { get; init; } = TimeSpan.FromSeconds(30);

    // --- desktop remote worker tier (feature 003, FR-001/003/004) ---
    // Both must be set (out-of-band, never committed) to enable the desktop tier; when
    // either is absent the tier is off and every render runs locally exactly as before
    // (SC-005 no-regression).

    /// <summary>Desktop launcher endpoint <c>host:port</c> (a private-network address), or null = off.</summary>
    public string? DesktopEndpoint { get; init; }

    /// <summary>Shared bearer token for the desktop launcher handshake (FR-003).</summary>
    public string? DesktopToken { get; init; }

    /// <summary>Bounded connect timeout for the desktop availability probe (FR-004).</summary>
    public TimeSpan DesktopProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Bounded connect+handshake timeout when bringing up a remote worker — kept
    /// short so an unreachable-but-cached-available desktop falls back to local promptly
    /// instead of stalling on a dead TCP connect.</summary>
    public TimeSpan DesktopConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a probe result is cached, so heavy ops don't probe on every call.</summary>
    public TimeSpan DesktopProbeTtl { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Never prints the token (the synthesized record printer would).</summary>
    public override string ToString() => $"AblOptions {{ Port = {Port}, StoreDir = {StoreDir} }}";
}
