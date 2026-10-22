using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Abl.Core.Config;

/// <summary>The Unix flavour whose search order, socket limit and fallback dir apply.</summary>
public enum HostPlatform
{
    Linux,
    MacOS,
}

/// <summary>What one <c>blender --version</c> probe found.</summary>
/// <param name="Version">The parsed release, or null when the probe failed.</param>
/// <param name="BuildHash">The <c>build hash:</c> line, when printed.</param>
/// <param name="Error">Why the probe failed (timeout, exit code, no version line).</param>
public sealed record BlenderProbe(BlenderVersion? Version, string? BuildHash, string? Error);

/// <summary>One executable that was probed.</summary>
public sealed record BlenderCandidate(string Path, BlenderProbe Probe)
{
    public BlenderSupport? Support => Probe.Version is { } v ? SupportedBlender.Classify(v) : null;
}

/// <summary>
/// The outcome of choosing Blender (008 FR-003): the selected path (null when none is usable),
/// its probe, every other candidate, and the failing check with its remedy when there is one.
/// </summary>
public sealed record BlenderResolution
{
    /// <summary>The Blender the daemon runs, or null when none was found.</summary>
    public string? Path { get; init; }

    public ConfigSource Source { get; init; }

    public BlenderVersion? Version { get; init; }

    public string? BuildHash { get; init; }

    public BlenderSupport? Support { get; init; }

    /// <summary>An unsupported version is in use because <c>blender.allowUnsupported</c> is set.</summary>
    public bool UnsupportedAllowed { get; init; }

    /// <summary>The other candidates discovery found, in search order.</summary>
    public IReadOnlyList<BlenderCandidate> Alternatives { get; init; } = [];

    /// <summary><c>blender.found</c> or <c>blender.version</c> when Blender is not usable.</summary>
    public string? Check { get; init; }

    public string? Problem { get; init; }

    public string? Hint { get; init; }

    /// <summary>A non-fatal note (a supported patch other than the reference).</summary>
    public string? Warning { get; init; }

    /// <summary>The <c>blender</c> object of <c>abl config show --json</c> (and later <c>daemon_status</c>).</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["path"] = Path,
            ["source"] = AblConfig.SourceName(Source),
            ["version"] = Version?.ToString(),
            ["buildHash"] = BuildHash,
            ["support"] = Support is { } s ? SupportedBlender.Name(s) : null,
            ["supportedLine"] = SupportedBlender.Line,
            ["reference"] = SupportedBlender.Reference.ToString(),
            ["allowUnsupported"] = UnsupportedAllowed,
            ["alternatives"] = new JsonArray([.. Alternatives.Select(a => (JsonNode)new JsonObject
            {
                ["path"] = a.Path,
                ["version"] = a.Probe.Version?.ToString(),
                ["support"] = a.Support is { } support ? SupportedBlender.Name(support) : null,
                ["error"] = a.Probe.Error,
            })]),
        };
        if (Warning is not null)
        {
            json["warning"] = Warning;
        }

        if (Check is not null)
        {
            json["check"] = Check;
            json["problem"] = Problem;
            json["hint"] = Hint;
        }

        return json;
    }
}

/// <summary>
/// Where and how to look for Blender. Every input is injected so tests run fake
/// <c>blender</c> scripts under a temp root and stub Spotlight (plan M10).
/// </summary>
public sealed record BlenderSearch
{
    public required HostPlatform Platform { get; init; }

    public required string Home { get; init; }

    /// <summary>The <c>PATH</c> value searched on Linux.</summary>
    public string? PathVariable { get; init; }

    /// <summary>Prefix for the fixed system locations (<c>/Applications</c>, <c>/usr</c>, <c>/opt</c>); tests use a temp dir.</summary>
    public string SystemRoot { get; init; } = "/";

    /// <summary>Bound on each <c>blender --version</c> probe (plan A4: 15 s).</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Bound on the Spotlight query (plan A4: 2 s).</summary>
    public TimeSpan SpotlightTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Spotlight: <c>.app</c> bundle paths of Blender (macOS, last resort).</summary>
    public Func<TimeSpan, IReadOnlyList<string>> Spotlight { get; init; } = BlenderDiscovery.QuerySpotlight;

    /// <summary>Runs one bounded version probe.</summary>
    public Func<string, TimeSpan, BlenderProbe> Probe { get; init; } = BlenderDiscovery.RunVersionProbe;

    /// <summary>The owner uid of a path, following symlinks; tests stub a foreign owner.</summary>
    public Func<string, uint> Owner { get; init; } = BlenderDiscovery.FileOwner;

    /// <summary>The search for this process (real PATH, <c>/</c>, real Spotlight).</summary>
    public static BlenderSearch ForProcess(HostPlatform platform, string home, IReadOnlyDictionary<string, string> env) => new()
    {
        Platform = platform,
        Home = home,
        PathVariable = env.TryGetValue("PATH", out var path) ? path : null,
    };
}

/// <summary>
/// Blender discovery and the version policy (008 FR-003, plan A4). An explicit path (flag,
/// <c>ABL_BLENDER</c>, <c>blender.path</c>) is probed and never falls through to discovery.
/// Otherwise the candidates are probed in order — macOS: <c>/Applications/Blender.app</c>,
/// <c>~/Applications/Blender.app</c>, <c>/Applications/Blender*.app</c>, then Spotlight;
/// Linux: <c>blender</c> on <c>PATH</c>, <c>/usr/bin</c>, <c>/usr/local/bin</c>,
/// <c>/opt/blender*/</c>, <c>~/.local/bin</c>, <c>~/blender*/</c> — and the reference 5.2.1
/// wins, else the highest 5.2.x, with the rest listed as alternatives.
/// </summary>
public static partial class BlenderDiscovery
{
    private const string MacBinary = "Contents/MacOS/Blender";

    /// <summary>Probes an explicitly configured Blender; problems never fall through to discovery.</summary>
    public static BlenderResolution ProbeExplicit(string path, ConfigSource source, bool allowUnsupported, BlenderSearch search)
    {
        var origin = $"{path} (from {AblConfig.SourceName(source)})";
        if (!IsExecutableFile(path, out var unreachable))
        {
            return new BlenderResolution
            {
                Path = path,
                Source = source,
                Check = "blender.found",
                Problem = $"Blender {origin} {unreachable ?? "does not exist or is not an executable file"}; discovery is not tried for an explicit path",
                Hint = SupportedBlender.FixPathRemedy,
            };
        }

        return Judge(new BlenderCandidate(path, search.Probe(path, search.ProbeTimeout)), source, allowUnsupported, [], origin);
    }

    /// <summary>Discovers Blender when nothing is configured.</summary>
    public static BlenderResolution Discover(bool allowUnsupported, BlenderSearch search)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var probed = new List<BlenderCandidate>();
        ProbeAll(Candidates(search), search, seen, probed);
        if (search.Platform == HostPlatform.MacOS && !probed.Any(IsSupported))
        {
            IReadOnlyList<string> apps;
            try
            {
                apps = search.Spotlight(search.SpotlightTimeout);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                apps = [];
            }

            // Spotlight indexes every volume (/Users/Shared, mounted images): only hits under
            // /Applications or the home directory are probed; the rest are listed, never run.
            // A hit is resolved once, so the rule, the probe and the stored path all name the
            // real bundle: a symlinked hit cannot be re-pointed after it was judged. The trust
            // rule then walks the hit's whole chain up to its anchor (UntrustedReason).
            var anchors = TrustAnchors.For(search);
            foreach (var hit in apps)
            {
                var app = Canonical(hit);
                var binary = Path.Combine(app, MacBinary);
                if (anchors.Contains(app))
                {
                    ProbeAll([binary], search, seen, probed);
                }
                else if (IsExecutableFile(binary, out _))
                {
                    ListNotProbed(
                        binary, "Spotlight found it outside /Applications and the home directory; set blender.path to use it", seen, probed);
                }
            }
        }

        var supported = probed.Where(IsSupported).ToList();
        var chosen = supported.FirstOrDefault(c => c.Support == BlenderSupport.Reference)
            ?? supported.OrderByDescending(c => c.Probe.Version).FirstOrDefault(); // stable: search order breaks ties
        if (chosen is null && allowUnsupported)
        {
            // The same "highest wins" rule as the supported branch; search order breaks ties.
            chosen = probed.Where(c => c.Probe.Version is not null).OrderByDescending(c => c.Probe.Version).FirstOrDefault();
        }

        if (chosen is not null)
        {
            return Judge(chosen, ConfigSource.Discovered, allowUnsupported, [.. probed.Where(c => c != chosen)], chosen.Path);
        }

        if (probed.FirstOrDefault(c => c.Probe.Version is not null) is { } unsupported)
        {
            // AC-09: the only candidates are outside the supported line.
            return new BlenderResolution
            {
                Source = ConfigSource.Default,
                Alternatives = probed,
                Check = "blender.version",
                Problem = SupportedBlender.Unsupported(unsupported.Probe.Version!, unsupported.Path) +
                    (probed.Count > 1 ? $"; also found {Describe(probed.Where(c => c != unsupported))}" : ""),
                Hint = SupportedBlender.Remedy,
            };
        }

        return new BlenderResolution
        {
            Source = ConfigSource.Default,
            Alternatives = probed,
            Check = "blender.found",
            Problem = probed.Count == 0
                ? $"no Blender found (searched {SearchedDescription(search)})"
                : $"no working Blender found: {Describe(probed)}",
            Hint = SupportedBlender.FoundRemedy,
        };
    }

    /// <summary>The search order, before deduplication and probing.</summary>
    public static IEnumerable<string> Candidates(BlenderSearch search)
    {
        if (search.Platform == HostPlatform.MacOS)
        {
            var applications = Path.Combine(search.SystemRoot, "Applications");
            yield return Path.Combine(applications, "Blender.app", MacBinary);
            yield return Path.Combine(search.Home, "Applications", "Blender.app", MacBinary);
            foreach (var app in Matching(applications, "Blender*.app"))
            {
                yield return Path.Combine(app, MacBinary);
            }

            yield break;
        }

        foreach (var dir in (search.PathVariable ?? "").Split(':'))
        {
            if (Path.IsPathRooted(dir))
            {
                yield return Path.Combine(dir, "blender");
            }
        }

        yield return Path.Combine(search.SystemRoot, "usr", "bin", "blender");
        yield return Path.Combine(search.SystemRoot, "usr", "local", "bin", "blender");
        foreach (var dir in Matching(Path.Combine(search.SystemRoot, "opt"), "blender*"))
        {
            yield return Path.Combine(dir, "blender");
        }

        yield return Path.Combine(search.Home, ".local", "bin", "blender");
        foreach (var dir in Matching(search.Home, "blender*"))
        {
            yield return Path.Combine(dir, "blender");
        }
    }

    /// <summary>Runs <c>blender --version</c> (no Python, no scene), bounded by <paramref name="timeout"/>.</summary>
    public static BlenderProbe RunVersionProbe(string path, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(path)
        {
            ArgumentList = { "--version" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // 008 FR-005: a probed binary is not trusted yet, so it gets the narrow allowlist.
        ChildEnvironment.Restrict(start.Environment, ChildEnvironment.AllowsForProbe);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new BlenderProbe(null, null, $"cannot run {path} --version: {ex.Message}");
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // already gone, or a descendant cannot be signalled: still a timeout
                }

                process.WaitForExit(TimeSpan.FromSeconds(2));
                return new BlenderProbe(null, null, string.Create(
                    CultureInfo.InvariantCulture, $"{path} --version did not finish within {timeout.TotalSeconds:0.#} s"));
            }

            // The pipes close when the process exits; a grandchild holding them open must not
            // stall the probe past its bound.
            if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2)))
            {
                return new BlenderProbe(null, null, $"{path} --version left its output open");
            }

            return ParseVersionOutput(stdout.Result, process.ExitCode, path);
        }
    }

    /// <summary>Parses <c>blender --version</c> output: <c>Blender 5.2.1</c> and <c>build hash: …</c>.</summary>
    public static BlenderProbe ParseVersionOutput(string output, int exitCode, string path)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Blender ", StringComparison.Ordinal));
        if (BlenderVersion.Parse(line) is not { } version)
        {
            return new BlenderProbe(null, null, exitCode == 0
                ? $"{path} --version printed no 'Blender <major>.<minor>.<patch>' line"
                : $"{path} --version exited with code {exitCode}");
        }

        var hash = BuildHash().Match(output) is { Success: true } match ? match.Groups[1].Value : null;
        return new BlenderProbe(version, hash, null);
    }

    /// <summary>Spotlight lookup by bundle id, bounded; empty when <c>mdfind</c> is missing or slow.</summary>
    public static IReadOnlyList<string> QuerySpotlight(TimeSpan timeout)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        var start = new ProcessStartInfo("/usr/bin/mdfind")
        {
            ArgumentList = { "kMDItemCFBundleIdentifier == 'org.blenderfoundation.blender'" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return [];
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            return stdout.Wait(TimeSpan.FromSeconds(1))
                ? [.. stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => p.EndsWith(".app", StringComparison.Ordinal))]
                : [];
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    private static void ProbeAll(IEnumerable<string> paths, BlenderSearch search, HashSet<string> seen, List<BlenderCandidate> probed)
    {
        // Only a probed (trusted) path takes a binary's dedup slot: an untrusted alias earlier in
        // the search order must not hide a trusted path to the same binary.
        foreach (var path in paths)
        {
            if (!IsExecutableFile(path, out var unreachable))
            {
                // A dangling link (a Blender removed after an upgrade) is listed, never fatal:
                // the search goes on to the next candidate.
                if (unreachable is not null)
                {
                    ListNotProbed(path, $"{path} {unreachable}", seen, probed);
                }

                continue;
            }

            var target = Canonical(path);
            if (seen.Contains(target))
            {
                continue;
            }

            if (UntrustedReason(path, search) is { } reason)
            {
                ListNotProbed(path, reason, seen, probed);
                continue;
            }

            seen.Add(target);
            probed.Add(new BlenderCandidate(path, search.Probe(path, search.ProbeTimeout)));
        }
    }

    /// <summary>
    /// Lists a candidate that is never run, once per binary. Its dedup key is separate from the
    /// probed one, so a trusted path to the same binary later in the search is still probed.
    /// </summary>
    private static void ListNotProbed(string path, string reason, HashSet<string> seen, List<BlenderCandidate> probed)
    {
        var target = Canonical(path);
        if (!seen.Contains(target) && seen.Add("not-probed:" + target))
        {
            probed.Add(NotProbed(path, reason));
        }
    }

    private static BlenderCandidate NotProbed(string path, string reason) => new(path, new BlenderProbe(null, null, "not probed: " + reason));

    /// <summary>
    /// A discovered (never an explicit) candidate is executed only when nobody but the owner of
    /// each step can change the path to it. For the binary and every hop of its symlink chain,
    /// the file and every directory from it up to its trust anchor must not be writable by
    /// group or others (<see cref="TrustAnchors.Chain"/>). A directory writable only by its
    /// owner gains entries only from that owner, so a chain that passes up to the home
    /// directory (itself checked) holds only entries its user or root put there, however deep
    /// the binary sits (a Spotlight hit in a subdirectory of the stock 0733
    /// <c>~/Public/Drop Box</c> is refused at the Drop Box). Write bits bound who created an
    /// entry, not who controls it now, so every checked path must also be owned by the
    /// current user or root (a <c>sudo tar</c> that kept a foreign archive owner, or a
    /// directory tightened after another user planted in it), and on macOS must carry no
    /// <c>allow</c> ACL entry, which can grant write access the mode does not show.
    /// </summary>
    /// <returns>Null when the candidate may be probed, otherwise why it is skipped.</returns>
    private static string? UntrustedReason(string path, BlenderSearch search)
    {
        const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var anchors = TrustAnchors.For(search);
        var target = Canonical(path);
        var checkedPaths = new List<string> { target };
        foreach (var file in LinkChain(path).Append(target))
        {
            checkedPaths.AddRange(anchors.Chain(file));
        }

        var user = UnixIdentity.EffectiveUserId;
        foreach (var checkedPath in checkedPaths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if ((File.GetUnixFileMode(checkedPath) & GroupOrOtherWrite) != 0)
                {
                    return $"{checkedPath} is writable by group or others; fix its mode or set blender.path explicitly";
                }

                if (search.Owner(checkedPath) is var owner && owner != 0 && owner != user)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"{checkedPath} is owned by another user (uid {owner}), who can replace it; chown it to you or root, or set blender.path explicitly");
                }

                if (OperatingSystem.IsMacOS() && UnixIdentity.HasAllowAcl(checkedPath))
                {
                    return $"{checkedPath} has an ACL with an allow entry, which can let others write to it; remove it (chmod -N) or set blender.path explicitly";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
            {
                return $"cannot inspect {checkedPath}: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>The owner uid of <paramref name="path"/> (the default <see cref="BlenderSearch.Owner"/>).</summary>
    public static uint FileOwner(string path) => OperatingSystem.IsWindows() ? 0 : UnixIdentity.OwnerOf(path);

    /// <summary>
    /// Where the trust walk of a discovered path stops (plan A4 item 4). The home directory is
    /// the user's own and is checked too: a home others can write voids every rule below it.
    /// <c>/Applications</c> (macOS) is root:admin 0775 and is not checked: its writers are
    /// administrators, who can already run anything as the user. A path under neither (the
    /// Linux system dirs, <c>PATH</c> entries, a symlink hop elsewhere) is the system's or the
    /// user's own choice of location, so its walk covers its own directory and, on macOS, the
    /// directories up to its enclosing <c>.app</c> bundle.
    /// </summary>
    /// <param name="Home">The full home directory, or null for a relative home or <c>/</c>.</param>
    /// <param name="Applications">The full <c>/Applications</c> on macOS, else null.</param>
    private sealed record TrustAnchors(string? Home, string? Applications, HostPlatform Platform)
    {
        public static TrustAnchors For(BlenderSearch search) => new(
            Root(search.Home),
            search.Platform == HostPlatform.MacOS ? Root(Path.Combine(search.SystemRoot, "Applications")) : null,
            search.Platform);

        /// <summary>True when <paramref name="path"/> lies below the home or <c>/Applications</c>.</summary>
        public bool Contains(string path) => IsUnder(path, Home) || IsUnder(path, Applications);

        /// <summary>The directories whose mode decides whether <paramref name="file"/> can be swapped.</summary>
        public IEnumerable<string> Chain(string file)
        {
            var anchored = Contains(file);
            for (var dir = Path.GetDirectoryName(Path.GetFullPath(file)); dir is { Length: > 0 } && dir != Applications; dir = Path.GetDirectoryName(dir))
            {
                yield return dir;
                if (dir == Home || (!anchored && EndsUnanchoredWalk(dir)))
                {
                    yield break;
                }
            }
        }

        private static string? Root(string root) =>
            !Path.IsPathRooted(root) || Path.GetDirectoryName(Path.GetFullPath(root)) is null
                ? null // a relative root, or "/" as a home directory, anchors nothing
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        private static bool IsUnder(string path, string? root) =>
            root is not null && Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        // Linux: the binary's own dir; macOS: up to the enclosing bundle.
        private bool EndsUnanchoredWalk(string dir) =>
            Platform != HostPlatform.MacOS || dir.EndsWith(".app", StringComparison.Ordinal)
            || Path.GetDirectoryName(dir) is not { } parent || !parent.Contains(".app", StringComparison.Ordinal);
    }

    private static BlenderResolution Judge(
        BlenderCandidate candidate, ConfigSource source, bool allowUnsupported, IReadOnlyList<BlenderCandidate> alternatives, string origin)
    {
        var resolution = new BlenderResolution
        {
            Path = candidate.Path,
            Source = source,
            Version = candidate.Probe.Version,
            BuildHash = candidate.Probe.BuildHash,
            Support = candidate.Support,
            Alternatives = alternatives,
        };
        if (candidate.Probe.Version is not { } version)
        {
            return resolution with
            {
                Check = "blender.version",
                Problem = $"cannot read the Blender version of {origin}: {candidate.Probe.Error}",
                Hint = "check that the path is a Blender binary that runs on this machine",
            };
        }

        if (candidate.Support == BlenderSupport.Unsupported)
        {
            return allowUnsupported
                ? resolution with { UnsupportedAllowed = true, Warning = SupportedBlender.Unsupported(version, origin) + "; allowed by blender.allowUnsupported" }
                : resolution with { Check = "blender.version", Problem = SupportedBlender.Unsupported(version, origin), Hint = SupportedBlender.Remedy };
        }

        return resolution with { Warning = SupportedBlender.Warning(version) };
    }

    private static bool IsSupported(BlenderCandidate candidate) => candidate.Support is BlenderSupport.Reference or BlenderSupport.Supported;

    /// <summary>
    /// True for an existing file with an execute bit. Total: it never throws. On Unix
    /// <c>File.Exists</c> is true for a symlink whose target is missing or unreachable
    /// (it falls back to <c>lstat</c>); <paramref name="unreachable"/> then says why.
    /// </summary>
    private static bool IsExecutableFile(string path, out string? unreachable)
    {
        unreachable = null;
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try
        {
            return (File.GetUnixFileMode(path) & AnyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string? target;
            try
            {
                target = new FileInfo(path).LinkTarget;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                target = null;
            }

            var to = target is null ? "" : $" ({target})";
            unreachable = ex is FileNotFoundException or DirectoryNotFoundException
                ? $"is a broken symlink: its target{to} does not exist or cannot be reached"
                : $"cannot be read{to}: {ex.Message}";
            return false;
        }
    }

    /// <summary>The path and every symlink hop after it, bounded; a broken or looping chain stops early.</summary>
    private static List<string> LinkChain(string path)
    {
        const int MaxHops = 40;
        var chain = new List<string> { Path.GetFullPath(path) };
        try
        {
            while (chain.Count <= MaxHops && File.ResolveLinkTarget(chain[^1], returnFinalTarget: false) is { } next)
            {
                chain.Add(next.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return chain;
    }

    /// <summary>The fully resolved path, so <c>/usr/bin/blender → /opt/blender/blender</c> is probed once.</summary>
    private static string Canonical(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unresolvable (a loop, or an untraversable dir): the lexical path is then judged,
            // and the candidate fails IsExecutableFile or the trust rule instead of throwing.
            return Path.GetFullPath(path);
        }
    }

    private static IEnumerable<string> Matching(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetDirectories(directory, pattern).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Describe(IEnumerable<BlenderCandidate> candidates) =>
        string.Join(", ", candidates.Select(c => c.Probe.Version is { } v ? $"{v} at {c.Path}" : $"{c.Path} ({c.Probe.Error})"));

    private static string SearchedDescription(BlenderSearch search) => search.Platform == HostPlatform.MacOS
        ? "/Applications/Blender.app, ~/Applications/Blender.app, /Applications/Blender*.app and Spotlight"
        : "PATH, /usr/bin, /usr/local/bin, /opt/blender*, ~/.local/bin and ~/blender*";

    [GeneratedRegex(@"build hash:\s*(\S+)")]
    private static partial Regex BuildHash();
}
