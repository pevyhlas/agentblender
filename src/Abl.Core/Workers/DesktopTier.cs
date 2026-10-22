using System.Net.Sockets;
using Abl.Core.Rendering;
using Microsoft.Extensions.Logging;

namespace Abl.Core.Workers;

/// <summary>
/// The host's view of the remote GPU workstation as a headless worker (feature 003,
/// D-15). Owns the desktop endpoint/token config, a cached availability probe, the
/// connect-and-warm handshake that yields a remote <see cref="WorkerHandle"/>, and a
/// SEPARATE GPU lease for the desktop card (FR-007). When it is not configured the whole
/// tier is inert and every render runs locally (SC-005).
/// </summary>
public sealed class DesktopTier(AblOptions options, ILogger<DesktopTier> logger)
{
    private readonly Lock _probeLock = new();
    private bool _cachedAvailable;
    private DateTimeOffset _probedAt = DateTimeOffset.MinValue;

    /// <summary>The desktop's own single-slot GPU lease — independent of the host's local GPU lease.</summary>
    public GpuLease GpuLease { get; } = new("desktop");

    /// <summary>Last bounded TCP probe, or -1 until probed. Not authentication/OptiX readiness.</summary>
    public int LastAvailability
    {
        get
        {
            lock (_probeLock) return _probedAt == DateTimeOffset.MinValue ? -1 : _cachedAvailable ? 1 : 0;
        }
    }

    /// <summary>
    /// <see cref="LastAvailability"/> in the one wire encoding that <c>/healthz</c>
    /// (<c>desktopTcpAvailable</c>) and <c>daemon_status</c> (<c>desktop.tcpAvailable</c>) share:
    /// true, false, or null until probed (008 T051, 2b round-1 eng-arch LOW-5).
    /// </summary>
    public bool? TcpAvailable => LastAvailability switch { 1 => true, 0 => false, _ => null };

    /// <summary>True only when both endpoint and token are configured out-of-band.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.DesktopEndpoint)
        && !string.IsNullOrWhiteSpace(options.DesktopToken);

    /// <summary>
    /// Availability = "the launcher accepts a TCP connection", bounded by
    /// <see cref="AblOptions.DesktopProbeTimeout"/> and cached for
    /// <see cref="AblOptions.DesktopProbeTtl"/> so a batch of ops does not probe each time.
    /// </summary>
    public async Task<bool> ProbeAvailableAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return false;
        }

        lock (_probeLock)
        {
            if (DateTimeOffset.UtcNow - _probedAt < options.DesktopProbeTtl)
            {
                return _cachedAvailable;
            }
        }

        var (host, port) = ParseEndpoint();
        var available = false;
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.DesktopProbeTimeout);
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            available = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled — not a probe timeout
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            available = false;
        }

        bool changed;
        lock (_probeLock)
        {
            changed = _probedAt == DateTimeOffset.MinValue || _cachedAvailable != available;
            _cachedAvailable = available;
            _probedAt = DateTimeOffset.UtcNow;
        }

        if (changed)
        {
            // One line per transition (008 FR-011), not per probe.
            logger.LogInformation("desktop {Endpoint} {State}", options.DesktopEndpoint, available ? "reachable" : "unreachable");
        }

        return available;
    }

    /// <summary>
    /// Connect to the launcher, authenticate, and warm a remote worker. Throws on any
    /// failure (connect/auth/spawn) — the render path treats that as "desktop unavailable"
    /// and falls back to the local tier. On success the returned handle is a normal
    /// disposable worker whose process lives on the desktop.
    /// </summary>
    public async Task<WorkerHandle> ConnectWorkerAsync(CancellationToken ct)
    {
        var (host, port) = ParseEndpoint();
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        WorkerConnection? connection = null;
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Bound connect + handshake tightly: an unreachable-but-cached-available
                // desktop (or one that accepts TCP but never answers the handshake) must fail
                // fast so the render falls back to local, not stall on a dead peer.
                cts.CancelAfter(options.DesktopConnectTimeout);
                await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);

                // Auth handshake on a non-owning stream; then hand the raw socket to the
                // WorkerConnection (which owns it). WireCodec reads exact byte counts, so
                // the handshake never over-reads into the op stream.
                await using var handshake = new NetworkStream(socket, ownsSocket: false);
                await DesktopHandshake.ClientAsync(handshake, options.DesktopToken!, cts.Token)
                    .ConfigureAwait(false);
            }

            connection = new WorkerConnection(socket);
            var handle = new WorkerHandle(
                $"desktop-{Guid.NewGuid().ToString("N")[..8]}", connection, WorkerTier.Desktop);

            var hello = await handle.RequestAsync("hello", null, options.SpawnTimeout, ct).ConfigureAwait(false);
            handle.ApplyHello(hello);
            handle.State = WorkerState.Warming;
            await handle.RequestAsync(
                    "warm", new System.Text.Json.Nodes.JsonObject { ["prewarm_render"] = true },
                    options.SpawnTimeout, ct)
                .ConfigureAwait(false);
            handle.State = WorkerState.Ready;

            logger.LogInformation(
                "desktop worker {Id} ready (blender {Version}, gpu {Backend})",
                handle.Id, handle.BlenderVersion, handle.Gpu.Backend);
            return handle;
        }
        catch
        {
            connection?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    private (string Host, int Port) ParseEndpoint()
    {
        var endpoint = options.DesktopEndpoint!;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out var port))
        {
            throw new AblException(
                "bad_request", $"ABL_DESKTOP_ENDPOINT '{endpoint}' is not host:port");
        }

        return (endpoint[..colon], port);
    }
}
