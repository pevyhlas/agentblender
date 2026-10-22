using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Abl.Core.Workers;

namespace Abl.Integration.Tests;

/// <summary>
/// A worker stub for the unit lane (no Blender ever runs): a fake Blender shell script plus a
/// protocol server in the test process. The script answers <c>--version</c> like Blender
/// 5.2.1; spawned as a worker, it writes <c>env</c> to <c>env.txt</c>, publishes its socket
/// path (its sixth argument, after <c>--background --factory-startup --python worker.py --</c>)
/// as <c>sockets/&lt;pid&gt;.txt</c> and waits, keeping that argv, until it is killed. The
/// server connects to every published socket and speaks the worker protocol, so the stub
/// serves an in-process <see cref="WorkerSupervisor"/> and a daemon in another process alike.
/// A crashing stub exits with code 3 and a stderr line instead of connecting; a silent stub
/// writes <c>env.txt</c> and then waits without ever publishing its socket (a worker that
/// hangs before it connects). A flaky stub crashes like a crashing one on its first spawn
/// only, then behaves normally. A late-crashing stub (<c>crashAfterSeconds</c>) waits that long,
/// then crashes like a crashing one (a worker that fails after a client began waiting). <c>version</c> is what <c>--version</c>
/// reports. A stub that dies at warm (<c>dieAtWarm</c>) writes <see cref="StartMessage"/> to
/// stderr, connects and answers hello; when <c>warm</c> arrives its socket closes unanswered,
/// and it writes <see cref="DeathMessage"/> to stderr and exits with
/// <see cref="DeathExitCode"/> 0.3 s later, like Blender aborting in the eevee prewarm.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class StubWorker : IAsyncDisposable
{
    public const string CrashMessage = "stub: simulated worker crash";
    public const string StartMessage = "stub: Blender started";
    public const string DeathMessage = "stub: eevee prewarm aborted";
    public const int DeathExitCode = 134;

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _dir;
    private readonly string _sockets;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watch;
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly ConcurrentQueue<string> _ops = new();
    private readonly ConcurrentQueue<string> _engines = new();
    private readonly bool _dieAtWarm;

    public StubWorker(
        string dir, bool crash = false, string version = "5.2.1", bool silent = false, bool flaky = false, int? crashAfterSeconds = null,
        bool dieAtWarm = false)
    {
        _dir = dir;
        _sockets = Path.Combine(dir, "sockets");
        Directory.CreateDirectory(_sockets, Private);
        ScriptPath = Path.Combine(dir, "fake-blender");
        var crashOnce = flaky
            ? $"""
                if mkdir '{dir}/crashed-once' 2>/dev/null; then
                  echo '{CrashMessage}' >&2
                  exit 3
                fi

                """
            : "";
        var body = crashAfterSeconds is { } delay
            ? $"""
                sleep {delay}
                echo '{CrashMessage}' >&2
                exit 3
                """
            : crash
            ? $"""
                echo '{CrashMessage}' >&2
                exit 3
                """
            : dieAtWarm
            ? $"""
                echo '{StartMessage}' >&2
                printf '%s\n' "$6" > "{_sockets}/$$.tmp" && mv "{_sockets}/$$.tmp" "{_sockets}/$$.txt"
                while [ ! -e '{dir}/die-'$$ ]; do sleep 0.05; done
                echo '{DeathMessage}' >&2
                sleep 0.3
                exit {DeathExitCode}
                """
            : silent
            ? $"""
                env > '{dir}/env.txt'
                sleep 300 &
                wait $!
                """
            : crashOnce + $"""
                env > '{dir}/env.txt'
                printf '%s\n' "$6" > "{_sockets}/$$.tmp" && mv "{_sockets}/$$.tmp" "{_sockets}/$$.txt"
                sleep 300 &
                wait $!
                """;
        File.WriteAllText(ScriptPath, $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              printf 'Blender {version}\n\tbuild hash: 0123456789ab\n'
              exit 0
            fi
            {body}

            """);
        File.SetUnixFileMode(ScriptPath, Private);
        _dieAtWarm = dieAtWarm;
        _watch = Task.Run(() => WatchAsync(_stop.Token));
    }

    public string ScriptPath { get; }

    /// <summary>When set, <c>warm</c> answers <c>ok: false</c> like a failed prewarm render (doctor's warm stage).</summary>
    public bool FailWarm { get; set; }

    /// <summary>When set, <c>hello</c> is never answered (a worker that hangs after it connects).</summary>
    public bool HangHello { get; set; }

    /// <summary>The hello's <c>gpu</c> object; set before the worker is spawned.</summary>
    public JsonObject Gpu { get; set; } = new() { ["backend"] = "none", ["devices"] = new JsonArray(), ["metalrt"] = null };

    public IReadOnlyList<string> Ops => [.. _ops];

    public IReadOnlyList<string> RenderedEngines => [.. _engines];

    /// <summary>How many workers have connected.</summary>
    public int Connections => _connections.Count;

    /// <summary>True once a worker has written <c>env.txt</c>.</summary>
    public bool HasEnvironment => File.Exists(Path.Combine(_dir, "env.txt"));

    public IReadOnlyList<string> EnvironmentNames() =>
        [.. File.ReadAllLines(Path.Combine(_dir, "env.txt"))
            .Where(line => line.Contains('='))
            .Select(line => line[..line.IndexOf('=')])];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var task in _connections.Append(_watch))
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        var served = new HashSet<string>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            foreach (var published in Directory.EnumerateFiles(_sockets, "*.txt"))
            {
                if (served.Add(published))
                {
                    var socketPath = (await File.ReadAllTextAsync(published, ct)).Trim();
                    var pid = Path.GetFileNameWithoutExtension(published);
                    _connections.Add(Task.Run(() => ServeAsync(socketPath, pid, ct), CancellationToken.None));
                }
            }

            await Task.Delay(20, ct);
        }
    }

    private async Task ServeAsync(string socketPath, string pid, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        }
        catch (SocketException)
        {
            return; // the host gave up on this worker before it connected
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        while (!ct.IsCancellationRequested)
        {
            JsonObject request;
            try
            {
                request = await WireCodec.ReadAsync(stream, ct);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException)
            {
                return; // the host closed the connection
            }

            var op = request["op"]!.GetValue<string>();
            _ops.Enqueue(op);
            if (op == "hello" && HangHello)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (op == "warm" && _dieAtWarm)
            {
                // Like a native crash, unanswered: the socket closes a moment before the process
                // has exited, so the host must wait for the exit code rather than kill at once.
                File.Create(Path.Combine(_dir, "die-" + pid)).Dispose();
                return;
            }
            var reply = Answer(op, request);
            reply["id"] = request["id"]!.GetValue<long>();
            try
            {
                await WireCodec.WriteAsync(stream, reply, ct);
            }
            catch (IOException)
            {
                return;
            }

            if (op == "quit")
            {
                return;
            }
        }
    }

    private JsonObject Answer(string op, JsonObject request)
    {
        switch (op)
        {
            case "hello":
                return new JsonObject
                {
                    ["ok"] = true,
                    ["blender_version"] = "5.2.1",
                    ["python_version"] = "3.13.0",
                    ["gpu"] = Gpu.DeepClone(),
                    ["capabilities"] = new JsonObject { ["blob_chunks"] = true },
                };
            case "warm" when FailWarm:
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = new JsonObject
                    {
                        ["kind"] = "render_failed",
                        ["message"] = "stub: prewarm render failed",
                        ["traceback"] = "Traceback (most recent call last):\n  File \"worker.py\", in op_warm\nRuntimeError: stub: prewarm render failed",
                    },
                };
            case "warm":
                return new JsonObject { ["ok"] = true, ["warmed_ms"] = 1.0 };
            case "save":
                byte[] blend = [0x42, 0x4c, 0x45, 0x4e, 0x44];
                File.WriteAllBytes(request["path"]!.GetValue<string>(), blend);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["bytes"] = blend.Length,
                    ["digest"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(blend)),
                    ["blender_version"] = "5.2.1",
                };
            case "render":
                var engine = request["engine"]!.GetValue<string>();
                _engines.Enqueue(engine);
                var outPath = request["out"]!.GetValue<string>();
                File.WriteAllBytes(outPath, [0x89, 0x50, 0x4e, 0x47]);
                var rendered = new JsonObject { ["ok"] = true, ["duration_ms"] = 1.0, ["out"] = outPath };
                if (engine == "cycles_cpu")
                {
                    rendered["device"] = new JsonObject { ["backend"] = "cpu", ["name"] = null };
                }

                if (engine == "cycles_gpu")
                {
                    rendered["device"] = new JsonObject { ["backend"] = Gpu["backend"]!.DeepClone(), ["name"] = Gpu["devices"]![0]!["name"]!.DeepClone() };
                }

                return rendered;
            case "load":
            case "quit":
                return new JsonObject { ["ok"] = true };
            default:
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = new JsonObject { ["kind"] = "bad_request", ["message"] = $"stub: no op {op}" },
                };
        }
    }
}
