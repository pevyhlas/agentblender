using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Abl.Cli;
using Abl.Cli.Host;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T052, T053, T054 (FR-012, FR-013; AC-30, AC-31): the MCP server instructions, the
/// golden unreachable hint, the <c>abl mcp config</c> snapshots, and the host-verb tables.
/// The initialize round-trip and the drift check against a running daemon are in the
/// integration suite (<c>AgentSurfaceSocketTests</c>).
/// </summary>
public sealed partial class AgentSurfaceTests
{
    [Fact]
    public void AC31_TheInstructionsStayInBudgetNameAgdForVectorWorkAndBatchRunForSweeps()
    {
        var text = AgentGuidance.ServerInstructions;
        var words = text.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

        Assert.InRange(words, 100, AgentGuidance.MaxInstructionWords);
        Assert.Contains("AgentGraphicDesign (`agd`)", text);
        Assert.Contains("vector logos/marks", text);
        Assert.Contains("`batch_run` a params table and judge one contact sheet", text);
        Assert.Contains("`delivery_export`", text);

        // Motion and family export are not offered (plan D3).
        foreach (var offered in new[] { "motion", "animation", "family", "video", "sprite" })
        {
            Assert.DoesNotContain(offered, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AC31_TheInstructionsCarryBothHalvesOfTheMetalClause()
    {
        // 009 plan "Agent-facing text": 008 T053 added the compile half; 009 T023 appends the
        // `abl doctor --check gpu.render` half now that the check exists (009 T021).
        Assert.Contains(
            "The first Metal render (`cycles_gpu` on a Mac) compiles kernels (~1 min; do not cancel it); `abl doctor --check gpu.render` primes the cache.",
            AgentGuidance.ServerInstructions);
    }

    [Fact]
    public void AC31_TheUnreachableHintIsGolden()
    {
        Assert.Equal(
            "abl: cannot reach the daemon at http://127.0.0.1:7811 — start it with 'abl daemon start' (diagnose: 'abl doctor')",
            CliText.Unreachable("http://127.0.0.1:7811"));
    }

    [Fact]
    public void FR013_TheAgentGuideQuotesTheInstructionsAndTheHintVerbatim()
    {
        // 008 T055: docs/agent-guide.md "says the same" as the initialize instructions.
        var guide = File.ReadAllText(Path.Combine(RepoRoot, "docs", "agent-guide.md"));
        var quoted = string.Join(' ', guide.Split('\n').Where(l => l.StartsWith("> ", StringComparison.Ordinal)).Select(l => l[2..]));

        Assert.Equal(Collapse(AgentGuidance.ServerInstructions), Collapse(quoted));
        Assert.Contains(CliText.Unreachable("<url>"), guide);
        Assert.Contains("The browser-based MCP Inspector is rejected by design", Collapse(guide)); // P0-d
        Assert.Contains("http://127.0.0.1:7811/mcp", guide);
        foreach (var offered in new[] { "animation", "video", "sprite sheet" })
        {
            Assert.DoesNotContain(offered, guide, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FR013_TheReadmeNamesTheAgdBoundaryAndLinksTheGuide()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
        var start = readme.IndexOf("## AgentBlender or AgentGraphicDesign?", StringComparison.Ordinal);
        Assert.True(start >= 0, "README lost its AB-vs-AGD section");
        var end = readme.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        var section = Collapse(readme[start..end]);

        foreach (var phrase in new[] { "vector logos and marks", "wordmarks", "pixel art", "icon and platform kits", "`batch_run`", "`delivery_export`", "AgentGraphicDesign (`agd`)", "(docs/agent-guide.md)", "MCP Inspector" })
        {
            Assert.Contains(phrase, section);
        }
    }

    private static string Collapse(string text) => WhiteSpace().Replace(text, " ").Trim();

    private static string RepoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "agentblender.slnx")))
                {
                    return d.FullName;
                }
            }

            throw new DirectoryNotFoundException("repository root (agentblender.slnx) not found above the test dir");
        }
    }

    [Theory]
    [InlineData("claude", false, """
        // Claude Code, project scope: .mcp.json   (or: claude mcp add --transport http agentblender http://127.0.0.1:7811/mcp)
        {
          "mcpServers": {
            "agentblender": {
              "type": "http",
              "url": "http://127.0.0.1:7811/mcp"
            }
          }
        }

        """)]
    [InlineData("opencode", false, """
        // OpenCode, project scope: opencode.json   (user scope: ~/.config/opencode/opencode.json)
        {
          "mcp": {
            "agentblender": {
              "type": "remote",
              "url": "http://127.0.0.1:7811/mcp",
              "enabled": true
            }
          }
        }

        """)]
    [InlineData("claude", true, """
        {"client":"claude","url":"http://127.0.0.1:7811/mcp","file":".mcp.json","scope":"project","entry":{"mcpServers":{"agentblender":{"type":"http","url":"http://127.0.0.1:7811/mcp"}}},"command":"claude mcp add --transport http agentblender http://127.0.0.1:7811/mcp"}

        """)]
    [InlineData("opencode", true, """
        {"client":"opencode","url":"http://127.0.0.1:7811/mcp","file":"opencode.json","scope":"project","userFile":"~/.config/opencode/opencode.json","entry":{"mcp":{"agentblender":{"type":"remote","url":"http://127.0.0.1:7811/mcp","enabled":true}}}}

        """)]
    public void AC30_McpConfigPrintsTheGoldenEntryAndReadsOnlyTheConfigFile(string client, bool json, string expected)
    {
        var fs = new FakeConfigFileSystem();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] args = json ? ["--client", client, "--json"] : ["--client", client];

        var code = HostVerbs.Run(("mcp", "config"), args, Inputs(fs), stdout, stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr.ToString());
        Assert.Equal(expected.ReplaceLineEndings("\n"), stdout.ToString().ReplaceLineEndings("\n"));
        Assert.All(fs.Reads, r => Assert.Equal("/home/u/.config/agentblender/config.json", r)); // reads nothing else, writes nothing
    }

    [Fact]
    public void AC30_TheEntryUrlIsTheDaemonStatusUrlPlusMcpForTheEffectivePort()
    {
        var fs = new FakeConfigFileSystem().File("/home/u/.config/agentblender/config.json", """{"version":1,"daemon":{"port":7123}}""");
        var stdout = new StringWriter();

        Assert.Equal(0, HostVerbs.Run(("mcp", "config"), ["--client", "claude", "--json"], Inputs(fs), stdout, new StringWriter()));

        var instance = new DaemonInstance(1, DateTimeOffset.UnixEpoch, "/s", null, 7123, "sha256:x", false);
        Assert.Equal(instance.Url + "/mcp", JsonNode.Parse(stdout.ToString())!["url"]!.GetValue<string>());
    }

    [Theory]
    [InlineData]
    [InlineData("--client")]
    [InlineData("--client", "cursor")]
    [InlineData("--client", "claude", "--scope", "user")]
    public void McpConfigUsage(params string[] args)
    {
        var stderr = new StringWriter();
        Assert.Equal(2, HostVerbs.Run(("mcp", "config"), args, Inputs(new FakeConfigFileSystem()), new StringWriter(), stderr));
        Assert.Contains("--client claude|opencode", stderr.ToString());
    }

    [Fact]
    public void SetupDoctorAndMcpConfigAreHostVerbsDisjointFromTheOpVerbs()
    {
        Assert.Contains(("mcp", "config"), HostVerbs.Verbs);
        Assert.Equal(new HashSet<string> { "setup", "doctor" }, HostVerbs.Commands.ToHashSet());
        var nouns = VerbMap.Verbs.Keys.Select(k => k.Item1).ToHashSet();
        Assert.Empty(HostVerbs.Commands.Intersect(nouns));
        Assert.DoesNotContain("mcp", nouns);
        Assert.DoesNotContain(OpSurface.All, op => op is "setup" or "doctor" or "mcp_config");
    }

    [Fact]
    public void TheWorkerSourceModulesAreTheWorkersOwnList()
    {
        // worker.py op_hello digests these modules in this order (inspection.py always loads).
        var worker = File.ReadAllText(Path.Combine(AblDefaults.WorkerDir(), "worker.py"));
        var tuple = ModulesTuple().Match(worker);
        Assert.True(tuple.Success, "worker.py no longer names its modules tuple");
        var modules = Regex.Matches(tuple.Groups[1].Value, "\"([a-z_]+\\.py)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Matches("import inspection", worker);
        modules.Add("inspection.py");

        Assert.Equal(modules, WorkerSource.Modules);
        Assert.Empty(WorkerSource.Missing(AblDefaults.WorkerDir()));
        Assert.Matches("^[0-9a-f]{64}$", WorkerSource.Digest(AblDefaults.WorkerDir()));
    }

    private static ConfigInputs Inputs(FakeConfigFileSystem fs) => new() { Env = new Dictionary<string, string>(), Home = "/home/u", FileSystem = fs };

    [GeneratedRegex(@"modules = \(([^)]*)\)")]
    private static partial Regex ModulesTuple();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
