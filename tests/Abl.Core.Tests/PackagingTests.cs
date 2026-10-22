using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Abl.Cli;
using Abl.Core.Config;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T070/T071 (FR-014, FR-015): packaged-mode worker resolution, the build-time worker
/// manifest, <c>abl --version --json</c>, and the tool-package and SDK-policy settings. The
/// pack, temporary install and <c>abl doctor --quick --json</c> run (AC-32) is CI's
/// <c>scripts/release-bundle.sh</c> step.
/// </summary>
public sealed class PackagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-pkg-" + Guid.NewGuid().ToString("N")[..12]);

    public PackagingTests() => Directory.CreateDirectory(_root);

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

    // ---- packaged-mode worker resolution (plan C1, L10) -------------------------------------

    [Fact]
    public void PackagedResolutionNeverWalksUpToACheckoutsWorker()
    {
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, "worker"));
        File.WriteAllText(Path.Combine(checkout, "worker", "worker.py"), "");
        var binaries = Directory.CreateDirectory(Path.Combine(checkout, "src", "Abl.Cli", "bin")).FullName;

        Assert.Equal(Path.Combine(binaries, "worker"), AblDefaults.WorkerDir(binaries, searchParents: false));
        Assert.Equal(Path.Combine(checkout, "worker"), AblDefaults.WorkerDir(binaries, searchParents: true));
    }

    [Fact]
    public void WorkerNextToTheBinariesWinsInEitherMode()
    {
        var binaries = Path.Combine(_root, "tools", "any");
        Directory.CreateDirectory(Path.Combine(binaries, "worker"));
        File.WriteAllText(Path.Combine(binaries, "worker", "worker.py"), "");
        Directory.CreateDirectory(Path.Combine(_root, "worker"));
        File.WriteAllText(Path.Combine(_root, "worker", "worker.py"), "");

        Assert.Equal(Path.Combine(binaries, "worker"), AblDefaults.WorkerDir(binaries, searchParents: false));
        Assert.Equal(Path.Combine(binaries, "worker"), AblDefaults.WorkerDir(binaries, searchParents: true));
    }

    [Fact]
    public void OnlyADebugBuildSearchesParentDirectories()
    {
        // The constant follows the build of Abl.Core: Debug (JIT optimizer off) searches, Release never does.
        var debug = typeof(AblDefaults).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true;
        Assert.Equal(AblDefaults.SearchesParentsForWorker, debug);
    }

    // ---- the worker manifest -----------------------------------------------------------------

    [Fact]
    public void TheBuildWritesAManifestOfEveryWorkerModuleNextToTheBinaries()
    {
        // Abl.Cli's AblWorkerManifest target; its content reaches this test's output like abl's.
        var manifest = WorkerManifest.Read(AppContext.BaseDirectory);

        Assert.NotNull(manifest);
        var modules = Directory.GetFiles(AblDefaults.WorkerDir(), "*.py").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(modules, manifest.Modules.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(WorkerSource.Digest(AblDefaults.WorkerDir()), manifest.Digest());
        Assert.Empty(manifest.Changed(AblDefaults.WorkerDir()));
    }

    [Fact]
    public void TheManifestDigestIsTheWorkersOwnConstruction()
    {
        var worker = CopyWorker();
        var manifest = WorkerManifest.Parse(ManifestText(worker, upper: true), "test");

        Assert.Equal(WorkerSource.Digest(worker), manifest.Digest());
        Assert.All(manifest.Modules.Values, v => Assert.Matches("^[0-9a-f]{64}$", v));
    }

    [Fact]
    public void ChangedNamesTheModulesThatDifferInWorkerOrder()
    {
        var worker = CopyWorker();
        var manifest = WorkerManifest.Parse(ManifestText(worker, upper: false), "test");
        File.AppendAllText(Path.Combine(worker, "inspection.py"), "#\n");
        File.AppendAllText(Path.Combine(worker, "worker.py"), "#\n");

        Assert.Equal(["worker.py", "inspection.py"], manifest.Changed(worker));
    }

    [Theory]
    [InlineData("zz  worker.py\n", "line 1 is not")]
    [InlineData("{0} worker.py\n", "line 1 is not")]
    [InlineData("{0}  ../worker.py\n", "line 1 is not")]
    [InlineData("{0}  sub/worker.py\n", "line 1 is not")]
    [InlineData("{0}  \n", "line 1 is not")]
    [InlineData("{0}  worker.py\n{0}  worker.py\n", "line 2 repeats worker.py")]
    [InlineData("{0}  worker.py\n", "does not list fingerprint.py, sheet.py")]
    public void AMalformedManifestIsRefusedNamingTheLine(string template, string expected)
    {
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, new string('a', 64));
        var error = Assert.Throws<InvalidDataException>(() => WorkerManifest.Parse(text, "m.sha256"));
        Assert.Contains(expected, error.Message);
        Assert.StartsWith("m.sha256", error.Message);
    }

    [Fact]
    public void AMissingManifestReadsAsNone() => Assert.Null(WorkerManifest.Read(_root));

    // ---- abl --version --json ------------------------------------------------------------------

    [Fact]
    public void VersionJsonCarriesTheBlenderLineAndThePackagedWorkerDigest()
    {
        var worker = CopyWorker();
        File.WriteAllText(WorkerManifest.PathIn(_root), ManifestText(worker, upper: true));

        var info = PackageInfo.ToJson(_root);
        var none = PackageInfo.ToJson(Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName);

        Assert.Equal("abl", info["tool"]!.GetValue<string>());
        Assert.Equal(AblVersion.Current, info["version"]!.GetValue<string>());
        Assert.Equal(SupportedBlender.Line, info["supportedBlender"]!["line"]!.GetValue<string>());
        Assert.Equal(SupportedBlender.Reference.ToString(), info["supportedBlender"]!["reference"]!.GetValue<string>());
        Assert.Equal(WorkerSource.Digest(worker), info["worker"]!["manifestDigest"]!.GetValue<string>());
        Assert.Null(none["worker"]!["manifestDigest"]);
    }

    [Fact]
    public void VersionJsonGivesNoManifestDigestForAMalformedManifest()
    {
        File.WriteAllText(WorkerManifest.PathIn(_root), "not a manifest\n");

        var info = PackageInfo.ToJson(_root);

        Assert.Null(info["worker"]!["manifestDigest"]);
    }

    // ---- package and SDK settings (FR-014, FR-015) ---------------------------------------------

    [Theory]
    [InlineData("install-local.sh")]
    [InlineData("release-bundle.sh")]
    public void InstallScriptsTakeThePackageFromTheirOwnFeedOnly(string script)
    {
        // --add-source also asks the configured feeds (nuget.org), which could serve the same id and version.
        var text = File.ReadAllText(Path.Combine(RepoRoot, "scripts", script));

        Assert.DoesNotContain("--add-source", text);
        Assert.DoesNotContain("--ignore-failed-sources", text);
        Assert.Contains("dotnet tool install \"$PACKAGE_ID\"", text);
        Assert.Contains("--source \"", text);
    }

    [Fact]
    public void AblCliPacksAsTheAgentblenderToolWithTheAblCommand()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot, "src", "Abl.Cli", "Abl.Cli.csproj"));
        string? Property(string name) => project.Descendants(name).SingleOrDefault()?.Value;

        Assert.Equal("true", Property("PackAsTool"));
        Assert.Equal("abl", Property("ToolCommandName"));
        Assert.Equal("agentblender", Property("PackageId"));
        Assert.Null(Property("RuntimeIdentifier")); // RID-agnostic
        Assert.Null(Property("RuntimeIdentifiers"));
    }

    [Fact]
    public void TheToolPackageDeclaresItsLicenseAndCarriesTheThirdPartyNotices()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot, "src", "Abl.Cli", "Abl.Cli.csproj"));
        string? Property(string name) => project.Descendants(name).SingleOrDefault()?.Value;

        Assert.Equal("MIT", Property("PackageLicenseExpression"));
        Assert.Equal("https://github.com/pevyhlas/agentblender", Property("PackageProjectUrl"));
        Assert.Equal("https://github.com/pevyhlas/agentblender", Property("RepositoryUrl"));
        var packed = project.Descendants("None")
            .Where(item => (string?)item.Attribute("Pack") == "true")
            .Select(item => ((string?)item.Attribute("Include"), (string?)item.Attribute("PackagePath")))
            .ToList();
        Assert.Contains(("../../LICENSE;../../THIRD-PARTY-NOTICES.md", ""), packed);
        Assert.Contains(("../../licenses/*.txt", "licenses"), packed);

        // Every license text the notices file names exists, and every one that exists is named.
        var notices = File.ReadAllText(Path.Combine(RepoRoot, "THIRD-PARTY-NOTICES.md"));
        var texts = Directory.GetFiles(Path.Combine(RepoRoot, "licenses"), "*.txt").Select(Path.GetFileName).ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, name => Assert.Contains($"(licenses/{name})", notices, StringComparison.Ordinal));
        Assert.All(
            System.Text.RegularExpressions.Regex.Matches(notices, @"\(licenses/([^)]+)\)").Select(m => m.Groups[1].Value),
            name => Assert.Contains(name, texts));
    }

    [Fact]
    public void GlobalJsonRollsForwardWithinTheReferenceMajorWithoutPrereleases()
    {
        // CI's "Select the reference SDK" step rewrites the checkout's global.json to
        // `rollForward: disable` before the tests run, so the policy is read from the
        // committed file in a git checkout, and from the working file otherwise.
        var sdk = JsonNode.Parse(CommittedGlobalJson())!["sdk"]!;

        Assert.Equal("10.0.112", sdk["version"]!.GetValue<string>());
        Assert.Equal("latestFeature", sdk["rollForward"]!.GetValue<string>());
        Assert.False(sdk["allowPrerelease"]!.GetValue<bool>());
    }

    [Fact]
    public void TheLanguageAndAnalyzerLevelsArePinned()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));

        Assert.Equal("14", props.Descendants("LangVersion").Single().Value);
        Assert.Equal("10.0", props.Descendants("AnalysisLevel").Single().Value);
    }

    private string CopyWorker()
    {
        var worker = Directory.CreateDirectory(Path.Combine(_root, "worker-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        foreach (var module in WorkerSource.Modules)
        {
            File.Copy(Path.Combine(AblDefaults.WorkerDir(), module), Path.Combine(worker, module));
        }

        return worker;
    }

    private static string ManifestText(string worker, bool upper) => string.Concat(
        Directory.GetFiles(worker, "*.py").Order(StringComparer.Ordinal).Select(path =>
        {
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            return (upper ? Convert.ToHexString(hash) : Convert.ToHexStringLower(hash)) + "  " + Path.GetFileName(path) + "\n";
        }));

    private static string CommittedGlobalJson()
    {
        try
        {
            var git = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-C", RepoRoot, "show", "HEAD:global.json" })
            {
                git.ArgumentList.Add(arg);
            }

            using var process = Process.Start(git)!;
            var text = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return text;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no git on PATH: fall back to the working file
        }

        return File.ReadAllText(Path.Combine(RepoRoot, "global.json"));
    }

    private static string RepoRoot
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
}
