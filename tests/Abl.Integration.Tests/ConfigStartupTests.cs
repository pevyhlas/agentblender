using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 FR-002 through the built programs (unit lane: no Blender ever runs). The daemon
/// refuses an invalid or untrusted config file before binding (AC-05, AC-07) and binds the
/// port the file names; the CLI reports sources (AC-04) and refuses the same file. Every
/// process gets a scrubbed environment: no inherited <c>ABL_*</c>, a temp <c>ABL_CONFIG</c>.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ConfigStartupTests : IDisposable
{
    private static readonly string Configuration = AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
    private readonly string _root;

    public ConfigStartupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "abl-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string ConfigPath => Path.Combine(_root, "config.json");

    [Fact]
    public async Task DaemonRefusesAnUnknownKeyNamingFileKeyAndCheck()
    {
        WriteConfig("""{"version":1,"daemon":{"prot":7811}}""");
        var (code, _, stderr) = await RunAsync(CliDll, DaemonRun, TimeSpan.FromSeconds(30));

        Assert.Equal(78, code);
        Assert.Contains("config.file", stderr);
        Assert.Contains(ConfigPath, stderr);
        Assert.Contains("unknown key 'daemon.prot'", stderr);
    }

    [Fact]
    public async Task DaemonRefusesACommentWithItsPosition()
    {
        WriteConfig("{\n  \"version\": 1 // pinned\n}");
        var (code, _, stderr) = await RunAsync(CliDll, DaemonRun, TimeSpan.FromSeconds(30));

        Assert.Equal(78, code);
        Assert.Contains($"config.file: config file {ConfigPath}: not strict JSON at line 2", stderr);
    }

    [Fact]
    public async Task DaemonAndCliRefuseAWorldWritableConfig()
    {
        WriteConfig("""{"version":1}""");
        File.SetUnixFileMode(ConfigPath, (UnixFileMode)0b110_110_110);

        var (daemonCode, _, daemonErr) = await RunAsync(CliDll, DaemonRun, TimeSpan.FromSeconds(30));
        Assert.Equal(78, daemonCode);
        Assert.Contains("refusing to load", daemonErr);
        Assert.Contains("writable by group or others (mode 0666)", daemonErr);

        var (cliCode, _, cliErr) = await RunAsync(CliDll, ["session", "list"], TimeSpan.FromSeconds(30));
        Assert.Equal(1, cliCode);
        Assert.StartsWith("abl: config.file:", cliErr);
    }

    [Fact]
    public async Task DaemonBindsThePortTheFileNames()
    {
        var port = FreePort();
        WriteConfig($$"""
            {
              "version": 1,
              "blender": { "path": "{{Path.Combine(_root, "no-blender")}}" },
              "daemon": { "port": {{port}} },
              "paths": { "store": "{{Path.Combine(_root, "store")}}", "renders": "{{Path.Combine(_root, "renders")}}" }
            }
            """);
        using var daemon = Process.Start(Start(CliDll, DaemonRun))!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                Assert.False(daemon.HasExited, "daemon exited during startup");
                try
                {
                    if ((await http.GetAsync("/healthz", deadline.Token)).IsSuccessStatusCode) break;
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(100, deadline.Token);
            }

            // The CLI follows the same file to the same port (no ABL_URL set).
            var (code, stdout, stderr) = await RunAsync(CliDll, ["session", "list"], TimeSpan.FromSeconds(30));
            Assert.True(code == 0, stderr);
            Assert.Equal("", stdout.Trim());
            Assert.True(Directory.Exists(Path.Combine(_root, "store", "objects")), "the file's paths.store is the store");
        }
        finally
        {
            daemon.Kill(entireProcessTree: true);
            await daemon.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task CliConfigShowReportsEnvOverFile()
    {
        // AC-04 through the real CLI.
        WriteConfig("""{"version":1,"daemon":{"port":7000},"paths":{"store":"~/abl-store"}}""");
        var (code, stdout, stderr) = await RunAsync(
            CliDll,
            ["config", "show", "--json"],
            TimeSpan.FromSeconds(30),
            // An explicit (missing) Blender keeps the test hermetic: no real Blender is discovered or probed.
            new() { ["ABL_PORT"] = "7100", ["ABL_BLENDER"] = Path.Combine(_root, "no-blender") });

        Assert.True(code == 0, stderr);
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal(ConfigPath, json["file"]!.GetValue<string>());
        Assert.True(json["fileLoaded"]!.GetValue<bool>());
        Assert.Equal("env", json["effective"]!["daemon.port"]!["source"]!.GetValue<string>());
        Assert.Equal(7100, json["effective"]!["daemon.port"]!["value"]!.GetValue<int>());
        Assert.Equal("file", json["effective"]!["paths.store"]!["source"]!.GetValue<string>());
        Assert.Equal(Path.Combine(_root, "home", "abl-store"), json["effective"]!["paths.store"]!["value"]!.GetValue<string>());

        var (pathCode, pathOut, _) = await RunAsync(CliDll, ["config", "path"], TimeSpan.FromSeconds(30));
        Assert.Equal(0, pathCode);
        Assert.Equal(ConfigPath, pathOut.Trim());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // 008 T031: the daemon is `abl daemon run`; its state dir follows HOME (the test's own).
    private static readonly string[] DaemonRun = ["daemon", "run"];

    private static string CliDll => Path.Combine(RepoRoot(), "src/Abl.Cli/bin", Configuration, "net10.0/abl.dll");

    private void WriteConfig(string content)
    {
        File.WriteAllText(ConfigPath, content);
        File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private ProcessStartInfo Start(string dll, string[] args, Dictionary<string, string>? env = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("ABL_", StringComparison.Ordinal)
            || k.StartsWith("OTEL_", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(name);
        }

        Directory.CreateDirectory(Path.Combine(_root, "home"));
        start.Environment["HOME"] = Path.Combine(_root, "home");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(_root, "state"); // plan M10: never the user's state dir
        start.Environment["ABL_CONFIG"] = ConfigPath;
        foreach (var (key, value) in env ?? []) start.Environment[key] = value;
        return start;
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string dll, string[] args, TimeSpan timeout, Dictionary<string, string>? env = null)
    {
        using var process = Process.Start(Start(dll, args, env))!;
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
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "agentblender.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
