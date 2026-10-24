using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Abl.Core.Config;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging;

namespace Abl.Launcher;

/// <summary>
/// The desktop-side agent (feature 003, FR-002/003). Listens on a specific private-network
/// interface, authenticates each host connection with the shared token BEFORE spawning
/// anything, then launches a HEADLESS <c>blender.exe --background --factory-startup</c>
/// worker and bridges its bytes to the host. It never drives an interactive GUI Blender —
/// the worker is always its own <c>--background</c> process (D-15). One worker per
/// connection; the worker is killed when the connection closes.
/// </summary>
public sealed class LauncherHost(LauncherOptions options, ILogger<LauncherHost> logger)
{
    private readonly TcpListener _listener = new(LauncherOptions.NormalizeEndpoint(options.BindEndpoint));

    // Anti-slow-loris (A1): a peer that connects but never finishes the handshake must not
    // pin a worker slot. Cap connections still in the pre-auth phase; each is also bounded
    // in time by AuthTimeout. Legit connections release the slot as soon as they authenticate,
    // so a long render never occupies one.
    private readonly SemaphoreSlim _preAuthSlots = new(options.MaxPendingHandshakes, options.MaxPendingHandshakes);

    /// <summary>The actual bound endpoint (useful when the config asked for an ephemeral port).</summary>
    public IPEndPoint BoundEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        LauncherOptions.ValidateBindAddress(((IPEndPoint)_listener.LocalEndpoint).Address);
        _listener.Start();
        logger.LogInformation("desktop launcher listening on {Endpoint} (private-network-only, token-gated)", BoundEndpoint);
    }

    /// <summary>Accept loop: each connection is handled independently (auth → spawn → bridge).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket edge;
            try
            {
                edge = await _listener.AcceptSocketAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = HandleAsync(edge, ct);
        }

        _listener.Stop();
    }

    private async Task HandleAsync(Socket edge, CancellationToken ct)
    {
        var remote = edge.RemoteEndPoint;
        Process? blender = null;
        using (edge)
        {
            try
            {
                if (!await _preAuthSlots.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
                {
                    logger.LogWarning("dropping connection from {Remote}: too many pending handshakes", remote);
                    return;
                }

                bool authorized;
                try
                {
                    using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    authCts.CancelAfter(options.AuthTimeout);
                    await using var authStream = new NetworkStream(edge, ownsSocket: false);
                    authorized = await DesktopHandshake.ServerAsync(authStream, options.Token, authCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException or IOException or SocketException or InvalidDataException)
                {
                    logger.LogWarning("handshake from {Remote} failed or timed out", remote);
                    return;
                }
                finally
                {
                    _preAuthSlots.Release();
                }

                if (!authorized)
                {
                    logger.LogWarning("rejected connection from {Remote}: bad token", remote);
                    return;
                }

                using var local = new TcpListener(IPAddress.Loopback, 0);
                local.Start();
                var localPort = ((IPEndPoint)local.LocalEndpoint).Port;

                blender = SpawnWorker(localPort);
                Socket worker;
                using (var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    acceptCts.CancelAfter(options.SpawnTimeout);
                    try
                    {
                        worker = await local.AcceptSocketAsync(acceptCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning("blender worker did not connect back within {Timeout}s", options.SpawnTimeout.TotalSeconds);
                        return;
                    }
                }

                logger.LogInformation("bridging {Remote} ↔ headless blender (pid {Pid})", remote, blender.Id);
                using (worker)
                {
                    await BridgeAsync(edge, worker, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                logger.LogWarning(ex, "connection from {Remote} ended", remote);
            }
            // This runs as a detached task (fire-and-forget in the accept loop): a spawn
            // failure (e.g. a bad blender path) must be logged, never lost as an unobserved
            // task exception.
            catch (Exception ex)
            {
                logger.LogError(ex, "connection from {Remote} failed", remote);
            }
            finally
            {
                KillBlender(blender);
            }
        }
    }

    private Process SpawnWorker(int localPort)
    {
        var startInfo = WorkerStartInfo(options, localPort);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start {options.BlenderPath}");
        process.OutputDataReceived += (_, e) => LogWorker(e.Data);
        process.ErrorDataReceived += (_, e) => LogWorker(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    internal static ProcessStartInfo WorkerStartInfo(LauncherOptions options, int localPort)
    {
        var workerScript = Path.Combine(options.WorkerDir, "worker.py");
        var startInfo = new ProcessStartInfo(options.BlenderPath)
        {
            ArgumentList =
            {
                "--background", "--factory-startup", "--python", workerScript, "--",
                "--tcp", "127.0.0.1", localPort.ToString(CultureInfo.InvariantCulture),
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ChildEnvironment.Restrict(startInfo.Environment, AllowsWorkerVariable);
        return startInfo;
    }

    // Windows needs its system/profile/temp variables; no launcher token, model/cloud credentials,
    // Python injection, loader hooks or .NET startup hooks are inherited on either platform.
    internal static bool AllowsWorkerVariable(string name) =>
        ChildEnvironment.WorkerNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.StartsWith("LC_", StringComparison.OrdinalIgnoreCase)
        || new[] { "SYSTEMROOT", "WINDIR", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" }
            .Contains(name, StringComparer.OrdinalIgnoreCase);

    private void LogWorker(string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            logger.LogDebug("blender: {Line}", line);
        }
    }

    private static async Task BridgeAsync(Socket edge, Socket worker, CancellationToken ct)
    {
        var toWorker = PumpAsync(edge, worker, ct);
        var toEdge = PumpAsync(worker, edge, ct);
        await Task.WhenAny(toWorker, toEdge).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Socket from, Socket to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await to.SendAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer went away; the sibling pump and HandleAsync teardown finish the job.
        }

        try
        {
            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private void KillBlender(Process? blender)
    {
        if (blender is null)
        {
            return;
        }

        try
        {
            if (!blender.HasExited)
            {
                blender.Kill(entireProcessTree: true);
                blender.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
        finally
        {
            blender.Dispose();
        }
    }
}
