using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Abl.Core.Config;
using Microsoft.Extensions.Logging;

namespace Abl.Core.Workers;

/// <summary>
/// Spawns Blender worker processes and walks them Spawning→Warming→Ready.
/// Rendezvous: the host BINDS the socket before spawning, the worker connects —
/// no filesystem polling. Before every spawn the runtime dir is checked (008 FR-005: a real,
/// user-owned 0700 directory whose socket paths fit <c>sun_path</c>), on every Unix.
/// </summary>
public sealed class WorkerSupervisor(AblOptions options, ILogger<WorkerSupervisor> logger)
{
    /// <summary>The runtime-dir file-system seam (tests inject a foreign owner).</summary>
    public IConfigFileSystem FileSystem { get; init; } = UnixConfigFileSystem.Instance;

    public async Task<WorkerHandle> SpawnAsync(bool prewarm, CancellationToken ct)
    {
        // 008 FR-003: the resolver's verdict refuses before any socket is bound or process
        // started, so a refused explicit Blender never runs worker.py.
        if (options.BlenderProblem is { } problem)
        {
            throw Refused(problem.ToException(), WorkerSpawnStage.Preflight);
        }

        var blenderPath = options.BlenderPath ?? throw Refused(new ConfigException(
            "blender.found",
            "no Blender is configured or discovered",
            SupportedBlender.FoundRemedy), WorkerSpawnStage.Preflight);
        PrepareRuntimeDir();

        var id = Guid.NewGuid().ToString("N")[..8];
        var socketPath = Path.Combine(options.RuntimeDir, RuntimePaths.SocketFileName(id));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            File.Delete(socketPath);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(socketPath, RuntimePaths.SocketMode);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            throw new WorkerDeadException($"cannot bind worker socket {socketPath}: {ex.Message}", ex) { Stage = WorkerSpawnStage.Spawn };
        }

        var workerScript = Path.Combine(options.WorkerDir, "worker.py");
        var startInfo = new ProcessStartInfo(blenderPath)
        {
            ArgumentList = { "--background", "--factory-startup", "--python", workerScript, "--", socketPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // 008 FR-005 (plan A6): the worker sees only the allowlist and worker.envPassthrough.
        ChildEnvironment.Restrict(
            startInfo.Environment, name => ChildEnvironment.AllowsForWorker(name, options.WorkerEnvPassthrough));

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            File.Delete(socketPath);
            throw Refused(new ConfigException(
                "blender.found",
                $"cannot start Blender {blenderPath}: {ex.Message}",
                SupportedBlender.FixPathRemedy), WorkerSpawnStage.Spawn);
        }

        WorkerHandle? handle = null;
        var stage = WorkerSpawnStage.Spawn;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.SpawnTimeout);
            Socket accepted;
            try
            {
                accepted = await AcceptOrExitAsync(listener, process, id, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new WorkerDeadException(
                    $"worker {id} did not connect within {options.SpawnTimeout.TotalSeconds:0}s");
            }

            handle = new WorkerHandle(id, process, new WorkerConnection(accepted));
            File.Delete(socketPath); // connection persists; keep the runtime dir clean

            stage = WorkerSpawnStage.Hello;
            var hello = await handle.RequestAsync("hello", null, options.SpawnTimeout, ct).ConfigureAwait(false);
            handle.ApplyHello(hello);
            CheckHelloVersion(handle.BlenderVersion, blenderPath);
            handle.State = WorkerState.Warming;
            stage = WorkerSpawnStage.Warm;
            await handle.RequestAsync(
                    "warm", new JsonObject { ["prewarm_render"] = prewarm }, options.SpawnTimeout, ct)
                .ConfigureAwait(false);
            handle.State = WorkerState.Ready;

            logger.LogInformation(
                "worker {Id} ready in {Ms} ms (blender {Version}, gpu {Backend})",
                id,
                stopwatch.ElapsedMilliseconds,
                handle.BlenderVersion,
                handle.Gpu.Backend);
            return handle;
        }
        catch (Exception failure)
        {
            // Plan B4: a failed spawn carries how Blender ended (exit code, output tail) as a
            // typed fact, never spliced into the message. A cancelled spawn needs no report.
            var report = failure is not OperationCanceledException;
            WorkerExit? exit = null;
            if (handle is not null && report)
            {
                exit = await handle.EndAsync(OutputFlush).ConfigureAwait(false);
            }
            else if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                // Before the connect nothing reads the pipe yet: the stderr tail is still in it.
                if (report && WorkerSpawnStage.ExitOf(failure) is null)
                {
                    exit = new WorkerExit(null, await PipedStderrAsync(process).ConfigureAwait(false));
                }

                process.Dispose();
            }

            File.Delete(socketPath);
            exit ??= WorkerSpawnStage.ExitOf(failure);
            if (exit is { Output.Count: > 0 })
            {
                logger.LogWarning(
                    "worker {Id} failed at {Stage} (exit code {ExitCode}); Blender's last output:\n{Output}",
                    id,
                    stage,
                    exit.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none: killed by the host",
                    string.Join('\n', exit.Output));
            }

            if (failure is OperationCanceledException
                || WorkerSpawnStage.Tag(failure, stage, exit) is WorkerException)
            {
                throw;
            }

            // A malformed reply or a broken accept: still a worker failure, at a typed stage.
            throw new WorkerDeadException($"worker {id} failed at {stage}: {failure.Message}", failure) { Stage = stage, Exit = exit };
        }
    }

    /// <summary>
    /// Waits for the worker to connect, failing at once when it exits first (008 AC-18: a
    /// crashing worker is reported with its exit code and stderr tail, not after the spawn
    /// timeout).
    /// </summary>
    private static async Task<Socket> AcceptOrExitAsync(Socket listener, Process process, string id, CancellationToken ct)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopAccepting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var accept = listener.AcceptAsync(stopAccepting.Token).AsTask();
        var exit = process.WaitForExitAsync(stopWaiting.Token);
        if (await Task.WhenAny(accept, exit).ConfigureAwait(false) == accept || !exit.IsCompletedSuccessfully)
        {
            await stopWaiting.CancelAsync().ConfigureAwait(false);
            return await accept.ConfigureAwait(false);
        }

        await stopAccepting.CancelAsync().ConfigureAwait(false);
        try
        {
            (await accept.ConfigureAwait(false)).Dispose(); // observe the cancelled (or raced) accept
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
        }

        var lines = await PipedStderrAsync(process).ConfigureAwait(false);
        var text = string.Join('\n', lines).Trim();
        var tail = text.Length > 0 ? ": " + (text.Length > 400 ? text[^400..] : text) : "";
        throw new WorkerDeadException($"worker {id} exited with code {process.ExitCode} before connecting{tail}")
        {
            Exit = new WorkerExit(process.ExitCode, lines),
        };
    }

    /// <summary>How long a failed spawn waits for the worker's output events to flush.</summary>
    private static readonly TimeSpan OutputFlush = TimeSpan.FromSeconds(1);

    /// <summary>The most output lines a failed spawn keeps (as <see cref="WorkerHandle.RecentOutput"/> does).</summary>
    private const int MaxOutputLines = 40;

    /// <summary>
    /// The last stderr lines of a worker that never connected: the output is not being read
    /// yet, so the tail is still in the pipe. A grandchild that keeps stderr open must not
    /// stall the report, so this reads for at most <see cref="OutputFlush"/>.
    /// </summary>
    private static async Task<IReadOnlyList<string>> PipedStderrAsync(Process process)
    {
        try
        {
            using var read = new CancellationTokenSource(OutputFlush);
            var stderr = await process.StandardError.ReadToEndAsync(read.Token).ConfigureAwait(false);
            var lines = stderr.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
            return lines is [""] ? [] : lines[Math.Max(0, lines.Length - MaxOutputLines)..];
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// The worker hello is authoritative (plan A4): a version that disagrees with the
    /// resolver's probe, or lies outside the supported line without
    /// <c>blender.allowUnsupported</c>, rejects the worker. A missing or unparseable version
    /// is rejected too (the gate fails closed) unless <c>blender.allowUnsupported</c> is set.
    /// </summary>
    internal void CheckHelloVersion(string? reported, string blenderPath)
    {
        if (BlenderVersion.Parse(reported) is not { } version)
        {
            if (options.AllowUnsupportedBlender && options.ProbedBlenderVersion is null)
            {
                return; // unprobed and allowed; a probed version must be confirmed by the hello
            }

            throw Refused(new ConfigException(
                "blender.version",
                $"the worker hello from {blenderPath} reports no readable Blender version ({reported ?? "none"})",
                SupportedBlender.Remedy), WorkerSpawnStage.Hello);
        }

        if (options.ProbedBlenderVersion is { } probed && probed != version)
        {
            throw Refused(new ConfigException(
                "blender.version",
                $"the worker reports Blender {version}, but probing {blenderPath} found {probed}",
                "restart the daemon after changing Blender"), WorkerSpawnStage.Hello);
        }

        if (!options.AllowUnsupportedBlender && SupportedBlender.Classify(version) == BlenderSupport.Unsupported)
        {
            throw Refused(new ConfigException(
                "blender.version", SupportedBlender.Unsupported(version, blenderPath), SupportedBlender.Remedy), WorkerSpawnStage.Hello);
        }
    }

    /// <summary>
    /// A configuration refusal: the message leads with the check ID and the
    /// <see cref="ConfigException"/> (check, hint) rides inside, so status and doctor (T036,
    /// T051) read the check without parsing the message.
    /// </summary>
    private static WorkerDeadException Refused(ConfigException error, string stage) =>
        new(error.Hint is null ? $"{error.Check}: {error.Message}" : $"{error.Check}: {error.Message} ({error.Hint})", error) { Stage = stage };

    /// <summary>AC-12/AC-13: refuse before spawning, naming the check.</summary>
    private void PrepareRuntimeDir()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(options.RuntimeDir);
            return;
        }

        try
        {
            RuntimePaths.Prepare(options.RuntimeDir, options.Platform, FileSystem);
        }
        catch (ConfigException error)
        {
            throw Refused(error, WorkerSpawnStage.Preflight);
        }
    }
}
