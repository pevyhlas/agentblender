using System.Text;
using System.Text.Json;

namespace Abl.Core.Telemetry;

/// <summary>Logical returned content in UTF-8 bytes; protocol envelopes are excluded.</summary>
public static class PayloadSizing
{
    public static long Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);
    public static long JsonBytes<T>(T value) => Utf8Bytes(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));
}
