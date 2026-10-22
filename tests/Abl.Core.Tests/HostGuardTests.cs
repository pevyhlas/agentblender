using Abl.Daemon;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// FR-001 (008 phase 0), AC-01..03 decision matrix: the guard checks the Host hostname
/// (never the port) and requires any Origin to be the same loopback origin. The live
/// pipeline (every route, real Kestrel socket) is HostGuardSocketTests in the
/// integration project.
/// </summary>
public class HostGuardTests
{
    [Theory]
    [InlineData("127.0.0.1:7811")]
    [InlineData("localhost:7811")]
    [InlineData("LOCALHOST:7811")]
    [InlineData("[::1]:7811")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:9000")] // ssh -L 9000:127.0.0.1:7811 — port is deliberately not checked
    public void LoopbackHostWithoutOriginIsAllowed(string host) =>
        Assert.Null(HostGuard.Check(new HostString(host), StringValues.Empty));

    [Theory]
    [InlineData("evil.example:7811")]
    [InlineData("0.0.0.0:7811")]
    [InlineData("127.0.0.1.evil.example:7811")]
    [InlineData("localhost.:7811")]
    [InlineData("127.0.0.2:7811")]
    [InlineData("[::ffff:127.0.0.1]:7811")]
    [InlineData("203.0.113.10:7811")]
    public void NonLoopbackHostIs421(string host) =>
        Assert.Equal(421, HostGuard.Check(new HostString(host), StringValues.Empty));

    [Fact]
    public void MissingHostIs400()
    {
        Assert.Equal(400, HostGuard.Check(default, StringValues.Empty));
        Assert.Equal(400, HostGuard.Check(new HostString(""), StringValues.Empty));
    }

    [Theory]
    [InlineData("127.0.0.1:7811", "http://127.0.0.1:7811")]
    [InlineData("127.0.0.1:7811", "http://localhost:7811")]
    [InlineData("localhost:7811", "http://[::1]:7811")]
    [InlineData("127.0.0.1", "http://127.0.0.1")]
    [InlineData("127.0.0.1:80", "http://127.0.0.1")]
    public void SameLoopbackOriginIsAllowed(string host, string origin) =>
        Assert.Null(HostGuard.Check(new HostString(host), origin));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://evil.example:7811")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("http://localhost:3000")] // another loopback web app
    [InlineData("https://127.0.0.1:7811")]
    [InlineData("http://0.0.0.0:7811")]
    [InlineData("http://127.0.0.1:7811/")]
    [InlineData("http://user@127.0.0.1:7811")]
    [InlineData("http://localhost:abc")]
    [InlineData("http://127.0.0.1.evil.example:7811")]
    [InlineData("http://")]
    public void ForeignOrMalformedOriginIs403(string origin) =>
        Assert.Equal(403, HostGuard.Check(new HostString("127.0.0.1:7811"), origin));

    [Fact]
    public void RepeatedOriginHeaderIs403() =>
        Assert.Equal(403, HostGuard.Check(
            new HostString("127.0.0.1:7811"), new StringValues(["http://127.0.0.1:7811", "http://127.0.0.1:7811"])));

    [Fact]
    public void ForeignHostWinsOverOrigin() =>
        Assert.Equal(421, HostGuard.Check(new HostString("evil.example:7811"), "null"));

    [Theory]
    [InlineData("evil.example:7811", null, 421)]
    [InlineData("127.0.0.1:7811", "https://evil.example", 403)]
    [InlineData("127.0.0.1:7811", "null", 403)]
    public async Task RejectedRequestNeverReachesTheEndpoint(string host, string? origin, int expected)
    {
        var reached = false;
        var logger = new CapturingLogger();
        var guard = new HostGuard(_ => { reached = true; return Task.CompletedTask; }, logger);
        var context = Context(host, origin);

        await guard.InvokeAsync(context);

        Assert.False(reached);
        Assert.Equal(expected, context.Response.StatusCode);
        var line = Assert.Single(logger.Lines);
        Assert.DoesNotContain("evil", line); // log hygiene: no header values
    }

    [Fact]
    public async Task AllowedRequestReachesTheEndpoint()
    {
        var reached = false;
        var guard = new HostGuard(_ => { reached = true; return Task.CompletedTask; }, new CapturingLogger());
        await guard.InvokeAsync(Context("localhost:7811", null));
        Assert.True(reached);
    }

    [Fact]
    public async Task RejectionLoggingIsRateLimited()
    {
        var logger = new CapturingLogger();
        var guard = new HostGuard(_ => Task.CompletedTask, logger);
        for (var i = 0; i < 5; i++) await guard.InvokeAsync(Context("evil.example:7811", null));
        Assert.Single(logger.Lines);
    }

    private static DefaultHttpContext Context(string host, string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/ops/session_open";
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class CapturingLogger : ILogger<HostGuard>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
