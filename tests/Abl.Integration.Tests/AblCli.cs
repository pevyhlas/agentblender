using System.Diagnostics;

namespace Abl.Integration.Tests;

/// <summary>
/// The built CLI, run as <c>dotnet abl.dll …</c> (008 T031): the daemon has no executable of
/// its own, so every fixture starts it with <c>abl daemon run</c>, with its state dir under the
/// test's own root (plan M10: no test touches the real <c>~/.local/state/agentblender</c>).
/// </summary>
internal static class AblCli
{
    public static string Configuration { get; } = AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";

    public static string RepoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "agentblender.slnx")))
                {
                    return d.FullName;
                }
            }

            throw new DirectoryNotFoundException("repository root (agentblender.slnx) not found above the test dir");
        }
    }

    /// <summary>The built <c>abl.dll</c>.</summary>
    public static string Dll => Path.Combine(RepoRoot, "src/Abl.Cli/bin", Configuration, "net10.0/abl.dll");

    /// <summary><c>dotnet abl.dll &lt;args&gt;</c> with stdout and stderr redirected.</summary>
    public static ProcessStartInfo Start(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Dll);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    /// <summary>Runs <paramref name="start"/> to exit (killed after <paramref name="timeout"/>) and returns its exit code and output.</summary>
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"abl {string.Join(' ', start.ArgumentList.Skip(1))} did not exit within {timeout}");
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary><c>abl daemon run</c> in the foreground, its state dir under <paramref name="root"/>.</summary>
    public static ProcessStartInfo Daemon(string root)
    {
        var start = Start("daemon", "run");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(root, "state");
        return start;
    }
}
