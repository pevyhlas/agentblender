namespace Abl.Core.Workers;

/// <summary>
/// Which hardware tier a worker runs on (feature 003, D-06). <c>Local</c> is the host's
/// own GPU over AF_UNIX; <c>Desktop</c> is a remote GPU workstation reached over TCP through
/// the desktop launcher. The tier rides on the render result/sidecar so callers and telemetry
/// can tell where pixels were produced.
/// </summary>
public enum WorkerTier
{
    Local,
    Desktop,
}
