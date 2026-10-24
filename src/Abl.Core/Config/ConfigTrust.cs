namespace Abl.Core.Config;

/// <summary>
/// Trust rules for files that choose what the daemon executes or hold a secret (008 FR-002,
/// plan A2/M1). The config file names the programs the daemon runs (<c>blender.path</c>,
/// <c>paths.workerDir</c>), so whoever can write it can run code as this user.
/// </summary>
public static class ConfigTrust
{
    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private const UnixFileMode GroupOrOtherAny =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// The config file rule: the file and its directory must be owned by the current user
    /// and not writable by group or others. A symlinked file is judged at its target, and
    /// the target's directory must pass too. Readability does not matter (no inline secrets).
    /// </summary>
    /// <returns>Null when trusted, otherwise the reason with a remedy, for <c>config.file</c>.</returns>
    public static string? CheckConfigFile(IConfigFileSystem fs, string path)
    {
        var file = fs.Inspect(path);
        if (file is null)
        {
            return $"{path} does not exist";
        }

        if (!file.TargetExists)
        {
            return $"{path} is a symlink to a missing target {file.Target}";
        }

        if (file.IsDirectory)
        {
            return $"{Describe(file)} is a directory, not a file";
        }

        if (!file.IsRegularFile)
        {
            return $"{Describe(file)} is not a regular file";
        }

        if (Writable(file) is { } fileProblem)
        {
            return fileProblem;
        }

        var directories = new List<string> { Parent(path) };
        if (file.IsSymlink && Parent(file.Target) is var targetDir && targetDir != directories[0])
        {
            directories.Add(targetDir);
        }

        foreach (var directory in directories)
        {
            var facts = fs.Inspect(directory);
            if (facts is not { TargetExists: true, IsDirectory: true })
            {
                return $"config directory {directory} is not a readable directory";
            }

            if (Writable(facts) is { } directoryProblem)
            {
                return directoryProblem;
            }
        }

        return null;
    }

    /// <summary>
    /// The token-file rule: owned by the current user with no group or other access at all
    /// (judged at the target of a symlink). A failing token file refuses the desktop tier.
    /// </summary>
    /// <returns>Null when trusted, otherwise the reason with a remedy.</returns>
    public static string? CheckTokenFile(IConfigFileSystem fs, string path)
    {
        var file = fs.Inspect(path);
        if (file is null || !file.TargetExists)
        {
            return $"token file {path} does not exist";
        }

        if (file.IsDirectory)
        {
            return $"token file {Describe(file)} is a directory";
        }

        if (!file.IsRegularFile)
        {
            return $"token file {Describe(file)} is not a regular file";
        }

        if (AllowAclProblem(file) is { } aclProblem)
        {
            return aclProblem;
        }

        if (!file.OwnedByCurrentUser)
        {
            return $"token file {Describe(file)} is owned by another user; recreate it as this user";
        }

        if ((file.Mode & GroupOrOtherAny) != 0)
        {
            return $"token file {Describe(file)} is accessible by group or others (mode {Octal(file.Mode)}); " +
                $"run: chmod 600 {file.Target}";
        }

        return null;
    }

    private static string? Writable(PathFacts facts)
    {
        if (AllowAclProblem(facts) is { } aclProblem)
        {
            return aclProblem;
        }
        if (!facts.OwnedByCurrentUser)
        {
            return $"{Describe(facts)} is owned by another user; it must be owned by this user (chown it, " +
                "or point ABL_CONFIG at a file you own)";
        }

        if ((facts.Mode & GroupOrOtherWrite) != 0)
        {
            return $"{Describe(facts)} is writable by group or others (mode {Octal(facts.Mode)}); " +
                $"run: chmod go-w {facts.Target}";
        }

        return null;
    }

    /// <summary>Mode bits alone cannot establish privacy in the presence of macOS allow ACLs.</summary>
    public static string? AllowAclProblem(PathFacts facts) => facts.HasAllowAcl
        ? $"{Describe(facts)} has an ACL with an allow entry; remove it (chmod -N) or choose a path without allow ACLs"
        : null;

    private static string Describe(PathFacts facts) =>
        facts.IsSymlink ? $"{facts.Path} (symlink to {facts.Target})" : facts.Path;

    private static string Parent(string path) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } parent ? parent : "/";

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(4, '0');
}
