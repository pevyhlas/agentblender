using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core.Config;
using Abl.Core.Lifecycle;

namespace Abl.Cli.Host;

/// <summary>
/// <c>abl mcp config --client claude|opencode [--json]</c> (008 FR-012, plan D2): prints the
/// <c>agentblender</c> HTTP entry for the effective port, the file it belongs in and, for
/// Claude Code, the equivalent <c>claude mcp add</c> command. It writes nothing; the shapes
/// stay provisional until AGD S03 is reviewed, and <c>mcp install</c> is deferred.
/// </summary>
public static class McpConfig
{
    /// <summary>The MCP server name every client entry uses.</summary>
    public const string ServerName = "agentblender";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The MCP endpoint on <paramref name="port"/>: <c>daemon_status.url</c> plus <c>/mcp</c> (AC-30).</summary>
    public static string Url(int port) => DaemonPaths.UrlFor(port) + "/mcp";

    /// <summary>The entry for <paramref name="client"/>, the files it belongs in and the CLI command (Claude Code only).</summary>
    public static JsonObject Describe(string client, int port)
    {
        var url = Url(port);
        return client switch
        {
            "claude" => new JsonObject
            {
                ["client"] = "claude",
                ["url"] = url,
                ["file"] = ".mcp.json",
                ["scope"] = "project",
                ["entry"] = new JsonObject
                {
                    ["mcpServers"] = new JsonObject { [ServerName] = new JsonObject { ["type"] = "http", ["url"] = url } },
                },
                ["command"] = $"claude mcp add --transport http {ServerName} {url}",
            },
            "opencode" => new JsonObject
            {
                ["client"] = "opencode",
                ["url"] = url,
                ["file"] = "opencode.json",
                ["scope"] = "project",
                ["userFile"] = "~/.config/opencode/opencode.json",
                ["entry"] = new JsonObject
                {
                    ["mcp"] = new JsonObject { [ServerName] = new JsonObject { ["type"] = "remote", ["url"] = url, ["enabled"] = true } },
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(client), client, "claude or opencode"),
        };
    }

    /// <summary>The command. Exit codes: 0 ok, 1 configuration refused, 2 usage.</summary>
    public static int Run(IReadOnlyList<string> rest, ConfigInputs inputs, TextWriter stdout, TextWriter stderr)
    {
        var json = false;
        string? client = null;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--client" when i + 1 < rest.Count && rest[i + 1] is "claude" or "opencode":
                    client = rest[++i];
                    break;
                default:
                    return Usage(stderr);
            }
        }

        if (client is null)
        {
            return Usage(stderr);
        }

        int port;
        try
        {
            port = AblConfig.Resolve(inputs with { Blender = null }).Options.Port;
        }
        catch (ConfigException error)
        {
            HostVerbs.WriteError(stderr, error);
            return 1;
        }

        var described = Describe(client, port);
        if (json)
        {
            stdout.WriteLine(described.ToJsonString());
            return 0;
        }

        stdout.WriteLine(client == "claude"
            ? $"// Claude Code, project scope: {described["file"]}   (or: {described["command"]})"
            : $"// OpenCode, project scope: {described["file"]}   (user scope: {described["userFile"]})");
        stdout.WriteLine(described["entry"]!.ToJsonString(Indented));
        return 0;
    }

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("abl: mcp config takes --client claude|opencode and optionally --json");
        return 2;
    }
}
