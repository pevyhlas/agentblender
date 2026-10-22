using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Abl.Core.Lifecycle;

/// <summary>
/// The host's process table, for <c>abl daemon stop</c>: a daemon without its own process
/// group (a foreground <c>daemon run</c>) has no group to sweep, so its workers are found as
/// its descendants before it is signalled (2a round-2 eng-code M-1, AC-20).
/// <para>
/// The table comes only from a fixed source the user cannot redirect (2a round-3): the
/// kernel's <c>/proc</c> on Linux, else <c>ps</c> at an absolute system path (POSIX options;
/// always present on macOS). A program name without a slash is never started, because
/// <c>Process.Start</c> would resolve it through the current directory and <c>PATH</c>. A
/// source whose table does not contain the caller itself is not a process table. When no
/// source yields one, the result is <see cref="Table.Unknown"/>, never an empty table, so a
/// caller can say that it could not check.
/// </para>
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static partial class ProcessTree
{
    /// <summary>The system paths <c>ps</c> is run from, in order. Every entry is absolute.</summary>
    public static readonly IReadOnlyList<string> PsPaths = ["/bin/ps", "/usr/bin/ps"];

    private static readonly string[] PsArguments = ["-A", "-ww", "-o", "pid=", "-o", "ppid=", "-o", "args="];

    /// <summary>One row: pid, parent pid and the full argv as one string.</summary>
    public sealed record Row(int Pid, int ParentPid, string Args);

    /// <summary>
    /// A process table, or why none could be read: <see cref="Rows"/> is null exactly when
    /// <see cref="Unreadable"/> names the reason.
    /// </summary>
    public sealed record Table(IReadOnlyList<Row>? Rows, string? Unreadable)
    {
        public static Table Unknown(string reason) => new(null, reason);

        /// <summary>Every transitive descendant of <paramref name="pid"/>; empty when the table is unknown.</summary>
        public IReadOnlyList<Row> DescendantsOf(int pid) => Rows is null ? [] : ProcessTree.DescendantsOf(Rows, pid);

        /// <summary>
        /// Why this table cannot show that no worker of a daemon outlives it (AC-20), or null.
        /// A daemon with its own process group is checked by that group, so only one without
        /// (<paramref name="processGroup"/> null) depends on the table.
        /// </summary>
        public string? UncheckedFor(int? processGroup) => processGroup is null ? Unreadable : null;
    }

    /// <summary>
    /// Where a snapshot reads from: the <c>/proc</c> root (null: none), the <c>ps</c> paths,
    /// how a <c>ps</c> is run (path, arguments → exit code and stdout, or null when it could
    /// not start), and the caller's pid that a real table must contain.
    /// </summary>
    internal sealed record Source(
        string? ProcRoot,
        IReadOnlyList<string> PsPaths,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)?> Run,
        int Self);

    /// <summary>Every process on the host, or <see cref="Table.Unknown"/> with the reason.</summary>
    public static Table Snapshot() => Snapshot(new Source(
        OperatingSystem.IsLinux() ? "/proc" : null, PsPaths, RunProgram, Environment.ProcessId));

    internal static Table Snapshot(Source source)
    {
        var reasons = new List<string>();
        if (source.ProcRoot is { } proc)
        {
            var listed = ReadProc(proc);
            if (listed.Any(r => r.Pid == source.Self))
            {
                return new Table(listed, null);
            }

            reasons.Add($"{proc} does not list this process");
        }

        // Only an absolute path is ever started; a bare name would be looked up in the
        // current directory and PATH.
        var ps = source.PsPaths.FirstOrDefault(p => Path.IsPathFullyQualified(p) && File.Exists(p));
        if (ps is null)
        {
            reasons.Add($"no ps at {string.Join(" or ", source.PsPaths.Where(Path.IsPathFullyQualified))}");
        }
        else if (source.Run(ps, PsArguments) is not { } run)
        {
            reasons.Add($"{ps} could not be started");
        }
        else if (run.ExitCode != 0)
        {
            reasons.Add($"{ps} exited with code {run.ExitCode}");
        }
        else if (Parse(run.Output) is var rows && rows.Any(r => r.Pid == source.Self))
        {
            return new Table(rows, null);
        }
        else
        {
            reasons.Add($"{ps} output does not list this process");
        }

        return Table.Unknown($"the process table cannot be read ({string.Join("; ", reasons)})");
    }

    /// <summary>The rows of <c>ps -o pid= -o ppid= -o args=</c> output; lines that do not parse are skipped.</summary>
    public static IReadOnlyList<Row> Parse(string output)
    {
        var rows = new List<Row>();
        foreach (var line in output.Split('\n'))
        {
            if (RowPattern().Match(line) is { Success: true } row
                && int.TryParse(row.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && int.TryParse(row.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ppid))
            {
                rows.Add(new Row(pid, ppid, row.Groups[3].Value.TrimEnd('\r')));
            }
        }

        return rows;
    }

    /// <summary>
    /// One row from <c>/proc/&lt;pid&gt;/stat</c> and <c>cmdline</c>, or null when it does not parse.
    /// The argv is the NUL-separated cmdline joined with spaces, as <c>ps -o args=</c> prints
    /// it; a process without one (a kernel thread or a zombie) shows <c>[comm]</c>, and a
    /// zombie <c>[comm] &lt;defunct&gt;</c>, so it never matches a live worker's argv.
    /// </summary>
    public static Row? ParseProc(int pid, string stat, byte[] cmdline)
    {
        // "<pid> (<comm>) <state> <ppid> ...": comm may hold spaces and parentheses.
        var open = stat.IndexOf('(', StringComparison.Ordinal);
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return null;
        }

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid))
        {
            return null;
        }

        var argv = Encoding.UTF8.GetString(cmdline).TrimEnd('\0').Replace('\0', ' ');
        if (argv.Length == 0)
        {
            argv = $"[{stat[(open + 1)..close]}]" + (fields[0] == "Z" ? " <defunct>" : "");
        }

        return new Row(pid, ppid, argv);
    }

    /// <summary>Every transitive descendant of <paramref name="pid"/> in <paramref name="table"/>, parents before children.</summary>
    public static IReadOnlyList<Row> DescendantsOf(IReadOnlyList<Row> table, int pid)
    {
        var children = table.Where(r => r.Pid != r.ParentPid).ToLookup(r => r.ParentPid);
        var found = new List<Row>();
        var seen = new HashSet<int> { pid };
        var queue = new Queue<int>([pid]);
        while (queue.TryDequeue(out var parent))
        {
            foreach (var child in children[parent])
            {
                if (seen.Add(child.Pid))
                {
                    found.Add(child);
                    queue.Enqueue(child.Pid);
                }
            }
        }

        return found;
    }

    private static List<Row> ReadProc(string root)
    {
        var rows = new List<Row>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return rows;
        }

        foreach (var dir in entries)
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                if (ParseProc(pid, File.ReadAllText(Path.Combine(dir, "stat")), File.ReadAllBytes(Path.Combine(dir, "cmdline"))) is { } row)
                {
                    rows.Add(row);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // it exited while the table was read, or it is not ours to read
            }
        }

        return rows;
    }

    private static (int ExitCode, string Output)? RunProgram(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = errors.GetAwaiter().GetResult();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s*(.*)$")]
    private static partial Regex RowPattern();
}
