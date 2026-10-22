using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Abl.Core.Rendering;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Abl.Cli.Host;

/// <summary>What one in-process worker round-trip found (doctor <c>worker.roundtrip</c>, AC-25).</summary>
public sealed record RoundtripResult(
    string? BlenderVersion, string? BuildHash, string? WorkerSourceSha256, GpuCapability Gpu, long ElapsedMs);

/// <summary>What doctor runs against: the configuration inputs and the host seams (fakes in unit tests).</summary>
/// <param name="Inputs">The configuration inputs, with a Blender search.</param>
/// <param name="WorkingDirectory">Where <c>mcp.clients</c> looks for <c>.mcp.json</c> and <c>opencode.json</c>.</param>
/// <param name="Services">The user service manager, or null when the host has none.</param>
/// <param name="Roundtrip">Spawns, hellos, warms and disposes one worker.</param>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed record DoctorHost(
    ConfigInputs Inputs,
    string WorkingDirectory,
    IServiceManager? Services,
    Func<AblOptions, CancellationToken, Task<RoundtripResult>> Roundtrip)
{
    /// <summary>Runs <c>loginctl</c> for the Linux linger check (N4).</summary>
    public CommandRunner Run { get; init; } = ServiceManagers.Run;

    /// <summary>This <c>abl</c> as a unit's command starts it (program and prefix), for <c>service.state</c>.</summary>
    public IReadOnlyList<string>? ThisAbl { get; init; }

    /// <summary>
    /// Where this <c>abl</c>'s package content lives: its packaged <c>worker/</c> and the
    /// <see cref="WorkerManifest"/> that <c>worker.files</c> checks it against (008 FR-014).
    /// </summary>
    public string PackageDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Free bytes on the volume of a path (<see cref="HostChecks.FreeBytes"/>).</summary>
    public Func<string, long?> FreeBytes { get; init; } = HostChecks.FreeBytes;

    /// <summary>Apple silicon: the <c>gpu.backend</c> remedy names the agent-harness sandbox (009 plan Doctor table).</summary>
    public bool AppleSilicon { get; init; } = OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>
    /// Spawns the disposable worker the opt-in <c>gpu.render</c> check renders on (009 T021):
    /// through <see cref="WorkerSupervisor"/>, so the 008 env allowlist and the per-spawn
    /// runtime-dir check apply, without the eevee prewarm. A fake in unit tests.
    /// </summary>
    public Func<AblOptions, CancellationToken, Task<WorkerHandle>> RenderWorker { get; init; } = Doctor.SpawnRenderWorkerAsync;

    /// <summary>This process: its environment, cwd, service manager and a real worker round-trip.</summary>
    public static DoctorHost ForProcess()
    {
        var inputs = ConfigInputs.FromProcess().WithBlenderSearch();
        var (program, prefix) = SelfCommand.Current();
        return new DoctorHost(inputs, Environment.CurrentDirectory, ServiceManagers.ForService(inputs.Home, inputs.Env), Doctor.RoundtripAsync)
        {
            ThisAbl = [DotnetRoot.Stable(program, Directory.Exists), .. prefix],
        };
    }
}

/// <summary>The doctor report: every check run, in catalogue order.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
    /// <summary>0 when no check failed, else 1 (FR-009).</summary>
    public int ExitCode => Checks.Any(c => c.Status == CheckStatus.Fail) ? 1 : 0;

    /// <summary>The <c>doctor.v1</c> envelope (plan B4; provisional until AGD S03 is reviewed).</summary>
    public JsonObject ToJson() => new()
    {
        ["tool"] = "abl",
        ["version"] = AblVersion.Current,
        ["schema"] = DoctorCatalogue.Schema,
        ["summary"] = new JsonObject
        {
            ["pass"] = Count(CheckStatus.Pass),
            ["warn"] = Count(CheckStatus.Warn),
            ["fail"] = Count(CheckStatus.Fail),
            ["skip"] = Count(CheckStatus.Skip),
        },
        ["checks"] = new JsonArray([.. Checks.Select(c => (JsonNode)c.ToJson())]),
    };

    /// <summary>One line per check, remedies indented under warn and fail.</summary>
    public IEnumerable<string> TextLines()
    {
        var width = Checks.Count == 0 ? 0 : Checks.Max(c => c.Id.Length) + 2;
        foreach (var check in Checks)
        {
            yield return DaemonLog.Clean($"{DoctorCheck.Name(check.Status),-4}  {check.Id.PadRight(width)}{check.Summary}");
            if (check.Remedy is not null && check.Status is CheckStatus.Warn or CheckStatus.Fail)
            {
                yield return DaemonLog.Clean($"{"",6}{"".PadRight(width)}remedy: {check.Remedy}");
            }
        }

        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"{Count(CheckStatus.Pass)} pass, {Count(CheckStatus.Warn)} warn, {Count(CheckStatus.Fail)} fail, {Count(CheckStatus.Skip)} skip");
    }

    private int Count(CheckStatus status) => Checks.Count(c => c.Status == status);
}

/// <summary>
/// <c>abl doctor [--json] [--quick] [--check &lt;id&gt;]</c> (008 FR-009, plan B4): the lean v1
/// catalogue (<see cref="DoctorCatalogue"/>). Read-only except the opt-in <c>gpu.render</c>,
/// which primes the per-user GPU kernel cache (009 plan, amends B4): remedies are exact
/// commands, and setup and start do the writing. The other exceptions are bounded and leave
/// nothing behind: the <c>worker.roundtrip</c> spawn (which creates a missing runtime dir 0700
/// like any spawn), the socket bind/unlink probe and a loopback port bind; <c>gpu.render</c>
/// also deletes its two PNGs and its private directory, and writes nothing to the store or the
/// renders dir. It reads the daemon through
/// <c>daemon.json</c> and <c>/healthz</c> and never probes the store lock (L-d). Exit codes:
/// 0, 1 when any check fails, 2 usage.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class Doctor(DoctorHost host)
{
    /// <summary>Bounds on the Blender probes under <c>--quick</c>.</summary>
    private static readonly TimeSpan QuickProbeTimeout = TimeSpan.FromSeconds(5);

    private const int MaxClientFileBytes = 1024 * 1024;

    /// <summary>The command: parses its flags and prints the report.</summary>
    public static async Task<int> MainAsync(IReadOnlyList<string> rest, DoctorHost host, TextWriter stdout, TextWriter stderr)
    {
        bool json = false, quick = false;
        string? only = null;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--quick":
                    quick = true;
                    break;
                case "--check" when i + 1 < rest.Count:
                    only = rest[++i];
                    break;
                default:
                    stderr.WriteLine($"abl: doctor takes only --json, --quick and --check <id> (got '{rest[i]}')");
                    return 2;
            }
        }

        if (only is not null)
        {
            if (DoctorCatalogue.Find(only) is not { } entry)
            {
                stderr.WriteLine($"abl: no doctor check '{only}' (checks: {string.Join(", ", DoctorCatalogue.Entries.Where(e => !e.LifecycleOnly).Select(e => e.Id))})");
                return 2;
            }

            if (entry.LifecycleOnly)
            {
                stderr.WriteLine($"abl: '{only}' is reported by the lifecycle verbs and configuration resolution, not run by doctor");
                return 2;
            }
        }

        var report = await new Doctor(host).RunAsync(quick, only, CancellationToken.None);
        if (json)
        {
            stdout.WriteLine(report.ToJson().ToJsonString());
        }
        else
        {
            foreach (var line in report.TextLines())
            {
                stdout.WriteLine(line);
            }
        }

        return report.ExitCode;
    }

    /// <summary>Runs the catalogue, or <paramref name="only"/> with its prerequisites.</summary>
    public async Task<DoctorReport> RunAsync(bool quick, string? only, CancellationToken ct)
    {
        var selected = only is null ? null : DoctorCatalogue.Closure(only);
        var results = new List<DoctorCheck>();
        bool Wanted(string id) => selected?.Contains(id) ?? DoctorCatalogue.Find(id) is { LifecycleOnly: false };

        var inputs = host.Inputs;
        if (quick && inputs.Blender is { } search)
        {
            inputs = inputs with { Blender = search with { ProbeTimeout = QuickProbeTimeout, SpotlightTimeout = TimeSpan.FromSeconds(1) } };
        }

        AblConfig config;
        try
        {
            config = AblConfig.Resolve(inputs);
        }
        catch (ConfigException error)
        {
            var failing = DoctorCatalogue.Find(error.Check) is not null ? error.Check : "config.file";
            if (failing != "config.file")
            {
                results.Add(DoctorCheck.Pass("config.file", FileSummary(inputs, loaded: null)));
            }

            results.Add(DoctorCheck.Fail(failing, error.Message, ConfigRemedy(failing, error), error.Hint is null ? null : new JsonObject { ["why"] = error.Hint }));
            foreach (var entry in DoctorCatalogue.Entries.Where(e => !e.LifecycleOnly && e.Id != "config.file" && Wanted(e.Id)))
            {
                results.Add(entry.OptIn && only != entry.Id ? OptInSkip(entry.Id) : DoctorCheck.Skip(entry.Id, "requires " + failing));
            }

            return new DoctorReport(results);
        }

        var context = new Context(config, quick, only);
        foreach (var entry in DoctorCatalogue.Entries)
        {
            if (!Wanted(entry.Id) || entry.LifecycleOnly)
            {
                continue;
            }

            // 009 FR-006: an opt-in check runs only when --check names it, so a full run never
            // spawns its worker (AC-12).
            if (entry.OptIn && only != entry.Id)
            {
                results.Add(OptInSkip(entry.Id));
                continue;
            }

            if (entry.Requires.Select(r => results.FirstOrDefault(c => c.Id == r)).FirstOrDefault(c => c is { Status: CheckStatus.Fail or CheckStatus.Skip }) is { } blocker)
            {
                results.Add(DoctorCheck.Skip(entry.Id, "requires " + blocker.Id));
                continue;
            }

            DoctorCheck check;
            try
            {
                check = await RunCheckAsync(entry.Id, inputs, config, context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // FR-009: one check that throws is that check's fail, never a crashed report.
                check = Unexpected(entry.Id, ex);
            }

            results.Add(check);
        }

        return new DoctorReport(results);
    }

    /// <summary>One check, by its catalogue ID.</summary>
    private async Task<DoctorCheck> RunCheckAsync(string id, ConfigInputs inputs, AblConfig config, Context context, CancellationToken ct)
    {
        var options = config.Options;
        var fs = inputs.FileSystem;
        return id switch
        {
            "config.file" => DoctorCheck.Pass("config.file", FileSummary(inputs, config), new JsonObject
            {
                ["path"] = config.FilePath,
                ["loaded"] = config.FileLoaded,
                ["effectiveDigest"] = config.EffectiveDigest,
            }),
            "blender.found" => config.Blender is { } found ? HostChecks.Blender(found).Found : DoctorCheck.Skip("blender.found", "no Blender search"),
            "blender.version" => HostChecks.Blender(config.Blender!).Version!,
            "worker.files" => WorkerFiles(options.WorkerDir, host.PackageDirectory, context),
            "runtime.dir" => HostChecks.RuntimeDir(options.RuntimeDir, fs),
            "runtime.socket-path" => HostChecks.SocketPath(options.RuntimeDir, options.Platform, fs, probe: true),
            "paths.store" => HostChecks.DataDir("paths.store", options.StoreDir, fs, host.FreeBytes),
            "paths.renders" => HostChecks.DataDir("paths.renders", options.RendersDir, fs, host.FreeBytes),
            "worker.roundtrip" => await WorkerRoundtripAsync(options, context, ct),
            "gpu.backend" => GpuBackend(context),
            "gpu.render" => await GpuRenderAsync(options, context, ct),
            "daemon.port" => await DaemonPortAsync(options, context),
            "daemon.reachable" => await DaemonReachableAsync(options, context),
            "daemon.config-drift" => await ConfigDriftAsync(options, context),
            "service.state" => ServiceStateCheck(inputs),
            "desktop.config" => await DesktopConfigAsync(config, ct),
            "mcp.clients" => McpClients(options.Port),
            _ => throw new InvalidOperationException($"doctor has no check {id}"),
        };
    }

    /// <summary>The real round-trip: spawn through the supervisor (env allowlist, runtime-dir check), hello, 64 px eevee warm, dispose.</summary>
    public static async Task<RoundtripResult> RoundtripAsync(AblOptions options, CancellationToken ct)
    {
        var supervisor = new WorkerSupervisor(options, NullLogger<WorkerSupervisor>.Instance);
        var clock = Stopwatch.StartNew();
        await using var worker = await supervisor.SpawnAsync(prewarm: true, ct);
        var elapsed = clock.ElapsedMilliseconds;
        return new RoundtripResult(worker.BlenderVersion, worker.BlenderBuildHash, worker.WorkerSourceSha256, worker.Gpu, elapsed);
    }

    /// <summary>
    /// The keys whose values differ between the CLI's and the daemon's effective configuration
    /// (AC-28, plan M6): values compare as canonical JSON, so a whitespace-only file edit is no
    /// difference; a secret compares by being set and by its file, never by value.
    /// </summary>
    public static IReadOnlyList<(string Key, JsonObject? Cli, JsonObject? Daemon)> Drift(JsonObject cli, JsonObject daemon)
    {
        var differences = new List<(string, JsonObject?, JsonObject?)>();
        foreach (var key in cli.Select(p => p.Key).Union(daemon.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var mine = cli[key] as JsonObject;
            var theirs = daemon[key] as JsonObject;
            if (!JsonNode.DeepEquals(mine?["value"], theirs?["value"]) || !JsonNode.DeepEquals(mine?["file"], theirs?["file"]))
            {
                differences.Add((key, mine, theirs));
            }
        }

        return differences;
    }

    // ---- checks ------------------------------------------------------------------------

    private static string FileSummary(ConfigInputs inputs, AblConfig? loaded)
    {
        var (path, _) = AblConfig.LocateFile(inputs);
        return loaded is { FileLoaded: true } || (loaded is null && inputs.FileSystem.Inspect(path) is not null)
            ? $"{path} is valid and trusted"
            : $"no config file at {path}: discovery and defaults apply (`abl setup` writes one)";
    }

    private static string ConfigRemedy(string check, ConfigException error) => check == "config.value"
        ? "fix or unset the variable or flag the message names"
        : error.Message.Contains("run: ", StringComparison.Ordinal)
            ? "run the command in the message, or point ABL_CONFIG at a file you own"
            : "fix the key the message names, or rewrite the file with `abl setup --reset`";

    private static DoctorCheck WorkerFiles(string workerDir, string packageDirectory, Context context)
    {
        var detail = new JsonObject { ["dir"] = workerDir, ["manifest"] = null };
        const string remedy = "reinstall abl (its package ships worker/), or set paths.workerDir (ABL_WORKER_DIR) to a complete worker/ directory";
        if (WorkerSource.Missing(workerDir) is { Count: > 0 } missing)
        {
            detail["missing"] = new JsonArray([.. missing.Select(m => (JsonNode)m)]);
            return DoctorCheck.Fail("worker.files", $"{workerDir} lacks {string.Join(", ", missing)}", remedy, detail);
        }

        WorkerManifest? manifest;
        IReadOnlyList<string> changed;
        try
        {
            context.WorkerDigest = WorkerSource.Digest(workerDir);
            manifest = WorkerManifest.Read(packageDirectory);
            changed = manifest?.Changed(workerDir) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // InvalidDataException: a malformed manifest (it is a SystemException, not an IOException).
            return DoctorCheck.Fail("worker.files", ex.Message, remedy, detail);
        }

        detail["digest"] = context.WorkerDigest;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{WorkerSource.Modules.Count} modules in {workerDir} (digest {context.WorkerDigest[..12]})");
        var manifestPath = WorkerManifest.PathIn(packageDirectory);
        if (manifest is null)
        {
            // 008 FR-014: every build writes the manifest next to abl, so a missing one is a broken install.
            return DoctorCheck.Warn(
                "worker.files",
                $"{summary}, but there is no package manifest {manifestPath} to prove it is the packaged worker/",
                "reinstall abl (its package ships the manifest)",
                detail);
        }

        var packaged = manifest.Digest();
        detail["manifest"] = packaged;
        detail["changed"] = new JsonArray([.. changed.Select(m => (JsonNode)m)]);
        var isPackaged = SamePath(workerDir, Path.Combine(packageDirectory, "worker"));
        detail["packaged"] = isPackaged;
        if (changed.Count == 0)
        {
            return DoctorCheck.Pass(
                "worker.files",
                isPackaged ? summary + ", equal to the package manifest" : summary + ", equal to the package manifest (paths.workerDir overrides the packaged worker/)",
                detail);
        }

        var differs = $"{summary}: {string.Join(", ", changed)} differ from the package manifest {manifestPath}";
        return isPackaged
            ? DoctorCheck.Fail("worker.files", differs, "reinstall abl: its packaged worker/ was changed after install", detail)
            : DoctorCheck.Warn(
                "worker.files",
                differs + " (paths.workerDir overrides the packaged worker/)",
                "unset paths.workerDir (ABL_WORKER_DIR) to run the packaged worker/, or keep the override knowingly",
                detail);

        static bool SamePath(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.Ordinal);
    }

    private async Task<DoctorCheck> WorkerRoundtripAsync(AblOptions options, Context context, CancellationToken ct)
    {
        if (context.Quick && context.Only is null)
        {
            return DoctorCheck.Skip("worker.roundtrip", "--quick");
        }

        try
        {
            var result = await host.Roundtrip(options, ct);
            context.Roundtrip = result;
            var detail = new JsonObject
            {
                ["blenderVersion"] = result.BlenderVersion,
                ["buildHash"] = result.BuildHash,
                ["workerSourceSha256"] = result.WorkerSourceSha256,
                ["gpu"] = new JsonObject { ["backend"] = result.Gpu.Backend },
                ["elapsedMs"] = result.ElapsedMs,
            };
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Blender {result.BlenderVersion ?? "?"} ({Short(result.BuildHash)}) answered hello and a 64 px eevee warm in {result.ElapsedMs} ms, gpu {result.Gpu.Backend}");
            if (context.WorkerDigest is { } onDisk && result.WorkerSourceSha256 is { } reported && reported != onDisk)
            {
                return DoctorCheck.Warn(
                    "worker.roundtrip",
                    $"{summary}, but it reports worker digest {Short(reported)}, not worker/ on disk {Short(onDisk)}",
                    "restart the daemon after updating worker/; reinstall abl if it persists",
                    detail);
            }

            return DoctorCheck.Pass("worker.roundtrip", summary, detail);
        }
        catch (Exception ex) when (ex is WorkerDeadException or WorkerOpException or WorkerTimeoutException or AblException
            or IOException or SocketException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            // The stage is the supervisor's typed fact, never parsed from the message (plan B4/B5);
            // an exception it did not tag happened before any connection.
            var config = ex.InnerException as ConfigException;
            var stage = WorkerSpawnStage.Of(ex) ?? WorkerSpawnStage.Spawn;
            var detail = new JsonObject { ["stage"] = stage, ["check"] = config?.Check };

            // Plan B4: how Blender ended, the supervisor's typed fact (exit code, output tail).
            var exit = WorkerSpawnStage.ExitOf(ex);
            var output = exit is { Output.Count: > 0 } ? Tail(string.Join('\n', exit.Output), MaxOutputChars) : "";
            if (exit is not null)
            {
                detail["exitCode"] = exit.ExitCode;
                detail["blenderOutput"] = output.Length > 0 ? output : null;
            }

            const string again = "then run `abl doctor --check worker.roundtrip` again";
            const string byHand = "start Blender by hand with --background --factory-startup to see why";
            var seeOutput = output.Length > 0 ? "Blender's last output (detail.blenderOutput in --json)" : null;
            var remedy = config?.Hint ?? (seeOutput is null
                ? "Blender printed nothing: " + byHand + ", " + again
                : "fix the cause " + seeOutput + " names, " + again);
            switch (ex)
            {
                case WorkerOpException op:
                    detail["kind"] = Bounded(op.Kind, 100);
                    detail["traceback"] = op.WorkerTraceback is { } traceback ? Tail(traceback, MaxTracebackChars) : null;
                    remedy = $"fix the cause the worker's {stage} error names (detail.traceback in --json), " + again;
                    break;
                case WorkerTimeoutException timeout:
                    detail["op"] = timeout.Op;
                    remedy = "a busy host or a first GPU kernel compile can take this long: " + again
                        + "; if it repeats, " + (seeOutput is null ? byHand : seeOutput + " shows where it stopped");
                    break;
            }

            var summary = ex is WorkerOpException failed ? $"{stage} failed ({failed.Kind}): {failed.Message}" : ex.Message;
            // A failure before the connect says what it needs in its message (an exit before
            // connecting carries its own code and tail); after it, the exit is only typed.
            if (exit is not null && stage is WorkerSpawnStage.Hello or WorkerSpawnStage.Warm)
            {
                summary += exit.ExitCode is { } code
                    ? string.Create(CultureInfo.InvariantCulture, $"; Blender exited with code {code}")
                    : "";
                summary += exit.Output.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) is { } last
                    ? $"; Blender's last output: {Bounded(last.Trim(), 200)}"
                    : "";
            }

            return DoctorCheck.Fail("worker.roundtrip", Bounded(summary, 500), remedy, detail);
        }
    }

    /// <summary>The longest worker traceback tail <c>worker.roundtrip</c> reports.</summary>
    private const int MaxTracebackChars = 2000;

    /// <summary>The longest Blender output tail <c>worker.roundtrip</c> reports (<c>detail.blenderOutput</c>).</summary>
    private const int MaxOutputChars = 2000;

    /// <summary>The longest string <c>mcp.clients</c> relays from a repository's client file.</summary>
    private const int MaxRelayedChars = 200;

    /// <summary>A check that threw something doctor did not anticipate: that check fails, the report goes on (FR-009).</summary>
    private static DoctorCheck Unexpected(string id, Exception ex) => DoctorCheck.Fail(
        id,
        Bounded($"the check failed unexpectedly ({ex.GetType().Name}): {ex.Message}", 500),
        $"run `abl doctor --check {id}` again; if it repeats, report it with this output",
        new JsonObject { ["exception"] = ex.GetType().FullName });

    /// <summary>One terminal-safe line of at most <paramref name="max"/> characters.</summary>
    private static string Bounded(string text, int max)
    {
        var clean = DaemonLog.Clean(text.ReplaceLineEndings(" "));
        return clean.Length > max ? clean[..max] : clean;
    }

    /// <summary>The last <paramref name="max"/> characters, each line terminal-safe.</summary>
    private static string Tail(string text, int max)
    {
        var tail = text.Length > max ? text[^max..] : text;
        return string.Join('\n', tail.ReplaceLineEndings("\n").Split('\n').Select(DaemonLog.Clean)).Trim();
    }

    private DoctorCheck GpuBackend(Context context)
    {
        if (context.Roundtrip is not { } roundtrip)
        {
            return DoctorCheck.Skip("gpu.backend", "requires worker.roundtrip");
        }

        var gpu = roundtrip.Gpu;
        var detail = new JsonObject
        {
            ["backend"] = gpu.Backend,
            ["devices"] = new JsonArray([.. gpu.Devices.Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["type"] = d.Type })]),
            ["metalrt"] = gpu.MetalRt,
        };
        if (gpu.Backend is "optix" or "metal")
        {
            var device = gpu.Devices.FirstOrDefault()?.Name;
            return DoctorCheck.Pass("gpu.backend", device is null ? gpu.Backend : $"{gpu.Backend} ({device})", detail);
        }

        return DoctorCheck.Warn("gpu.backend", "the worker reports no GPU backend, so cycles_gpu is unavailable", NoGpuRemedy, detail);
    }

    /// <summary>
    /// The remedy when no GPU backend is usable (009 plan Doctor table): on Apple silicon the
    /// likely cause, the agent-harness sandbox, first. Never <c>install-service</c>: starting
    /// the daemon elsewhere is an owner choice (008 M11).
    /// </summary>
    private string NoGpuRemedy => host.AppleSilicon
        ? "Metal is hidden from this process, usually by an agent-harness sandbox; the owner can start the daemon outside the sandbox (README); meanwhile use engine=cycles_cpu or engine=eevee"
        : "use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback";

    /// <summary>The <c>gpu.render</c> image: 32 px, 1 sample (009 plan Doctor table).</summary>
    internal const int GpuRenderRes = 32;

    /// <summary>A primed (second) render slower than this is the anomaly <c>gpu.render</c> warns about.</summary>
    internal const long WarmRenderLimitMs = 10_000;

    /// <summary>A first render slower than this most likely compiled the GPU kernels (<c>firstCompileSuspected</c>).</summary>
    internal const long CompileSuspectedMs = 30_000;

    /// <summary>An opt-in check that was not named (009 FR-006).</summary>
    private static DoctorCheck OptInSkip(string id) =>
        DoctorCheck.Skip(id, DoctorCatalogue.OptInReason) with { Summary = $"skipped: opt-in, run `abl doctor --check {id}`" };

    /// <summary>The real <see cref="DoctorHost.RenderWorker"/>: a supervisor spawn without the eevee prewarm.</summary>
    public static Task<WorkerHandle> SpawnRenderWorkerAsync(AblOptions options, CancellationToken ct) =>
        new WorkerSupervisor(options, NullLogger<WorkerSupervisor>.Instance).SpawnAsync(prewarm: false, ct);

    /// <summary>
    /// The opt-in <c>gpu.render</c> check (009 T021, plan D8 and Doctor table): a disposable
    /// worker renders a 32 px, 1-sample <c>cycles_gpu</c> image twice into a private 0700
    /// directory inside the checked runtime dir. The first render primes the per-user kernel
    /// cache (on Metal it compiles kernels); the second shows whether the cache works. Both
    /// PNGs and the directory are deleted when the worker is disposed; nothing reaches the store
    /// or the renders dir. It runs outside the daemon's <c>GpuLease</c> (plan Complexity
    /// Tracking). Times are the worker's own <c>duration_ms</c> for each render op.
    /// </summary>
    private async Task<DoctorCheck> GpuRenderAsync(AblOptions options, Context context, CancellationToken ct)
    {
        if (context.Roundtrip?.Gpu is not { } reported || !GpuGuard.HasBackend(reported))
        {
            // The host guard would refuse this render before sending it: nothing is spawned.
            return DoctorCheck.Fail(
                "gpu.render",
                "gpu_unavailable: the worker reports no GPU backend, so no cycles_gpu render was attempted",
                NoGpuRemedy,
                new JsonObject { ["kind"] = WorkerErrors.WorkerGpuUnavailable });
        }

        WorkerHandle? worker = null;
        string? dir = null;
        try
        {
            worker = await host.RenderWorker(options, ct);
            dir = Path.Combine(options.RuntimeDir, "d-gpu-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir, RuntimePaths.DirMode);
            var (firstMs, device) = await RenderOnceAsync(worker, Path.Combine(dir, "first.png"), options, ct);
            var (secondMs, _) = await RenderOnceAsync(worker, Path.Combine(dir, "second.png"), options, ct);

            var compiled = firstMs > CompileSuspectedMs;
            var detail = new JsonObject
            {
                ["device"] = device?.DeepClone(),
                ["firstMs"] = firstMs,
                ["secondMs"] = secondMs,
                ["firstCompileSuspected"] = compiled,
                ["res"] = GpuRenderRes,
                ["samples"] = 1,
            };
            var on = device is null
                ? "cycles_gpu"
                : "cycles_gpu on " + RenderDevice.Describe(Text(device["backend"]) ?? "?", Text(device["name"]), null);
            var times = string.Create(CultureInfo.InvariantCulture, $"{on}: first render {firstMs} ms, second {secondMs} ms");
            if (secondMs > WarmRenderLimitMs)
            {
                return DoctorCheck.Warn(
                    "gpu.render",
                    Bounded(times + "; the second render should reuse the kernel cache and finish within 10 s", 500),
                    "check for other GPU load, then run `abl doctor --check gpu.render` again; if the second render stays slow, report it with this output",
                    detail);
            }

            return DoctorCheck.Pass(
                "gpu.render",
                Bounded(times + (compiled ? " (the first compiled the GPU kernels; the cache is now primed)" : ""), 500),
                detail);
        }
        catch (WorkerOpException op) when (WorkerErrors.OpKind(op.Kind) == WorkerErrors.WorkerGpuUnavailable)
        {
            return DoctorCheck.Fail(
                "gpu.render",
                Bounded($"gpu_unavailable: the worker's device check failed: {op.Message}", 500),
                NoGpuRemedy,
                new JsonObject { ["kind"] = WorkerErrors.WorkerGpuUnavailable });
        }
        catch (Exception ex) when (ex is WorkerDeadException or WorkerOpException or WorkerTimeoutException or AblException
            or IOException or SocketException or UnauthorizedAccessException or TimeoutException)
        {
            var detail = new JsonObject { ["stage"] = WorkerSpawnStage.Of(ex) ?? (worker is null ? WorkerSpawnStage.Spawn : "render") };
            if (ex is WorkerOpException failed)
            {
                detail["kind"] = Bounded(failed.Kind, 100);
                detail["traceback"] = failed.WorkerTraceback is { } traceback ? Tail(traceback, MaxTracebackChars) : null;
            }

            return DoctorCheck.Fail(
                "gpu.render",
                Bounded(ex is WorkerOpException op ? $"render failed ({op.Kind}): {op.Message}" : ex.Message, 500),
                "fix the cause the message names (detail.traceback in --json), then run `abl doctor --check gpu.render` again",
                detail);
        }
        finally
        {
            if (worker is not null)
            {
                await worker.ShutdownAsync();
                await worker.DisposeAsync();
            }

            if (dir is not null)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the dir sits inside the user's own 0700 runtime dir.
                }
            }
        }
    }

    /// <summary>One <c>gpu.render</c> render: the worker's <c>duration_ms</c> (rounded) and the reported <c>device</c>.</summary>
    private static async Task<(long Ms, JsonObject? Device)> RenderOnceAsync(WorkerHandle worker, string outPath, AblOptions options, CancellationToken ct)
    {
        var reply = await worker.RequestAsync("render", new JsonObject
        {
            ["engine"] = "cycles_gpu",
            ["res"] = GpuRenderRes,
            ["samples"] = 1,
            ["seed"] = 0,
            ["out"] = outPath,
        }, options.RenderTimeout, ct);
        var ms = (long)Math.Round(reply["duration_ms"]?.GetValue<double>() ?? 0, MidpointRounding.AwayFromZero);
        return (ms, reply["device"] as JsonObject);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private async Task<DoctorCheck> DaemonPortAsync(AblOptions options, Context context)
    {
        var facts = await context.DaemonAsync(options);
        var port = options.Port;
        if (facts.Running && facts.State!.Port == port)
        {
            return DoctorCheck.Pass("daemon.port", $"port {port} is held by this store's daemon (pid {facts.State.Pid})");
        }

        const string remedy = "stop the other program, or set another daemon.port (`abl setup --port <n>`)";
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException)
        {
            using var http = DaemonLifecycle.HealthClient();
            return await DaemonLifecycle.ProbeAsync(http, DaemonPaths.UrlFor(port)) is { } other
                ? DoctorCheck.Fail("daemon.port", $"port {port} is held by another AgentBlender daemon (pid {other.Pid}), not this store's", "stop it with `abl daemon stop` under its configuration, or set another daemon.port (`abl setup --port <n>`)")
                : facts.State is { } recorded && recorded.Port == port
                    ? DoctorCheck.Fail("daemon.port", $"port {port} is in use, and this store's daemon record (pid {recorded.Pid}) names it, but nothing answers /healthz there", "see `abl daemon logs`; `abl daemon stop --force` ends a hung daemon")
                    : DoctorCheck.Fail("daemon.port", $"port {port} is in use by another program", remedy);
        }

        return facts.Running
            ? DoctorCheck.Warn("daemon.port", $"port {port} is free, but this store's daemon serves port {facts.State!.Port}", "`abl daemon stop && abl daemon start` moves it to this configuration's port")
            : DoctorCheck.Pass("daemon.port", $"port {port} is free");
    }

    private static async Task<DoctorCheck> DaemonReachableAsync(AblOptions options, Context context)
    {
        var facts = await context.DaemonAsync(options);
        if (facts.StoreProblem is { } untrusted)
        {
            return DoctorCheck.Fail("daemon.reachable", untrusted.Message, untrusted.Hint ?? "fix paths.store");
        }

        if (!facts.Running)
        {
            return facts.State is { } last
                ? DoctorCheck.Warn("daemon.reachable", $"no daemon is running: the last one (pid {last.Pid}, {DaemonPaths.UrlFor(last.Port)}) does not answer /healthz", "`abl daemon start`; if a daemon hangs, see `abl daemon logs`")
                : DoctorCheck.Warn("daemon.reachable", $"no daemon is running on store {facts.Store}", "`abl daemon start`");
        }

        var state = facts.State!;
        var health = facts.Health!;
        var detail = new JsonObject
        {
            ["pid"] = health.Pid,
            ["url"] = DaemonPaths.UrlFor(state.Port),
            ["version"] = health.Version,
            ["readyWorkers"] = health.ReadyWorkers,
            ["sessions"] = health.Sessions,
            ["lastWorkerError"] = health.LastWorkerError?.DeepClone(),
        };
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"daemon pid {health.Pid} at {DaemonPaths.UrlFor(state.Port)} (version {health.Version}, {health.ReadyWorkers} ready worker(s), {health.Sessions} session(s))");
        if (health.Version != AblVersion.Current)
        {
            return DoctorCheck.Warn("daemon.reachable", $"{summary}; this abl is {AblVersion.Current}", "restart it after an upgrade: `abl daemon stop && abl daemon start`", detail);
        }

        if (health.ReadyWorkers == 0 && health.LastWorkerError is { } error)
        {
            return DoctorCheck.Warn(
                "daemon.reachable",
                DaemonLog.Clean($"{summary}; last worker error ({error["stage"]}): {error["message"]}"),
                "the error's check ID names the fix; `abl daemon logs` shows more",
                detail);
        }

        return DoctorCheck.Pass("daemon.reachable", summary, detail);
    }

    private static async Task<DoctorCheck> ConfigDriftAsync(AblOptions options, Context context)
    {
        var facts = await context.DaemonAsync(options);
        if (!facts.Running)
        {
            return DoctorCheck.Skip("daemon.config-drift", "no daemon is running");
        }

        JsonObject status;
        try
        {
            using var http = DaemonLifecycle.HealthClient();
            status = await DaemonLifecycle.ReadOpAsync(http, DaemonPaths.UrlFor(facts.State!.Port), "daemon_status");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return DoctorCheck.Warn("daemon.config-drift", $"daemon_status failed ({ex.Message})", "a daemon older than this abl has no daemon_status: `abl daemon stop && abl daemon start`");
        }

        var cli = context.Config.ToJson()["effective"]!.AsObject();
        if (status["config"]?["effective"] is not JsonObject daemon)
        {
            return DoctorCheck.Warn("daemon.config-drift", "daemon_status has no config.effective", "`abl daemon stop && abl daemon start`");
        }

        var differences = Drift(cli, daemon);
        if (differences.Count == 0)
        {
            return DoctorCheck.Pass("daemon.config-drift", $"the daemon runs this shell's effective configuration ({Short(context.Config.EffectiveDigest)})");
        }

        static string Side(JsonObject? side) => side is null ? "absent"
            : $"{(side["value"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : side["value"]?.ToJsonString() ?? "null")} ({side["source"]})";
        return DoctorCheck.Warn(
            "daemon.config-drift",
            DaemonLog.Clean(string.Join("; ", differences.Select(d => $"{d.Key}: this shell {Side(d.Cli)}, daemon {Side(d.Daemon)}"))),
            "`abl daemon stop && abl daemon start` to apply this shell's configuration, or align this shell's environment with the daemon's",
            new JsonObject
            {
                ["differences"] = new JsonArray([.. differences.Select(d => (JsonNode)new JsonObject
                {
                    ["key"] = d.Key,
                    ["cli"] = d.Cli?.DeepClone(),
                    ["daemon"] = d.Daemon?.DeepClone(),
                })]),
            });
    }

    private DoctorCheck ServiceStateCheck(ConfigInputs inputs)
    {
        if (host.Services is not { } services)
        {
            return DoctorCheck.Skip("service.state", "no user service manager on this host");
        }

        var state = services.Query();
        if (!state.Installed)
        {
            return DoctorCheck.Skip("service.state", "no user service installed");
        }

        var detail = state.ToJson(null);
        detail.Remove("runsThisDaemon");
        detail["dotnetRoot"] = state.DotnetRoot;
        const string reinstall = "`abl daemon install-service` from the abl you want the service to run";
        if (state.DotnetRoot is null || !DotnetRoot.HasAspNetCore10(state.DotnetRoot))
        {
            return DoctorCheck.Fail("service.state", $"the unit's DOTNET_ROOT {state.DotnetRoot ?? "(none)"} has no ASP.NET Core 10 runtime, so the service cannot start abl", reinstall, detail);
        }

        var command = ServiceUnits.CommandOf(state.ExecStart);
        if (host.ThisAbl is { } self && (command is null || !command.Take(self.Count).SequenceEqual(self, StringComparer.Ordinal)))
        {
            return DoctorCheck.Warn("service.state", $"{state.UnitFile} runs {command?.FirstOrDefault() ?? "(unreadable)"}, not this abl ({self[0]})", reinstall, detail);
        }

        if (state.Failure is { } failure)
        {
            return DoctorCheck.Warn(
                "service.state",
                $"the {state.Kind} job is down after a failure ({failure})",
                "see `abl daemon logs`, fix the cause, then `abl daemon start`",
                detail);
        }

        if (Linger(inputs) is { } lingerOff)
        {
            return DoctorCheck.Warn("service.state", lingerOff, "`loginctl enable-linger` keeps the user service running after logout", detail);
        }

        return DoctorCheck.Pass(
            "service.state",
            $"{state.Kind} {state.UnitFile} ({(state.Active ? "active" : state.Loaded ? "loaded" : "not loaded")})",
            detail);
    }

    /// <summary>N4: on Linux, a user service stops at logout unless linger is on. Null when on or unknown.</summary>
    private string? Linger(ConfigInputs inputs)
    {
        if (inputs.Platform != HostPlatform.Linux)
        {
            return null;
        }

        var user = inputs.Env.GetValueOrDefault("USER") is { Length: > 0 } name ? name : inputs.UserId.ToString(CultureInfo.InvariantCulture);
        var loginctl = new[] { "/usr/bin/loginctl", "/bin/loginctl" }.FirstOrDefault(File.Exists) ?? "/usr/bin/loginctl";
        return host.Run(loginctl, ["show-user", user, "-p", "Linger"]) is { ExitCode: 0 } result
            && result.Output.Trim() == "Linger=no"
                ? $"linger is off for {user}, so systemd stops the user service at logout"
                : null;
    }

    private static async Task<DoctorCheck> DesktopConfigAsync(AblConfig config, CancellationToken ct)
    {
        var endpoint = config[AblConfig.DesktopEndpointKey];
        if (config.Options.Platform == HostPlatform.MacOS)
        {
            return endpoint.Value is null
                ? DoctorCheck.Skip("desktop.config", "unsupported on macOS")
                : DoctorCheck.Warn("desktop.config", AblConfig.DesktopUnsupported, "unset desktop.endpoint (ABL_DESKTOP_ENDPOINT) on this host");
        }

        if (endpoint.Value?.GetValue<string>() is not { } text)
        {
            return DoctorCheck.Skip("desktop.config", "not configured");
        }

        const string readme = "see the desktop remote worker README (feature 003)";
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return DoctorCheck.Fail("desktop.config", $"desktop.endpoint '{text}' is not host:port", "set desktop.endpoint to the desktop launcher's host:port; " + readme);
        }

        var hostName = text[..colon].Trim('[', ']');
        if (hostName is "0.0.0.0" or "*" or "::" or "")
        {
            return DoctorCheck.Fail("desktop.config", $"desktop.endpoint '{text}' is a wildcard address, not the desktop's", "set desktop.endpoint to the desktop launcher's own address; " + readme);
        }

        if (config[AblConfig.TokenKey] is { Error: { } tokenProblem })
        {
            return DoctorCheck.Fail("desktop.config", tokenProblem, "fix the token file as the message says; " + readme);
        }

        if (config.Options.DesktopToken is null)
        {
            return DoctorCheck.Fail("desktop.config", "desktop.endpoint is set but no token is (desktop.tokenFile or ABL_DESKTOP_TOKEN)", "set desktop.tokenFile to a 0600 file holding the launcher token; " + readme);
        }

        var tier = new DesktopTier(config.Options, NullLogger<DesktopTier>.Instance);
        return await tier.ProbeAvailableAsync(ct)
            ? DoctorCheck.Pass("desktop.config", $"the desktop launcher at {text} accepts TCP (not proof of a GPU)")
            : DoctorCheck.Warn("desktop.config", $"the desktop launcher at {text} does not accept TCP; renders stay local", "start the desktop launcher; " + readme);
    }

    private DoctorCheck McpClients(int port)
    {
        var url = McpConfig.Url(port);
        var found = new List<string>();
        var problems = new List<string>();
        foreach (var (file, client, path) in new[]
        {
            (".mcp.json", "claude", new[] { "mcpServers", McpConfig.ServerName, "url" }),
            ("opencode.json", "opencode", new[] { "mcp", McpConfig.ServerName, "url" }),
        })
        {
            var full = Path.Combine(host.WorkingDirectory, file);
            if (!File.Exists(full))
            {
                continue;
            }

            found.Add(file);
            JsonNode? node;
            try
            {
                // A cloned repository can commit these files, so one open and a bounded read, of
                // a regular file only (a link to /dev/zero or a FIFO must not hang doctor).
                if (BoundedFile.Read(full, MaxClientFileBytes) is not { } bytes)
                {
                    problems.Add($"{file} is larger than 1 MiB");
                    continue;
                }

                node = JsonNode.Parse(bytes.AsSpan().StartsWith("\uFEFF"u8) ? bytes.AsSpan(3) : bytes.AsSpan(), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                // Inside the try: JsonNode accepts a duplicate key and throws on first access.
                foreach (var segment in path)
                {
                    node = node is JsonObject obj ? obj[segment] : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // The message can quote a repository-chosen key: bounded like every relayed string.
                problems.Add($"{file} cannot be read as JSON ({Bounded(ex.Message, MaxRelayedChars)})");
                continue;
            }

            var entry = node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (entry is null)
            {
                problems.Add($"{file} has no {McpConfig.ServerName} entry (`abl mcp config --client {client}`)");
            }
            else if (entry != url)
            {
                problems.Add($"{file} names {Bounded(entry, MaxRelayedChars)}, not {url} (`abl mcp config --client {client}`)");
            }
        }

        if (found.Count == 0)
        {
            return DoctorCheck.Skip("mcp.clients", $"no .mcp.json or opencode.json in {host.WorkingDirectory}");
        }

        return problems.Count > 0
            ? DoctorCheck.Warn("mcp.clients", DaemonLog.Clean(string.Join("; ", problems)), "paste the entry `abl mcp config --client claude|opencode` prints")
            : DoctorCheck.Pass("mcp.clients", $"{string.Join(" and ", found)} name {McpConfig.ServerName} at {url}");
    }

    private static string Short(string? hash) =>
        hash is null ? "?" : hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[..Math.Min(hash.Length, 19)] : hash[..Math.Min(hash.Length, 12)];

    /// <summary>The store's daemon as doctor reads it: the record behind the store-root gate, then <c>/healthz</c> (L-d).</summary>
    private sealed record DaemonFacts(string Store, DaemonState? State, Health? Health, ConfigException? StoreProblem)
    {
        public bool Running => State is not null && Health is not null && Health.Pid == State.Pid;
    }

    private sealed class Context(AblConfig config, bool quick, string? only)
    {
        private DaemonFacts? _daemon;

        public AblConfig Config => config;

        public bool Quick => quick;

        public string? Only => only;

        public string? WorkerDigest { get; set; }

        public RoundtripResult? Roundtrip { get; set; }

        public async Task<DaemonFacts> DaemonAsync(AblOptions options)
        {
            if (_daemon is not null)
            {
                return _daemon;
            }

            var store = options.StoreDir;
            if (!Directory.Exists(store))
            {
                return _daemon = new DaemonFacts(store, null, null, null);
            }

            if (DaemonLifecycle.UntrustedStore(store) is { } untrusted)
            {
                return _daemon = new DaemonFacts(store, null, null, untrusted);
            }

            var state = DaemonState.Read(store);
            Health? health = null;
            if (state is { Pid: > 1, Port: > 0 and <= 65535 })
            {
                using var http = DaemonLifecycle.HealthClient();
                health = await DaemonLifecycle.ProbeAsync(http, DaemonPaths.UrlFor(state.Port));
            }

            return _daemon = new DaemonFacts(store, state, health, null);
        }
    }
}
