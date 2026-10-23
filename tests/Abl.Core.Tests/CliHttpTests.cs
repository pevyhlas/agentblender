using System.Net;
using Abl.Cli;
using Xunit;

namespace Abl.Core.Tests;

public class CliHttpTests
{
    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(150, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    /// <summary>Answers only by being cancelled, so a deadline is the one way out however loaded the host is.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task DefaultHasNoImplicitDeadlineAndServesTheResponse()
    {
        using var client = CliHttp.Create("http://localhost:7811", null, new DelayedHandler());
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        using var response = await client.PostAsync("/api/ops/batch_run", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ExplicitDeadlineAndCallerCancellationAreHonored()
    {
        using var timed = CliHttp.Create("http://localhost:7811", "0.02", new HangingHandler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timed.GetAsync("/healthz")).WaitAsync(TimeSpan.FromSeconds(30));
        using var indefinite = CliHttp.Create("http://localhost:7811", null, new HangingHandler());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indefinite.GetAsync("/healthz", cancelled.Token));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("not-a-number")]
    public void InvalidDeadlineIsActionable(string value) =>
        Assert.Contains("ABL_HTTP_TIMEOUT_SECONDS",
            Assert.Throws<ArgumentException>(() => CliHttp.Create("http://localhost:7811", value)).Message);
}
