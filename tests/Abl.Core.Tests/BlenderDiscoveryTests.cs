using System.Diagnostics;
using System.Text.RegularExpressions;
using Abl.Core.Config;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T018b, FR-003 / AC-08..10: discovery runs real <c>blender --version</c> probes against
/// fake <c>blender</c> shell scripts under a temp system root and home, with Spotlight
/// stubbed. Nothing here looks at the real <c>/Applications</c>, <c>PATH</c> or Spotlight.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BlenderDiscoveryTests : IDisposable
{
    private const string MacBinary = "Contents/MacOS/Blender";
    private readonly string _root;

    public BlenderDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "abl-disc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(SysRoot);
        Directory.CreateDirectory(Home);

        // The trust walk checks the home itself: do not depend on the runner's umask.
        File.SetUnixFileMode(Home, (UnixFileMode)0b111_101_101);
    }

    private string SysRoot => Path.Combine(_root, "sys");

    private string Home => Path.Combine(_root, "home");

    private string ProbeLog => Path.Combine(_root, "probes.log");

    private List<TimeSpan> SpotlightCalls { get; } = [];

    [Fact]
    public void AC08_ApplicationsBlenderIsSelectedAsDiscovered()
    {
        var app = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1", "a1b2c3d4");

        var config = ResolveWith(Mac());

        Assert.Equal(app, config.Options.BlenderPath);
        Assert.Equal(ConfigSource.Discovered, config[AblConfig.BlenderKey].Source);
        Assert.Null(config[AblConfig.BlenderKey].Error);
        Assert.Equal(new BlenderVersion(5, 2, 1), config.Options.ProbedBlenderVersion);
        var json = config.ToJson();
        Assert.Equal("discovered", json["effective"]![AblConfig.BlenderKey]!["source"]!.GetValue<string>());
        Assert.Equal("reference", json["blender"]!["support"]!.GetValue<string>());
        Assert.Equal("a1b2c3d4", json["blender"]!["buildHash"]!.GetValue<string>());
        Assert.Empty(SpotlightCalls); // a supported fixed-location candidate: Spotlight is never asked
        Assert.Null(config.Options.BlenderProblem); // the spawn gate lets a supported Blender through
    }

    [Fact]
    public void AC09_OnlyAnUnsupportedCandidateFailsBlenderVersionWithFoundAndSupportedVersions()
    {
        var app = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "4.5.3");

        var config = ResolveWith(Mac());

        Assert.Null(config.Options.BlenderPath);
        var blender = config.Blender!;
        Assert.Equal("blender.version", blender.Check);
        Assert.Contains($"Blender 4.5.3 at {app} is not supported (supported 5.2.x, reference 5.2.1)", blender.Problem);
        Assert.Contains(SupportedBlender.DownloadPage, blender.Hint);
        Assert.Contains("blender.allowUnsupported", blender.Hint);
        Assert.StartsWith("blender.version: ", config[AblConfig.BlenderKey].Error);
        Assert.Single(SpotlightCalls); // last resort, bounded
        Assert.Equal(TimeSpan.FromSeconds(2), SpotlightCalls[0]);
    }

    [Fact]
    public void AC10_TheReferenceWinsOverHigherPatchesAndTheRestAreListed()
    {
        var newer = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.4");
        var reference = FakeBlender(Path.Combine(Home, "Applications", "Blender.app", MacBinary), "5.2.1");
        var other = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender 5.2.2.app", MacBinary), "5.2.2");

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Mac());

        Assert.Equal(reference, blender.Path);
        Assert.Equal(BlenderSupport.Reference, blender.Support);
        Assert.Null(blender.Check);
        Assert.Null(blender.Warning);
        Assert.Equal([newer, other], blender.Alternatives.Select(a => a.Path));
        Assert.Equal(["5.2.4", "5.2.2"], blender.Alternatives.Select(a => a.Probe.Version!.ToString()));
    }

    [Fact]
    public void AC10_WithoutTheReferenceTheHighestPatchWinsWithAWarning()
    {
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.2");
        var highest = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender-5.2.5.app", MacBinary), "5.2.5");
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender-6.0.0.app", MacBinary), "6.0.0");

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Mac());

        Assert.Equal(highest, blender.Path);
        Assert.Equal(BlenderSupport.Supported, blender.Support);
        Assert.Contains("the reference is 5.2.1", blender.Warning);
        Assert.Equal(2, blender.Alternatives.Count);
    }

    [Fact]
    public void SpotlightIsTheLastResortOnMacOS()
    {
        var found = FakeBlender(Path.Combine(Home, "Downloads", "Blender.app", MacBinary), "5.2.1");
        var search = Mac() with
        {
            Spotlight = timeout =>
            {
                SpotlightCalls.Add(timeout);
                return [Path.Combine(Home, "Downloads", "Blender.app"), Path.Combine(_root, "gone", "Blender.app")];
            },
        };

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, search);

        Assert.Equal(found, blender.Path);
        Assert.Equal(ConfigSource.Discovered, blender.Source);
        Assert.Single(SpotlightCalls);
    }

    [Fact]
    public void SpotlightHitsOutsideApplicationsAndHomeAreListedButNeverRun()
    {
        // Another local user can plant a bundle with Blender's id on a shared volume.
        var planted = FakeBlender(Path.Combine(_root, "Shared", "Blender.app", MacBinary), "5.2.1");
        var search = Mac() with { Spotlight = _ => [Path.Combine(_root, "Shared", "Blender.app")] };

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, search);

        Assert.Null(blender.Path);
        Assert.Equal("blender.found", blender.Check);
        var listed = Assert.Single(blender.Alternatives);
        Assert.Equal(planted, listed.Path);
        Assert.StartsWith("not probed: Spotlight found it outside /Applications", listed.Probe.Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void ASymlinkedSpotlightHitIsJudgedProbedAndStoredAtItsRealBundle()
    {
        // A link outside the allowed roots to a real bundle under home: the stored path must not
        // go through the link, or whoever owns it could re-point it before the next spawn.
        var real = FakeBlender(Path.Combine(Home, "Downloads", "Blender.app", MacBinary), "5.2.1");
        var shared = Path.Combine(_root, "Shared");
        Directory.CreateDirectory(shared);
        var link = Path.Combine(shared, "x.app");
        Directory.CreateSymbolicLink(link, Path.Combine(Home, "Downloads", "Blender.app"));
        var search = Mac() with { Spotlight = _ => [link] };

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, search);

        Assert.Null(blender.Check);
        Assert.Equal(real, blender.Path);
        Assert.Equal([$"{real} --version"], File.ReadAllLines(ProbeLog));
    }

    [Fact]
    public void ASpotlightHitInADirectoryOthersCanWriteIsNeverRun()
    {
        var dir = Path.Combine(Home, "dropbox");
        var planted = FakeBlender(Path.Combine(dir, "Blender.app", MacBinary), "5.2.1");
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_111);
        var search = Mac() with { Spotlight = _ => [Path.Combine(dir, "Blender.app")] };

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, search);

        Assert.Equal("blender.found", blender.Check);
        var listed = Assert.Single(blender.Alternatives);
        Assert.Equal(planted, listed.Path);
        Assert.Equal($"not probed: {dir} is writable by group or others; fix its mode or set blender.path explicitly", listed.Probe.Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void ASpotlightHitDeepInsideADirectoryOthersCanWriteIsNeverRun()
    {
        // Stock macOS: ~/Public/Drop Box is 0733, so another local user can create their own
        // 0755 evil/Blender.app two levels below it. Every dir up to the home is walked.
        var dropBox = Path.Combine(Home, "Public", "Drop Box");
        var app = Path.Combine(dropBox, "evil", "Blender.app");
        var planted = FakeBlender(Path.Combine(app, MacBinary), "5.2.1");
        File.SetUnixFileMode(dropBox, (UnixFileMode)0b111_011_011);
        var search = Mac() with { Spotlight = _ => [app] };

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, search);

        Assert.Equal("blender.found", blender.Check);
        var listed = Assert.Single(blender.Alternatives);
        Assert.Equal(planted, listed.Path);
        Assert.Equal($"not probed: {dropBox} is writable by group or others; fix its mode or set blender.path explicitly", listed.Probe.Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void ASpotlightHitDeepUnderApplicationsIsWalkedUpToApplicationsItself()
    {
        // /Applications (root:admin 0775) is the anchor and is not checked; every dir below it is.
        var applications = Path.Combine(SysRoot, "Applications");
        var shared = Path.Combine(applications, "Shared");
        var plantedApp = Path.Combine(shared, "evil", "Blender.app");
        var planted = FakeBlender(Path.Combine(plantedApp, MacBinary), "5.2.1");
        var trustedApp = Path.Combine(applications, "Tools", "Sub", "Blender.app");
        var trusted = FakeBlender(Path.Combine(trustedApp, MacBinary), "5.2.1");
        File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111);
        File.SetUnixFileMode(applications, (UnixFileMode)0b111_111_101);
        var search = Mac() with { Spotlight = _ => [plantedApp, trustedApp] };

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, search);

        Assert.Null(blender.Check);
        Assert.Equal(trusted, blender.Path);
        var listed = Assert.Single(blender.Alternatives);
        Assert.Equal(planted, listed.Path);
        Assert.Equal($"not probed: {shared} is writable by group or others; fix its mode or set blender.path explicitly", listed.Probe.Error);
        Assert.Equal([$"{trusted} --version"], File.ReadAllLines(ProbeLog));
    }

    [Fact]
    public void TheHomeAnchorAppliesToFixedCandidatesAndSpotlightHitsAlike()
    {
        // A group-writable ~/Applications refuses the fixed candidate and the same bundle found
        // by Spotlight (listed once); a home others can write refuses everything under it.
        var userApps = Path.Combine(Home, "Applications");
        var app = Path.Combine(userApps, "Blender.app");
        var binary = FakeBlender(Path.Combine(app, MacBinary), "5.2.1");
        File.SetUnixFileMode(userApps, (UnixFileMode)0b111_111_101);
        var mac = BlenderDiscovery.Discover(allowUnsupported: false, Mac() with { Spotlight = _ => [app] });
        Assert.Equal("blender.found", mac.Check);
        var listed = Assert.Single(mac.Alternatives);
        Assert.Equal(binary, listed.Path);
        Assert.Contains($"not probed: {userApps} is writable by group or others", listed.Probe.Error);

        File.SetUnixFileMode(userApps, (UnixFileMode)0b111_101_101);
        var linuxBinary = FakeBlender(Path.Combine(Home, "blender-5.2.1", "blender"), "5.2.1");
        File.SetUnixFileMode(Home, (UnixFileMode)0b111_111_101);
        var macHome = BlenderDiscovery.Discover(allowUnsupported: false, Mac());
        var linuxHome = BlenderDiscovery.Discover(allowUnsupported: false, Linux(pathVariable: null));
        Assert.Contains($"not probed: {Home} is writable by group or others", Assert.Single(macHome.Alternatives).Probe.Error);
        var linuxListed = Assert.Single(linuxHome.Alternatives);
        Assert.Equal(linuxBinary, linuxListed.Path);
        Assert.Contains($"not probed: {Home} is writable by group or others", linuxListed.Probe.Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void EveryHopOfASymlinkChainIsTrustChecked()
    {
        // PATH/blender -> <hop>/blender -> /opt/blender-5.2.1/blender, with <hop> world-writable.
        var real = FakeBlender(Path.Combine(SysRoot, "opt", "blender-5.2.1", "blender"), "5.2.1");
        var hop = Path.Combine(_root, "hop");
        Directory.CreateDirectory(hop);
        File.CreateSymbolicLink(Path.Combine(hop, "blender"), real);
        File.SetUnixFileMode(hop, (UnixFileMode)0b111_111_111);
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "blender"), Path.Combine(hop, "blender"));
        File.SetUnixFileMode(bin, (UnixFileMode)0b111_101_101);

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Linux(pathVariable: bin));

        // The alias is refused; the trusted /opt path to the same binary is still chosen.
        Assert.Null(blender.Check);
        Assert.Equal(real, blender.Path);
        var listed = Assert.Single(blender.Alternatives);
        Assert.Equal(Path.Combine(bin, "blender"), listed.Path);
        Assert.Contains($"not probed: {hop} is writable by group or others", listed.Probe.Error);
    }

    [Fact]
    public void AllowUnsupportedWithNoSupportedBlenderPicksTheHighestUnsupported()
    {
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "4.2.9");
        var higher = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender 5.1.app", MacBinary), "5.1.0");

        var blender = BlenderDiscovery.Discover(allowUnsupported: true, Mac());

        Assert.Equal(higher, blender.Path);
        Assert.True(blender.UnsupportedAllowed);
    }

    [Fact]
    public void ACandidateOthersCanWriteIsNeverRun()
    {
        // macOS: a group-writable bundle; Linux: a world-writable PATH dir.
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        File.SetUnixFileMode(Path.Combine(SysRoot, "Applications", "Blender.app"), (UnixFileMode)0b111_111_101);
        var mac = BlenderDiscovery.Discover(allowUnsupported: false, Mac());
        Assert.Equal("blender.found", mac.Check);
        Assert.Contains("Blender.app is writable by group or others", Assert.Single(mac.Alternatives).Probe.Error);

        var shared = Path.Combine(_root, "shared-bin");
        FakeBlender(Path.Combine(shared, "blender"), "5.2.1");
        File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111);
        var linux = BlenderDiscovery.Discover(allowUnsupported: false, Linux(pathVariable: shared));
        Assert.Equal("blender.found", linux.Check);
        Assert.Contains($"{shared} is writable by group or others", Assert.Single(linux.Alternatives).Probe.Error);

        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void AnUntrustedAliasDoesNotHideATrustedPathToTheSameBinary()
    {
        // A 0775 ~/.local/bin (umask 002 with user-private groups) on PATH links to a 0755
        // ~/blender-5.2.1/blender: the alias is listed as not probed, the real path is chosen.
        var real = FakeBlender(Path.Combine(Home, "blender-5.2.1", "blender"), "5.2.1");
        var localBin = Path.Combine(Home, ".local", "bin");
        Directory.CreateDirectory(localBin);
        var alias = Path.Combine(localBin, "blender");
        File.CreateSymbolicLink(alias, real);
        File.SetUnixFileMode(localBin, (UnixFileMode)0b111_111_101);

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Linux(pathVariable: localBin));

        Assert.Null(blender.Check);
        Assert.Equal(real, blender.Path);
        var listed = Assert.Single(blender.Alternatives); // PATH and the fixed ~/.local/bin entry: listed once
        Assert.Equal(alias, listed.Path);
        Assert.Contains($"not probed: {localBin} is writable by group or others", listed.Probe.Error);
        Assert.Equal([$"{real} --version"], File.ReadAllLines(ProbeLog));
    }

    [Fact]
    public async Task AnAllowedUnsupportedBlenderGetsPastTheSpawnGate()
    {
        var old = FakeBlender(Path.Combine(_root, "custom", "blender"), "4.2.9");
        var allowed = ResolveWith(Mac(), file: $$$"""{"version":1,"blender":{"path":"{{{old}}}","allowUnsupported":true}}""");

        var error = await SpawnFails(allowed.Options, spawnTimeout: TimeSpan.FromSeconds(1));

        // The fake exits without connecting back: the spawn fails after starting it as a
        // worker (008 AC-18: reported at its exit, not after the spawn timeout).
        Assert.Matches("^worker [0-9a-f]+ exited with code 0 before connecting$", error.Message);
        Assert.Equal(WorkerSpawnStage.Spawn, error.Stage);
        Assert.IsNotType<ConfigException>(error.InnerException);
        Assert.Contains(File.ReadAllLines(ProbeLog), line => !line.EndsWith(" --version", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowUnsupportedStillPrefersAnInstalledSupportedBlender()
    {
        var old = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "4.5.0");
        var supported = FakeBlender(Path.Combine(Home, "Applications", "Blender.app", MacBinary), "5.2.1");

        var blender = BlenderDiscovery.Discover(allowUnsupported: true, Mac());

        Assert.Equal(supported, blender.Path);
        Assert.False(blender.UnsupportedAllowed);
        Assert.Null(blender.Check);
        Assert.Equal([old], blender.Alternatives.Select(a => a.Path));
    }

    [Fact]
    public async Task AC09_ASpawnAfterOnlyUnsupportedDiscoveryFailsWithBlenderVersion()
    {
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "4.5.3");
        var config = ResolveWith(Mac());

        var error = await SpawnFails(config.Options);

        Assert.StartsWith("blender.version: Blender 4.5.3 at ", error.Message);
        Assert.Contains("blender.allowUnsupported", error.Message);
        Assert.Equal("blender.version", Assert.IsType<ConfigException>(error.InnerException).Check);
        Assert.Equal(WorkerSpawnStage.Preflight, error.Stage);
        AssertOnlyVersionProbesRan();
    }

    [Fact]
    public async Task ASpawnWithARefusedExplicitBlenderNeverRunsIt()
    {
        // Missing, unsupported and unreadable-version explicit paths: each spawn fails with
        // the resolver's check before any socket is bound or the binary runs worker.py.
        var missing = Path.Combine(_root, "no-blender");
        var error = await SpawnFails(ResolveWith(Mac(), env: new() { ["ABL_BLENDER"] = missing }).Options);
        Assert.StartsWith("blender.found: Blender " + missing, error.Message);
        Assert.Equal("blender.found", Assert.IsType<ConfigException>(error.InnerException).Check);

        var old = FakeBlender(Path.Combine(_root, "custom", "blender"), "4.2.9");
        error = await SpawnFails(ResolveWith(Mac(), env: new() { ["ABL_BLENDER"] = old }).Options);
        Assert.StartsWith("blender.version: Blender 4.2.9 at " + old, error.Message);

        var broken = Script(Path.Combine(_root, "broken", "blender"), $"echo \"$0 $*\" >> '{ProbeLog}'\nexit 139");
        error = await SpawnFails(ResolveWith(Mac(), env: new() { ["ABL_BLENDER"] = broken }).Options);
        Assert.StartsWith("blender.version: cannot read the Blender version of " + broken, error.Message);

        AssertOnlyVersionProbesRan();
        Assert.Empty(Directory.GetFiles(RuntimeDir));
    }

    [Fact]
    public async Task ABlenderThatCannotStartFailsBlenderFoundWithoutLeakingTheSocket()
    {
        // No resolver verdict (options built directly): Process.Start itself fails.
        var options = new AblOptions { BlenderPath = Path.Combine(_root, "no-blender"), RuntimeDir = RuntimeDir };

        var error = await SpawnFails(options);

        Assert.StartsWith($"blender.found: cannot start Blender {options.BlenderPath}", error.Message);
        Assert.Equal(WorkerSpawnStage.Spawn, error.Stage); // the process start, not the preflight
        Assert.IsType<ConfigException>(error.InnerException);
        Assert.Empty(Directory.GetFiles(RuntimeDir));
    }

    [Fact]
    public void ExplicitPathNeverFallsThroughToDiscovery()
    {
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        var missing = Path.Combine(_root, "no-blender");

        var config = ResolveWith(Mac(), env: new() { ["ABL_BLENDER"] = missing });

        Assert.Equal(missing, config.Options.BlenderPath); // kept: the spawn fails with it, never a different Blender
        Assert.Equal(ConfigSource.Env, config[AblConfig.BlenderKey].Source);
        Assert.Equal("blender.found", config.Blender!.Check);
        Assert.Contains($"{missing} (from env) does not exist", config.Blender.Problem);
        Assert.False(File.Exists(ProbeLog)); // the discoverable Blender.app was never probed
        Assert.Empty(SpotlightCalls);
    }

    [Fact]
    public void ExplicitUnsupportedPathIsRefusedUnlessAllowedAndAllowingIsRecorded()
    {
        var old = FakeBlender(Path.Combine(_root, "custom", "blender"), "4.2.9");
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        var file = $$$"""{"version":1,"blender":{"path":"{{{old}}}"}}""";

        var refused = ResolveWith(Mac(), file: file);
        Assert.Equal("blender.version", refused.Blender!.Check);
        Assert.Equal(old, refused.Options.BlenderPath);
        Assert.Equal(ConfigSource.File, refused[AblConfig.BlenderKey].Source);
        Assert.Equal("blender.version", refused.Options.BlenderProblem!.Check);

        var allowed = ResolveWith(Mac(), file: $$$"""{"version":1,"blender":{"path":"{{{old}}}","allowUnsupported":true}}""");
        Assert.Null(allowed.Blender!.Check);
        Assert.Null(allowed.Options.BlenderProblem); // the spawn gate honours blender.allowUnsupported
        Assert.True(allowed.Blender.UnsupportedAllowed);
        Assert.Contains("allowed by blender.allowUnsupported", allowed.Blender.Warning);
        var json = allowed.ToJson()["blender"]!;
        Assert.True(json["allowUnsupported"]!.GetValue<bool>());
        Assert.Equal("unsupported", json["support"]!.GetValue<string>());
        var probes = File.ReadAllLines(ProbeLog);
        Assert.Equal(2, probes.Length); // one probe per resolve, of the explicit path only
        Assert.All(probes, line => Assert.Equal($"{old} --version", line));
    }

    [Fact]
    public void AllowUnsupportedDiscoversAnUnsupportedBlenderWhenNothingElseExists()
    {
        var old = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "4.5.0");
        var blender = BlenderDiscovery.Discover(allowUnsupported: true, Mac());
        Assert.Equal(old, blender.Path);
        Assert.True(blender.UnsupportedAllowed);
        Assert.Null(blender.Check);
    }

    [Fact]
    public void NothingFoundFailsBlenderFoundWithTheSearchAndARemedy()
    {
        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Linux(pathVariable: Path.Combine(_root, "empty-bin")));
        Assert.Null(blender.Path);
        Assert.Equal("blender.found", blender.Check);
        Assert.Contains("no Blender found (searched PATH, /usr/bin", blender.Problem);
        Assert.Contains("ABL_BLENDER", blender.Hint);
    }

    [Fact]
    public void LinuxOrderIsPathThenFixedDirsThenHomeAndALinkedBinaryIsProbedOnce()
    {
        var bin = Path.Combine(_root, "bin");
        var optBlender = FakeBlender(Path.Combine(SysRoot, "opt", "blender-5.2.1", "blender"), "5.2.1");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "blender"), optBlender);
        Directory.CreateDirectory(Path.Combine(SysRoot, "usr", "bin"));
        File.CreateSymbolicLink(Path.Combine(SysRoot, "usr", "bin", "blender"), optBlender);
        var home = FakeBlender(Path.Combine(Home, "blender-5.2.3", "blender"), "5.2.3");

        var search = Linux(pathVariable: $"relative/dir::{bin}");
        var order = BlenderDiscovery.Candidates(search).ToList();
        Assert.Equal(Path.Combine(bin, "blender"), order[0]);
        Assert.Equal(
            [
                Path.Combine(bin, "blender"),
                Path.Combine(SysRoot, "usr", "bin", "blender"),
                Path.Combine(SysRoot, "usr", "local", "bin", "blender"),
                optBlender,
                Path.Combine(Home, ".local", "bin", "blender"),
                home,
            ],
            order);

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, search);
        Assert.Equal(Path.Combine(bin, "blender"), blender.Path); // the reference, first in order
        Assert.Equal([home], blender.Alternatives.Select(a => a.Path)); // /usr/bin and /opt are the same binary
        Assert.Equal(2, File.ReadAllLines(ProbeLog).Length);
    }

    [Fact]
    public void ANonExecutableCandidateIsSkipped()
    {
        var path = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Mac());
        Assert.Equal("blender.found", blender.Check);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void TheProbeIsBoundedAndAFailingProbeIsReported()
    {
        var slow = Script(Path.Combine(_root, "slow", "blender"), "sleep 30");
        var stopwatch = Stopwatch.StartNew();
        var probe = BlenderDiscovery.RunVersionProbe(slow, TimeSpan.FromMilliseconds(500));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"probe took {stopwatch.Elapsed}");
        Assert.Null(probe.Version);
        Assert.Contains("did not finish within 0.5 s", probe.Error);

        var broken = Script(Path.Combine(_root, "broken", "blender"), "echo 'Segmentation fault' >&2; exit 139");
        var explicitBroken = BlenderDiscovery.ProbeExplicit(broken, ConfigSource.Flag, allowUnsupported: false, Mac());
        Assert.Equal("blender.version", explicitBroken.Check);
        Assert.Contains("exited with code 139", explicitBroken.Problem);
    }

    [Theory]
    [InlineData("Blender 5.2.1\n\tbuild date: 2026-06-01\n\tbuild hash: 9a8b7c6d5e4f\n", "5.2.1", "9a8b7c6d5e4f")]
    [InlineData("Color management: using fallback mode\nBlender 5.2.3 LTS\n", "5.2.3", null)]
    [InlineData("Blender 10.12.300\n", "10.12.300", null)]
    public void VersionOutputIsParsed(string output, string version, string? hash)
    {
        var probe = BlenderDiscovery.ParseVersionOutput(output, 0, "/b");
        Assert.Equal(version, probe.Version!.ToString());
        Assert.Equal(hash, probe.BuildHash);
    }

    [Theory]
    [InlineData("5.2.1", BlenderSupport.Reference)]
    [InlineData("5.2.0", BlenderSupport.Supported)]
    [InlineData("5.2.7 LTS", BlenderSupport.Supported)]
    [InlineData("5.3.0", BlenderSupport.Unsupported)]
    [InlineData("5.1.1", BlenderSupport.Unsupported)]
    [InlineData("4.2.1", BlenderSupport.Unsupported)]
    public void SupportFollowsRuleB(string version, BlenderSupport expected) =>
        Assert.Equal(expected, SupportedBlender.Classify(BlenderVersion.Parse(version)!));

    [Fact]
    public void TheWorkerHelloIsAuthoritative()
    {
        // Plan A4: a hello that disagrees with the probe, or an unsupported one without
        // allowUnsupported, rejects the worker.
        static WorkerSupervisor Supervisor(AblOptions options) => new(options, NullLogger<WorkerSupervisor>.Instance);

        var probed = Supervisor(new AblOptions { ProbedBlenderVersion = new BlenderVersion(5, 2, 1) });
        probed.CheckHelloVersion("5.2.1 LTS", "/b");
        var mismatch = Assert.Throws<WorkerDeadException>(() => probed.CheckHelloVersion("5.2.2", "/b"));
        Assert.StartsWith("blender.version: the worker reports Blender 5.2.2, but probing /b found 5.2.1", mismatch.Message);
        Assert.Equal(WorkerSpawnStage.Hello, mismatch.Stage);

        var strict = Supervisor(new AblOptions());
        strict.CheckHelloVersion("5.2.4", "/b");
        Assert.Contains("not supported", Assert.Throws<WorkerDeadException>(() => strict.CheckHelloVersion("4.5.0", "/b")).Message);

        // The gate fails closed: no readable version is refused unless allowUnsupported is set.
        var unreadable = Assert.Throws<WorkerDeadException>(() => strict.CheckHelloVersion(null, "/b"));
        Assert.StartsWith("blender.version: the worker hello from /b reports no readable Blender version", unreadable.Message);
        Assert.Equal("blender.version", Assert.IsType<ConfigException>(unreadable.InnerException).Check);
        Assert.Throws<WorkerDeadException>(() => strict.CheckHelloVersion("garbage", "/b"));

        var allowed = Supervisor(new AblOptions { AllowUnsupportedBlender = true });
        allowed.CheckHelloVersion("4.5.0", "/b");
        allowed.CheckHelloVersion(null, "/b");

        // allowUnsupported widens the line; it does not waive the hello confirming a probed version.
        var allowedProbed = Supervisor(new AblOptions { AllowUnsupportedBlender = true, ProbedBlenderVersion = new BlenderVersion(4, 2, 9) });
        allowedProbed.CheckHelloVersion("4.2.9", "/b");
        Assert.StartsWith(
            "blender.version: the worker hello from /b reports no readable Blender version",
            Assert.Throws<WorkerDeadException>(() => allowedProbed.CheckHelloVersion(null, "/b")).Message);
    }

    [Fact]
    public void DanglingCandidatesAreListedAndTheSearchGoesOn()
    {
        // The usual manual upgrade: /opt/blender-5.2.1 deleted for 5.2.2, leaving the
        // /usr/local/bin and ~/.local/bin links (and a PATH link) dangling. No throw; the
        // valid candidates later in the order are still probed.
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "blender"), Path.Combine(_root, "gone", "blender"));
        var usrLocalBin = Path.Combine(SysRoot, "usr", "local", "bin");
        Directory.CreateDirectory(usrLocalBin);
        File.CreateSymbolicLink(Path.Combine(usrLocalBin, "blender"), Path.Combine(SysRoot, "opt", "blender-5.2.1", "blender"));
        var newer = FakeBlender(Path.Combine(SysRoot, "opt", "blender-5.2.2", "blender"), "5.2.2");
        var localBin = Path.Combine(Home, ".local", "bin");
        Directory.CreateDirectory(localBin);
        var dangling = Path.Combine(localBin, "blender");
        File.CreateSymbolicLink(dangling, Path.Combine(Home, "blender-5.1.0", "blender"));
        var reference = FakeBlender(Path.Combine(Home, "blender-5.2.1", "blender"), "5.2.1");

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Linux(pathVariable: bin));

        Assert.Null(blender.Check);
        Assert.Equal(reference, blender.Path);
        Assert.Equal(
            [Path.Combine(bin, "blender"), Path.Combine(usrLocalBin, "blender"), newer, dangling],
            blender.Alternatives.Select(a => a.Path));
        Assert.Equal(
            $"not probed: {dangling} is a broken symlink: its target ({Path.Combine(Home, "blender-5.1.0", "blender")}) does not exist or cannot be reached",
            blender.Alternatives[3].Probe.Error);
        Assert.Equal([$"{newer} --version", $"{reference} --version"], File.ReadAllLines(ProbeLog));

        // The resolver the daemon and `abl config show` call: a verdict, not an exception.
        var config = ResolveWith(Linux(pathVariable: bin));
        Assert.Equal(reference, config.Options.BlenderPath);
    }

    [Fact]
    public void ADanglingExplicitBlenderIsBlenderFound()
    {
        FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        var link = Path.Combine(_root, "blender");
        var target = Path.Combine(_root, "blender-5.2.1", "blender");
        File.CreateSymbolicLink(link, target);

        var config = ResolveWith(Mac(), env: new() { ["ABL_BLENDER"] = link });

        Assert.Equal(link, config.Options.BlenderPath);
        Assert.Equal("blender.found", config.Blender!.Check);
        Assert.Equal(
            $"Blender {link} (from env) is a broken symlink: its target ({target}) does not exist or cannot be reached; discovery is not tried for an explicit path",
            config.Blender.Problem);
        Assert.StartsWith("blender.found: ", config[AblConfig.BlenderKey].Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void AnUnreachableCandidateNeverThrows()
    {
        if (UnixIdentity.EffectiveUserId == 0)
        {
            return; // root searches a mode-000 directory anyway
        }

        // A Spotlight hit, and a PATH link, below a directory the user cannot search.
        var locked = Path.Combine(Home, "locked");
        FakeBlender(Path.Combine(locked, "Blender.app", MacBinary), "5.2.1");
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "blender"), Path.Combine(locked, "Blender.app", MacBinary));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var mac = BlenderDiscovery.Discover(allowUnsupported: false, Mac() with { Spotlight = _ => [Path.Combine(locked, "Blender.app")] });
            Assert.Equal("blender.found", mac.Check);
            Assert.Empty(mac.Alternatives); // not even lstat-able: skipped

            var linux = BlenderDiscovery.Discover(allowUnsupported: false, Linux(pathVariable: bin));
            Assert.Equal("blender.found", linux.Check);
            var listed = Assert.Single(linux.Alternatives);
            Assert.Equal(Path.Combine(bin, "blender"), listed.Path);
            // .NET can surface EACCES on the target as not found or as access denied, by platform.
            Assert.Matches($"^not probed: {Regex.Escape(Path.Combine(bin, "blender"))} (is a broken symlink|cannot be read)", listed.Probe.Error);
            Assert.False(File.Exists(ProbeLog));
        }
        finally
        {
            File.SetUnixFileMode(locked, (UnixFileMode)0b111_101_101);
        }
    }

    [Fact]
    public void AWritableBinaryIsNeverRun()
    {
        // The file itself, not only its directories (plan A4 item 4).
        var bin = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        File.SetUnixFileMode(bin, (UnixFileMode)0b111_111_101);

        var mac = BlenderDiscovery.Discover(allowUnsupported: false, Mac());

        Assert.Equal("blender.found", mac.Check);
        Assert.Contains($"not probed: {bin} is writable by group or others", Assert.Single(mac.Alternatives).Probe.Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void AnUnanchoredMacTargetIsWalkedUpToItsBundle()
    {
        // /Applications/Blender.app/Contents/MacOS/Blender links into a bundle elsewhere (an
        // external volume) whose Contents is group-writable.
        var ext = Path.Combine(_root, "ext", "Blender.app");
        FakeBlender(Path.Combine(ext, MacBinary), "5.2.1");
        File.SetUnixFileMode(Path.Combine(ext, "Contents"), (UnixFileMode)0b111_111_101);
        var link = Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, Path.Combine(ext, MacBinary));
        for (var dir = Path.GetDirectoryName(link)!; dir != Path.Combine(SysRoot, "Applications"); dir = Path.GetDirectoryName(dir)!)
        {
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101); // do not depend on the umask
        }

        var mac = BlenderDiscovery.Discover(allowUnsupported: false, Mac());

        Assert.Equal("blender.found", mac.Check);
        Assert.Contains(
            $"not probed: {Path.Combine(ext, "Contents")} is writable by group or others", Assert.Single(mac.Alternatives).Probe.Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Fact]
    public void AnEntryOwnedByAnotherUserIsNeverRunButRootIsTrusted()
    {
        // `sudo tar -xf` keeps a foreign archive owner: that user can replace the 0755 entry.
        var other = UnixIdentity.EffectiveUserId + 1;
        var opt = Path.Combine(SysRoot, "opt", "blender-5.2.1");
        var foreignDir = FakeBlender(Path.Combine(opt, "blender"), "5.2.1");
        var home = FakeBlender(Path.Combine(Home, "blender-5.2.1", "blender"), "5.2.1");
        var linux = BlenderDiscovery.Discover(
            allowUnsupported: false, Linux(pathVariable: null) with
            {
                Inspect = p => UnixConfigFileSystem.Instance.Inspect(p) is { } facts
                    ? facts with { OwnerUserId = p == opt ? other : 0 } : null,
            }); // the rest: root

        Assert.Null(linux.Check);
        Assert.Equal(home, linux.Path);
        var listed = Assert.Single(linux.Alternatives);
        Assert.Equal(foreignDir, listed.Path);
        Assert.Equal(
            $"not probed: {opt} is owned by another user (uid {other}), who can replace it; chown it to you or root, or set blender.path explicitly",
            listed.Probe.Error);
        Assert.Equal([$"{home} --version"], File.ReadAllLines(ProbeLog));

        // The binary itself (macOS bundle), with the real owner for every other path.
        var bin = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        var mac = BlenderDiscovery.Discover(
            allowUnsupported: false, Mac() with
            {
                Inspect = p => UnixConfigFileSystem.Instance.Inspect(p) is { } facts && p == bin
                    ? facts with { OwnerUserId = other } : UnixConfigFileSystem.Instance.Inspect(p),
            });
        Assert.Equal("blender.found", mac.Check);
        Assert.Contains($"not probed: {bin} is owned by another user (uid {other})", Assert.Single(mac.Alternatives).Probe.Error);
        Assert.Single(File.ReadAllLines(ProbeLog));
    }

    [Fact]
    public void ADirectoryWithAnAllowAclIsNeverRun()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return; // macOS extended ACLs (Linux POSIX ACLs show in the mode's group bits)
        }

        // A 0755 dir shared Read & Write with another account through an ACL entry.
        var shared = Path.Combine(Home, "Shared");
        var app = Path.Combine(shared, "evil", "Blender.app");
        var planted = FakeBlender(Path.Combine(app, MacBinary), "5.2.1");
        UnixIdentityTests.ChmodAcl("group:everyone allow add_file,add_subdirectory,delete_child", shared);

        var blender = BlenderDiscovery.Discover(allowUnsupported: false, Mac() with { Spotlight = _ => [app] });

        Assert.Equal("blender.found", blender.Check);
        var listed = Assert.Single(blender.Alternatives);
        Assert.Equal(planted, listed.Path);
        Assert.Equal(
            $"not probed: {shared} has an ACL with an allow entry, which can let others write to it; remove it (chmod -N) or set blender.path explicitly",
            listed.Probe.Error);
        Assert.False(File.Exists(ProbeLog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedFactsAclOrInspectionFailurePreventsAProbe(bool inspectionFails)
    {
        var bin = FakeBlender(Path.Combine(SysRoot, "Applications", "Blender.app", MacBinary), "5.2.1");
        var search = Mac() with
        {
            Inspect = p => p != bin ? UnixConfigFileSystem.Instance.Inspect(p)
                : inspectionFails ? throw new IOException("inspection refused")
                : UnixConfigFileSystem.Instance.Inspect(p)! with { HasAllowAcl = true },
        };

        var result = BlenderDiscovery.Discover(allowUnsupported: false, search);

        Assert.Equal("blender.found", result.Check);
        Assert.Contains(inspectionFails ? "cannot inspect" : "ACL with an allow entry", Assert.Single(result.Alternatives).Probe.Error);
        Assert.False(File.Exists(ProbeLog));
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

    private AblConfig ResolveWith(BlenderSearch search, Dictionary<string, string>? env = null, string? file = null)
    {
        var fs = new FakeConfigFileSystem();
        if (file is not null)
        {
            fs.File("/home/u/.config/agentblender/config.json", file);
        }

        return AblConfig.Resolve(new ConfigInputs
        {
            Env = env ?? [],
            Home = "/home/u",
            FileSystem = fs,
            Platform = search.Platform,
            Blender = search,
        });
    }

    private BlenderSearch Mac() => new()
    {
        Platform = HostPlatform.MacOS,
        Home = Home,
        SystemRoot = SysRoot,
        Spotlight = timeout =>
        {
            SpotlightCalls.Add(timeout);
            return [];
        },
    };

    private BlenderSearch Linux(string? pathVariable) => new()
    {
        Platform = HostPlatform.Linux,
        Home = Home,
        SystemRoot = SysRoot,
        PathVariable = pathVariable,
        Spotlight = _ => throw new InvalidOperationException("Spotlight is macOS only"),
    };

    /// <summary>A fake <c>blender</c> that logs each probe and prints a version banner.</summary>
    private string FakeBlender(string path, string version, string hash = "0123456789ab") =>
        Script(path, $"echo \"$0 $*\" >> '{ProbeLog}'\nprintf 'Blender {version}\\n\\tbuild date: 2026-06-01\\n\\tbuild hash: {hash}\\n'");

    private string Script(string path, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Discovery refuses group/other-writable dirs; do not depend on the runner's umask.
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.StartsWith(_root, StringComparison.Ordinal); dir = Path.GetDirectoryName(dir))
        {
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
        }

        return path;
    }

    private string RuntimeDir => Path.Combine(_root, "rt");

    private async Task<WorkerDeadException> SpawnFails(AblOptions options, TimeSpan? spawnTimeout = null)
    {
        var supervisor = new WorkerSupervisor(
            options with
            {
                RuntimeDir = RuntimeDir,
                Platform = OperatingSystem.IsMacOS() ? HostPlatform.MacOS : HostPlatform.Linux,
                SpawnTimeout = spawnTimeout ?? TimeSpan.FromSeconds(5),
            },
            NullLogger<WorkerSupervisor>.Instance);
        Directory.CreateDirectory(RuntimeDir, RuntimePaths.DirMode);
        File.SetUnixFileMode(RuntimeDir, RuntimePaths.DirMode);
        return await Assert.ThrowsAsync<WorkerDeadException>(() => supervisor.SpawnAsync(prewarm: false, CancellationToken.None));
    }

    /// <summary>The fake Blenders log every invocation: only <c>--version</c> probes may appear.</summary>
    private void AssertOnlyVersionProbesRan()
    {
        var runs = File.Exists(ProbeLog) ? File.ReadAllLines(ProbeLog) : [];
        Assert.All(runs, line => Assert.EndsWith(" --version", line));
    }
}
