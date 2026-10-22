using Microsoft.Extensions.Primitives;

namespace Abl.Daemon;

/// <summary>
/// FR-001 (008 phase 0): DNS-rebinding and cross-origin guard, first in the pipeline and
/// with no path exemption (<c>/mcp</c>, <c>/api/ops/*</c>, <c>/healthz</c>, unknown paths).
/// <list type="bullet">
/// <item><c>Host</c> missing → 400; hostname not <c>127.0.0.1</c>, <c>localhost</c> or
/// <c>[::1]</c> → 421. Only the hostname is checked, not the port, so a port-remapping
/// tunnel (<c>ssh -L 9000:127.0.0.1:7811</c>) keeps working; rebinding is defeated by the
/// hostname alone.</item>
/// <item>An <c>Origin</c> header, when present, must be exactly
/// <c>http://&lt;loopback name&gt;[:port]</c> on the same port as <c>Host</c>; anything else
/// (<c>null</c>, <c>https://…</c>, another loopback port, a foreign site) → 403. Agents,
/// the CLI and MCP HTTP clients send no <c>Origin</c> and pass unchanged.</item>
/// </list>
/// Rejections log at Warning at most once a minute and never echo header values.
/// </summary>
public sealed class HostGuard(RequestDelegate next, ILogger<HostGuard> logger)
{
    private static readonly string[] LoopbackNames = ["127.0.0.1", "localhost", "[::1]"];
    private static readonly long LogIntervalTicks = TimeSpan.FromMinutes(1).Ticks;
    private long _lastLogTicks = long.MinValue;
    private long _suppressed;

    public Task InvokeAsync(HttpContext context)
    {
        var status = Check(context.Request.Host, context.Request.Headers.Origin);
        if (status is null) return next(context);
        Log(status.Value);
        context.Response.StatusCode = status.Value;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(status switch
        {
            StatusCodes.Status400BadRequest => "missing Host header\n",
            StatusCodes.Status421MisdirectedRequest =>
                "agentblender accepts only loopback Host values (127.0.0.1, localhost, [::1])\n",
            _ => "cross-origin requests are not accepted\n",
        });
    }

    /// <summary>Returns the rejection status, or null when the request may proceed.</summary>
    public static int? Check(HostString host, StringValues origin)
    {
        if (!host.HasValue || host.Host.Length == 0) return StatusCodes.Status400BadRequest;
        if (!IsLoopbackName(host.Host)) return StatusCodes.Status421MisdirectedRequest;
        if (origin.Count == 0) return null;
        return origin.Count == 1 && IsSameLoopbackOrigin(origin[0], host)
            ? null
            : StatusCodes.Status403Forbidden;
    }

    private static bool IsLoopbackName(string name) =>
        LoopbackNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsSameLoopbackOrigin(string? origin, HostString host)
    {
        const string scheme = "http://";
        if (origin is null || !origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var authority = origin[scheme.Length..];
        if (authority.Length == 0 || authority.IndexOfAny(['/', '?', '#', '@', '\\']) >= 0) return false;
        var originHost = HostString.FromUriComponent(authority);
        // Canonical form only: rejects an unparseable or padded port ("localhost:abc").
        var canonical = originHost.Port is { } port ? $"{originHost.Host}:{port}" : originHost.Host;
        return string.Equals(authority, canonical, StringComparison.OrdinalIgnoreCase)
            && IsLoopbackName(originHost.Host)
            && (originHost.Port ?? 80) == (host.Port ?? 80);
    }

    private void Log(int status)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastLogTicks);
        if (last != long.MinValue && now - last < LogIntervalTicks)
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }
        if (Interlocked.CompareExchange(ref _lastLogTicks, now, last) != last)
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }
        logger.LogWarning(
            "host guard rejected a request with {Status} ({Reason}); {Suppressed} earlier rejection(s) not logged",
            status,
            status == StatusCodes.Status403Forbidden ? "cross-origin" : "non-loopback or missing Host",
            Interlocked.Exchange(ref _suppressed, 0));
    }
}
