namespace Abl.Core.Config;

/// <summary>
/// The environment allowlists for the Blender processes AgentBlender starts (008 FR-005,
/// plan A6). A worker gets <c>HOME</c>, <c>USER</c>, <c>PATH</c>, <c>LANG</c>,
/// <c>LC_*</c> and <c>TMPDIR</c>, plus the names in <c>worker.envPassthrough</c>; the
/// discovery <c>blender --version</c> probe gets the narrower <c>HOME</c>, <c>PATH</c>,
/// <c>LANG</c>, <c>LC_*</c> and <c>TMPDIR</c>, with no passthrough. <c>PYTHON*</c> never
/// reaches either, even when listed (the resolver already refuses such names). Agent API
/// keys, tokens and other stray variables in the daemon's environment are dropped. The
/// detached daemon itself (<c>abl daemon start</c>, plan A6) keeps a wider list: the
/// configuration inputs (<c>ABL_*</c>, <c>OTEL_*</c>, <c>XDG_*</c>, <c>Logging__*</c>), the
/// .NET root and the user's session basics, plus the <c>worker.envPassthrough</c> names, so a
/// listed variable reaches workers from a detached daemon as it does from a foreground one.
/// A listed name that steers the .NET host or Kestrel (<c>DOTNET_*</c>, <c>COREHOST_*</c>,
/// <c>CORECLR_*</c>, <c>ASPNETCORE_*</c>, <c>Kestrel__*</c>, and the unprefixed <c>URLS</c>,
/// <c>HTTP_PORTS</c>, <c>HTTPS_PORTS</c>, <c>AllowedHosts</c>) is never admitted to the daemon:
/// it would configure the daemon, not Blender. Listed loader variables (<c>LD_*</c>,
/// <c>DYLD_*</c>) do reach the daemon, deliberately: they could not reach the workers otherwise.
/// </summary>
public static class ChildEnvironment
{
    /// <summary>The fixed names a worker keeps, besides <c>LC_*</c>.</summary>
    public static IReadOnlyList<string> WorkerNames { get; } = ["HOME", "USER", "PATH", "LANG", "TMPDIR"];

    /// <summary>The fixed names the version probe keeps, besides <c>LC_*</c>.</summary>
    public static IReadOnlyList<string> ProbeNames { get; } = ["HOME", "PATH", "LANG", "TMPDIR"];

    /// <summary>The fixed names the detached daemon keeps, besides the prefixed families.</summary>
    public static IReadOnlyList<string> DaemonNames { get; } =
        ["HOME", "USER", "LOGNAME", "PATH", "SHELL", "LANG", "TZ", "TMPDIR", "DOTNET_ROOT"];

    /// <summary>The variable families the detached daemon keeps (configuration and logging inputs).</summary>
    public static IReadOnlyList<string> DaemonPrefixes { get; } = ["LC_", "XDG_", "ABL_", "OTEL_", "Logging__"];

    /// <summary>True when the detached daemon may receive <paramref name="name"/> (plan A6).</summary>
    public static bool AllowsForDaemon(string name) =>
        DaemonNames.Contains(name) || DaemonPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The variable families that steer the daemon's own runtime or web host (compared ignoring case).</summary>
    public static IReadOnlyList<string> HostSteeringPrefixes { get; } = ["DOTNET_", "COREHOST_", "CORECLR_", "ASPNETCORE_", "Kestrel__"];

    /// <summary>
    /// The unprefixed ASP.NET Core host keys the web host reads from any environment variable
    /// (compared ignoring case; 2a round-2 eng-code L-3).
    /// </summary>
    public static IReadOnlyList<string> HostSteeringNames { get; } = ["URLS", "HTTP_PORTS", "HTTPS_PORTS", "ALLOWEDHOSTS"];

    /// <summary>
    /// True when <c>abl daemon start</c> passes <paramref name="name"/> to the detached daemon:
    /// the A6 list, or a <c>worker.envPassthrough</c> name that neither is <c>PYTHON*</c> nor
    /// steers the host.
    /// </summary>
    public static bool AllowsForDetachedDaemon(string name, IReadOnlyCollection<string> passthrough) =>
        AllowsForDaemon(name)
        || (passthrough.Contains(name) && !IsPython(name)
            && !HostSteeringPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            && !HostSteeringNames.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>True when a worker may receive <paramref name="name"/>.</summary>
    public static bool AllowsForWorker(string name, IReadOnlyCollection<string> passthrough) =>
        !IsPython(name) && (IsLocale(name) || WorkerNames.Contains(name) || passthrough.Contains(name));

    /// <summary>True when the version probe may receive <paramref name="name"/>.</summary>
    public static bool AllowsForProbe(string name) =>
        !IsPython(name) && (IsLocale(name) || ProbeNames.Contains(name));

    /// <summary>
    /// Removes, in place, every variable <paramref name="allowed"/> rejects. Pass a
    /// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>, which starts as a copy
    /// of this process's environment.
    /// </summary>
    public static void Restrict(IDictionary<string, string?> environment, Func<string, bool> allowed)
    {
        foreach (var name in environment.Keys.ToList())
        {
            if (!allowed(name))
            {
                environment.Remove(name);
            }
        }
    }

    private static bool IsPython(string name) => name.StartsWith("PYTHON", StringComparison.Ordinal);

    private static bool IsLocale(string name) => name.StartsWith("LC_", StringComparison.Ordinal);
}
