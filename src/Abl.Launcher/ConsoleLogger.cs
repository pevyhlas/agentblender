using Microsoft.Extensions.Logging;

namespace Abl.Launcher;

/// <summary>
/// A tiny stderr/stdout logger so the launcher needs no logging-provider package (it
/// references only Logging.Abstractions). Info and above; exceptions are printed in full.
/// </summary>
internal sealed class ConsoleLogger<T> : ILogger<T>
{
    private static readonly string Category = typeof(T).Name;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {logLevel,-11} {Category}: {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.WriteLine(exception);
        }
    }
}
