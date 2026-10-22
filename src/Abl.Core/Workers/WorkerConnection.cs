using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Abl.Core.Workers;

/// <summary>
/// One accepted worker socket. Single in-flight request (the worker is single-threaded
/// blocking); the HOST enforces timeouts — a breach poisons the connection and the
/// supervisor kills the process (checkpoints make workers disposable).
/// </summary>
public sealed class WorkerConnection(Socket socket) : IDisposable
{
    private readonly NetworkStream _stream = new(socket, ownsSocket: true);
    private long _nextId;

    // The backend of the last validated hello (009 T004): a cycles_gpu render reply's
    // device must report it.
    private string? _helloBackend;

    /// <summary>True once a timeout/IO fault made responses unmatchable.</summary>
    public bool Poisoned { get; private set; }

    public async Task<JsonObject> RequestAsync(
        string op, JsonObject? fields, TimeSpan timeout, CancellationToken ct)
    {
        if (Poisoned)
        {
            throw new WorkerDeadException("connection poisoned by an earlier timeout/fault");
        }

        var id = ++_nextId;
        var request = fields ?? [];
        request["id"] = id;
        request["op"] = op;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await WireCodec.WriteAsync(_stream, request, cts.Token).ConfigureAwait(false);
            var response = await WireCodec.ReadAsync(_stream, cts.Token).ConfigureAwait(false);
            if (response["id"] is not JsonValue responseId || !responseId.TryGetValue<long>(out var returnedId))
                throw new InvalidDataException("worker response id is not an integer");
            if (returnedId != id)
            {
                Poisoned = true;
                throw new WorkerDeadException($"worker answered id {response["id"]} to request {id}");
            }

            if (response["ok"] is not JsonValue responseOk || !responseOk.TryGetValue<bool>(out var ok))
                throw new InvalidDataException("worker response ok is not a boolean");
            if (ok)
            {
                WorkerReplies.Validate(op, request, response, _helloBackend);
                if (op == "hello") _helloBackend = WorkerReplies.Gpu(response).Backend;
                return response;
            }

            if (response["error"] is not JsonObject error)
                throw new InvalidDataException("worker error response has no error object");
            throw new WorkerOpException(
                ErrorString(error, "kind", 128)!, ErrorString(error, "message", 65536)!,
                ErrorString(error, "traceback", 65536, optional: true));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Poisoned = true;
            throw new WorkerTimeoutException(op, timeout);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or SocketException or InvalidDataException or JsonException ||
            op == "inspect" && ex is ArgumentException)
        {
            Poisoned = true;
            throw new WorkerDeadException($"worker socket failed during '{op}'", ex);
        }
    }

    private static string? ErrorString(JsonObject error, string field, int maximum, bool optional = false)
    {
        if (optional && error[field] is null) return null;
        return error[field] is JsonValue value && value.TryGetValue<string>(out var text)
            && text is not null && text.Length <= maximum
            ? text : throw new InvalidDataException($"worker error {field} has invalid type/length");
    }

    public void Dispose() => _stream.Dispose();
}
