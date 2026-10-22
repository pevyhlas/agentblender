using System.Globalization;

namespace Abl.Cli;

/// <summary>Worker deadlines govern operations; a batch has no implicit overall deadline.</summary>
public static class CliHttp
{
    public static HttpClient Create(string baseUrl, string? timeoutSeconds, HttpMessageHandler? handler = null)
    {
        var timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrWhiteSpace(timeoutSeconds))
        {
            if (!double.TryParse(timeoutSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds <= 0 || seconds > int.MaxValue / 1000.0)
            {
                throw new ArgumentException("ABL_HTTP_TIMEOUT_SECONDS must be a positive number of seconds (at most 2147483)");
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        return new HttpClient(handler ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = timeout,
        };
    }
}
