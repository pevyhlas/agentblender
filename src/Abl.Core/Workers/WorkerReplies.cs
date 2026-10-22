using System.Text.Json.Nodes;

namespace Abl.Core.Workers;

/// <summary>Validate success payloads before a caller can advance a checkpoint or journal.</summary>
internal static class WorkerReplies
{
    /// <summary>
    /// The GPU capability of a hello that <see cref="Validate"/> accepted (009 T004): the
    /// single source of <see cref="WorkerHandle.Gpu"/> for the local and the desktop tier.
    /// </summary>
    public static GpuCapability Gpu(JsonObject hello)
    {
        var gpu = (JsonObject)hello["gpu"]!;
        var devices = ((JsonArray)gpu["devices"]!)
            .Select(d => new GpuDevice(d!["name"]!.GetValue<string>(), d["type"]!.GetValue<string>()))
            .ToArray();
        return new GpuCapability(gpu["backend"]!.GetValue<string>(), devices, gpu["metalrt"]?.GetValue<string>());
    }

    /// <summary>
    /// Validate one success reply. <paramref name="helloBackend"/> is the backend of the
    /// connection's last validated hello, which a <c>cycles_gpu</c> render's <c>device</c>
    /// must match (null before any hello).
    /// </summary>
    public static void Validate(string op, JsonObject request, JsonObject reply, string? helloBackend = null)
    {
        switch (op)
        {
            case "inspect": Sessions.SceneInspection.ValidateReply(request, reply); break;
            case "hello":
                Text(reply, "blender_version", 128);
                Text(reply, "python_version", 128);
                ValidateGpu(Object(reply, "gpu"));
                if (reply["capabilities"] is not null) Boolean(Object(reply, "capabilities"), "blob_chunks");
                break;
            case "warm": Number(reply, "warmed_ms"); break;
            case "exec":
                Number(reply, "duration_ms");
                Delta(Object(reply, "state_delta"));
                break;
            case "save":
                Integer(reply, "bytes");
                Hash(reply, "digest");
                Text(reply, "blender_version", 128);
                break;
            case "state":
                var state = Object(reply, "state");
                if (request["mode"]?.GetValue<string>() == "full")
                {
                    foreach (var (_, node) in Object(state, "objects"))
                    {
                        if (node is not JsonObject obj) throw Bad("state object inventory entry");
                        Text(obj, "type", 128);
                        if (obj["parent"] is not null) Text(obj, "parent", 4096);
                        Integer(obj, "verts", int.MaxValue);
                        Integer(obj, "faces", int.MaxValue);
                        Names(obj, "materials");
                    }
                    Names(state, "collections");
                    Names(state, "materials");
                }
                else Delta(state);
                break;
            case "render":
                Number(reply, "duration_ms");
                Pixels(request, reply);
                RenderDevice(request, reply, helloBackend);
                if (request["profile"]?.GetValue<bool>() == true)
                {
                    try
                    {
                        var settings = Object(reply, "settings");
                        if (Text(settings, "format", 16) != "PNG" || Text(settings, "mode", 16) != "RGBA" ||
                            Text(settings, "depth", 16) != "8" || Text(settings, "display", 16) != "sRGB" ||
                            Text(settings, "view", 16) != "Standard" || Text(settings, "look", 16) != "None" ||
                            Text(settings, "colorManagement", 32) != "FOLLOW_SCENE" ||
                            settings["transparent"] is not JsonValue transparent || !transparent.TryGetValue<bool>(out var alpha) || !alpha ||
                            settings["border"] is not JsonValue border || !border.TryGetValue<bool>(out var cropped) || cropped ||
                            settings["crop"] is not JsonValue crop || !crop.TryGetValue<bool>(out var trim) || trim ||
                            Integer(settings, "width", 4096) != request["res"]!.GetValue<int>() ||
                            Integer(settings, "height", 4096) != request["res"]!.GetValue<int>() || Integer(settings, "percentage") != 100 ||
                            settings["exposure"]?.GetValue<double>() != 0 || settings["gamma"]?.GetValue<double>() != 1 ||
                            settings["dither"]?.GetValue<double>() != 0 || settings["pixelAspect"] is not JsonArray aspect || aspect.Count != 2 ||
                            aspect[0]?.GetValue<double>() != 1 || aspect[1]?.GetValue<double>() != 1)
                            throw Bad("delivery render profile settings");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException)
                    {
                        throw Bad("delivery render profile settings");
                    }
                }
                break;
            case "contact_sheet": Pixels(request, reply); break;
            case "load_bytes":
            case "blob_put_commit":
                if (request["keep"]?.GetValue<bool>() == true) Text(reply, "path", 4096);
                break;
            case "blob_put_begin":
                Id(reply);
                if (Integer(reply, "chunk_bytes") != WorkerTransfers.ChunkBytes) throw Bad("chunk_bytes");
                break;
            case "blob_put_chunk": Integer(reply, "offset"); break;
            case "blob_get_chunk":
                Integer(reply, "offset");
                Text(reply, "data", ((WorkerTransfers.ChunkBytes + 2) / 3) * 4);
                break;
        }
    }

    /// <summary>
    /// The hello <c>gpu</c> object per the 009 plan's contract table. The older shape
    /// (<c>optix</c> present, <c>backend</c> absent: a worker older than the host) is
    /// <c>worker_upgrade_required</c> on any tier (plan D2); any other malformed object is
    /// an invalid reply.
    /// </summary>
    private static void ValidateGpu(JsonObject gpu)
    {
        if (gpu["backend"] is null && gpu.ContainsKey("optix"))
        {
            throw new AblException(
                "worker_upgrade_required",
                "the worker reports an older hello shape (gpu.optix without gpu.backend)",
                hint: WorkerTransfers.UpgradeHint);
        }

        var backend = Text(gpu, "backend", 16);
        if (backend is not ("optix" or "metal" or "none")) throw Bad("gpu.backend");
        if (gpu["devices"] is not JsonArray devices || devices.Count > 8 || (devices.Count == 0) != (backend == "none"))
            throw Bad("gpu.devices");
        var type = backend.ToUpperInvariant();
        foreach (var node in devices)
        {
            if (node is not JsonObject device) throw Bad("gpu.devices");
            DeviceName(device, "gpu.devices[].name");
            if (Text(device, "type", 16) != type) throw Bad("gpu.devices[].type");
        }

        if (gpu["metalrt"] is not null
            && (backend != "metal" || Text(gpu, "metalrt", 8) is not ("OFF" or "ON" or "AUTO")))
            throw Bad("gpu.metalrt");
    }

    /// <summary>
    /// A render reply's <c>device</c>: present for the Cycles engines, absent otherwise; its
    /// backend is the hello backend for <c>cycles_gpu</c> and <c>cpu</c> for <c>cycles_cpu</c>.
    /// </summary>
    private static void RenderDevice(JsonObject request, JsonObject reply, string? helloBackend)
    {
        var engine = request["engine"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
        if (engine is not ("cycles_gpu" or "cycles_cpu"))
        {
            if (reply.ContainsKey("device")) throw Bad("device");
            return;
        }

        var device = Object(reply, "device");
        var backend = Text(device, "backend", 16);
        var expected = engine == "cycles_cpu" ? "cpu" : helloBackend;
        if (backend is not ("optix" or "metal" or "cpu") || backend != expected) throw Bad("device.backend");
        if (backend == "cpu")
        {
            if (device["name"] is not null) DeviceName(device, "device.name");
        }
        else DeviceName(device, "device.name");
    }

    /// <summary>A device name: at most 128 UTF-16 code units, no C0 or C1 control character.</summary>
    private static void DeviceName(JsonObject device, string field)
    {
        if (device["name"] is not JsonValue value || !value.TryGetValue<string>(out var name) || name is null
            || name.Length > 128 || name.Any(c => c < 0x20 || c is >= '\u007f' and <= '\u009f'))
            throw Bad(field);
    }

    private static void Pixels(JsonObject request, JsonObject reply)
    {
        Text(reply, "out", 4096);
        if (request["return_blob"]?.GetValue<bool>() == true)
        {
            var blob = Object(reply, "blob");
            Id(blob);
            Integer(blob, "bytes");
            Hash(blob, "sha256");
        }
        else if (request["return_bytes"]?.GetValue<bool>() == true) Text(reply, "png_b64", WireCodec.MaxFrameBytes);
    }

    private static void Delta(JsonObject delta)
    {
        Names(delta, "added"); Names(delta, "removed"); Names(delta, "changed");
        foreach (var field in new[] { "collections", "materials" })
        {
            if (delta[field] is null) continue;
            var set = Object(delta, field);
            Names(set, "added"); Names(set, "removed");
        }
    }

    private static void Names(JsonObject obj, string field)
    {
        if (obj[field] is not JsonArray names) throw Bad(field);
        foreach (var value in names)
            if (value is not JsonValue text || !text.TryGetValue<string>(out var name) || name is null || name.Length > 4096)
                throw Bad(field);
    }

    private static JsonObject Object(JsonObject obj, string field) => obj[field] as JsonObject ?? throw Bad(field);
    private static string Text(JsonObject obj, string field, int maximum) =>
        obj[field] is JsonValue value && value.TryGetValue<string>(out var text)
            && text is not null && text.Length <= maximum ? text : throw Bad(field);
    private static long Integer(JsonObject obj, string field, long maximum = long.MaxValue) =>
        obj[field] is JsonValue value && value.TryGetValue<long>(out var number) && number >= 0 && number <= maximum
            ? number : throw Bad(field);
    private static void Number(JsonObject obj, string field)
    {
        if (obj[field] is not JsonValue value || !value.TryGetValue<double>(out var number)
            || !double.IsFinite(number) || number < 0) throw Bad(field);
    }
    private static void Boolean(JsonObject obj, string field)
    {
        if (obj[field] is not JsonValue value || !value.TryGetValue<bool>(out _)) throw Bad(field);
    }
    private static void Hash(JsonObject obj, string field)
    {
        var hash = Text(obj, field, 64);
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower)) throw Bad(field);
    }
    private static void Id(JsonObject obj)
    {
        var id = Text(obj, "transfer_id", 128);
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw Bad("transfer_id");
    }
    private static InvalidDataException Bad(string field) => new($"worker success response has invalid {field}");
}
