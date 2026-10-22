using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Workers;

namespace Abl.Integration.Tests.Support;

/// <summary>
/// Feature 009 T030 (AC-04, Q3; plan D4 c): a worker the qualification test spawns itself,
/// because <c>--debug-cycles</c> has no product flag (plan Complexity Tracking). The spawn
/// reuses the supervisor's helpers, so it runs under the product environment: the runtime
/// dir is prepared by <see cref="RuntimePaths.Prepare"/> (a real, user-owned 0700 dir), the
/// socket is bound with <see cref="RuntimePaths.SocketMode"/> (0600) before Blender starts,
/// and the child environment is cut by <see cref="ChildEnvironment.AllowsForWorker"/> (the
/// 008 allowlist, <c>PYTHON*</c> stripped). The observed modes and the variable names (never
/// values) are kept for the test to assert. Requests are raw <see cref="WireCodec"/> frames,
/// so a reply is read as the worker sent it.
/// <para>
/// Cycles' own <c>Path tracing on: …</c> lines from stdout and stderr are the device oracle.
/// <see cref="TracedAsync"/> brackets one request with sentinel lines that an <c>exec</c>
/// prints to both streams after flushing every C stdio buffer (<c>fflush(NULL)</c>, so a
/// block-buffered Cycles line on the stdout pipe is pushed out before the sentinel). It waits
/// until both closing sentinels have been read, then takes, per stream, the lines between
/// that stream's opening and closing sentinel. A stream keeps its own order, so the window
/// holds every line the request produced and none from a request before or after it.
/// </para>
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class DebugCyclesWorker : IAsyncDisposable
{
    private const string PathTracing = "Path tracing on:";
    private const string MarkPrefix = "ABL_MARK ";
    private const int TailLines = 200;

    /// <summary>How long a sentinel may take to appear on both streams after its exec replied.</summary>
    private static readonly TimeSpan SentinelSettle = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan SentinelTimeout = TimeSpan.FromSeconds(30);

    // Flush C stdio first (Cycles may log through it), then print the mark on both streams.
    private const string Sentinel = """
        import ctypes, sys
        try:
            ctypes.CDLL(None).fflush(None)
        except Exception:
            pass
        line = "ABL_MARK " + params["mark"]
        for stream in (sys.stdout, sys.stderr):
            print(line, file=stream, flush=True)
        """;

    private readonly Process _process;
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly OutputLog _output;
    private long _nextId;
    private long _windows;

    private DebugCyclesWorker(Process process, Socket socket, OutputLog output)
    {
        _process = process;
        _socket = socket;
        _output = output;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    /// <summary>The mode of the runtime dir when the socket was bound.</summary>
    public UnixFileMode RuntimeDirMode { get; private init; }

    /// <summary>The mode of the bound socket file before Blender started.</summary>
    public UnixFileMode SocketMode { get; private init; }

    /// <summary>The names (only) of the variables the child process was started with.</summary>
    public IReadOnlyList<string> EnvironmentNames { get; private init; } = [];

    /// <summary>The last output lines, for a failure message.</summary>
    public string Tail => _output.Tail;

    public static async Task<DebugCyclesWorker> StartAsync(AblOptions options, CancellationToken ct)
    {
        var blender = options.BlenderPath ?? throw new InvalidOperationException("no Blender is configured (ABL_BLENDER)");
        RuntimePaths.Prepare(options.RuntimeDir, options.Platform, UnixConfigFileSystem.Instance);
        var dirMode = File.GetUnixFileMode(options.RuntimeDir);

        var socketPath = Path.Combine(options.RuntimeDir, RuntimePaths.SocketFileName(Guid.NewGuid().ToString("N")[..8]));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Process? process = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            File.SetUnixFileMode(socketPath, RuntimePaths.SocketMode);
            var socketMode = File.GetUnixFileMode(socketPath);

            var start = new ProcessStartInfo(blender)
            {
                ArgumentList =
                {
                    "--background", "--factory-startup", "--debug-cycles",
                    "--python", Path.Combine(options.WorkerDir, "worker.py"), "--", socketPath,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            ChildEnvironment.Restrict(
                start.Environment, name => ChildEnvironment.AllowsForWorker(name, options.WorkerEnvPassthrough));
            var names = start.Environment.Keys.Order(StringComparer.Ordinal).ToList();

            process = Process.Start(start) ?? throw new InvalidOperationException("Blender did not start");
            var output = new OutputLog();
            process.OutputDataReceived += (_, e) => output.Record(OutputStream.Stdout, e.Data);
            process.ErrorDataReceived += (_, e) => output.Record(OutputStream.Stderr, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var accepted = await AcceptOrExitAsync(listener, process, output, options.SpawnTimeout, ct);
            return new DebugCyclesWorker(process, accepted, output)
            {
                RuntimeDirMode = dirMode,
                SocketMode = socketMode,
                EnvironmentNames = names,
            };
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }

            throw;
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    /// <summary>
    /// Sends one request and returns the reply as sent, <c>ok</c> true or false. A reply id
    /// that does not match is a test failure.
    /// </summary>
    public async Task<JsonObject> RequestAsync(string op, JsonObject? fields, TimeSpan timeout, CancellationToken ct)
    {
        var request = fields ?? [];
        var id = ++_nextId;
        request["id"] = id;
        request["op"] = op;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        await WireCodec.WriteAsync(_stream, request, deadline.Token);
        var reply = await WireCodec.ReadAsync(_stream, deadline.Token);
        if (reply["id"]?.GetValue<long>() != id)
            throw new InvalidDataException($"worker answered {reply["id"]} to request {id}");
        return reply;
    }

    /// <summary>
    /// Runs one request between an opening and a closing sentinel and returns its reply with
    /// every <c>Path tracing on:</c> line that either stream carried between the two. A
    /// sentinel that does not reach both streams fails the harness, never the oracle.
    /// </summary>
    public async Task<(JsonObject Reply, IReadOnlyList<string> PathTracing)> TracedAsync(
        string op, JsonObject? fields, TimeSpan timeout, CancellationToken ct)
    {
        var window = ++_windows;
        var open = await SentinelAsync($"{window} open", ct);
        var reply = await RequestAsync(op, fields, timeout, ct);
        var close = await SentinelAsync($"{window} close", ct);
        return (reply, _output.PathTracingBetween(open, close));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RequestAsync("quit", null, TimeSpan.FromSeconds(10), quit.Token);
            await _process.WaitForExitAsync(quit.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or SocketException)
        {
        }
        finally
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await _stream.DisposeAsync();
            _socket.Dispose();
            _process.Dispose();
        }
    }

    // As the supervisor's AcceptOrExitAsync: an early Blender exit is reported at once, with
    // the output tail, instead of after the whole spawn timeout.
    private static async Task<Socket> AcceptOrExitAsync(
        Socket listener, Process process, OutputLog output, TimeSpan timeout, CancellationToken ct)
    {
        using var stopAccepting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stopAccepting.CancelAfter(timeout);
        var accept = listener.AcceptAsync(stopAccepting.Token).AsTask();
        var exit = process.WaitForExitAsync(stopWaiting.Token);
        if (await Task.WhenAny(accept, exit) == accept || !exit.IsCompletedSuccessfully)
        {
            await stopWaiting.CancelAsync();
            try
            {
                return await accept;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Blender did not connect within {timeout.TotalSeconds:0} s\n{output.Tail}");
            }
        }

        await stopAccepting.CancelAsync();
        try
        {
            await accept;
        }
        catch (OperationCanceledException)
        {
        }

        throw new InvalidOperationException($"Blender exited ({process.ExitCode}) before connecting\n{output.Tail}");
    }

    private async Task<string> SentinelAsync(string label, CancellationToken ct)
    {
        var line = MarkPrefix + label;
        var reply = await RequestAsync("exec", new JsonObject
        {
            ["source"] = Sentinel,
            ["params"] = new JsonObject { ["mark"] = label },
        }, SentinelTimeout, ct);
        if (reply["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"sentinel exec failed: {reply["error"]}\n{Tail}");
        var deadline = DateTime.UtcNow + SentinelSettle;
        while (!_output.SawOnBothStreams(line))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"sentinel '{line}' did not reach both stdout and stderr\n{Tail}");
            await Task.Delay(20, ct);
        }

        return line;
    }

    private enum OutputStream
    {
        Stdout,
        Stderr,
    }

    /// <summary>
    /// The child's stdout and stderr lines, numbered in arrival order; each stream's reader
    /// delivers its lines in the order they were written. Only the oracle lines, the
    /// sentinels and a tail are kept.
    /// </summary>
    private sealed class OutputLog
    {
        private readonly Lock _gate = new();
        private readonly List<(OutputStream Stream, long Seq, string Line)> _pathTracing = [];
        private readonly Dictionary<(OutputStream Stream, string Line), long> _marks = [];
        private readonly Queue<string> _tail = new();
        private long _lines;

        public string Tail
        {
            get
            {
                lock (_gate) return string.Join('\n', _tail);
            }
        }

        public bool SawOnBothStreams(string mark)
        {
            lock (_gate) return _marks.ContainsKey((OutputStream.Stdout, mark)) && _marks.ContainsKey((OutputStream.Stderr, mark));
        }

        public List<string> PathTracingBetween(string open, string close)
        {
            lock (_gate)
            {
                return
                [
                    .. _pathTracing
                        .Where(entry => entry.Seq > _marks[(entry.Stream, open)] && entry.Seq < _marks[(entry.Stream, close)])
                        .Select(entry => entry.Line),
                ];
            }
        }

        public void Record(OutputStream stream, string? line)
        {
            if (line is null) return;
            lock (_gate)
            {
                _lines++;
                if (line.Contains(PathTracing, StringComparison.Ordinal)) _pathTracing.Add((stream, _lines, line.Trim()));
                if (line.StartsWith(MarkPrefix, StringComparison.Ordinal)) _marks.TryAdd((stream, line.TrimEnd()), _lines);
                _tail.Enqueue(line);
                if (_tail.Count > TailLines) _tail.Dequeue();
            }
        }
    }
}
