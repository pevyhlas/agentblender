using Abl.Cli.Host;
using Abl.Core.Config;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T035/T039, AC-22: the systemd user unit and launchd agent are golden; they start the
/// daemon through <c>env -i</c> with only the login identity names and <c>DOTNET_ROOT</c> (the
/// running runtime's root, a Homebrew Cellar root made stable), so the service manager's own
/// environment never reaches it; no <c>ABL_*</c> key or secret is ever written; and the service
/// managers are driven through absolute tool paths only, with launchd falling back from
/// <c>gui/</c> to <c>user/</c> and both reporting a job that is down after a failure.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ServiceUnitTests : IDisposable
{
    private const string Home = "/home/u";
    private const string Log = "/home/u/.local/state/agentblender/daemon-0123456789ab.log";
    private const string State = "/home/u/.local/state/agentblender";

    private static readonly KeyValuePair<string, string>[] Identity =
        [new("HOME", Home), new("USER", "u"), new("LOGNAME", "u"), new("PATH", "/usr/bin:/bin")];

    private static readonly ServiceDefinition HomeDotnet = ServiceUnits.Define(
        "/home/u/.dotnet/dotnet", ["/home/u/.local/share/agentblender/cli/abl.dll"], "/home/u/.dotnet", Log, State, Identity);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-svc-" + Guid.NewGuid().ToString("N")[..8]);

    public ServiceUnitTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheSystemdUnitIsGolden()
    {
        Assert.Equal(
            """
            # AgentBlender daemon: a systemd user unit written by `abl daemon install-service`.
            # Run that command again after moving abl or its .NET runtime;
            # `abl daemon uninstall-service` removes it. The configuration comes from the config
            # file (`abl config path`): the daemon starts through `env -i` with only the
            # variables named on the ExecStart line (as at install, plus DOTNET_ROOT), so
            # neither the user manager's environment nor a shell's ABL_* variables reach it.
            #
            # The daemon binds 127.0.0.1 only. Do not put a proxy in front of it.

            [Unit]
            Description=AgentBlender daemon (warm Blender workers; MCP and REST on loopback)
            After=network.target

            [Service]
            Type=exec
            ExecStart=/usr/bin/env -i HOME=/home/u USER=u LOGNAME=u PATH=/usr/bin:/bin DOTNET_ROOT=/home/u/.dotnet /home/u/.dotnet/dotnet /home/u/.local/share/agentblender/cli/abl.dll daemon run --log-file /home/u/.local/state/agentblender/daemon-0123456789ab.log
            WorkingDirectory=/home/u/.local/state/agentblender
            Restart=on-failure
            RestartSec=5
            # Blender workers are children: stopping takes the whole control group.
            KillMode=control-group
            TimeoutStopSec=20
            # Defense in depth: the daemon's own startup guard, which refuses any non-loopback
            # bind, is the load-bearing control. A user manager cannot program the IP firewall,
            # so systemd logs that it ignores these two lines; they apply to a system unit only.
            IPAddressDeny=any
            IPAddressAllow=localhost

            [Install]
            WantedBy=default.target

            """.ReplaceLineEndings("\n"),
            ServiceUnits.Systemd(HomeDotnet));
    }

    [Fact]
    public void TheLaunchdPlistIsGolden()
    {
        var mac = ServiceUnits.Define(
            "/Users/u/.dotnet/tools/abl", [], "/opt/homebrew/opt/dotnet/libexec",
            "/Users/u/.local/state/agentblender/daemon-0123456789ab.log", "/Users/u/.local/state/agentblender",
            [new("HOME", "/Users/u"), new("PATH", "/usr/bin:/bin"), new("TMPDIR", "/var/folders/xy/T/")]);
        Assert.Equal(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <!-- AgentBlender daemon: a launchd agent written by `abl daemon install-service`.
                 Run that command again after moving abl or its .NET runtime;
                 `abl daemon uninstall-service` removes it. The configuration comes from the
                 config file (`abl config path`): the daemon starts through `env -i` with only
                 the variables in ProgramArguments (as at install, plus DOTNET_ROOT), so no
                 launchctl setenv value reaches it. The daemon binds 127.0.0.1 only. -->
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>dev.agentblender.daemon</string>
              <key>ProgramArguments</key>
              <array>
                <string>/usr/bin/env</string>
                <string>-i</string>
                <string>HOME=/Users/u</string>
                <string>PATH=/usr/bin:/bin</string>
                <string>TMPDIR=/var/folders/xy/T/</string>
                <string>DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec</string>
                <string>/Users/u/.dotnet/tools/abl</string>
                <string>daemon</string>
                <string>run</string>
                <string>--log-file</string>
                <string>/Users/u/.local/state/agentblender/daemon-0123456789ab.log</string>
              </array>
              <key>WorkingDirectory</key>
              <string>/Users/u/.local/state/agentblender</string>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <dict>
                <key>SuccessfulExit</key>
                <false/>
              </dict>
              <key>ProcessType</key>
              <string>Background</string>
              <key>ExitTimeOut</key>
              <integer>20</integer>
              <key>StandardOutPath</key>
              <string>/Users/u/.local/state/agentblender/launchd/daemon.out</string>
              <key>StandardErrorPath</key>
              <string>/Users/u/.local/state/agentblender/launchd/daemon.out</string>
            </dict>
            </plist>

            """.ReplaceLineEndings("\n"),
            ServiceUnits.Launchd(mac));
    }

    [Fact]
    public void UnitsRoundTripAwkwardPaths()
    {
        // Spaces, quotes, a backslash, systemd specifiers (%) and variables ($), and XML
        // markup all come back exactly as written.
        var awkward = ServiceUnits.Define(
            "/opt/my tools/dotnet", ["/srv/a \"b\"/c\\d/abl.dll"], "/opt/my tools/100%$$x", "/s/$HOME/x&<y>.log", "/s/state dir",
            [new("HOME", "/home/a b"), new("PATH", "/opt/100%/bin:$PATH")]);

        var unit = ServiceUnits.Systemd(awkward);
        Assert.Equal(awkward.ExecStart, ServiceUnits.ReadSystemdExecStart(unit));
        Assert.Equal("/opt/my tools/100%$$x", ServiceUnits.ReadSystemdDotnetRoot(unit));
        Assert.Contains("\"/opt/my tools/dotnet\"", unit);
        Assert.Contains("$$HOME", unit);
        Assert.Equal(
            new Dictionary<string, string> { ["HOME"] = "/home/a b", ["PATH"] = "/opt/100%/bin:$PATH", ["DOTNET_ROOT"] = "/opt/my tools/100%$$x" },
            ServiceUnits.EnvironmentOf(ServiceUnits.ReadSystemdExecStart(unit)));
        Assert.Equal(["/opt/my tools/dotnet", "/srv/a \"b\"/c\\d/abl.dll", "daemon", "run", "--log-file", "/s/$HOME/x&<y>.log"],
            ServiceUnits.CommandOf(ServiceUnits.ReadSystemdExecStart(unit)));

        var plist = ServiceUnits.Launchd(awkward);
        Assert.Equal(awkward.ExecStart, ServiceUnits.ReadLaunchdExecStart(plist));
        Assert.Equal("/opt/my tools/100%$$x", ServiceUnits.ReadLaunchdDotnetRoot(plist));
        Assert.Equal("/s/$HOME/x&<y>.log", ServiceUnits.LogFileOf(ServiceUnits.ReadLaunchdExecStart(plist)));
        Assert.Equal("/s/state dir/launchd/daemon.out", ServiceUnits.ReadLaunchdOutput(plist));
    }

    [Fact]
    public void EveryUnitStartsTheDaemonThroughEnvDashIWithOnlyItsOwnVariables()
    {
        // T035 review round 1, eng-security MEDIUM-1: the service manager's environment
        // (environment.d, an imported login environment, launchctl setenv) never reaches the
        // daemon: ExecStart begins with `/usr/bin/env -i`, and the only variables are the
        // definition's identity names and DOTNET_ROOT.
        foreach (var argv in new[] { ServiceUnits.ReadSystemdExecStart(ServiceUnits.Systemd(HomeDotnet)), ServiceUnits.ReadLaunchdExecStart(ServiceUnits.Launchd(HomeDotnet)) })
        {
            Assert.Equal(["/usr/bin/env", "-i"], argv!.Take(2));
            Assert.Equal(["HOME", "USER", "LOGNAME", "PATH", "DOTNET_ROOT"], ServiceUnits.EnvironmentOf(argv)!.Keys);
            Assert.Equal("/home/u/.dotnet/dotnet", ServiceUnits.CommandOf(argv)![0]);
        }

        Assert.DoesNotContain("Environment=", ServiceUnits.Systemd(HomeDotnet));
        Assert.DoesNotContain("EnvironmentVariables", ServiceUnits.Launchd(HomeDotnet));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void AControlCharacterOrLineSeparatorIsNeverWrittenIntoAUnit(string separator)
    {
        // eng-security LOW-1 (round 1): U+2028/U+2029 are not char.IsControl, but
        // ReplaceLineEndings turns them into a new unit line.
        var injected = ServiceUnits.Define("/usr/bin/dotnet", [$"/a.dll{separator}ExecStartPre=/bin/evil"], "/usr/lib/dotnet", Log, State, Identity);
        Assert.Throws<ArgumentException>(() => ServiceUnits.Systemd(injected));
        Assert.Throws<ArgumentException>(() => ServiceUnits.Launchd(injected));

        var state = ServiceUnits.Define("/usr/bin/dotnet", [], "/usr/lib/dotnet", Log, $"/s{separator}ExecStartPre=/bin/evil", Identity);
        Assert.Throws<ArgumentException>(() => ServiceUnits.Systemd(state));
    }

    [Fact]
    public void AProgramThatEnvWouldTakeAsAnAssignmentIsRefused()
    {
        var program = ServiceUnits.Define("/opt/a=b/abl", [], "/usr/lib/dotnet", Log, State, Identity);
        Assert.Throws<ArgumentException>(() => ServiceUnits.Systemd(program));
        var name = ServiceUnits.Define("/usr/bin/abl", [], "/usr/lib/dotnet", Log, State, [new("BAD NAME", "x")]);
        Assert.Throws<ArgumentException>(() => ServiceUnits.Launchd(name));
    }

    [Fact]
    public void DotnetRootPrefersTheRunningRuntimeOverAnInheritedValue()
    {
        // N4: the root of the runtime that runs abl, not an inherited DOTNET_ROOT naming another install.
        var (root, problem) = DotnetRoot.Resolve("/home/u/.dotnet/shared/Microsoft.NETCore.App/10.0.1/", "/opt/other-dotnet", _ => false);
        Assert.Null(problem);
        Assert.Equal("/home/u/.dotnet", root);

        // A runtime dir of another shape falls back to the inherited value (the AC-22 shape).
        Assert.Equal("/home/u/.dotnet", DotnetRoot.Resolve("/somewhere/odd/", "/home/u/.dotnet/", _ => false).Root);
        Assert.NotNull(DotnetRoot.Resolve("/somewhere/odd/", null, _ => false).Problem);
        Assert.NotNull(DotnetRoot.Resolve("/somewhere/odd/", "relative/dotnet", _ => false).Problem);
    }

    [Theory]
    [InlineData("/opt/homebrew/Cellar/dotnet/10.0.100/libexec", true, "/opt/homebrew/opt/dotnet/libexec")]
    [InlineData("/opt/homebrew/Cellar/dotnet/10.0.100/libexec/dotnet", true, "/opt/homebrew/opt/dotnet/libexec/dotnet")]
    [InlineData("/usr/local/Cellar/dotnet@10/10.0.100/libexec", true, "/usr/local/opt/dotnet@10/libexec")]
    [InlineData("/opt/homebrew/Cellar/dotnet/10.0.100/libexec", false, "/opt/homebrew/Cellar/dotnet/10.0.100/libexec")]
    [InlineData("/home/u/.dotnet", true, "/home/u/.dotnet")]
    public void AHomebrewCellarRootBecomesItsStableOptLink(string path, bool linkExists, string expected) =>
        Assert.Equal(expected, DotnetRoot.Stable(path, dir => linkExists && dir.EndsWith("/libexec", StringComparison.Ordinal)));

    [Fact]
    public void ADerivedCellarRuntimeRootIsMadeStable() =>
        Assert.Equal(
            "/opt/homebrew/opt/dotnet/libexec",
            DotnetRoot.Resolve("/opt/homebrew/Cellar/dotnet/10.0.100/libexec/shared/Microsoft.NETCore.App/10.0.1/", null,
                dir => dir == "/opt/homebrew/opt/dotnet/libexec").Root);

    [Fact]
    public void ThePlanForAHomeDotnetRootHasOnlyDotnetRootAndWarnsAboutWhatStaysBehind()
    {
        // AC-22: DOTNET_ROOT=$HOME/.dotnet; this shell's ABL_* values (and a token) never reach
        // the unit, the log follows the store the service resolves from the config file, and
        // nothing is written (the plan is computed only).
        var fs = new FakeConfigFileSystem().File(
            "/home/u/.config/agentblender/config.json", """{"version":1,"worker":{"envPassthrough":["CUDA_VISIBLE_DEVICES"]}}""");
        var shell = new ConfigInputs
        {
            Env = new Dictionary<string, string>
            {
                ["HOME"] = Home,
                ["PATH"] = "/usr/bin",
                ["DOTNET_ROOT"] = "/home/u/.dotnet",
                ["ABL_STORE"] = "/tmp/elsewhere",
                ["ABL_DESKTOP_TOKEN"] = "s3cret-token",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4318",
            },
            Home = Home,
            FileSystem = fs,
        };

        var plan = ServicePlan.For(shell, "/home/u/.dotnet/dotnet", ["/home/u/app/abl.dll"], "/odd/runtime/", "/home/u/.dotnet", _ => false, HostPlatform.Linux);

        Assert.Null(plan.Error);
        var text = plan.Text!;
        Assert.Contains(" DOTNET_ROOT=/home/u/.dotnet /home/u/.dotnet/dotnet ", text);
        Assert.DoesNotContain("ABL_STORE", text[text.IndexOf("[Service]", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("s3cret", plan.Text);
        Assert.DoesNotContain("/tmp/elsewhere", plan.Text);
        Assert.Equal(
            Abl.Core.Lifecycle.DaemonPaths.LogFile(State, "/home/u/.local/share/agentblender/store"), plan.Definition!.LogFile);

        // The variables the config was resolved with are exactly the unit's (HOME and this
        // shell's PATH), then DOTNET_ROOT: nothing the plan did not model reaches the service.
        Assert.Equal(
            ["/usr/bin/env", "-i", "HOME=/home/u", "PATH=/usr/bin", "DOTNET_ROOT=/home/u/.dotnet",
             "/home/u/.dotnet/dotnet", "/home/u/app/abl.dll", "daemon", "run", "--log-file", plan.Definition.LogFile],
            plan.Definition.ExecStart);
        Assert.Equal(plan.Definition.ExecStart, ServiceUnits.ReadSystemdExecStart(text));
        Assert.Contains(plan.Warnings, w => w.StartsWith("ABL_DESKTOP_TOKEN, ABL_STORE, OTEL_EXPORTER_OTLP_ENDPOINT are set in this shell", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("worker.envPassthrough names CUDA_VISIBLE_DEVICES", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("blender.path is not set in the config file", StringComparison.Ordinal));
        Assert.All(plan.Warnings, w => Assert.DoesNotContain("s3cret", w));

        var plist = ServicePlan.For(shell, "/home/u/.dotnet/dotnet", ["/home/u/app/abl.dll"], "/odd/runtime/", "/home/u/.dotnet", _ => false, HostPlatform.MacOS).Text!;
        Assert.Equal("/home/u/.dotnet", ServiceUnits.ReadLaunchdDotnetRoot(plist));
        Assert.DoesNotContain("ABL_", plist[plist.IndexOf("<plist", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void ThePlanRefusesAConfigurationTheServiceCouldNotStartWith()
    {
        var fs = new FakeConfigFileSystem().File("/home/u/.config/agentblender/config.json", """{"version":1,"daemon":{"prot":1}}""");
        var plan = ServicePlan.For(
            new ConfigInputs { Env = new Dictionary<string, string> { ["HOME"] = Home }, Home = Home, FileSystem = fs },
            "/usr/bin/dotnet", [], "/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.1", null, _ => false, HostPlatform.Linux);

        Assert.Equal("config.file", plan.Error?.Check);
        Assert.StartsWith("the service would refuse to start:", plan.Error!.Message);
        Assert.Null(plan.Text);
    }

    [Fact]
    public void SystemdIsDrivenThroughSystemctlUserAndBootOutQueuesAStop()
    {
        var calls = new List<string>();
        var unit = Path.Combine(_root, "systemd", "user", ServiceUnits.SystemdName);
        var systemd = new SystemdUserService(unit, (program, args) =>
        {
            Assert.True(Path.IsPathFullyQualified(program), program);
            calls.Add(string.Join(' ', args));
            return args.Contains("show")
                ? (0, "LoadState=loaded\nActiveState=active\nMainPID=4242\n", "")
                : (0, "", "");
        });

        Assert.False(systemd.Query().Installed);
        Assert.Empty(systemd.Install(ServiceUnits.Systemd(HomeDotnet), enableAtLogin: true));
        Assert.Equal(ServiceUnits.Systemd(HomeDotnet), File.ReadAllText(unit));

        var state = systemd.Query();
        Assert.True(state is { Installed: true, Loaded: true, Active: true, MainPid: 4242 });
        Assert.Equal(Log, state.LogFile);
        Assert.Equal("/home/u/.dotnet", state.DotnetRoot);
        Assert.True(state.Runs(4242));
        Assert.False(state.Runs(4243));

        Assert.True(systemd.Start().Ok);
        Assert.True(systemd.Stop().Ok);
        Assert.True(systemd.BootOut().Ok);
        Assert.Equal(
            ["--user daemon-reload", "--user enable abl-daemon.service", "--user start abl-daemon.service",
             "--user stop abl-daemon.service", "--user stop --no-block abl-daemon.service"],
            calls.Where(c => !c.Contains(" show ", StringComparison.Ordinal)));

        Assert.Equal([unit], systemd.Uninstall());
        Assert.False(File.Exists(unit));
    }

    [Fact]
    public void AFailedSystemctlIsReportedWithItsOutput()
    {
        var systemd = new SystemdUserService(Path.Combine(_root, "u.service"), (_, args) =>
            args.Contains("show") ? null : (1, "", "Failed to connect to bus: No medium found"));

        var state = systemd.Query();
        Assert.False(state.Loaded || state.Active);
        var outcome = systemd.Start();
        Assert.False(outcome.Ok);
        Assert.Equal("systemctl --user start abl-daemon.service exited with code 1: Failed to connect to bus: No medium found", outcome.Error);
    }

    [Theory]
    [InlineData("ActiveState=activating\nSubState=auto-restart\nResult=exit-code\nMainPID=0\n", "ActiveState=activating, SubState=auto-restart, Result=exit-code")]
    [InlineData("ActiveState=failed\nSubState=failed\nResult=start-limit-hit\nMainPID=0\n", "ActiveState=failed, SubState=failed, Result=start-limit-hit")]
    [InlineData("ActiveState=active\nSubState=running\nResult=success\nMainPID=4242\n", null)]
    [InlineData("ActiveState=inactive\nSubState=dead\nResult=success\nMainPID=0\n", null)]
    public void SystemdReportsAJobThatIsDownAfterAFailure(string shown, string? failure)
    {
        // T035 review round 1, eng-code MEDIUM-1: what `start` fails fast on.
        var systemd = new SystemdUserService(Path.Combine(_root, "u.service"), (_, args) =>
            args.Contains("show") ? (0, "LoadState=loaded\n" + shown, "") : (0, "", ""));
        Assert.Equal(failure, systemd.Query().Failure);
    }

    [Theory]
    [InlineData("state = running\n\tpid = 777\n\tlast exit code = 1\n", null)]
    [InlineData("state = not running\n\tlast exit code = 78: EX_CONFIG\n", "last exit code = 78: EX_CONFIG")]
    [InlineData("state = not running\n\tlast exit code = (never exited)\n", null)]
    [InlineData("state = not running\n\tlast exit code = 0\n", null)]
    [InlineData("state = not running\n\tlast terminating signal = Abort trap: 6\n", "last terminating signal = Abort trap: 6")]
    public void LaunchdReportsAJobThatIsDownAfterAFailure(string printed, string? failure)
    {
        var plist = Path.Combine(_root, "state", "a.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
        File.WriteAllText(plist, ServiceUnits.Launchd(HomeDotnet));
        var launchd = new LaunchdAgent(plist, Path.Combine(_root, "LaunchAgents", "a.plist"), 501, (_, args) =>
            args[0] == "print" && args[1].StartsWith("gui/", StringComparison.Ordinal) ? (0, printed, "") : (113, "", "Could not find service"));

        var state = launchd.Query();
        Assert.Equal(failure, state.Failure);
        Assert.Equal(ServiceUnits.LaunchdOutput(State), state.OutputFile);
    }

    [Fact]
    public void LaunchdBootstrapsIntoTheGuiDomainOrFallsBackToUser()
    {
        var calls = new List<string>();
        var loaded = false;
        var launchd = new LaunchdAgent(Path.Combine(_root, "state", "a.plist"), Path.Combine(_root, "LaunchAgents", "a.plist"), 501, (program, args) =>
        {
            Assert.Equal("/bin/launchctl", program);
            calls.Add(string.Join(' ', args));
            return args[0] switch
            {
                "print" when loaded && args[1] == "user/501/dev.agentblender.daemon" => (0, "state = running\n\tpid = 777\n", ""),
                "print" => (113, "", "Could not find service"),
                "bootstrap" when args[1] == "gui/501" => (125, "", "Bootstrap failed: 125: Domain does not support specified action"),
                "bootstrap" => Loaded(),
                _ => (0, "", ""),
            };

            (int, string, string)? Loaded()
            {
                loaded = true;
                return (0, "", "");
            }
        });

        Assert.Equal("no launchd plist is installed", launchd.Start().Error);

        // Without --enable-at-login the plist lives in the state dir; with it, in LaunchAgents.
        launchd.Install(ServiceUnits.Launchd(HomeDotnet), enableAtLogin: false);
        Assert.True(File.Exists(Path.Combine(_root, "state", "a.plist")));
        launchd.Install(ServiceUnits.Launchd(HomeDotnet), enableAtLogin: true);
        Assert.True(File.Exists(Path.Combine(_root, "LaunchAgents", "a.plist")));
        Assert.False(File.Exists(Path.Combine(_root, "state", "a.plist")));

        var started = launchd.Start();
        Assert.True(started.Ok);
        Assert.Equal("user/501", started.Domain);
        Assert.Contains($"bootstrap gui/501 {Path.Combine(_root, "LaunchAgents", "a.plist")}", calls);

        var state = launchd.Query();
        Assert.True(state is { Loaded: true, Active: true, MainPid: 777, Domain: "user/501" });
        Assert.Equal(Log, state.LogFile);

        // A loaded job is kickstarted, and stop and boot-out both boot it out of its domain.
        Assert.True(launchd.Start().Ok);
        Assert.Contains("kickstart user/501/dev.agentblender.daemon", calls);
        Assert.True(launchd.BootOut().Ok);
        Assert.Contains("bootout user/501/dev.agentblender.daemon", calls);
    }

    [Fact]
    public void ServiceToolsRunOnlyFromAnAbsolutePath()
    {
        // Like the process-table rule: a bare name would be resolved through the cwd and PATH.
        File.WriteAllText(Path.Combine(_root, "systemctl"), "#!/bin/sh\nexit 0\n");
        Assert.Null(ServiceManagers.Run("systemctl", ["--user", "show"]));
        Assert.Null(ServiceManagers.Run(Path.Combine(_root, "missing"), []));
        Assert.Equal(0, ServiceManagers.Run("/bin/sh", ["-c", "exit 0"])?.ExitCode);
    }
}
