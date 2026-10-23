using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Cli;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Delivery;
using Abl.Core.Telemetry;
using Abl.Core.Sessions;
using Abl.Core.Store;

// `abl` — thin CLI over the daemon's REST surface (one POST /api/ops/<op> per verb).
// No business logic here: build the body, post, render the terse output form.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

if (args is ["--version"])
{
    // 008 AC-23: a unit's ExecStart under an empty environment proves its runtime with this.
    Console.WriteLine($"abl {AblVersion.Current}");
    return 0;
}

if (args is ["--version", "--json"])
{
    // 008 FR-014: the package's version, supported Blender line and packaged worker digest.
    Console.WriteLine(PackageInfo.ToJson(AppContext.BaseDirectory).ToJsonString());
    return 0;
}

if (HostVerbs.Commands.Contains(args[0]))
{
    // 008 FR-006, FR-009: `abl setup` and `abl doctor` act on this machine only.
    if (OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine($"abl: {args[0]} is unsupported on Windows (Blender's Python has no AF_UNIX there)");
        return 2;
    }

    // FR-006/FR-009's 0/1/2 exit codes hold for an unexpected failure too, as for the daemon verbs.
    try
    {
        return args[0] == "setup"
            ? Abl.Cli.Host.Setup.Run(args[1..], ConfigInputs.FromProcess().WithBlenderSearch(), Console.Out, Console.Error)
            : await Abl.Cli.Host.Doctor.MainAsync(args[1..], Abl.Cli.Host.DoctorHost.ForProcess(), Console.Out, Console.Error);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        Console.Error.WriteLine(Abl.Cli.Host.DaemonLog.Clean($"abl: {args[0]} failed: {ex.Message.ReplaceLineEndings(" ")}"));
        return 1;
    }
}

if (args.Length >= 2 && args[0] == "daemon" && (OperatingSystem.IsWindows() || Abl.Cli.Host.DaemonVerbs.Handles(args[1])))
{
    // 008 FR-007: lifecycle verbs, and `daemon status` (the CLI form of daemon_status, which
    // adds its `local` object). `daemon run` must not touch the console before it points
    // fds 1/2 at its log, so this dispatch comes before any console use.
    if (OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("abl: daemon verbs are unsupported on Windows (Blender's Python has no AF_UNIX there)");
        return 2;
    }

    // The 0/1/2/3 exit contract holds for an unexpected failure too (eng-code L-2).
    try
    {
        return await Abl.Cli.Host.DaemonVerbs.RunAsync(args[1], args[2..]);
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
        or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
        or ArgumentOutOfRangeException)
    {
        // ArgumentOutOfRangeException: the signal primitive refused the caller's own group.
        Console.Error.WriteLine($"abl: daemon {args[1]} failed: {ex.Message}");
        return 1;
    }
}

if (args.Length >= 2 && HostVerbs.Verbs.Contains((args[0], args[1])))
{
    // Host verbs act on this machine and never reach the daemon (008 plan B1).
    // They see Blender as the daemon does: probed or discovered (008 FR-003).
    return HostVerbs.Run((args[0], args[1]), args[2..], ConfigInputs.FromProcess().WithBlenderSearch(), Console.Out, Console.Error);
}

if (args.Length < 2 || !VerbMap.Verbs.TryGetValue((args[0], args[1]), out var op))
{
    Console.Error.WriteLine($"abl: unknown verb '{string.Join(' ', args.Take(2))}'");
    PrintUsage();
    return 2;
}

string? baseUrl = null;
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var (positionals, flags) = ParseArgs(args.Skip(2).ToArray());
    var body = BuildBody(op, positionals, flags);
    if (op is "delivery_verify" or "delivery_list")
    {
        var directory = body["payload"]?.GetValue<string>() ?? throw new UsageException("delivery check needs a completed generation directory");
        if (op == "delivery_list")
        {
            var listed = await AblTelemetry.RunAsync(op, null, null, () => Task.FromResult(DeliveryPayload.List(directory)), PayloadSizing.JsonBytes);
            var listedJson = JsonSerializer.SerializeToNode(listed, JsonSerializerOptions.Web)!.AsObject();
            if (flags.ContainsKey("json")) Console.WriteLine(listedJson.ToJsonString());
            else return Render(op, listedJson);
        }
        else
        {
            var checkedDelivery = await AblTelemetry.RunAsync(op, null, null, () => Task.FromResult(DeliveryPayload.Verify(directory)), PayloadSizing.JsonBytes);
            var checkedJson = JsonSerializer.SerializeToNode(checkedDelivery, JsonSerializerOptions.Web)!.AsObject();
            if (flags.ContainsKey("json"))
            {
                Console.WriteLine(checkedJson.ToJsonString());
                return checkedDelivery.Pass ? 0 : 1;
            }
            return Render(op, checkedJson);
        }
        return 0;
    }
    // ABL_URL wins; otherwise the effective daemon.port (the config file is read, and its
    // trust rule enforced, only when the daemon has to be reached).
    baseUrl = Environment.GetEnvironmentVariable("ABL_URL") is { Length: > 0 } url
        ? url
        : $"http://127.0.0.1:{AblConfig.Resolve(ConfigInputs.FromProcess()).Options.Port}";
    using var http = CliHttp.Create(baseUrl, Environment.GetEnvironmentVariable("ABL_HTTP_TIMEOUT_SECONDS"));
    using var response = await http.PostAsync(
        $"/api/ops/{op}",
        new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellation.Token);

    var raw = await response.Content.ReadAsStringAsync(cancellation.Token);
    JsonObject? payload = null;
    try
    {
        // `as` (not AsObject()) so a valid-but-non-object body (array/scalar) is null
        // rather than throwing InvalidOperationException past this guard.
        payload = JsonNode.Parse(raw) as JsonObject;
    }
    catch (JsonException)
    {
        // fall through to the unreadable-body path
    }

    if (payload is null)
    {
        // The daemon always answers with a JSON object; an empty, non-JSON, or non-object
        // body means an unhandled server error. Report it instead of crashing on the parse.
        Console.Error.WriteLine($"abl: daemon returned HTTP {(int)response.StatusCode} with an unreadable body");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            // Strip control chars so a stray body can't drive the terminal via escapes.
            var snippet = new string(raw[..Math.Min(raw.Length, 500)]
                .Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? '?' : c).ToArray());
            Console.Error.WriteLine($"  body: {snippet}");
        }

        return 1;
    }

    if (!response.IsSuccessStatusCode)
    {
        foreach (var line in CliText.ErrorLines(payload))
        {
            Console.Error.WriteLine(line);
        }

        return 1;
    }

    return Render(op, payload);
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine(CliText.Unreachable(baseUrl));
    Console.Error.WriteLine($"  cause: {Abl.Cli.Host.DaemonLog.Clean(ex.Message)}");
    return 1;
}
catch (ConfigException ex)
{
    HostVerbs.WriteError(Console.Error, ex);
    return 1;
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"abl: {ex.Message}");
    return 2;
}
catch (AblException ex)
{
    Console.Error.WriteLine($"abl: {ex.Kind}: {ex.Message}");
    if (ex.Hint is not null) Console.Error.WriteLine("  hint: " + ex.Hint);
    return 2;
}
catch (Exception ex) when (op == "scene_inspect" && ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("abl: cannot read inspection request; create the JSON file, correct its path or fix file access");
    return 2;
}
catch (Exception ex) when (op is "delivery_verify" or "delivery_list" or "delivery_export" && ex is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine("abl: delivery input is unreadable or invalid JSON; correct the path/access or provide a JSON array with zero/one parameter row");
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(cancellation.IsCancellationRequested
        ? "abl: request cancelled"
        : "abl: request exceeded ABL_HTTP_TIMEOUT_SECONDS; increase it or unset it for long renders/batches");
    return 1;
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"abl: {ex.Message}");
    return 2;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static JsonObject BuildBody(string op, List<string> positionals, Dictionary<string, List<string>> flags)
{
    var body = new JsonObject();
    void Pos(int index, string field)
    {
        if (positionals.Count > index)
        {
            body[field] = positionals[index];
        }
    }

    void Flag(string name, string field, bool asInt = false)
    {
        if (flags.TryGetValue(name, out var values))
        {
            body[field] = asInt ? int.Parse(values[^1]) : values[^1];
        }
    }

    switch (op)
    {
        case "session_open":
            Flag("project", "project");
            break;
        case "session_close":
            Pos(0, "session");
            break;
        case "scene_state":
            Pos(0, "session");
            body["mode"] = flags.ContainsKey("full") ? "full" : "delta";
            break;
        case "scene_exec":
            Pos(0, "session");
            var scriptPath = flags.TryGetValue("script", out var s)
                ? s[^1]
                : throw new UsageException("scene exec needs --script FILE");
            body["script"] = File.ReadAllText(scriptPath);
            if (flags.TryGetValue("params", out var pj))
            {
                body["params"] = JsonNode.Parse(pj[^1]);
            }

            break;
        case "scene_inspect":
            Pos(0, "session");
            if (!flags.TryGetValue("request", out var inspectionPath))
                throw new UsageException("scene inspect needs --request FILE (bounded inspection JSON)");
            using (var input = File.OpenRead(inspectionPath[^1]))
            {
                if (input.Length > SceneInspection.MaxRequestBytes) throw new UsageException("inspection request exceeds 16 KiB");
                var bytes = new byte[SceneInspection.MaxRequestBytes + 1];
                var length = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                body["request"] = SceneInspection.ToWorker(SceneInspection.Parse(bytes.AsSpan(0, length)));
            }
            break;
        case "scene_preview":
            Pos(0, "session");
            Flag("size", "size", asInt: true);
            Flag("engine", "engine");
            break;
        case "checkpoint_save":
            Pos(0, "session");
            AddList(body, "tags", flags, "tag");
            break;
        case "checkpoint_list":
            Flag("session", "session");
            break;
        case "checkpoint_branch":
            Pos(0, "checkpoint");
            Flag("project", "project");
            break;
        case "checkpoint_tag":
            Pos(0, "checkpoint");
            body["tags"] = new JsonArray([.. positionals.Skip(1).Select(t => (JsonNode)t)]);
            break;
        case "checkpoint_diff":
            Pos(0, "a");
            Pos(1, "b");
            break;
        case "checkpoint_prune":
            body["force"] = flags.ContainsKey("force");
            break;
        case "trajectory_freeze":
            Pos(0, "session");
            Flag("name", "name");
            Flag("from", "from", asInt: true);
            Flag("to", "to", asInt: true);
            break;
        case "trajectory_export":
            Pos(0, "artifact");
            if (!flags.ContainsKey("out"))
            {
                throw new UsageException("trajectory export needs --out DIR");
            }

            Flag("out", "out");
            break;
        case "trajectory_import":
            Pos(0, "bundle");
            break;
        case "replay_run":
            Pos(0, "artifact"); // digest verification is always on — there is no flag
            break;
        case "render_final":
            Flag("session", "session");
            Flag("checkpoint", "checkpoint");
            Flag("engine", "engine");
            Flag("res", "res", asInt: true);
            Flag("samples", "samples", asInt: true);
            Flag("out", "out");
            Flag("lease", "lease");
            Flag("seed", "seed", asInt: true);
            break;
        case "batch_run":
            Pos(0, "artifact");
            var tablePath = flags.TryGetValue("params-file", out var pf)
                ? pf[^1]
                : throw new UsageException("batch run needs --params-file FILE (a JSON array of objects)");
            body["paramTable"] = JsonNode.Parse(File.ReadAllText(tablePath));
            Flag("res", "res", asInt: true);
            Flag("columns", "columns", asInt: true);
            Flag("engine", "engine");
            break;
        case "delivery_export":
            Pos(0, "artifact");
            if (!flags.ContainsKey("out"))
            {
                throw new UsageException("delivery export needs --out DIR");
            }

            Flag("out", "out");
            // First qualified profile accepts zero/one parameter-overlay row.
            if (flags.TryGetValue("params-file", out var deliveryTable))
            {
                body["paramTable"] = JsonNode.Parse(File.ReadAllText(deliveryTable[^1]));
            }

            Flag("engine", "engine");
            Flag("res", "res", asInt: true);
            Flag("samples", "samples", asInt: true);
            Flag("seed", "seed", asInt: true);
            Flag("columns", "columns", asInt: true);
            break;
        case "delivery_list" or "delivery_verify":
            Pos(0, "payload");
            break;
    }

    return body;
}

static int Render(string op, JsonObject payload)
{
    switch (op)
    {
        case "session_open" or "checkpoint_branch":
            Console.WriteLine(
                $"session {payload["id"]} project {payload["project"]} " +
                $"head {Short(payload["head"])} worker {payload["workerId"]}");
            break;
        case "session_list":
            // session_list returns a bare array; it arrives wrapped — see PostAsync note below.
            foreach (var row in payload["items"]!.AsArray())
            {
                Console.WriteLine(
                    $"{row!["id"]} {row["project"]} {row["state"]} head {Short(row["head"])}");
            }

            break;
        case "session_close":
            Console.WriteLine($"closed {payload["closed"]}");
            break;
        case "scene_state":
            Console.WriteLine(payload["text"]!.GetValue<string>());
            break;
        case "scene_exec":
            Console.WriteLine(payload["delta"]!.GetValue<string>());
            Console.WriteLine($"checkpoint {Short(payload["checkpoint"])} ({payload["durationMs"]} ms)");
            break;
        case "scene_preview":
            Console.WriteLine($"{payload["path"]} ({payload["durationMs"]} ms, {payload["engine"]})");
            break;
        case "checkpoint_save":
            Console.WriteLine(payload["checkpoint"]!.GetValue<string>());
            break;
        case "checkpoint_list":
            // Same renderer as the MCP tool (FR-009) — the terse form cannot drift.
            var now = DateTimeOffset.UtcNow;
            foreach (var row in payload["items"]!.AsArray())
            {
                var meta = row.Deserialize<CheckpointMeta>(JsonSerializerOptions.Web)!;
                Console.WriteLine(StateText.RenderCheckpointLine(meta, now));
            }

            break;
        case "checkpoint_tag":
            Console.WriteLine("ok");
            break;
        case "checkpoint_diff":
            foreach (var name in payload["added"]!.AsArray())
            {
                Console.WriteLine($"+ {name}");
            }

            foreach (var name in payload["removed"]!.AsArray())
            {
                Console.WriteLine($"- {name}");
            }

            break;
        case "checkpoint_prune":
            Console.WriteLine($"deleted {payload["deleted"]}, refused {payload["refused"]!.AsArray().Count}");
            break;
        case "trajectory_freeze":
            Console.WriteLine(
                $"frozen {payload["name"]}: {payload["ops"]} ops, " +
                $"base {Short(payload["baseCheckpoint"])}, digest {Short(payload["expectedDigest"])}");
            break;
        case "trajectory_export":
            Console.WriteLine($"bundle {payload["directory"]}");
            Console.WriteLine($"  {payload["artifactJson"]}");
            Console.WriteLine($"  {payload["baseBlend"]}");
            Console.WriteLine("commit both files; reconstitute with: abl trajectory import <dir>");
            break;
        case "trajectory_import":
            Console.WriteLine(
                $"imported {payload["name"]}: {payload["ops"]} ops, " +
                $"base {Short(payload["baseCheckpoint"])}, digest {Short(payload["expectedDigest"])}" +
                (payload["blenderVersion"] is { } bv ? $" (blender {bv})" : ""));
            Console.WriteLine($"verify with: abl replay run {payload["name"]}");
            break;
        case "replay_run":
            var pass = payload["pass"]!.GetValue<bool>();
            Console.WriteLine(pass
                ? $"PASS {Short(payload["finalDigest"])} ({payload["durationMs"]} ms)"
                : $"FAIL got {Short(payload["finalDigest"])} expected {Short(payload["expectedDigest"])}");
            if (payload["artifactBlenderVersion"] is { } frozenOn)
            {
                Console.Error.WriteLine(
                    $"  note: frozen on Blender {frozenOn}, replayed on {payload["workerBlenderVersion"]}");
            }

            return pass ? 0 : 1;
        case "render_final":
            Console.WriteLine(payload["path"]!.GetValue<string>());
            Console.WriteLine(
                $"sidecar {payload["sidecarPath"]} ({payload["durationMs"]} ms, tier {payload["tier"]})");
            PrintDevice(payload);
            PrintFallback(payload);
            break;
        case "batch_run":
            Console.WriteLine($"sheet {payload["contactSheet"]} (tier {payload["tier"]})");
            PrintDevice(payload);
            PrintFallback(payload);
            foreach (var render in payload["renders"]!.AsArray())
            {
                Console.WriteLine(render!.GetValue<string>());
            }

            break;
        case "delivery_export":
            Console.WriteLine($"delivery {payload["name"]}: {payload["items"]} image(s)");
            Console.WriteLine($"  payload:       {payload["payloadDir"]}");
            Console.WriteLine($"  descriptor:    {payload["descriptorPath"]}");
            Console.WriteLine($"  replay-source: {payload["replaySourceDir"]}");
            Console.WriteLine($"  contact sheet: {payload["contactSheet"]}");
            Console.WriteLine("verify the completed generation offline: abl delivery verify <generation-dir>; AGD preflight/verify is separate from admission");
            break;
        case "delivery_list":
            Console.WriteLine(
                $"{payload["name"]} [{payload["profile"]}]"
                + (payload["blenderVersion"] is { } dbv ? $" (blender {dbv})" : ""));
            foreach (var row in payload["items"]!.AsArray())
            {
                Console.WriteLine(
                    $"{row!["file"]}  {row["width"]}x{row["height"]}  "
                    + $"sha {Short(row["sha256"])}  scene {Short(row["sceneDigest"])}  {row["recipe"]}");
            }

            break;
        case "delivery_verify":
            var ok = payload["pass"]!.GetValue<bool>();
            Console.WriteLine(ok
                ? $"PASS {payload["items"]} image(s) intact"
                : $"FAIL {payload["items"]} image(s), {payload["problems"]!.AsArray().Count} problem(s)");
            foreach (var problem in payload["problems"]!.AsArray())
            {
                Console.Error.WriteLine($"  - {problem!.GetValue<string>()}");
            }

            if (!ok && payload["hint"] is { } vhint)
            {
                Console.Error.WriteLine($"  hint: {vhint.GetValue<string>()}");
            }

            return ok ? 0 : 1;
        default:
            Console.WriteLine(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            break;
    }

    return 0;
}

static (List<string>, Dictionary<string, List<string>>) ParseArgs(string[] rest)
{
    var positionals = new List<string>();
    var flags = new Dictionary<string, List<string>>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i].StartsWith("--", StringComparison.Ordinal))
        {
            var name = rest[i][2..];
            var isValueFlag = name is not ("full" or "force" or "json");
            if (isValueFlag && i + 1 >= rest.Length)
            {
                throw new UsageException($"--{name} needs a value");
            }

            if (!flags.TryGetValue(name, out var list))
            {
                flags[name] = list = [];
            }

            if (isValueFlag)
            {
                list.Add(rest[++i]);
            }
        }
        else
        {
            positionals.Add(rest[i]);
        }
    }

    return (positionals, flags);
}

static void AddList(JsonObject body, string field, Dictionary<string, List<string>> flags, string flag)
{
    if (flags.TryGetValue(flag, out var values))
    {
        body[field] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
    }
}

static string Short(JsonNode? node) =>
    node?.GetValue<string>() is { Length: >= 12 } hash ? hash[..12] : node?.GetValue<string>() ?? "-";

static void PrintDevice(JsonObject payload)
{
    if (CliText.DeviceLine(payload) is { } line) Console.WriteLine(line);
}

static void PrintFallback(JsonObject payload)
{
    if (payload["fallbackReason"] is not { } reason) return;
    Console.WriteLine($"fallback {reason}");
    if (reason.GetValue<string>() == "worker_upgrade_required")
        Console.Error.WriteLine("  hint: update the desktop launcher together with its bundled worker/");
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        abl — AgentBlender CLI (daemon at $ABL_URL, default http://127.0.0.1:<daemon.port>, 7811)
        No total request timeout by default. Set ABL_HTTP_TIMEOUT_SECONDS for an explicit deadline.

          abl setup [--json] [--dry-run] [--reset] [--blender PATH] [--port N] [--store PATH]
                    [--renders PATH] [--runtime-dir PATH]
                                                discover Blender, check paths, merge them into the config file
          abl doctor [--json] [--quick] [--check ID]   diagnose this host (exit 1 when a check fails)
          abl mcp config --client claude|opencode [--json]   print the MCP client entry (writes nothing)

          abl config show [--json]              effective configuration, each key with its source
          abl config path [--json]              where the config file is looked for

          abl daemon start [--json] [--timeout S]   start detached; returns once a worker is warm
          abl daemon stop [--json] [--force]        refuses while sessions are active unless --force
          abl daemon status [--json]                daemon_status plus `local`; exit 3 when not running
          abl daemon logs [--lines N]               tail of this store's daemon log
          abl daemon run [--detached] [--log-file FILE]   the daemon itself (services, foreground)
          abl daemon install-service [--dry-run] [--enable-at-login] [--json]
                                                    systemd user unit / launchd agent (only DOTNET_ROOT)
          abl daemon uninstall-service [--json]     remove it (refused while it runs)
          abl --version [--json]                    version; --json adds the Blender line and worker digest

          abl session open --project P          abl session list
          abl session close <session>
          abl scene state <session> [--full]
          abl scene inspect <session> --request FILE
            Request example: {"objects":["Cube"],"landmarks":[{"name":"origin","world":[0,0,0]}]}
            Optional camera; geometry/materials/modifiers default false. Limits: 32 objects / 64 points.
            Full contract: docs/scene-inspection.md
          abl scene exec <session> --script FILE [--params JSON]
          abl scene preview <session> [--size N] [--engine E]
          abl checkpoint save <session> [--tag T]...
          abl checkpoint list [--session S]     abl checkpoint branch <ckpt> [--project P]
          abl checkpoint tag <ckpt> <tag>...    abl checkpoint diff <a> <b>
          abl checkpoint prune [--force]
          abl trajectory freeze <session> --name N [--from K] [--to K]
          abl trajectory export <artifact> --out DIR
          abl trajectory import <bundle-dir>
          abl replay run <artifact>
          abl render final (--session S | --checkpoint C) [--engine E] [--res N]
                           [--samples N] [--out PATH] [--lease wait|cpu_fallback|fail] [--seed N]
          abl batch run <artifact> --params-file FILE [--res N] [--columns N] [--engine E]
          abl delivery export <artifact> --out DIR [--params-file FILE] [--engine E]
                              [--res N] [--samples N] [--seed N] [--columns N]
          abl delivery list <generation-dir>  abl delivery verify <generation-dir> (offline, no daemon)
                                              [--json] for structured output
        """);
}

internal sealed class UsageException(string message) : Exception(message);
