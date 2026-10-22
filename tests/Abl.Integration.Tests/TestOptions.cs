using Abl.Core;
using Abl.Core.Config;

namespace Abl.Integration.Tests;

/// <summary>
/// Options for in-process tests: the 008 resolver over the test environment
/// (<c>ABL_BLENDER</c> on the CI lane), never the user's config file (plan M10).
/// </summary>
internal static class TestOptions
{
    public static AblOptions FromEnvironment() =>
        AblConfig.Resolve(ConfigInputs.FromProcess() with { LoadFile = false }).Options;
}
