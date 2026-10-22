using System.Globalization;
using System.Text.RegularExpressions;

namespace Abl.Core.Config;

/// <summary>A Blender release number, <c>major.minor.patch</c>.</summary>
public sealed partial record BlenderVersion(int Major, int Minor, int Patch) : IComparable<BlenderVersion>
{
    /// <summary>
    /// Reads the version from the start of a version string (<c>5.2.1</c>, <c>5.2.1 LTS</c>;
    /// <c>bpy.app.version_string</c>) or from a <c>blender --version</c> first line
    /// (<c>Blender 5.2.1</c>).
    /// </summary>
    public static BlenderVersion? Parse(string? text)
    {
        if (text is null || VersionPattern().Match(text.Trim()) is not { Success: true } match)
        {
            return null;
        }

        return new BlenderVersion(Number(match.Groups[1].Value), Number(match.Groups[2].Value), Number(match.Groups[3].Value));

        static int Number(string digits) =>
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
    }

    public int CompareTo(BlenderVersion? other) =>
        other is null ? 1
        : Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"^(?:Blender )?(\d{1,6})\.(\d{1,6})\.(\d{1,6})")]
    private static partial Regex VersionPattern();
}

/// <summary>How a Blender version relates to the supported line (008 FR-003, plan A4 rule b).</summary>
public enum BlenderSupport
{
    /// <summary>The reference release (5.2.1): replay digests are qualified against it.</summary>
    Reference,

    /// <summary>Another patch of the supported line: usable, with a warning.</summary>
    Supported,

    /// <summary>Outside the supported line: refused unless <c>blender.allowUnsupported</c>.</summary>
    Unsupported,
}

/// <summary>
/// The one place the supported Blender line and its reference release live; doctor, setup,
/// the worker hello check and provenance use it (plan A4). 5.2.x is supported, 5.2.1 is the
/// reference, another 5.2 patch is a warning, any other line is refused unless
/// <c>blender.allowUnsupported</c> is set.
/// </summary>
public static class SupportedBlender
{
    public const int Major = 5;
    public const int Minor = 2;

    /// <summary>The reference release (D-14 as amended by D-16).</summary>
    public static readonly BlenderVersion Reference = new(Major, Minor, 1);

    /// <summary>The supported line as people write it.</summary>
    public const string Line = "5.2.x";

    /// <summary>Where to get the supported line (never downloaded by AgentBlender, O-8).</summary>
    public const string DownloadPage = "https://www.blender.org/download/lts/";

    public static BlenderSupport Classify(BlenderVersion version) =>
        version == Reference ? BlenderSupport.Reference
        : version.Major == Major && version.Minor == Minor ? BlenderSupport.Supported
        : BlenderSupport.Unsupported;

    /// <summary>The wire name of a support level.</summary>
    public static string Name(BlenderSupport support) => support switch
    {
        BlenderSupport.Reference => "reference",
        BlenderSupport.Supported => "supported",
        _ => "unsupported",
    };

    /// <summary>The warning for a supported patch other than the reference, else null.</summary>
    public static string? Warning(BlenderVersion version) =>
        Classify(version) == BlenderSupport.Supported
            ? $"Blender {version} is supported, but the reference is {Reference}; replay digests are qualified on {Reference}"
            : null;

    /// <summary>The refusal text for an unsupported version.</summary>
    public static string Unsupported(BlenderVersion version, string where) =>
        $"Blender {version} at {where} is not supported (supported {Line}, reference {Reference})";

    /// <summary>The remedy for an unsupported or missing Blender.</summary>
    public const string Remedy =
        "install Blender 5.2 LTS (" + DownloadPage + ") and set blender.path or ABL_BLENDER to it, " +
        "or set blender.allowUnsupported to use this version anyway";

    /// <summary>
    /// The <c>blender.found</c> remedy when no Blender was configured or discovered (1b
    /// eng-arch LOW-A: one text for discovery and the spawn gate).
    /// </summary>
    public const string FoundRemedy =
        "set blender.path or ABL_BLENDER to a Blender 5.2 LTS binary (" + DownloadPage + ")";

    /// <summary>The <c>blender.found</c> remedy when an explicit path is missing or cannot be started.</summary>
    public const string FixPathRemedy =
        "fix blender.path / ABL_BLENDER, or unset it to let AgentBlender discover Blender; " + DownloadPage;
}
