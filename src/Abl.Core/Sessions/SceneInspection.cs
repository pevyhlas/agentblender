using System.Text;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Abl.Core.Sessions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InspectionLandmark(
    [property: Description("Unique landmark label, <=128 characters")] string Name,
    [property: Description("World-space XYZ, exactly three finite numbers of magnitude <=1e9")] double[] World);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SceneInspectionRequest
{
    [Description("Selected active-scene object names, maximum32; omitted means camera-only inspection")]
    public string[] Objects { get; init; } = [];
    [Description("Named perspective/orthographic camera; omitted uses the active scene camera")]
    public string? Camera { get; init; }
    [Description("Maximum64 unique named world-space XYZ points; projection requires a camera")]
    public InspectionLandmark[] Landmarks { get; init; } = [];
    [Description("Include bounded raw mesh coordinate/topology observations; defaultfalse")]
    public bool Geometry { get; init; }
    [Description("Include bounded declared material nodes/sockets/links; defaultfalse")]
    public bool Materials { get; init; }
    [Description("Include bounded declared scalar/enum modifier values; defaultfalse")]
    public bool Modifiers { get; init; }
}

/// <summary>One bounded request contract shared by CLI, REST, MCP and worker replies.</summary>
public static class SceneInspection
{
    public const string Profile = "abl.scene-inspection.v1";
    public const int MaxRequestBytes = 16 * 1024;
    public const int MaxResponseBytes = 256 * 1024;
    public const int MaxObjects = 32;
    public const int MaxLandmarks = 64;
    public const int MaxName = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 24,
    };

    public static SceneInspectionRequest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxRequestBytes) throw Bad("inspection request exceeds 16 KiB");
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 24 });
            var keys = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) keys.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) keys.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !keys.Peek().Add(reader.GetString()!))
                    throw Bad("duplicate inspection property");
            }
            return Capture(JsonSerializer.Deserialize<SceneInspectionRequest>(bytes, Json)
                ?? throw Bad("inspection request must be an object"));
        }
        catch (JsonException) { throw Bad("invalid inspection request shape or number"); }
    }

    public static SceneInspectionRequest Capture(SceneInspectionRequest request)
    {
        if (request is null || request.Objects is null || request.Landmarks is null ||
            request.Objects.Length > MaxObjects || request.Landmarks.Length > MaxLandmarks)
            throw Bad("inspection supports at most 32 objects and 64 landmarks");
        if (request.Camera is not null) Name(request.Camera);
        var objects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in request.Objects)
            if (!objects.Add(Name(name))) throw Bad("duplicate selected object");
        var landmarks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var landmark in request.Landmarks)
        {
            if (landmark is null || !landmarks.Add(Name(landmark.Name))) throw Bad("invalid or duplicate landmark");
            if (landmark.World is null || landmark.World.Length != 3 ||
                landmark.World.Any(n => !double.IsFinite(n) || Math.Abs(n) > 1e9))
                throw Bad("landmark world must have three finite coordinates of magnitude <= 1e9");
        }
        var captured = request with
        {
            Objects = [.. request.Objects],
            Landmarks = [.. request.Landmarks.Select(p => p with { World = [.. p.World] })],
        };
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(captured, Json)) > MaxRequestBytes)
            throw Bad("inspection request exceeds 16 KiB");
        return captured;
    }

    public static JsonObject ToWorker(SceneInspectionRequest request) =>
        JsonSerializer.SerializeToNode(Capture(request), Json)!.AsObject();

    public static void ValidateReply(JsonObject request, JsonObject reply)
    {
        try { ValidateReplyCore(request, reply); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or JsonException)
        {
            throw new InvalidDataException("invalid inspection worker response", ex);
        }
    }

    private static void ValidateReplyCore(JsonObject request, JsonObject reply)
    {
        if (reply["inspection"] is not JsonObject data || data["profile"]?.GetValue<string>() != Profile ||
            data["objects"] is not JsonArray objects || data["landmarks"] is not JsonArray landmarks ||
            data["framing"] is not JsonObject ||
            objects.Count != request["objects"]!.AsArray().Count ||
            landmarks.Count != request["landmarks"]!.AsArray().Count)
            throw new InvalidDataException("invalid inspection response shape/profile/count");
        Fields(data, "profile", "objects", "landmarks", "framing", "camera", "coverage");
        if (Text(data["coverage"]) != "selected-raw-facts-not-replay-or-visual-equivalence") throw Invalid();
        var framing = data["framing"]!.AsObject();
        Fields(framing, "width", "height", "pixelAspect", "coordinates", "border", "cropToBorder", "borderRect");
        Integer(framing["width"], int.MaxValue, 1); Integer(framing["height"], int.MaxValue, 1);
        Vector(framing["pixelAspect"], 2); Vector(framing["borderRect"], 4);
        if (Text(framing["coordinates"]) != "top-left-pixel-edges-x-right-y-down") throw Invalid();
        Boolean(framing["border"]); Boolean(framing["cropToBorder"]);
        if (data["camera"] is JsonObject camera)
        {
            Fields(camera, "name", "type", "matrixWorld", "right", "up", "forward", "lens", "orthoScale", "shift", "sensorFit", "sensor", "clip");
            var cameraName = Text(camera["name"]);
            if (request["camera"] is not null && cameraName != request["camera"]!.GetValue<string>()) throw Invalid();
            if (Text(camera["type"]) is not ("PERSP" or "ORTHO")) throw Invalid();
            Matrix(camera["matrixWorld"]); Vector(camera["right"], 3); Vector(camera["up"], 3); Vector(camera["forward"], 3);
            Number(camera["lens"]); Number(camera["orthoScale"]); Vector(camera["shift"], 2); Vector(camera["sensor"], 2); Vector(camera["clip"], 2);
            if (Text(camera["sensorFit"]) is not ("AUTO" or "HORIZONTAL" or "VERTICAL")) throw Invalid();
        }
        else if (data["camera"] is not null || request["camera"] is not null || landmarks.Count > 0) throw Invalid();
        for (var i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not JsonObject obj || obj["name"]?.GetValue<string>() != request["objects"]![i]!.GetValue<string>() ||
                obj["matrixWorld"] is not JsonArray matrix || matrix.Count != 4 ||
                matrix.Any(row => row is not JsonArray values || values.Count != 4))
                throw new InvalidDataException("invalid inspection object identity/transform");
            var keys = new List<string> { "name", "type", "parent", "matrixWorld", "hideViewport", "hideRender" };
            foreach (var section in new[] { "geometry", "materials", "modifiers" })
                if (request[section]!.GetValue<bool>()) keys.Add(section);
            Fields(obj, [.. keys]);
            Text(obj["type"]); if (obj["parent"] is not null) Text(obj["parent"]);
            Matrix(obj["matrixWorld"]); Boolean(obj["hideViewport"]); Boolean(obj["hideRender"]);
            if (request["geometry"]!.GetValue<bool>()) Geometry(obj["geometry"], Text(obj["type"]));
            if (request["materials"]!.GetValue<bool>()) Materials(obj["materials"]);
            if (request["modifiers"]!.GetValue<bool>()) Modifiers(obj["modifiers"]);
        }
        for (var i = 0; i < landmarks.Count; i++)
        {
            if (landmarks[i] is not JsonObject point || point["name"]?.GetValue<string>() != request["landmarks"]![i]!["name"]!.GetValue<string>())
                throw new InvalidDataException("invalid inspection landmark identity");
            Fields(point, "name", "pixels", "depth", "inFront", "inFrame", "occlusionTested");
            if (point["pixels"] is not null && (point["pixels"] is not JsonArray pixels || pixels.Count != 2) ||
                point["depth"] is not JsonValue depth || !depth.TryGetValue<double>(out _) ||
                point["inFront"] is not JsonValue front || !front.TryGetValue<bool>(out _) ||
                point["inFrame"] is not JsonValue frame || !frame.TryGetValue<bool>(out _) ||
                point["occlusionTested"]?.GetValue<bool>() != false)
                throw new InvalidDataException("invalid inspection projection");
            if (point["pixels"] is not null) Vector(point["pixels"], 2);
            Number(point["depth"]);
        }
        var json = data.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
            throw new InvalidDataException("inspection response exceeds 256 KiB");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        Check(doc.RootElement);

        static void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var n) || !double.IsFinite(n)))
                throw new InvalidDataException("nonfinite inspection response number");
            if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 512)
                throw new InvalidDataException("inspection response string exceeds limit");
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Check(item);
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var item in value.EnumerateObject()) Check(item.Value);
        }
    }

    private static void Geometry(JsonNode? value, string type)
    {
        var obj = Object(value);
        if (type != "MESH") { Fields(obj, "unsupported"); Strings(obj["unsupported"], 16); return; }
        Fields(obj, "profile", "vertices", "edges", "loops", "faces", "sha256", "unsupported");
        if (Text(obj["profile"]) != "abl.raw-mesh-observation.v1") throw Invalid();
        Integer(obj["vertices"], 100000); Integer(obj["edges"], 400000);
        Integer(obj["loops"], 400000); Integer(obj["faces"], 100000);
        Hash(obj["sha256"]); Strings(obj["unsupported"], 16);
    }

    private static void Materials(JsonNode? value)
    {
        foreach (var item in Array(value, 64))
        {
            if (item is null) continue;
            var mat = Object(item);
            Fields(mat, "name", "diffuseColor", "useNodes", "unsupported", "nodes", "links", "observationSha256");
            Text(mat["name"]); Vector(mat["diffuseColor"], 4); Boolean(mat["useNodes"]); Hash(mat["observationSha256"]);
            Strings(mat["unsupported"], 272);
            foreach (var node in Array(mat["nodes"], 256))
            {
                var n = Object(node); Fields(n, "name", "type", "mute", "inputs", "parameters");
                Text(n["name"]); Text(n["type"]); Boolean(n["mute"]);
                foreach (var socket in Array(n["inputs"], 2048))
                {
                    var s = Object(socket);
                    if (s.ContainsKey("value") && s.ContainsKey("unsupported")) throw Invalid();
                    Fields(s, s.ContainsKey("value") ? ["name", "identifier", "linked", "value"] :
                        s.ContainsKey("unsupported") ? ["name", "identifier", "linked", "unsupported"] : ["name", "identifier", "linked"]);
                    Text(s["name"]); Text(s["identifier"]); Boolean(s["linked"]);
                    if (s.ContainsKey("value")) Primitive(s["value"]);
                    if (s.ContainsKey("unsupported")) Text(s["unsupported"]);
                }
                var parameters = Object(n["parameters"]);
                if (parameters.Count > 16) throw Invalid();
                foreach (var (key, parameter) in parameters) { Text(JsonValue.Create(key)); Primitive(parameter); }
            }
            foreach (var link in Array(mat["links"], 1024))
            {
                var entries = Array(link, 4); if (entries.Count != 4) throw Invalid();
                foreach (var entry in entries) Text(entry);
            }
        }
    }

    private static void Modifiers(JsonNode? value)
    {
        foreach (var item in Array(value, 64))
        {
            var modifier = Object(item); Fields(modifier, "name", "type", "values", "unsupported");
            Text(modifier["name"]); Text(modifier["type"]); Strings(modifier["unsupported"], 4096);
            var values = Object(modifier["values"]); if (values.Count > 4096) throw Invalid();
            foreach (var (key, entry) in values) { Text(JsonValue.Create(key)); Primitive(entry); }
        }
    }

    private static void Primitive(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            if (array.Count > 16) throw Invalid();
            foreach (var item in array) Scalar(item);
        }
        else Scalar(node);
        static void Scalar(JsonNode? item)
        {
            if (item is not JsonValue value) throw Invalid();
            if (value.TryGetValue<bool>(out _)) return;
            if (value.TryGetValue<string>(out _)) { Text(value); return; }
            Number(value);
        }
    }

    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw Invalid();
    private static JsonArray Array(JsonNode? value, int max) => value is JsonArray a && a.Count <= max ? a : throw Invalid();
    private static void Fields(JsonObject obj, params string[] keys)
    {
        if (obj.Count != keys.Length || keys.Any(k => !obj.ContainsKey(k))) throw Invalid();
    }
    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) &&
        !string.IsNullOrWhiteSpace(s) && s.Length <= 512 && !s.Any(char.IsControl) ? s : throw Invalid();
    private static void Boolean(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<bool>(out _)) throw Invalid();
    }
    private static void Number(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<double>(out var n) || !double.IsFinite(n)) throw Invalid();
    }
    private static void Integer(JsonNode? node, int max, int min = 0)
    {
        if (node is not JsonValue v || !v.TryGetValue<int>(out var n) || n < min || n > max) throw Invalid();
    }
    private static void Hash(JsonNode? node)
    {
        var text = Text(node); if (text.Length != 64 || !text.All(char.IsAsciiHexDigitLower)) throw Invalid();
    }
    private static void Strings(JsonNode? node, int max)
    {
        foreach (var item in Array(node, max)) Text(item);
    }
    private static void Vector(JsonNode? node, int length)
    {
        var array = Array(node, length); if (array.Count != length) throw Invalid();
        foreach (var item in array) Number(item);
    }
    private static void Matrix(JsonNode? node)
    {
        var rows = Array(node, 4); if (rows.Count != 4) throw Invalid();
        foreach (var row in rows) Vector(row, 4);
    }
    private static InvalidDataException Invalid() => new("invalid inspection response schema/value");

    public static async Task RecordRejectionAsync(AblException error, string? session, string? project)
    {
        try
        {
            await Telemetry.AblTelemetry.RunAsync<object>("scene_inspect", session, project,
                () => Task.FromException<object>(error)).ConfigureAwait(false);
        }
        catch (AblException) { /* Measurement records the real failure; adapters own its public mapping. */ }
    }

    private static string Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxName &&
        !value.Any(char.IsControl) ? value : throw Bad("names must be nonblank, <=128 characters and contain no controls");
    private static AblException Bad(string message) => new("bad_request", message);
}
