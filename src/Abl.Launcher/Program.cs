using Abl.Core;
using Abl.Launcher;

// Desktop remote-worker launcher (feature 003). Configure out-of-band, never committed:
//   ABL_LAUNCHER_BIND   host:port on a SPECIFIC private-network address (e.g. 192.0.2.10:7812)
//   ABL_LAUNCHER_TOKEN  shared bearer token (matches the host's ABL_DESKTOP_TOKEN)
//   ABL_BLENDER         path to blender.exe (required: the launcher does no discovery)
//   ABL_WORKER_DIR      worker/ directory (defaults to next to the binary; a Debug build also searches parent directories)
// The launcher reads only these variables; the daemon's config file (008 FR-002) and its
// Unix trust rule do not apply to this Windows-side program.
var bind = Environment.GetEnvironmentVariable("ABL_LAUNCHER_BIND");
var token = Environment.GetEnvironmentVariable("ABL_LAUNCHER_TOKEN");
if (string.IsNullOrWhiteSpace(bind) || string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine(
        "set ABL_LAUNCHER_BIND (host:port on a private-network address) and ABL_LAUNCHER_TOKEN");
    return 2;
}

if (Environment.GetEnvironmentVariable("ABL_BLENDER") is not { Length: > 0 } blenderPath)
{
    Console.Error.WriteLine("set ABL_BLENDER to the Blender binary (the launcher does no discovery)");
    return 2;
}

var workerDir = Environment.GetEnvironmentVariable("ABL_WORKER_DIR") is { Length: > 0 } worker
    ? worker
    : AblDefaults.WorkerDir();
LauncherOptions options;
try
{
    options = new LauncherOptions
    {
        BindEndpoint = LauncherOptions.ResolveBind(bind),
        Token = token,
        BlenderPath = blenderPath,
        WorkerDir = workerDir,
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (!File.Exists(options.BlenderPath))
{
    Console.Error.WriteLine($"blender not found at {options.BlenderPath} (set ABL_BLENDER)");
    return 2;
}

var logger = new ConsoleLogger<LauncherHost>();
var host = new LauncherHost(options, logger);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

host.Start();
try
{
    await host.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}

return 0;
