namespace Abl.Cli.Host;

/// <summary>
/// How to run this CLI again (008 review N2): the detached re-spawn of <c>abl daemon start</c>
/// (and, in T035, a unit's <c>ExecStart</c>). An apphost (<c>abl</c>) runs as itself; when
/// the host is the <c>dotnet</c> muxer (<c>dotnet abl.dll …</c>), <see cref="Environment.ProcessPath"/>
/// is <c>dotnet</c>, so the entry assembly must follow it or the child would run
/// <c>dotnet daemon run</c>.
/// </summary>
public static class SelfCommand
{
    /// <summary>The program to start and the arguments that precede the verb.</summary>
    /// <param name="processPath">The running executable (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="entryAssembly">The entry assembly's path (<c>abl.dll</c>).</param>
    /// <exception cref="InvalidOperationException">The running program cannot be determined.</exception>
    public static (string FileName, IReadOnlyList<string> Prefix) Resolve(string? processPath, string? entryAssembly)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            throw new InvalidOperationException("cannot tell which program is running (no process path)");
        }

        if (!Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, []);
        }

        return string.IsNullOrEmpty(entryAssembly)
            ? throw new InvalidOperationException("running under dotnet, but the entry assembly has no path")
            : (processPath, [Path.GetFullPath(entryAssembly)]);
    }

    /// <summary><see cref="Resolve"/> for this process.</summary>
    public static (string FileName, IReadOnlyList<string> Prefix) Current() =>
        Resolve(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location);
}
