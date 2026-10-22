using System.Text;
using System.Text.Json.Nodes;
using Abl.Core.Store;

namespace Abl.Core.Sessions;

/// <summary>
/// The terse text forms of state reads (Principle 2 — these are what agents pay tokens
/// for, so golden-file tests pin them; changing a form is a surface change).
/// </summary>
public static class StateText
{
    /// <summary>Delta form: one +/-/~ line per object, set-lines for collections/materials.</summary>
    public static string RenderDelta(JsonObject delta)
    {
        var sb = new StringBuilder();
        AppendNames(sb, "+", delta["added"]);
        AppendNames(sb, "-", delta["removed"]);
        AppendNames(sb, "~", delta["changed"]);
        AppendSetDelta(sb, "collections", delta["collections"]);
        AppendSetDelta(sb, "materials", delta["materials"]);
        return sb.Length == 0 ? "(no changes)" : sb.ToString().TrimEnd('\n');
    }

    /// <summary>Full form: one line per object (name, type, mesh counts, materials, parent).</summary>
    public static string RenderFull(JsonObject state)
    {
        var sb = new StringBuilder();
        if (state["objects"] is JsonObject objects)
        {
            foreach (var (name, node) in objects.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var obj = node!.AsObject();
                sb.Append(name).Append(' ').Append(obj["type"]?.GetValue<string>());
                if (obj["type"]?.GetValue<string>() == "MESH")
                {
                    sb.Append(" v").Append(obj["verts"]?.GetValue<int>() ?? 0)
                      .Append(" f").Append(obj["faces"]?.GetValue<int>() ?? 0);
                }

                if (obj["materials"] is JsonArray { Count: > 0 } mats)
                {
                    sb.Append(" [").AppendJoin(",", mats.Select(m => m?.GetValue<string>())).Append(']');
                }

                if (obj["parent"]?.GetValue<string>() is { } parent)
                {
                    sb.Append(" parent:").Append(parent);
                }

                sb.Append('\n');
            }
        }

        AppendSet(sb, "collections", state["collections"]);
        AppendSet(sb, "materials", state["materials"]);
        return sb.Length == 0 ? "(empty scene)" : sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// checkpoint_list line — ONE implementation shared by MCP and the CLI (FR-009), so
    /// the terse form cannot drift between surfaces.
    /// </summary>
    public static string RenderCheckpointLine(CheckpointMeta meta, DateTimeOffset now) =>
        $"{meta.Hash[..12]} parent:{(meta.Parent is null ? "-" : meta.Parent[..12])} " +
        $"session:{meta.SessionId} age:{Age(now - meta.CreatedAt)}" +
        (meta.Tags.Count > 0 ? $" tags:{string.Join(",", meta.Tags)}" : "");

    private static string Age(TimeSpan span) => span.TotalHours >= 1
        ? $"{span.TotalHours:0}h"
        : $"{span.TotalMinutes:0}m";

    private static void AppendNames(StringBuilder sb, string marker, JsonNode? names)
    {
        if (names is JsonArray { Count: > 0 } array)
        {
            foreach (var name in array)
            {
                sb.Append(marker).Append(' ').Append(name?.GetValue<string>()).Append('\n');
            }
        }
    }

    private static void AppendSetDelta(StringBuilder sb, string label, JsonNode? node)
    {
        if (node is not JsonObject setDelta)
        {
            return;
        }

        var parts = new List<string>();
        if (setDelta["added"] is JsonArray added)
        {
            parts.AddRange(added.Select(n => "+" + n?.GetValue<string>()));
        }

        if (setDelta["removed"] is JsonArray removed)
        {
            parts.AddRange(removed.Select(n => "-" + n?.GetValue<string>()));
        }

        if (parts.Count > 0)
        {
            sb.Append(label).Append(": ").AppendJoin(" ", parts).Append('\n');
        }
    }

    private static void AppendSet(StringBuilder sb, string label, JsonNode? node)
    {
        if (node is JsonArray { Count: > 0 } array)
        {
            sb.Append(label).Append(": ")
              .AppendJoin(", ", array.Select(n => n?.GetValue<string>())).Append('\n');
        }
    }
}
