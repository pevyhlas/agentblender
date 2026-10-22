using System.Text.Json.Nodes;
using Abl.Core.Sessions;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// Golden forms of the terse state text (Principle 2). These strings are what agents
/// pay tokens for — changing one is a deliberate surface change, not a refactor.
/// </summary>
public class StateTextGoldenTests
{
    [Fact]
    public void DeltaGoldenForm()
    {
        var delta = new JsonObject
        {
            ["added"] = new JsonArray("SmokeCube", "Lamp"),
            ["removed"] = new JsonArray("OldCube"),
            ["changed"] = new JsonArray("Camera"),
            ["materials"] = new JsonObject
            {
                ["added"] = new JsonArray("Steel"),
                ["removed"] = new JsonArray("Rust"),
            },
        };
        Assert.Equal(
            """
            + SmokeCube
            + Lamp
            - OldCube
            ~ Camera
            materials: +Steel -Rust
            """.ReplaceLineEndings("\n"),
            StateText.RenderDelta(delta));
    }

    [Fact]
    public void EmptyDeltaGoldenForm()
    {
        var delta = new JsonObject
        {
            ["added"] = new JsonArray(),
            ["removed"] = new JsonArray(),
            ["changed"] = new JsonArray(),
        };
        Assert.Equal("(no changes)", StateText.RenderDelta(delta));
    }

    [Fact]
    public void FullGoldenForm()
    {
        var state = new JsonObject
        {
            ["objects"] = new JsonObject
            {
                ["Camera"] = new JsonObject
                {
                    ["type"] = "CAMERA", ["parent"] = null, ["verts"] = 0, ["faces"] = 0,
                    ["materials"] = new JsonArray(),
                },
                ["Body"] = new JsonObject
                {
                    ["type"] = "MESH", ["parent"] = "Rig", ["verts"] = 8, ["faces"] = 6,
                    ["materials"] = new JsonArray("Steel", "Gold"),
                },
            },
            ["collections"] = new JsonArray("Collection"),
            ["materials"] = new JsonArray("Steel", "Gold"),
        };
        Assert.Equal(
            """
            Body MESH v8 f6 [Steel,Gold] parent:Rig
            Camera CAMERA
            collections: Collection
            materials: Steel, Gold
            """.ReplaceLineEndings("\n"),
            StateText.RenderFull(state));
    }

    [Fact]
    public void CheckpointLineGoldenForm()
    {
        // ONE renderer serves MCP and the CLI (FR-009) — this pins the shared form.
        var now = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
        var meta = new Abl.Core.Store.CheckpointMeta(
            "aabbccddeeff00112233", "112233445566778899aa", "s1", 123,
            now.AddHours(-2), ["v1", "hero"]);
        Assert.Equal(
            "aabbccddeeff parent:112233445566 session:s1 age:2h tags:v1,hero",
            StateText.RenderCheckpointLine(meta, now));

        var untaggedRoot = new Abl.Core.Store.CheckpointMeta(
            "aabbccddeeff00112233", null, "s1", 123, now.AddMinutes(-5), []);
        Assert.Equal(
            "aabbccddeeff parent:- session:s1 age:5m",
            StateText.RenderCheckpointLine(untaggedRoot, now));
    }
}
