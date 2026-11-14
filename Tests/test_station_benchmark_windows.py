"""Synthetic clock/sidecar tests for independent capture/game frame ratios."""
import importlib.util
import json
import tempfile
import sys
import unittest
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "Tests"))
from normalized_capture_fixture import NormalizedFixture
SPEC = importlib.util.spec_from_file_location("window_reader", PROJECT / "BuildRequests" / "read_station_capture_benchmark_windows.py")
READER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(READER)


class BenchmarkWindowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="station-benchmark-window-")
        self.root = Path(self.temp.name)
        self.capture = self.root / "capture"
        self.fixture = NormalizedFixture(self.capture, agents=False)
        self.fixture.add_camera()
        self.camera = self.capture / "video" / "HALL-TEST"
        self.stamps = [dict(stamp, realElapsedSeconds=float(i), simulationSeconds=float(i), stationClockSeconds=18000 + i) for i, stamp in enumerate(self.fixture.stamps, 1)]
        self.rows(self.capture / "sync.jsonl", [{"kind": "clock", "stamp": stamp} for stamp in self.stamps])
        self.rows(self.camera / "frames.jsonl", [{"stamp": stamp} for stamp in self.stamps])
        self.manifest = self.fixture.manifest
        self.report = {"request": {"label": "ULP-boundaries"}, "success": True, "gameFps": 1., "measuredRealSeconds": 2., "measuredUnityFrames": 2, "processCpuSeconds": 0., "applicationBuildGuid": "test-build", "seed": 1, "operatingDate": "2026-10-20", "startSimulationSeconds": 1., "endSimulationSeconds": 3., "actors": [], "captureDirectory": str(self.capture), "captureRunId": "test", "sessionWindowStartRealSeconds": 1.0000000000000002, "sessionWindowEndRealSeconds": 2.9999999999999996, "cameras": [{"cameraId": "HALL-TEST", "measuredAcceptedFrames": 1, "acceptedFps": .5}]}

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def rows(path, values):
        # Author physical current-format references from the fixture's explicit stamps.
        if path.name == "sync.jsonl":
            values = [dict(definitionId=1, **value) for value in values]
        elif path.name == "frames.jsonl":
            values = [dict(syncSampleId=value["stamp"]["syncSampleId"], cameraStateId=min(value["stamp"]["syncSampleId"], 2),
                           segment=1, videoFrame=i, requestId=i, videoTimeSeconds=i/5,
                           captureIntervalRealSeconds=None if i==0 else 1., captureFpsInstantaneous=None if i==0 else 1., captureFpsRolling1s=1.)
                      for i, value in enumerate(values)]
        path.write_text("".join(json.dumps(value) + "\n" for value in values), encoding="utf-8")

    def inspect(self):
        (self.capture / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        path = self.root / "benchmark.json"
        path.write_text(json.dumps(self.report), encoding="utf-8")
        return READER.inspect(path, 1e-6)

    def test_ulp_boundary_uses_sync_ids_and_ratio_denominator(self):
        result = self.inspect()
        camera = result["cameras"][0]
        self.assertEqual((camera["anchoredMeasuredFrames"], camera["rawFloatMeasuredFrames"]), (2, 1))
        self.assertEqual(camera["acceptedToGameFrameRatio"], 1)
        self.assertEqual(result["anchoredUnityFrameDelta"], 2)
        self.assertEqual(result["cameraGameFrameRatios"], {"min": 1., "max": 1., "mean": 1.})

    def test_ratio_does_not_use_stopwatch_fps_or_nominal_playback_rate(self):
        self.report["gameFps"] = 1000
        self.report["request"]["fps"] = 17
        self.rows(self.camera / "frames.jsonl", [{"stamp": self.stamps[2]}])
        self.manifest["cameraResults"][0]["framesWritten"] = 1
        result = self.inspect()
        self.assertEqual(result["cameras"][0]["acceptedToGameFrameRatio"], .5)
        self.assertEqual(result["cameras"][0]["anchoredAcceptedFps"], .5)

    def test_explicit_driver_sync_boundaries_are_honored(self):
        self.report.update(sessionWindowStartSyncSampleId=1, sessionWindowEndSyncSampleId=3)
        result = self.inspect()
        self.assertEqual(result["endAnchor"]["method"], "explicitSyncId")
        self.report["sessionWindowEndSyncSampleId"] = 999
        with self.assertRaisesRegex(ValueError, "no sync clock"):
            self.inspect()

    def test_new_pipeline_metadata_is_reported_without_inference(self):
        self.report.update(resolvedAdmission="common", resolvedReadbackSlots=4,
                           resolvedRenderProfile="lean", resolvedDataWriterMode="background",
                           hardware={"renderingThreadingMode": "NativeGraphicsJobsSplitThreading"})
        pipeline = self.inspect()["resolvedPipeline"]
        self.report["captureSetupRealSeconds"] = 4.25
        self.assertEqual(self.inspect()["captureSetupRealSeconds"], 4.25)
        self.assertEqual(pipeline, {"admission": "common", "readbackSlots": 4,
                                   "renderProfile": "lean", "dataWriterMode": "background",
                                   "renderingThreadingMode": "NativeGraphicsJobsSplitThreading"})

    def test_unspecified_pipeline_metadata_remains_unknown(self):
        pipeline = self.inspect()["resolvedPipeline"]
        self.assertIsNone(self.inspect()["captureSetupRealSeconds"])
        self.assertIsNone(pipeline["dataWriterMode"])
        self.assertIsNone(pipeline["renderingThreadingMode"])
        self.assertEqual(self.inspect()["cameraGameFrameRatios"]["min"], 1.)

    def enable_writer_manifest(self):
        self.report["resolvedDataWriterMode"] = "background"
        self.manifest.update(dataWriterMode="background", dataCommandsAccepted=100, dataCommandsCompleted=100,
                             dataQueueHighWaterCommands=5, dataQueueMaxPendingCommands=32,
                             dataQueueHighWaterRetainedBytes=1024, dataQueueMaxRetainedBytes=8192)

    def test_completed_writer_manifest_is_joined_and_checked(self):
        self.enable_writer_manifest()
        result = self.inspect()
        self.assertEqual(result["resolvedPipeline"]["manifestDataWriterMode"], "background")
        self.assertEqual(result["dataWriterFinalization"]["dataCommandsCompleted"], 100)

    def test_undrained_writer_cannot_appear_complete(self):
        self.enable_writer_manifest()
        self.manifest["dataCommandsCompleted"] = 99
        with self.assertRaisesRegex(ValueError, "not completely drained"):
            self.inspect()

    def test_writer_mode_mismatch_is_rejected(self):
        self.enable_writer_manifest()
        self.report["resolvedDataWriterMode"] = "main"
        with self.assertRaisesRegex(ValueError, "writer mode differs"):
            self.inspect()

    def test_writer_declared_memory_bound_is_verified(self):
        self.enable_writer_manifest()
        self.manifest["dataQueueHighWaterRetainedBytes"] = 8193
        with self.assertRaisesRegex(ValueError, "exceeded its declared bound"):
            self.inspect()

    def test_missing_anchor_fails(self):
        self.report["sessionWindowEndRealSeconds"] = 3.001
        with self.assertRaisesRegex(ValueError, "within tolerance"):
            self.inspect()

    def test_missing_video_clock_reference_fails(self):
        bad = [dict(stamp) for stamp in self.stamps]
        bad[1]["syncSampleId"] = 999
        self.rows(self.camera / "frames.jsonl", [{"stamp": stamp} for stamp in bad])
        with self.assertRaisesRegex(ValueError, "Missing clock"):
            self.inspect()

    def test_duplicate_clock_fails(self):
        self.rows(self.capture / "sync.jsonl", [{"kind": "clock", "stamp": stamp} for stamp in self.stamps + [self.stamps[1]]])
        with self.assertRaisesRegex(ValueError, "monotonic|Duplicate clock"):
            self.inspect()

    def test_duplicate_video_observation_cannot_inflate_ratio(self):
        self.rows(self.camera / "frames.jsonl", [{"stamp": stamp} for stamp in self.stamps + [self.stamps[2]]])
        self.manifest["cameraResults"][0]["framesWritten"] = 4
        with self.assertRaisesRegex(ValueError, "Duplicate or backwards observation"):
            self.inspect()

    def test_unfinalized_capture_fails(self):
        self.manifest["status"] = "recording"
        with self.assertRaisesRegex(ValueError, "not finalized"):
            self.inspect()


if __name__ == "__main__":
    unittest.main()
