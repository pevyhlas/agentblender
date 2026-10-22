using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Abl.Cli.Host;

/// <summary>
/// What a user service runs (008 FR-008, plan B3 "Services"): the absolute program and its
/// arguments (<c>abl daemon run --log-file …</c>, or <c>dotnet abl.dll daemon run …</c> under
/// the muxer, N2), its environment and the state dir it runs in. The environment is exact
/// (T035 review round 1, eng-security MEDIUM-1): the unit starts the daemon through
/// <c>/usr/bin/env -i</c> with only <paramref name="Environment"/> (the login identity names
/// recorded at install) and <c>DOTNET_ROOT</c>, so nothing in the service manager's own
/// environment (<c>environment.d</c>, <c>launchctl setenv</c>, an imported login
/// environment) reaches it. The configuration comes from the config file.
/// </summary>
public sealed record ServiceDefinition(
    string Program, IReadOnlyList<string> Arguments, string DotnetRoot, string LogFile, string StateDir,
    IReadOnlyList<KeyValuePair<string, string>> Environment)
{
    /// <summary>The program that clears the inherited environment (present on Linux and macOS).</summary>
    public const string Env = "/usr/bin/env";

    /// <summary>The variables the daemon gets: <see cref="Environment"/>, then <c>DOTNET_ROOT</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Variables => [.. Environment, new("DOTNET_ROOT", DotnetRoot)];

    /// <summary><c>env -i NAME=value… DOTNET_ROOT=… </c>, the program, then its arguments.</summary>
    public IReadOnlyList<string> ExecStart => [Env, "-i", .. Variables.Select(v => $"{v.Key}={v.Value}"), Program, .. Arguments];
}

/// <summary>
/// The systemd user unit and launchd agent that <c>abl daemon install-service</c> writes,
/// and their parsers (for <c>start</c>/<c>stop</c> handoff, <c>status</c> and doctor
/// <c>service.state</c>). The text is golden-tested (AC-22): no <c>ABL_*</c> key and no
/// secret is ever written.
/// </summary>
public static partial class ServiceUnits
{
    /// <summary>The systemd user unit name.</summary>
    public const string SystemdName = "abl-daemon.service";

    /// <summary>The launchd job label.</summary>
    public const string LaunchdLabel = "dev.agentblender.daemon";

    /// <summary>
    /// A service's definition: <paramref name="program"/>, then <paramref name="prefix"/>, then
    /// <c>daemon run --log-file</c>, run with only <paramref name="environment"/> (in this
    /// order) and <c>DOTNET_ROOT</c>.
    /// </summary>
    public static ServiceDefinition Define(
        string program, IReadOnlyList<string> prefix, string dotnetRoot, string logFile, string stateDir,
        IReadOnlyList<KeyValuePair<string, string>> environment) =>
        new(program, [.. prefix, "daemon", "run", "--log-file", logFile], dotnetRoot, logFile, stateDir, environment);

    /// <summary>
    /// The variables an <c>ExecStart</c> written by <see cref="Systemd"/> or <see cref="Launchd"/>
    /// gives the daemon (the <c>NAME=value</c> words after <c>env -i</c>), or null when it does not
    /// start through <c>env -i</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? EnvironmentOf(IReadOnlyList<string>? execStart)
    {
        if (execStart is not [ServiceDefinition.Env, "-i", ..])
        {
            return null;
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in execStart.Skip(2))
        {
            var eq = word.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                break;
            }

            variables[word[..eq]] = word[(eq + 1)..];
        }

        return variables;
    }

    /// <summary>The program and arguments an <c>ExecStart</c> runs after its <c>env -i</c> assignments (the whole argv otherwise).</summary>
    public static IReadOnlyList<string>? CommandOf(IReadOnlyList<string>? execStart) =>
        execStart is null ? null
        : execStart is [ServiceDefinition.Env, "-i", ..] ? [.. execStart.Skip(2).SkipWhile(w => w.IndexOf('=', StringComparison.Ordinal) > 0)]
        : execStart;

    /// <summary>Where launchd sends the output written before the daemon's own redirect (a missing runtime, for example).</summary>
    public static string LaunchdOutput(string stateDir) => Path.Combine(stateDir, "launchd", "daemon.out");

    /// <summary>The systemd user unit (Linux).</summary>
    /// <exception cref="ArgumentException">A value holds a control character, which no unit line can carry.</exception>
    public static string Systemd(ServiceDefinition service)
    {
        Validate(service);
        var text = $"""
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
            ExecStart={string.Join(' ', service.ExecStart.Select(SystemdExecWord))}
            WorkingDirectory={service.StateDir.Replace("%", "%%", StringComparison.Ordinal)}
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

            """;
        return text.ReplaceLineEndings("\n");
    }

    /// <summary>The launchd agent property list (macOS).</summary>
    /// <exception cref="ArgumentException">A value holds a control character.</exception>
    public static string Launchd(ServiceDefinition service)
    {
        Validate(service);
        var output = LaunchdOutput(service.StateDir);
        var builder = new StringBuilder();
        builder.Append("""
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
            """);
        builder.Append('\n').Append(CultureInfo.InvariantCulture, $"  <string>{LaunchdLabel}</string>\n");
        builder.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var word in service.ExecStart)
        {
            builder.Append(CultureInfo.InvariantCulture, $"    <string>{Xml(word)}</string>\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"""
              </array>
              <key>WorkingDirectory</key>
              <string>{Xml(service.StateDir)}</string>
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
              <string>{Xml(output)}</string>
              <key>StandardErrorPath</key>
              <string>{Xml(output)}</string>
            </dict>
            </plist>

            """);
        return builder.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>The <c>ExecStart</c> words of a unit written by <see cref="Systemd"/>, or null when it has none.</summary>
    public static IReadOnlyList<string>? ReadSystemdExecStart(string unit) =>
        Line(unit, "ExecStart=") is { } line ? SplitSystemd(line) : null;

    /// <summary>The <c>DOTNET_ROOT</c> of a unit written by <see cref="Systemd"/>, or null.</summary>
    public static string? ReadSystemdDotnetRoot(string unit) =>
        EnvironmentOf(ReadSystemdExecStart(unit))?.GetValueOrDefault("DOTNET_ROOT");

    /// <summary>Where a plist written by <see cref="Launchd"/> sends the job's own output, or null.</summary>
    public static string? ReadLaunchdOutput(string plist) => LaunchdValue(plist, "StandardOutPath") is { Name.LocalName: "string" } path ? path.Value : null;

    /// <summary>The <c>ProgramArguments</c> of a plist written by <see cref="Launchd"/>, or null when it does not parse.</summary>
    public static IReadOnlyList<string>? ReadLaunchdExecStart(string plist) =>
        LaunchdValue(plist, "ProgramArguments") is { Name.LocalName: "array" } array
            ? [.. array.Elements("string").Select(e => e.Value)]
            : null;

    /// <summary>The <c>DOTNET_ROOT</c> of a plist written by <see cref="Launchd"/>, or null.</summary>
    public static string? ReadLaunchdDotnetRoot(string plist) =>
        EnvironmentOf(ReadLaunchdExecStart(plist))?.GetValueOrDefault("DOTNET_ROOT");

    /// <summary>The value after <c>--log-file</c> in an argv, or null.</summary>
    public static string? LogFileOf(IReadOnlyList<string>? execStart)
    {
        if (execStart is null)
        {
            return null;
        }

        for (var i = 0; i + 1 < execStart.Count; i++)
        {
            if (execStart[i] == "--log-file")
            {
                return execStart[i + 1];
            }
        }

        return null;
    }

    private static void Validate(ServiceDefinition service)
    {
        // U+2028 and U+2029 are not control characters, but they end a line for
        // ReplaceLineEndings (T035 review round 1, eng-security LOW-1).
        foreach (var value in service.ExecStart.Append(service.DotnetRoot).Append(service.StateDir))
        {
            if (value.Length == 0 || value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            {
                throw new ArgumentException(
                    $"a service value is empty or holds a control character or line separator: '{new string([.. value.Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c)])}'",
                    nameof(service));
            }
        }

        // env takes every leading NAME=value word as an assignment, so the program must not
        // look like one, and a variable name must be a plain name.
        if (service.Program.Contains('=', StringComparison.Ordinal) || !Path.IsPathFullyQualified(service.Program))
        {
            throw new ArgumentException($"the service program must be an absolute path without '=': '{service.Program}'", nameof(service));
        }

        if (service.Variables.FirstOrDefault(v => !VariableName().IsMatch(v.Key)) is { Key: { } bad })
        {
            throw new ArgumentException($"not a variable name: '{bad}'", nameof(service));
        }
    }

    /// <summary>One <c>ExecStart</c> word: quoted when needed, specifiers and variables escaped.</summary>
    private static string SystemdExecWord(string word) =>
        SystemdQuote(word).Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal);

    private static string SystemdQuote(string word) => PlainWord().IsMatch(word)
        ? word
        : "\"" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>The inverse of <see cref="SystemdExecWord"/> over a whole line.</summary>
    private static List<string> SplitSystemd(string line)
    {
        var words = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ')
            {
                i++;
                continue;
            }

            var word = new StringBuilder();
            if (line[i] == '"')
            {
                for (i++; i < line.Length && line[i] != '"'; i++)
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                    }

                    word.Append(line[i]);
                }

                i++; // the closing quote
            }
            else
            {
                for (; i < line.Length && line[i] != ' '; i++)
                {
                    word.Append(line[i]);
                }
            }

            words.Add(word.ToString().Replace("%%", "%", StringComparison.Ordinal).Replace("$$", "$", StringComparison.Ordinal));
        }

        return words;
    }

    private static string? Line(string unit, string key) =>
        unit.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal))?[key.Length..];

    private static XElement? LaunchdValue(string plist, string key)
    {
        try
        {
            var document = XDocument.Parse(plist, LoadOptions.None);
            return document.Root?.Element("dict") is { } dict ? DictValue(dict, key) : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static XElement? DictValue(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    private static string Xml(string value) => SecurityElement.Escape(value);

    [GeneratedRegex(@"^[A-Za-z0-9/._+:=,@-]+$")]
    private static partial Regex PlainWord();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}

/// <summary>
/// The <c>DOTNET_ROOT</c> a service gets (008 FR-008, N4): the root of the runtime running
/// this CLI, derived from its runtime directory, in preference to an inherited
/// <c>DOTNET_ROOT</c> (which may name another install); a Homebrew Cellar root, which a
/// <c>brew upgrade</c> deletes, becomes the stable <c>opt</c> link.
/// </summary>
public static partial class DotnetRoot
{
    /// <summary>The root, or null with the reason.</summary>
    /// <param name="runtimeDirectory">This runtime's directory (<c>&lt;root&gt;/shared/Microsoft.NETCore.App/10.x/</c>).</param>
    /// <param name="inherited">This process's <c>DOTNET_ROOT</c>, used only when the runtime directory has another shape.</param>
    /// <param name="directoryExists">The file-system seam (tests).</param>
    public static (string? Root, string? Problem) Resolve(string runtimeDirectory, string? inherited, Func<string, bool> directoryExists)
    {
        var root = Derive(runtimeDirectory)
            ?? (string.IsNullOrWhiteSpace(inherited) || !Path.IsPathFullyQualified(inherited)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(inherited)));
        return root is null
            ? (null, $"cannot tell which .NET runtime runs abl ({runtimeDirectory} is not <root>/shared/Microsoft.NETCore.App/<version>, and DOTNET_ROOT is not set to an absolute path)")
            : (Stable(root, directoryExists), null);
    }

    /// <summary><c>&lt;root&gt;</c> of <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>, or null.</summary>
    public static string? Derive(string runtimeDirectory)
    {
        var version = new DirectoryInfo(Path.TrimEndingDirectorySeparator(runtimeDirectory));
        return version.Parent is { Name: "Microsoft.NETCore.App" } framework
            && framework.Parent is { Name: "shared" } shared
            && shared.Parent is { } root
                ? Path.TrimEndingDirectorySeparator(root.FullName)
                : null;
    }

    /// <summary>
    /// A Homebrew Cellar path (<c>&lt;prefix&gt;/Cellar/dotnet/&lt;version&gt;/libexec…</c>) as its
    /// <c>&lt;prefix&gt;/opt/dotnet/libexec…</c> link when that link exists; any other path unchanged.
    /// </summary>
    public static string Stable(string path, Func<string, bool> directoryExists)
    {
        if (CellarPath().Match(path) is not { Success: true } cellar)
        {
            return path;
        }

        var link = $"{cellar.Groups["prefix"].Value}/opt/{cellar.Groups["formula"].Value}/libexec";
        return directoryExists(link) ? link + cellar.Groups["rest"].Value : path;
    }

    /// <summary>True when <paramref name="root"/> holds an ASP.NET Core 10 shared runtime (doctor <c>service.state</c>, H3).</summary>
    public static bool HasAspNetCore10(string root)
    {
        try
        {
            var shared = Path.Combine(root, "shared", "Microsoft.AspNetCore.App");
            return Directory.Exists(shared) && Directory.EnumerateDirectories(shared, "10.*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?<prefix>/.+)/Cellar/(?<formula>dotnet(?:@[0-9.]+)?)/[^/]+/libexec(?<rest>/.*)?$")]
    private static partial Regex CellarPath();
}
