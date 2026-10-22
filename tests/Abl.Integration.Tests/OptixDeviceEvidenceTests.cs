using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Abl.Integration.Tests;

/// <summary>
/// Feature 009 T010: OptiX device evidence on a Linux host with an NVIDIA OptiX GPU. It asserts the facts the
/// design depends on (plan D1: the read-only <c>detect</c> finds OptiX devices without
/// assigning <c>compute_device_type</c>; FR-004: a <c>cycles_gpu</c> render restores the
/// device preferences) and records the device set before, during and after one render (plan
/// D3: whether OptiX's enable-all also enables the CPU entry). Every device is identified
/// only by a per-type label (<c>OPTIX#0</c>, <c>CPU#0</c>) taken from its position in the
/// append-only <c>prefs.devices</c> collection; no device <c>id</c> or <c>name</c> (both carry
/// the hardware model, and an OptiX id also the PCI location) is written out. When
/// <c>ABL_EVIDENCE_DIR</c> is set, the JSON is copied there for the CI log, also when an
/// assertion or the render fails. Category <c>OptixQualification</c>: run by hand on that host;
/// the hosted lane has no GPU and excludes it.
/// </summary>
[Collection("blender")]
[Trait("Category", "OptixQualification")]
public class OptixDeviceEvidenceTests(BlenderFixture fixture, ITestOutputHelper output)
{
    private const string Probe = """
        import bpy, json, __main__, cycles_devices
        prefs = bpy.context.preferences.addons["cycles"].preferences

        def entries(devices):
            counts = {}
            listed = []
            for d in devices:
                counts[d.type] = counts.get(d.type, 0) + 1
                listed.append({"label": f"{d.type}#{counts[d.type] - 1}", "type": d.type, "use": d.use})
            return listed

        def use_map(devices):
            # Keyed by label, never by d.id: an id embeds the device name and PCI location.
            return {entry["label"]: entry["use"] for entry in entries(devices)}

        if params["phase"] == "before":
            known = entries(prefs.devices)  # hello already ran detect; read before enumerating again
            before_type = prefs.compute_device_type
            detected = cycles_devices.detect(prefs)
            __main__.abl_evidence = {
                "computeDeviceTypeAfterHello": before_type,
                "computeDeviceTypeAfterDetect": prefs.compute_device_type,
                "detectBackend": detected["backend"],
                "detectDeviceTypes": [d["type"] for d in detected["devices"]],
                "entriesAfterHello": known,
                "optixListBefore": entries(prefs.get_devices_for_type("OPTIX")),
                "useBefore": use_map(prefs.devices),
            }
            original = __main__._enable_gpu
            evidence_path = params["evidence"]

            def recording(scene):
                device = original(scene)
                __main__.abl_evidence["during"] = {
                    "computeDeviceType": prefs.compute_device_type,
                    "optixList": entries(prefs.get_devices_for_type("OPTIX")),
                    "cpuEntryEnabled": any(d.use for d in prefs.devices if d.type == "CPU"),
                    "hasActiveDevice": bool(prefs.has_active_device()),
                    "configuredBackend": device["backend"],
                }
                # Persist now: if the render itself fails, the "after" phase never runs.
                with open(evidence_path, "w", encoding="utf-8") as out:
                    json.dump(__main__.abl_evidence, out, indent=2, sort_keys=True)
                return device

            __main__._enable_gpu = recording
            with open(params["evidence"], "w", encoding="utf-8") as out:
                json.dump(__main__.abl_evidence, out, indent=2, sort_keys=True)
        else:
            evidence = __main__.abl_evidence
            evidence["computeDeviceTypeAfterRender"] = prefs.compute_device_type
            evidence["useAfter"] = use_map(prefs.devices)
            evidence["optixListAfter"] = entries(prefs.get_devices_for_type("OPTIX"))
            with open(params["evidence"], "w", encoding="utf-8") as out:
                json.dump(evidence, out, indent=2, sort_keys=True)
        """;

    [Fact(Timeout = 300_000)]
    public async Task OptixDeviceSet_AroundACyclesGpuRender_IsRecordedAndRestored()
    {
        Assert.True(fixture.BlenderAvailable, "This evidence requires ABL_BLENDER and an OptiX GPU");
        var evidencePath = Path.Combine(fixture.Root, "optix-devices.json");
        try
        {
            var session = await fixture.Sessions.OpenAsync("optix-device-evidence", default);
            try
            {
                await fixture.Sessions.ExecAsync(session.Id, Probe, new JsonObject
                {
                    ["phase"] = "before",
                    ["evidence"] = evidencePath,
                }, default);
                var hello = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!;
                output.WriteLine(hello.ToJsonString());
                // Plan D1 and the compute_device_type part of AC-03's real-Blender half
                // (the `use` part is T009's second-hello snapshot): hello assigns no
                // compute_device_type (factory startup leaves it NONE), so the OptiX devices
                // below were found without it. If the backend check fails, apply the
                // assign-then-restore fallback in cycles_devices.detect.
                Assert.Equal("NONE", hello["computeDeviceTypeAfterHello"]!.GetValue<string>());
                Assert.Equal("optix", hello["detectBackend"]!.GetValue<string>());
                var render = await fixture.Sessions.RenderFinalAsync(
                    session.Id, null, "cycles_gpu", 32, 1, null, "wait", 0, default);
                Assert.True(new FileInfo(render.Path).Length > 0);
                await fixture.Sessions.ExecAsync(session.Id, Probe, new JsonObject
                {
                    ["phase"] = "after",
                    ["evidence"] = evidencePath,
                }, default);
            }
            finally
            {
                await fixture.Sessions.CloseAsync(session.Id);
            }
        }
        finally
        {
            // Keep the record on the failures it exists to diagnose (D1, the denoiser gotcha).
            await PublishEvidenceAsync(evidencePath);
        }

        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!.AsObject();
        Assert.Equal("NONE", evidence["computeDeviceTypeAfterDetect"]!.GetValue<string>());
        var during = evidence["during"]!.AsObject();
        Assert.Equal("OPTIX", during["computeDeviceType"]!.GetValue<string>());
        Assert.True(during["hasActiveDevice"]!.GetValue<bool>());
        Assert.Equal("optix", during["configuredBackend"]!.GetValue<string>());
        // FR-004: the render restores the device preferences it changed.
        Assert.Equal("NONE", evidence["computeDeviceTypeAfterRender"]!.GetValue<string>());
        var before = evidence["useBefore"]!.AsObject();
        var after = evidence["useAfter"]!.AsObject();
        foreach (var (key, use) in before)
        {
            Assert.True(after.ContainsKey(key), $"device entry {key} disappeared");
            Assert.Equal(use!.GetValue<bool>(), after[key]!.GetValue<bool>());
        }
        foreach (var (key, use) in after)
        {
            if (!before.ContainsKey(key)) Assert.False(use!.GetValue<bool>(), $"new entry {key} left enabled");
        }
    }

    private async Task PublishEvidenceAsync(string evidencePath)
    {
        if (!File.Exists(evidencePath)) return;
        var text = await File.ReadAllTextAsync(evidencePath);
        output.WriteLine(text);
        var dir = Environment.GetEnvironmentVariable("ABL_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "optix-devices.json"), text);
    }
}
