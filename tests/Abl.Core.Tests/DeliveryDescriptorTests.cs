using System.Text.Json.Nodes;
using Abl.Core.Delivery;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// Feature 004 FR-002: the delivery descriptor serializes deterministically (the 002 bundle
/// bytes policy) and round-trips, so re-exports diff clean and a consumer parses it stably.
/// </summary>
public sealed class DeliveryDescriptorTests
{
    private static DeliveryDescriptor Sample() => new(
        DeliveryJson.FormatVersion,
        "sample-drone",
        new DeliveryProducer("agentblender", "5.2.1"),
        DeliveryJson.ProfileTag,
        DateTimeOffset.Parse("2026-09-29T10:00:00Z"),
        [
            new DeliveryItem(
                "sample-drone-00.png", 4096, "aa11", "basehash", "scene0",
                new DeliveryRecipe("sample-drone", new JsonObject { ["tint"] = "amber" }),
                new DeliveryRender("cycles_gpu", 1024, 128, 0, "srgb", "straight"),
                new DeliveryFraming(1024, 1024)),
            new DeliveryItem(
                "sample-drone-01.png", 4100, "bb22", "basehash", "scene1",
                new DeliveryRecipe("sample-drone", null),
                new DeliveryRender("cycles_gpu", 1024, 128, 0, "srgb", "straight"),
                new DeliveryFraming(1024, 1024)),
        ]);

    [Fact]
    public void SerializationIsDeterministicLfAndTrailingNewline()
    {
        var text = DeliveryJson.Serialize(Sample());
        Assert.Equal(text, DeliveryJson.Serialize(Sample())); // stable across calls
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void TopLevelAndItemKeyOrderIsPinned()
    {
        var text = DeliveryJson.Serialize(Sample());

        // The on-disk key order is part of the format — a consumer repo's committed delivery
        // must not reorder if the record shape or serializer drifts.
        AssertOrder(text, "\"formatVersion\"", "\"name\"", "\"producer\"", "\"profile\"", "\"createdAt\"", "\"items\"");
        AssertOrder(
            text, "\"file\"", "\"bytes\"", "\"sha256\"", "\"sourceCheckpoint\"", "\"sceneDigest\"",
            "\"recipe\"", "\"render\"", "\"framing\"");
    }

    [Fact]
    public void RoundTripsThroughDeserialize()
    {
        var text = DeliveryJson.Serialize(Sample());
        var back = DeliveryJson.Deserialize(text);
        Assert.NotNull(back);
        // Re-serialization is the fidelity check (records with lists/JsonObject don't have
        // value equality).
        Assert.Equal(text, DeliveryJson.Serialize(back!));
        Assert.Equal(2, back!.Items.Count);
        Assert.Equal("sample-drone-00.png", back.Items[0].File);
        Assert.Equal("amber", back.Items[0].Recipe.Params!["tint"]!.GetValue<string>());
        Assert.Null(back.Items[1].Recipe.Params);
    }

    [Fact]
    public void ProfileTagAndVersionAreTheDeclaredConstants()
    {
        Assert.Equal(2, DeliveryJson.FormatVersion);
        Assert.Equal("png8-rgba-srgb-straight-fixed-canvas-v1", DeliveryJson.ProfileTag);
        Assert.Equal("srgb", DeliveryProfile.ColorSpace);
        Assert.Equal("straight", DeliveryProfile.Alpha);
    }

    private static void AssertOrder(string text, params string[] keys)
    {
        var positions = keys.Select(k => text.IndexOf(k, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p), positions);
    }
}
