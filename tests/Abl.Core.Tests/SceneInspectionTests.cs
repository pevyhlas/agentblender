using System.Text;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Sessions;
using Xunit;

namespace Abl.Core.Tests;

[Collection("op-telemetry")]
public class SceneInspectionTests
{
    [Fact]
    public async Task RejectedIngressMeasuresExactlyOneBadRequest()
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == Telemetry.AblTelemetry.SourceName && instrument.Name == "abl.ops") l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var fields = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (fields["op"]?.ToString() == "scene_inspect") outcomes.Add(fields["outcome"]!.ToString()!);
        });
        listener.Start();
        await SceneInspection.RecordRejectionAsync(new("bad_request", "invalid input"), "s", "demo");
        Assert.Equal(["bad_request"], outcomes);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"objects\":null}")]
    [InlineData("{\"objects\":[\"Cube\",\"Cube\"]}")]
    [InlineData("{\"camera\":\"\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"geometry\":true,\"geometry\":false}")]
    [InlineData("{\"geometry\":true,\"\\u0067eometry\":false}")]
    [InlineData("{\"Geometry\":true}")]
    [InlineData("{\"landmarks\":[{\"name\":\"p\",\"world\":[0,0]}]}")]
    [InlineData("{\"landmarks\":[{\"name\":\"p\",\"world\":[0,0,1e309]}]}")]
    public void InvalidRequestsFailStructured(string json)
    {
        Assert.Equal("bad_request", Assert.Throws<AblException>(() =>
            SceneInspection.Parse(Encoding.UTF8.GetBytes(json))).Kind);
    }

    [Fact]
    public void CaptureOwnsInputsAndLimitsAreInclusive()
    {
        var request = new SceneInspectionRequest
        {
            Objects = Enumerable.Range(0, 32).Select(i => "object" + i).ToArray(),
            Landmarks = [new("p", [1, 2, 3])],
        };
        var copy = SceneInspection.Capture(request);
        request.Objects[0] = "changed";
        request.Landmarks[0].World[0] = 5;
        Assert.Equal("object0", copy.Objects[0]);
        Assert.Equal(1, copy.Landmarks[0].World[0]);
        Assert.Throws<AblException>(() => SceneInspection.Capture(copy with { Objects = [.. copy.Objects, "extra"] }));
        Assert.Throws<AblException>(() => SceneInspection.Parse(new byte[SceneInspection.MaxRequestBytes + 1]));
        Assert.Throws<AblException>(() => SceneInspection.Capture(new() { Objects = [new string('x', 129)] }));
    }

    [Fact]
    public void WorkerReplyIdentityAndShapesAreNotSilentlyTrusted()
    {
        var request = SceneInspection.ToWorker(new() { Objects = ["Cube"] });
        var response = JsonNode.Parse("""
            {"inspection":{"profile":"abl.scene-inspection.v1","coverage":"selected-raw-facts-not-replay-or-visual-equivalence",
              "framing":{"width":200,"height":100,"pixelAspect":[1,1],"border":false,"cropToBorder":false,
                 "coordinates":"top-left-pixel-edges-x-right-y-down","borderRect":[0,0,1,1]},"camera":null,"objects":[
                {"name":"Cube","type":"MESH","parent":null,"hideViewport":false,"hideRender":false,
                 "matrixWorld":[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]]}],"landmarks":[]}}
            """)!.AsObject();
        SceneInspection.ValidateReply(request, response);
        response["inspection"]!["objects"]![0]!["matrixWorld"]![0]![0] = "not-a-number";
        Assert.Throws<InvalidDataException>(() => SceneInspection.ValidateReply(request, response));
        response["inspection"]!["objects"]![0]!["matrixWorld"]![0]![0] = 1;
        response["inspection"]!["camera"] = new JsonObject();
        Assert.Throws<InvalidDataException>(() => SceneInspection.ValidateReply(request, response));
        response["inspection"]!["camera"] = null;
        Assert.Throws<InvalidDataException>(() => SceneInspection.ValidateReply(
            SceneInspection.ToWorker(new() { Objects = ["Cube"], Geometry = true }), response));
        response["inspection"]!["objects"]![0]!["name"] = "other";
        Assert.Throws<InvalidDataException>(() => SceneInspection.ValidateReply(request, response));
        response["inspection"]!["profile"] = 42;
        Assert.Throws<InvalidDataException>(() => SceneInspection.ValidateReply(request, response));
    }
}
