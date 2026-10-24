using System.Net;
using System.Net.Sockets;

namespace Abl.Launcher;

/// <summary>
/// Desktop launcher configuration (feature 003). Everything is supplied out-of-band on the
/// desktop (env / user config), never committed. <see cref="ResolveBind"/> enforces the
/// security invariant that the launcher binds a specific private-network interface and never a
/// wildcard/public one (FR-003, SC-003).
/// </summary>
public sealed class LauncherOptions
{
    public required IPEndPoint BindEndpoint { get; init; }

    public required string Token { get; init; }

    public required string BlenderPath { get; init; }

    public required string WorkerDir { get; init; }

    /// <summary>Time allowed for the spawned Blender worker to connect back locally.</summary>
    public TimeSpan SpawnTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Deadline for a peer to complete the token handshake (anti-slow-loris, A1).</summary>
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Cap on connections still in their pre-auth handshake; excess is dropped (A1).</summary>
    public int MaxPendingHandshakes { get; init; } = 8;

    /// <summary>
    /// Turn a <c>host:port</c> bind string into an endpoint, REFUSING any wildcard/all-
    /// interfaces or public address. The desktop operator supplies a concrete private-network address
    /// (RFC1918 or IPv6 ULA), or loopback; <c>0.0.0.0</c>, <c>::</c>, <c>*</c> and <c>+</c> are rejected
    /// so the launcher can never be exposed on a public interface (FR-003, SC-003).
    /// </summary>
    public static IPEndPoint ResolveBind(string bind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bind);
        var colon = bind.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(bind[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"bind '{bind}' is not host:port", nameof(bind));
        }

        var host = bind[..colon].Trim().Trim('[', ']');
        if (host is "*" or "+" or "0.0.0.0" or "::" || host.Length == 0)
        {
            throw new ArgumentException(
                $"refusing wildcard bind host '{host}' — bind a specific private-network address (FR-003)", nameof(bind));
        }

        var address = IPAddress.TryParse(host, out var parsed)
            ? parsed
            : Array.Find(
                  Dns.GetHostAddresses(host),
                  a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
              ?? throw new ArgumentException($"bind host '{host}' did not resolve to an IP", nameof(bind));

        ValidateBindAddress(address);
        return new IPEndPoint(address, port);
    }

    /// <summary>Also enforced at Start so directly constructed options cannot bypass C-03.</summary>
    public static void ValidateBindAddress(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var bytes = normalized.GetAddressBytes();
        var allowed = IPAddress.IsLoopback(normalized)
            || (normalized.AddressFamily == AddressFamily.InterNetwork
                && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)))
            || (normalized.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc);
        if (!allowed)
        {
            throw new ArgumentException("launcher bind must be loopback, RFC1918 or IPv6 ULA (C-03)", nameof(address));
        }
    }
}
