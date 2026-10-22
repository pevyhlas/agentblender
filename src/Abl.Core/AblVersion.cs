using System.Reflection;

namespace Abl.Core;

/// <summary>
/// The AgentBlender version (the informational version without build metadata). Every
/// assembly shares it (<c>Directory.Build.props</c>), so the CLI, the daemon record,
/// <c>/healthz</c> and <c>daemon_status</c> report one value.
/// </summary>
public static class AblVersion
{
    public static string Current { get; } =
        typeof(AblVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version
            ? version.Split('+')[0]
            : "0.0.0";
}
