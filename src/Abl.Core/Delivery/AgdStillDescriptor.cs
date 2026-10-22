using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abl.Core.Delivery;

/// <summary>Existing AGD provided-artifact v1 still shape; producer receipts stay outside it.</summary>
public static class AgdStillDescriptor
{
    public const string FileName = "artifact.json";
    public const string ImageName = "still.png";
    public const string Profile = "png8-rgba-srgb-straight-fixed-canvas-v1";

    public static string Serialize(string asset, DeliveryItem item, string build, string recipeHash) =>
        Create(asset, item, build, recipeHash).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    public static JsonObject Create(string asset, DeliveryItem item, string build, string recipeHash) => new()
    {
        ["schema"] = "agd.provided-artifact",
        ["version"] = 1,
        ["assetId"] = asset,
        ["profile"] = Profile,
        ["canvas"] = new JsonObject
        {
            ["width"] = item.Framing.Width,
            ["height"] = item.Framing.Height,
            ["coordinates"] = "top-left-pixel-edges-x-right-y-down",
        },
        ["cropOrigin"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
        ["files"] = new JsonArray(new JsonObject
        {
            ["path"] = ImageName,
            ["byteLength"] = item.Bytes,
            ["sha256"] = item.Sha256,
        }),
        ["still"] = new JsonObject
        {
            ["name"] = "rest",
            ["fileSha256"] = item.Sha256,
            ["pivot"] = new JsonObject { ["x"] = item.Framing.Width / 2m, ["y"] = item.Framing.Height / 2m },
        },
        ["provenance"] = new JsonObject
        {
            ["producer"] = new JsonObject { ["id"] = "agentblender", ["version"] = build },
            ["recipeSnapshotSha256"] = recipeHash,
        },
    };

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
