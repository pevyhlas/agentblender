using System.Text.Json.Nodes;

namespace Abl.Cli.Host;

/// <summary>A check's outcome (008 FR-009).</summary>
public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    Skip,
}

/// <summary>
/// One check result in the <c>doctor.v1</c> envelope (plan B4): a stable ID, a status, a
/// one-line summary, optional detail, a concrete remedy for warn and fail, and the reason for
/// a skip. <c>abl setup</c> reports its checks in the same shape.
/// </summary>
public sealed record DoctorCheck(string Id, CheckStatus Status, string Summary)
{
    public JsonObject? Detail { get; init; }

    public string? Remedy { get; init; }

    /// <summary>Why a check was skipped (<c>requires blender.found</c>, <c>--quick</c>, <c>not configured</c>).</summary>
    public string? Reason { get; init; }

    public static DoctorCheck Pass(string id, string summary, JsonObject? detail = null) =>
        new(id, CheckStatus.Pass, summary) { Detail = detail };

    public static DoctorCheck Warn(string id, string summary, string remedy, JsonObject? detail = null) =>
        new(id, CheckStatus.Warn, summary) { Remedy = remedy, Detail = detail };

    public static DoctorCheck Fail(string id, string summary, string remedy, JsonObject? detail = null) =>
        new(id, CheckStatus.Fail, summary) { Remedy = remedy, Detail = detail };

    public static DoctorCheck Skip(string id, string reason) =>
        new(id, CheckStatus.Skip, "skipped: " + reason) { Reason = reason };

    /// <summary>The lowercase wire name of a status.</summary>
    public static string Name(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "pass",
        CheckStatus.Warn => "warn",
        CheckStatus.Fail => "fail",
        _ => "skip",
    };

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["id"] = Id,
            ["status"] = Name(Status),
            ["summary"] = Summary,
        };
        if (Detail is not null)
        {
            json["detail"] = Detail.DeepClone();
        }

        if (Remedy is not null)
        {
            json["remedy"] = Remedy;
        }

        if (Reason is not null)
        {
            json["reason"] = Reason;
        }

        return json;
    }
}

/// <summary>One catalogue entry: the ID, whether only lifecycle verbs emit it, and its prerequisites.</summary>
/// <param name="Id">The stable check ID (API: AC-26).</param>
/// <param name="LifecycleOnly">
/// Emitted by a lifecycle verb or by configuration resolution, never run by doctor as a check
/// of its own (plan B4): doctor reports one only when the configuration it resolves refuses
/// with that ID (<c>config.value</c>).
/// </param>
/// <param name="Requires">Checks that must not fail (or be skipped) for this one to run.</param>
/// <param name="OptIn">
/// Runs only when named by <c>--check</c>; every other run reports it as <c>skip</c> with
/// <c>reason: "opt-in"</c> (009 FR-006: <c>gpu.render</c>, the one check with a lasting side
/// effect, priming the per-user GPU kernel cache).
/// </param>
public sealed record CatalogueEntry(string Id, bool LifecycleOnly, IReadOnlyList<string> Requires, bool OptIn = false);

/// <summary>
/// The lean v1 check catalogue (008 FR-009, plan B4), in report order. The IDs are one
/// namespace for doctor, <c>abl setup</c>, the lifecycle verbs and every <c>ConfigException</c>;
/// a contract snapshot test fails when they or the envelope change (AC-26).
/// </summary>
/// <remarks>
/// <c>--check &lt;id&gt;</c> runs the named check and its prerequisites (transitively), reports
/// them normally and omits the rest of the catalogue; a check whose prerequisite failed or was
/// skipped reports <c>skip</c> with <c>reason: "requires &lt;prerequisite&gt;"</c>, the same rule
/// as a full run. <c>--check</c> runs what it names even under <c>--quick</c>. 009 T021 adopts
/// this definition for <c>gpu.render</c> and adds only its opt-in rule
/// (<see cref="CatalogueEntry.OptIn"/>).
/// </remarks>
public static class DoctorCatalogue
{
    /// <summary>The envelope's schema name, provisional until AGD S03 is reviewed (plan D2).</summary>
    public const string Schema = "doctor.v1";

    private static readonly string[] Config = ["config.file"];

    public static IReadOnlyList<CatalogueEntry> Entries { get; } =
    [
        new("config.file", false, []),
        new("config.value", true, []),
        new("blender.found", false, Config),
        new("blender.version", false, ["blender.found"]),
        new("worker.files", false, Config),
        new("runtime.dir", false, Config),
        new("runtime.socket-path", false, Config),
        new("paths.store", false, Config),
        new("paths.renders", false, Config),
        new("worker.roundtrip", false, ["blender.found", "blender.version", "worker.files", "runtime.dir", "runtime.socket-path"]),
        new("gpu.backend", false, ["worker.roundtrip"]),
        new("gpu.render", false, ["gpu.backend"], OptIn: true),
        new("daemon.port", false, Config),
        new("daemon.reachable", false, Config),
        new("daemon.workers", true, []),
        new("daemon.config-drift", false, ["daemon.reachable"]),
        new("service.state", false, Config),
        new("desktop.config", false, Config),
        new("mcp.clients", false, Config),
    ];

    /// <summary>Every ID, in report order.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Entries.Select(e => e.Id)];

    /// <summary>The reason an opt-in check reports when it was not named (009 FR-006).</summary>
    public const string OptInReason = "opt-in";

    public static CatalogueEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>The named check and all its prerequisites.</summary>
    public static IReadOnlySet<string> Closure(string id)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([id]);
        while (pending.TryPop(out var next))
        {
            if (closure.Add(next) && Find(next) is { } entry)
            {
                foreach (var requirement in entry.Requires)
                {
                    pending.Push(requirement);
                }
            }
        }

        return closure;
    }
}
