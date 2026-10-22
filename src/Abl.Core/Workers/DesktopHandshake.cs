using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Abl.Core.Workers;

/// <summary>
/// The bearer-token handshake for the desktop remote worker (feature 003, FR-003): one
/// framed JSON exchange before any op is served. the host is the client; the desktop
/// launcher is the server. The launcher runs this BEFORE spawning Blender, so an
/// unauthenticated peer never triggers a worker launch. Tokens are hashed to a fixed width
/// and compared in constant time, so a wrong token leaks neither its length nor a prefix.
/// The handshake frame is capped tightly (a token, not a payload).
/// </summary>
public static class DesktopHandshake
{
    /// <summary>A token frame is tiny; cap it far below the worker-op frame limit.</summary>
    public const int MaxHandshakeBytes = 4096;
    /// <summary>
    /// Host side: send the token, expect <c>{ok:true}</c>. Throws
    /// <see cref="UnauthorizedAccessException"/> if the launcher rejects it — the caller
    /// treats that (like an unreachable desktop) as "use the local tier".
    /// </summary>
    public static async Task ClientAsync(Stream stream, string token, CancellationToken ct)
    {
        await WireCodec.WriteAsync(stream, new JsonObject { ["token"] = token }, ct).ConfigureAwait(false);
        var reply = await WireCodec.ReadAsync(stream, ct, MaxHandshakeBytes).ConfigureAwait(false);
        if (reply["ok"]?.GetValue<bool>() != true)
        {
            throw new UnauthorizedAccessException(
                reply["error"]?.GetValue<string>() ?? "desktop launcher rejected the token");
        }
    }

    /// <summary>
    /// Launcher side: read the presented token, compare it in constant time to the
    /// expected one, and reply. Returns whether the peer is authorized (the caller spawns
    /// Blender only when true).
    /// </summary>
    public static async Task<bool> ServerAsync(Stream stream, string expectedToken, CancellationToken ct)
    {
        var request = await WireCodec.ReadAsync(stream, ct, MaxHandshakeBytes).ConfigureAwait(false);
        var presented = request["token"]?.GetValue<string>() ?? "";
        // Hash both to a fixed 32-byte width before the constant-time compare: this hides
        // the token's length (FixedTimeEquals short-circuits on unequal lengths) as well as
        // its bytes.
        var ok = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken)));
        var reply = ok
            ? new JsonObject { ["ok"] = true }
            : new JsonObject { ["ok"] = false, ["error"] = "unauthorized" };
        await WireCodec.WriteAsync(stream, reply, ct).ConfigureAwait(false);
        return ok;
    }
}
