using Abl.Core.Config;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T015 (FR-005, plan A6): the worker and version-probe environment allowlists. The
/// spawn-level proof through <c>WorkerSupervisor</c> is <c>WorkerStubTests</c>.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ChildEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-env-" + Guid.NewGuid().ToString("N"));

    public ChildEnvironmentTests() =>
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Dictionary<string, string?> Daemon() => new()
    {
        ["HOME"] = "/home/u",
        ["USER"] = "u",
        ["PATH"] = "/usr/bin",
        ["LANG"] = "C.UTF-8",
        ["LC_ALL"] = "C.UTF-8",
        ["LC_CTYPE"] = "C.UTF-8",
        ["TMPDIR"] = "/tmp",
        ["ANTHROPIC_API_KEY"] = "secret",
        ["ABL_DESKTOP_TOKEN"] = "secret",
        ["OTEL_EXPORTER_OTLP_HEADERS"] = "secret",
        ["LD_PRELOAD"] = "/evil.so",
        ["PYTHONPATH"] = "/evil",
        ["PYTHONHOME"] = "/evil",
        ["PYTHONSTARTUP"] = "/evil.py",
        ["NVIDIA_DRIVER_CAPABILITIES"] = "all",
        ["home"] = "case matters",
    };

    [Fact]
    public void WorkerKeepsOnlyTheAllowlistAndPassthrough()
    {
        var env = Daemon();
        ChildEnvironment.Restrict(env, name => ChildEnvironment.AllowsForWorker(name, ["NVIDIA_DRIVER_CAPABILITIES"]));

        Assert.Equal(
            ["HOME", "LANG", "LC_ALL", "LC_CTYPE", "NVIDIA_DRIVER_CAPABILITIES", "PATH", "TMPDIR", "USER"],
            env.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("/home/u", env["HOME"]);
    }

    [Theory]
    [InlineData("PYTHONPATH")]
    [InlineData("PYTHONHOME")]
    [InlineData("PYTHONSTARTUP")]
    [InlineData("PYTHONSAFEPATH")]
    public void PythonVariablesNeverReachAWorkerEvenWhenListed(string name) =>
        Assert.False(ChildEnvironment.AllowsForWorker(name, [name]));

    [Fact]
    public void ProbeIsNarrowerAndIgnoresPassthrough()
    {
        var env = Daemon();
        ChildEnvironment.Restrict(env, ChildEnvironment.AllowsForProbe);

        Assert.Equal(["HOME", "LANG", "LC_ALL", "LC_CTYPE", "PATH", "TMPDIR"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void VersionProbeRunsWithTheProbeAllowlist()
    {
        // 1b eng-security LOW: no secret in the daemon's environment reaches a probed binary.
        const string canary = "ABL_T015_PROBE_CANARY";
        var envFile = Path.Combine(_root, "env.txt");
        var script = Path.Combine(_root, "blender");
        File.WriteAllText(script, $"#!/bin/sh\nenv > '{envFile}'\nprintf 'Blender 5.2.1\\n\\tbuild hash: 0123456789ab\\n'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable(canary, "must-not-leak");
        try
        {
            var probe = BlenderDiscovery.RunVersionProbe(script, TimeSpan.FromSeconds(10));

            Assert.Null(probe.Error);
            Assert.Equal(new BlenderVersion(5, 2, 1), probe.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable(canary, null);
        }

        var names = File.ReadAllLines(envFile).Where(l => l.Contains('=')).Select(l => l[..l.IndexOf('=')]).ToList();
        Assert.DoesNotContain(canary, names);
        Assert.Contains("PATH", names);
        Assert.All(names, name => Assert.True(
            ChildEnvironment.AllowsForProbe(name) || name is "PWD" or "OLDPWD" or "SHLVL" or "_", name));
    }
}
