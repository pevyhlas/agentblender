namespace Abl.Core;

/// <summary>
/// The built-in defaults: the lowest precedence tier of <see cref="Config.AblConfig"/>, and
/// the initial values of a directly constructed <see cref="AblOptions"/> (tests). They read
/// no <c>ABL_*</c> variable; overrides belong to the resolver alone. Blender has no built-in
/// path (it is discovered, <see cref="Config.BlenderDiscovery"/>) and the runtime dir has its
/// own rule (<see cref="Config.RuntimePaths"/>).
/// </summary>
public static class AblDefaults
{
    public const int Port = 7811;
    public const string LogLevel = "information";
    public const string OtlpEndpoint = "http://127.0.0.1:4318";

    public static string StoreDir(string home) => Path.Combine(home, ".local", "share", "agentblender", "store");

    public static string RendersDir(string home) => Path.Combine(home, ".local", "share", "agentblender", "renders");

    /// <summary>
    /// Whether this build also looks for <c>worker/</c> in the directories above the binaries
    /// (a source checkout). Only a Debug build does (008 plan C1, L10); a Release build, which
    /// is what the <c>agentblender</c> tool package carries, resolves <c>worker/</c> only next
    /// to the binaries (package content) or from <c>paths.workerDir</c>/<c>ABL_WORKER_DIR</c>.
    /// </summary>
    public const bool SearchesParentsForWorker =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Directory holding worker.py (the only bpy-importing code, D-10).</summary>
    public static string WorkerDir() => WorkerDir(AppContext.BaseDirectory, SearchesParentsForWorker);

    /// <summary>
    /// <c>worker/</c> next to <paramref name="baseDirectory"/> (the packaged and published
    /// layout); with <paramref name="searchParents"/>, else the first <c>worker/</c> holding
    /// <c>worker.py</c> above it. Without a hit it is the packaged path, so <c>worker.files</c>
    /// names the directory that should have been there.
    /// </summary>
    internal static string WorkerDir(string baseDirectory, bool searchParents)
    {
        var baseDir = Path.Combine(baseDirectory, "worker");
        if (!searchParents || File.Exists(Path.Combine(baseDir, "worker.py")))
        {
            return baseDir;
        }

        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "worker");
            if (File.Exists(Path.Combine(candidate, "worker.py")))
            {
                return candidate;
            }
        }

        return baseDir;
    }

    internal static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
