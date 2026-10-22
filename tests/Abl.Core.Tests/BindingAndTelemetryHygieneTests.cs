using System.Diagnostics;
using System.Diagnostics.Metrics;
using Abl.Core;
using Abl.Core.Telemetry;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// FR-012/FR-013 hygiene: spans and metrics carry op context but NEVER payload contents;
/// metric labels stay low-cardinality {op, outcome}. (The loopback-bind half of this
/// domain is asserted live in the E2E test — it needs a running daemon.)
/// </summary>
[Collection("op-telemetry")]
public class BindingAndTelemetryHygieneTests
{
    private const string Payload = "SECRET-SCENE-PAYLOAD-import-bpy-vertex-soup";

    [Fact]
    public void ReturnedTextSizeCountsUtf8BytesRatherThanUtf16Characters()
    {
        Assert.Equal(6, PayloadSizing.Utf8Bytes("é🚀"));
    }

    private sealed record Captured(
        List<Activity> Activities,
        List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements);

    private static async Task<Captured> CaptureAsync(Func<Task> action)
    {
        var captured = new Captured([], []);
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AblTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Activities.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // The shared meter also emits independent GPU-lease measurements from
                // parallel tests; this fixture observes only the public-op contract.
                if (instrument.Meter.Name == AblTelemetry.SourceName &&
                    instrument.Name is "abl.ops" or "abl.op.duration" or "abl.op.payload_bytes")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            where T : struct =>
            captured.Measurements.Add((
                instrument.Name,
                Convert.ToDouble(value),
                tags.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value)));
        meterListener.SetMeasurementEventCallback<long>(Record);
        meterListener.SetMeasurementEventCallback<double>(Record);
        meterListener.Start();

        await action();
        return captured;
    }

    [Fact]
    public async Task SuccessEmitsOpContextButNeverPayloadContents()
    {
        var captured = await CaptureAsync(() => AblTelemetry.RunAsync(
            "scene_exec", "s1", "demo", () => Task.FromResult(Payload), r => r.Length));

        var activity = Assert.Single(captured.Activities);
        Assert.Equal("abl.scene_exec", activity.DisplayName);
        Assert.Equal("scene_exec", activity.GetTagItem("abl.op"));
        Assert.Equal("s1", activity.GetTagItem("abl.session"));
        Assert.Equal("demo", activity.GetTagItem("abl.project"));
        Assert.Equal("ok", activity.GetTagItem("abl.outcome"));
        Assert.Equal((long)Payload.Length, activity.GetTagItem("abl.payload_bytes"));
        Assert.DoesNotContain(activity.TagObjects, kv => $"{kv.Value}".Contains("SECRET"));

        Assert.Contains(captured.Measurements, m => m.Instrument == "abl.ops" && m.Value == 1);
        Assert.Contains(captured.Measurements, m => m.Instrument == "abl.op.duration");
        Assert.Contains(captured.Measurements,
            m => m.Instrument == "abl.op.payload_bytes" && m.Value == Payload.Length);
        Assert.All(captured.Measurements, m =>
        {
            Assert.Subset(new HashSet<string> { "op", "outcome", "tier" }, m.Tags.Keys.ToHashSet());
            Assert.DoesNotContain(m.Tags.Values, v => $"{v}".Contains("SECRET"));
        });
    }

    [Theory]
    [InlineData(WorkerTier.Local, "local")]
    [InlineData(WorkerTier.Desktop, "desktop")]
    public async Task ServingTierSurvivesSuccessAndStructuredFailure(WorkerTier tier, string label)
    {
        var captured = await CaptureAsync(async () =>
        {
            await AblTelemetry.RunAsync("render_final", "s1", "demo", async () =>
            {
                await Task.Yield();
                AblTelemetry.SetTier(tier);
                return "path-only";
            }, r => r.Length);
            await Assert.ThrowsAsync<AblException>(() => AblTelemetry.RunAsync<string>("batch_run", null, null, () =>
            {
                AblTelemetry.SetTier(tier);
                throw new AblException("exec_failed", Payload);
            }));
        });
        Assert.All(captured.Activities, a => Assert.Equal(label, a.GetTagItem("abl.tier")));
        Assert.All(captured.Measurements, m => Assert.Equal(label, m.Tags["tier"]));
        Assert.All(captured.Measurements, m =>
            Assert.Subset(new HashSet<string> { "op", "outcome", "tier" }, m.Tags.Keys.ToHashSet()));
    }

    [Fact]
    public async Task StructuredErrorsBecomeTheOutcomeWithoutLeakingTheMessage()
    {
        var captured = await CaptureAsync(async () =>
        {
            await Assert.ThrowsAsync<AblException>(() => AblTelemetry.RunAsync<string>(
                "scene_exec", "s1", null,
                () => throw new AblException("exec_failed", $"boom: {Payload}")));
        });

        var activity = Assert.Single(captured.Activities);
        Assert.Equal("exec_failed", activity.GetTagItem("abl.outcome"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.DoesNotContain(activity.TagObjects, kv => $"{kv.Value}".Contains("SECRET"));

        var ops = captured.Measurements.Single(m => m.Instrument == "abl.ops");
        Assert.Equal("exec_failed", ops.Tags["outcome"]);
        Assert.DoesNotContain(
            captured.Measurements.SelectMany(m => m.Tags.Values), v => $"{v}".Contains("SECRET"));
    }
}
