"""Cycles device detection/selection regressions (feature 009): no Blender, fake objects only.

The fakes log every attribute write, so "read-only" is checked by an empty write log rather
than by re-reading values the code under test produced."""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "worker"))
import cycles_devices  # noqa: E402


class Recorder:
    """Logs writes to declared attributes; an undeclared attribute is missing (AttributeError)
    and a value outside `allowed` is rejected (TypeError), like a Blender enum."""

    def __init__(self, log, label, allowed=None, **values):
        object.__setattr__(self, "_log", log)
        object.__setattr__(self, "_label", label)
        object.__setattr__(self, "_allowed", allowed or {})
        object.__setattr__(self, "_values", dict(values))

    def __getattr__(self, name):
        values = object.__getattribute__(self, "_values")
        if name not in values:
            raise AttributeError(name)
        return values[name]

    def __setattr__(self, name, value):
        values = object.__getattribute__(self, "_values")
        if name not in values:
            raise AttributeError(name)
        allowed = object.__getattribute__(self, "_allowed").get(name)
        if allowed is not None and value not in allowed:
            raise TypeError(f"enum {value!r} not found in {allowed}")
        self._log.append((self._label, name, value))
        values[name] = value


class FakePrefs(Recorder):
    """`available` maps a backend to its (name, type) device list, as Blender's
    `get_devices_for_type` returns it: the backend's devices plus the CPU entry."""

    def __init__(self, log, available, compute_device_type="NONE", metalrt="AUTO", types=None,
                 active=None, has_active=True):
        super().__init__(log, "prefs", {"compute_device_type": types or ("NONE", "OPTIX", "METAL")},
                         compute_device_type=compute_device_type, metalrt=metalrt)
        object.__setattr__(self, "available", available)
        object.__setattr__(self, "devices", [])
        object.__setattr__(self, "_active", active)
        if has_active:
            object.__setattr__(self, "has_active_device", self._has_active_device)

    def _has_active_device(self):
        if self._active is not None:
            return self._active
        kind = self.compute_device_type
        return any(d.use and d.type == kind for d in self.devices)

    def get_devices_for_type(self, kind):
        if kind not in self.available:
            raise TypeError(f"unknown device type {kind!r}")
        entries = []
        for index, (name, dtype) in enumerate(self.available[kind]):
            entry = next((d for d in self.devices if d.name == name and d.type == dtype), None)
            if entry is None:  # Blender's enumeration side effect: new entries are added
                entry = Recorder(self._log, f"device:{name}", id=f"{dtype}-{index}", name=name,
                                 type=dtype, use=False)
                self.devices.append(entry)
            entries.append(entry)
        return entries


def fake_cycles(log, denoisers=("OPTIX", "OPENIMAGEDENOISE"), with_gpu_denoise=True):
    values = {"device": "CPU", "denoiser": "OPENIMAGEDENOISE"}
    if with_gpu_denoise:
        values["denoising_use_gpu"] = False
    return Recorder(log, "cycles", {"device": ("CPU", "GPU"), "denoiser": denoisers}, **values)


CPU = ("Test CPU", "CPU")
OPTIX = {"OPTIX": [("Test OptiX GPU", "OPTIX"), CPU], "METAL": [CPU]}
METAL = {"OPTIX": [CPU], "METAL": [("Test Metal GPU", "METAL"), CPU]}


class DetectTests(unittest.TestCase):
    def test_cpu_only_lists_give_none(self):
        log = []
        prefs = FakePrefs(log, {"OPTIX": [CPU], "METAL": [CPU]})
        self.assertEqual({"backend": "none", "devices": [], "metalrt": None}, cycles_devices.detect(prefs))
        self.assertEqual([], log)

    def test_unknown_backend_types_are_absent_not_fatal(self):
        prefs = FakePrefs([], {"METAL": METAL["METAL"]})  # OPTIX unknown on this platform
        self.assertEqual("metal", cycles_devices.detect(prefs)["backend"])

    def test_optix_takes_priority_over_metal(self):
        both = {"OPTIX": [("OptiX GPU", "OPTIX"), CPU], "METAL": [("Metal GPU", "METAL"), CPU]}
        gpu = cycles_devices.detect(FakePrefs([], both))
        self.assertEqual({"backend": "optix", "devices": [{"name": "OptiX GPU", "type": "OPTIX"}], "metalrt": None}, gpu)

    def test_metal_reports_devices_and_metalrt(self):
        gpu = cycles_devices.detect(FakePrefs([], METAL, compute_device_type="METAL"))
        self.assertEqual(
            {"backend": "metal", "devices": [{"name": "Test Metal GPU", "type": "METAL"}],
             "metalrt": "AUTO"}, gpu)

    def test_hello_time_detect_makes_no_writes(self):
        log = []
        prefs = FakePrefs(log, OPTIX)
        cycles_devices.detect(prefs)
        cycles_devices.detect(prefs)  # a second, mid-life hello
        self.assertEqual([], log)
        self.assertEqual("NONE", prefs.compute_device_type)
        self.assertTrue(all(not d.use for d in prefs.devices))

    def test_pre_existing_entries_keep_their_values(self):
        log = []
        prefs = FakePrefs(log, OPTIX)
        prefs.get_devices_for_type("OPTIX")[1].use = True  # caller enabled the CPU entry
        log.clear()
        cycles_devices.detect(prefs)
        self.assertEqual([], log)
        self.assertEqual([False, True], [d.use for d in prefs.devices])

    def test_names_are_cleaned_and_bounded(self):
        name = "GPU\x00\x1b[31m\x7f\x85\x9f" + "x" * 200
        many = [(f"{name}{i}", "METAL") for i in range(12)] + [CPU]
        gpu = cycles_devices.detect(FakePrefs([], {"OPTIX": [], "METAL": many}))
        self.assertEqual(cycles_devices.MAX_DEVICES, len(gpu["devices"]))
        for device in gpu["devices"]:
            self.assertLessEqual(len(device["name"]), 128)
            self.assertTrue(device["name"].startswith("GPU��[31m���"))
            self.assertFalse(any(ord(c) < 0x20 or 0x7F <= ord(c) <= 0x9F for c in device["name"]))

    def test_printable_non_ascii_is_preserved(self):
        gpu = cycles_devices.detect(FakePrefs([], {"OPTIX": [("Gr\u00e4fik\u2122 \u00a0GPU", "OPTIX"), CPU]}))
        self.assertEqual("Gr\u00e4fik\u2122 \u00a0GPU", gpu["devices"][0]["name"])

    def test_length_bound_counts_utf16_units_without_splitting_pairs(self):
        astral = "\U0001D53E"  # outside the BMP: two UTF-16 code units
        self.assertEqual(astral * 64, cycles_devices.clean_name(astral * 100))
        name = cycles_devices.clean_name("x" + astral * 100)  # 127 units fit, the next pair would not
        self.assertEqual("x" + astral * 63, name)
        self.assertLessEqual(len(name.encode("utf-16-le")) // 2, cycles_devices.MAX_NAME)

    def test_unexpected_metalrt_is_null(self):
        gpu = cycles_devices.detect(FakePrefs([], METAL, metalrt="SOMETIMES"))
        self.assertIsNone(gpu["metalrt"])


class SelectTests(unittest.TestCase):
    def test_metal_enables_only_metal_devices_and_gpu_denoise(self):
        log = []
        prefs = FakePrefs(log, METAL)
        prefs.get_devices_for_type("METAL")[1].use = True  # CPU entry enabled beforehand
        cycles = fake_cycles(log)
        cycles_devices.select(prefs, cycles, "metal")
        uses = {(d.type, d.use) for d in prefs.devices}
        self.assertEqual({("METAL", True), ("CPU", False)}, uses)
        self.assertEqual("METAL", prefs.compute_device_type)
        self.assertEqual(("GPU", "OPENIMAGEDENOISE", True),
                         (cycles.device, cycles.denoiser, cycles.denoising_use_gpu))
        self.assertEqual({"backend": "metal", "name": "Test Metal GPU"},
                         cycles_devices.check(prefs, "metal"))

    def test_optix_keeps_enable_all(self):
        log = []
        prefs = FakePrefs(log, OPTIX)
        cycles = fake_cycles(log)
        cycles_devices.select(prefs, cycles, "optix")
        self.assertTrue(all(d.use for d in prefs.devices))  # the CPU entry included (plan D3)
        self.assertEqual(("OPTIX", "GPU", "OPTIX"), (prefs.compute_device_type, cycles.device, cycles.denoiser))
        self.assertFalse(cycles.denoising_use_gpu)  # untouched on OptiX
        self.assertEqual({"backend": "optix", "name": "Test OptiX GPU"}, cycles_devices.check(prefs, "optix"))

    def test_missing_denoising_use_gpu_raises(self):
        prefs = FakePrefs([], METAL)
        with self.assertRaises(cycles_devices.GpuUnavailable) as raised:
            cycles_devices.select(prefs, fake_cycles([], with_gpu_denoise=False), "metal")
        self.assertIn("denoising_use_gpu", str(raised.exception))

    def test_rejected_denoiser_raises_with_the_value(self):
        prefs = FakePrefs([], OPTIX)
        with self.assertRaises(cycles_devices.GpuUnavailable) as raised:
            cycles_devices.select(prefs, fake_cycles([], denoisers=("OPENIMAGEDENOISE",)), "optix")
        self.assertIn("denoiser='OPTIX'", str(raised.exception))

    def test_rejected_compute_device_type_raises(self):
        prefs = FakePrefs([], OPTIX, types=("NONE", "METAL"))  # macOS enum: no OPTIX
        with self.assertRaises(cycles_devices.GpuUnavailable) as raised:
            cycles_devices.select(prefs, fake_cycles([]), "optix")
        self.assertIn("compute_device_type='OPTIX'", str(raised.exception))

    def test_backend_none_raises(self):
        with self.assertRaises(cycles_devices.GpuUnavailable):
            cycles_devices.select(FakePrefs([], METAL), fake_cycles([]), "none")


class CheckTests(unittest.TestCase):
    def selected(self, **kwargs):
        prefs = FakePrefs([], METAL, **kwargs)
        cycles_devices.select(prefs, fake_cycles([]), "metal")
        return prefs

    def test_inactive_device_raises(self):
        prefs = self.selected(active=False)
        with self.assertRaises(cycles_devices.GpuUnavailable) as raised:
            cycles_devices.check(prefs, "metal")
        self.assertEqual("cycles_gpu: no enabled METAL device (has_active_device=false)", str(raised.exception))

    def test_no_enabled_backend_device_raises(self):
        prefs = self.selected(active=True)
        for device in prefs.devices:
            device.use = device.type == "CPU"
        with self.assertRaises(cycles_devices.GpuUnavailable):
            cycles_devices.check(prefs, "metal")

    def test_wrong_compute_device_type_raises(self):
        # active=True pins has_active_device, so only the compute_device_type check can raise.
        prefs = self.selected(active=True)
        prefs.compute_device_type = "NONE"
        with self.assertRaises(cycles_devices.GpuUnavailable) as raised:
            cycles_devices.check(prefs, "metal")
        self.assertIn("compute_device_type", str(raised.exception))

    def test_missing_has_active_device_raises(self):
        prefs = self.selected(has_active=False)
        with self.assertRaises(cycles_devices.GpuUnavailable):
            cycles_devices.check(prefs, "metal")

    def test_stale_enabled_entry_outside_the_backend_list_is_never_reported(self):
        log = []
        prefs = FakePrefs(log, METAL)
        prefs.devices.append(Recorder(log, "device:stale", id="METAL-stale", name="stale entry",
                                      type="METAL", use=True))  # e.g. added by scene_exec
        cycles_devices.select(prefs, fake_cycles(log), "metal")
        self.assertEqual({"backend": "metal", "name": "Test Metal GPU"}, cycles_devices.check(prefs, "metal"))

    def test_configured_name_is_cleaned(self):
        prefs = FakePrefs([], {"OPTIX": [("GPU\x07" + "y" * 300, "OPTIX")]})
        cycles_devices.select(prefs, fake_cycles([]), "optix")
        name = cycles_devices.check(prefs, "optix")["name"]
        self.assertEqual(128, len(name))
        self.assertTrue(name.startswith("GPU�"))


if __name__ == "__main__":
    unittest.main()
