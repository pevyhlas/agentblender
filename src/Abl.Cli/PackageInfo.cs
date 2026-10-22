using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Workers;

namespace Abl.Cli;

/// <summary>
/// <c>abl --version --json</c> (008 FR-014): what this package is. The release bundle's
/// <c>provenance.json</c> takes the supported Blender line and the packaged worker digest from
/// the installed package through it, so the record describes what was installed.
/// </summary>
public static class PackageInfo
{
    public static JsonObject ToJson(string packageDirectory)
    {
        string? manifest;
        try
        {
            manifest = WorkerManifest.Read(packageDirectory)?.Digest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            manifest = null; // `abl doctor --check worker.files` says why
        }

        return new JsonObject
        {
            ["tool"] = "abl",
            ["version"] = AblVersion.Current,
            ["supportedBlender"] = new JsonObject
            {
                ["line"] = SupportedBlender.Line,
                ["reference"] = SupportedBlender.Reference.ToString(),
            },
            ["worker"] = new JsonObject { ["manifestDigest"] = manifest },
        };
    }
}
