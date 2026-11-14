"""Independent, read-only reader of finalized benchmark clocks and video sidecars.

Only the explicit --out receipt is written. Nominal AVI playback FPS is never
treated as a capture target. This helper does not decode or alter media/data.
"""
import argparse
import hashlib
import importlib.util
import json
import math
from datetime import datetime, timezone
from pathlib import Path

_READER_SPEC = importlib.util.spec_from_file_location("stationflow_capture_window_reader", Path(__file__).resolve().parents[1] / "Assets/StreamingAssets/TrainStationData/read_capture.py")
_READER_MODULE = importlib.util.module_from_spec(_READER_SPEC)
_READER_SPEC.loader.exec_module(_READER_MODULE)
CaptureReader = _READER_MODULE.CaptureReader


def load(path):
    with Path(path).open(encoding="utf-8-sig") as stream:
        return json.load(stream)



def describe(values):
    ordered = sorted(values)
    if not ordered:
        return {"count": 0}
    def percentile(fraction):
        return ordered[max(0, math.ceil(fraction * len(ordered)) - 1)]
    return {"count": len(ordered), "min": ordered[0], "max": ordered[-1],
            "mean": sum(ordered) / len(ordered), "medianNearestRank": percentile(.5),
            "p95NearestRank": percentile(.95), "p99NearestRank": percentile(.99)}


def _inspect(report_path, tolerance, capture_reader=None):
    report_path = Path(report_path).resolve()
    report = load(report_path)
    result = {"benchmarkPath": str(report_path), "label": report["request"]["label"],
              "request": report["request"], "driverSuccess": report["success"],
              "driverGameFps": report["gameFps"], "driverMeasuredRealSeconds": report["measuredRealSeconds"],
              "driverMeasuredUnityFrames": report["measuredUnityFrames"],
              "driverProcessCpuSeconds": report["processCpuSeconds"],
              "captureSetupRealSeconds": report.get("captureSetupRealSeconds"),
              "driverGameFrameWallMs": report.get("gameFrameWallMs"),
              "driverMainThreadMs": report.get("profilerMainThreadMs"),
              "buildGuid": report["applicationBuildGuid"], "seed": report["seed"],
              "date": report["operatingDate"], "timetableSha256": report.get("timetableSha256"),
              "arrivalPlanSha256": report.get("arrivalPlanSha256"),
              "simulationStart": report["startSimulationSeconds"],
              "simulationEnd": report["endSimulationSeconds"],
              "actorFirst": report["actors"][0] if report.get("actors") else None,
              "actorLast": report["actors"][-1] if report.get("actors") else None,
              "resolvedPipeline": {"admission": report.get("resolvedAdmission"),
                                   "readbackSlots": report.get("resolvedReadbackSlots"),
                                   "renderProfile": report.get("resolvedRenderProfile"),
                                   "dataWriterMode": report.get("resolvedDataWriterMode"),
                                   "renderingThreadingMode": report.get("hardware", {}).get("renderingThreadingMode")},
              "cameras": []}
    capture_path = report.get("captureDirectory")
    if not capture_path:
        result["clockAvailable"] = False
        result["notes"] = ["Observation mode does not collect sync clocks or video."]
        return result
    capture = Path(capture_path)
    manifest = capture_reader.manifest
    if manifest.get("status") != "complete" or manifest.get("errors"):
        raise ValueError("Capture is not finalized cleanly: " + str(capture))
    if manifest.get("runId") != report.get("captureRunId"):
        raise ValueError("Manifest run ID differs from benchmark report")
    if "dataWriterMode" in manifest:
        mode = manifest["dataWriterMode"]
        if mode not in ("main", "background"):
            raise ValueError("Manifest data writer mode is invalid")
        reported_mode = result["resolvedPipeline"]["dataWriterMode"]
        if reported_mode is not None and reported_mode != mode:
            raise ValueError("Manifest data writer mode differs from benchmark report")
        accepted, completed = manifest.get("dataCommandsAccepted"), manifest.get("dataCommandsCompleted")
        if not isinstance(accepted, int) or isinstance(accepted, bool) or accepted < 0 or not isinstance(completed, int) or isinstance(completed, bool) or completed != accepted:
            raise ValueError("Finalized capture data commands were not completely drained")
        for high_field, cap_field in (("dataQueueHighWaterCommands", "dataQueueMaxPendingCommands"),
                                      ("dataQueueHighWaterRetainedBytes", "dataQueueMaxRetainedBytes")):
            high, cap = manifest.get(high_field), manifest.get(cap_field)
            if not isinstance(high, int) or not isinstance(cap, int) or isinstance(high, bool) or isinstance(cap, bool) or cap < 1 or high < 0 or high > cap:
                raise ValueError("Manifest data queue exceeded its declared bound: " + high_field)
        result["resolvedPipeline"]["manifestDataWriterMode"] = mode
        result["dataWriterFinalization"] = {field: manifest[field] for field in (
            "dataCommandsAccepted", "dataCommandsCompleted", "dataQueueHighWaterCommands",
            "dataQueueMaxPendingCommands", "dataQueueHighWaterRetainedBytes", "dataQueueMaxRetainedBytes")}
    clocks = {}
    for row in capture_reader.iter_records(manifest.get("syncFile", "sync.jsonl")):
        if row.get("kind") != "clock":
            continue
        stamp = row["stamp"]
        sync_id = stamp["syncSampleId"]
        if sync_id in clocks:
            raise ValueError("Duplicate clock sync ID: " + str(sync_id))
        clocks[sync_id] = stamp
    ordered = sorted(clocks.values(), key=lambda item: item["syncSampleId"])
    if not ordered:
        raise ValueError("Capture contains no clocks")
    if any(b["realElapsedSeconds"] < a["realElapsedSeconds"] for a, b in zip(ordered, ordered[1:])):
        raise ValueError("Clock real time is not monotonic")
    def anchor(field):
        requested = report[field]
        explicit_sync_id = report.get(field.replace("RealSeconds", "SyncSampleId"))
        if explicit_sync_id:
            if explicit_sync_id not in clocks:
                raise ValueError("Explicit measurement boundary has no sync clock")
            stamp = clocks[explicit_sync_id]
        else:
            stamp = min(ordered, key=lambda item: abs(item["realElapsedSeconds"] - requested))
        difference = stamp["realElapsedSeconds"] - requested
        if abs(difference) > tolerance:
            raise ValueError("No sync clock within tolerance for " + field + ": " + str(difference))
        return {"requestedRealSeconds": requested, "differenceSeconds": difference,
                "method": "explicitSyncId" if explicit_sync_id else "nearestRealClock",
                "stamp": stamp}
    start = anchor("sessionWindowStartRealSeconds")
    end = anchor("sessionWindowEndRealSeconds")
    first_id = start["stamp"]["syncSampleId"]
    last_id = end["stamp"]["syncSampleId"]
    duration = end["stamp"]["realElapsedSeconds"] - start["stamp"]["realElapsedSeconds"]
    if duration <= 0 or last_id <= first_id:
        raise ValueError("Invalid anchored measurement window")
    clock_frame_delta = end["stamp"]["unityFrame"] - start["stamp"]["unityFrame"]
    if clock_frame_delta <= 0:
        raise ValueError("Anchored measurement contains no Unity frames")
    result.update({"clockAvailable": True, "captureDirectory": str(capture),
                   "startAnchor": start, "endAnchor": end, "anchoredRealSeconds": duration,
                   "anchoredUnityFrameDelta": clock_frame_delta,
                   "anchoredGameFps": clock_frame_delta / duration,
                   "anchoredMinusDriverGameFps": clock_frame_delta / duration - report["gameFps"],
                   "clockUnityFrameDeltaMinusDriver": clock_frame_delta - report["measuredUnityFrames"]})
    driver_cameras = {item["cameraId"]: item for item in report.get("cameras", [])}
    for camera in manifest.get("cameraResults", []):
        camera_id = camera["cameraId"]
        directory = camera.get("directory", "video/" + camera_id)
        frame_path = capture / directory / "frames.jsonl"
        selected, raw_selected, all_frames = [], [], 0
        boundary_rows = []
        for number, frame in enumerate(capture_reader.iter_records((frame_path).relative_to(capture).as_posix()), 1):
            all_frames += 1
            stamp = frame["stamp"]
            sync_id = stamp["syncSampleId"]
            if sync_id not in clocks:
                raise ValueError("Video sidecar has no matching clock: " + str(frame_path) + ":" + str(number))
            clock = clocks[sync_id]
            if stamp["unityFrame"] != clock["unityFrame"] or abs(stamp["realElapsedSeconds"] - clock["realElapsedSeconds"]) > tolerance:
                raise ValueError("Video sidecar stamp differs from matching clock")
            if first_id < sync_id <= last_id:
                selected.append(stamp)
            if report["sessionWindowStartRealSeconds"] < stamp["realElapsedSeconds"] <= report["sessionWindowEndRealSeconds"]:
                raw_selected.append(stamp)
            if sync_id in (first_id, last_id):
                boundary_rows.append({"syncSampleId": sync_id, "realElapsedSeconds": stamp["realElapsedSeconds"],
                                      "includedAnchored": first_id < sync_id <= last_id,
                                      "includedRawFloat": report["sessionWindowStartRealSeconds"] < stamp["realElapsedSeconds"] <= report["sessionWindowEndRealSeconds"]})
        times = [stamp["realElapsedSeconds"] for stamp in selected]
        if len({stamp["unityFrame"] for stamp in selected}) != len(selected):
            raise ValueError("Camera repeats a Unity frame instead of observing new frames")
        if any(b <= a for a, b in zip(times, times[1:])):
            raise ValueError("Video timestamps are not strictly increasing")
        if all_frames != camera["framesWritten"]:
            raise ValueError("Finalized manifest/sidecar frame count differs")
        driver = driver_cameras.get(camera_id, {})
        accepted_fps = len(times) / duration
        ratio = len(times) / clock_frame_delta
        if ratio > 1:
            raise ValueError("Accepted camera frames exceed measured game frames")
        camera_result = {"cameraId": camera_id, "sidecarPath": str(frame_path),
                         "sessionFrames": all_frames, "sessionRequestsDropped": camera.get("requestsDropped"),
                         "anchoredMeasuredFrames": len(times), "anchoredAcceptedFps": accepted_fps,
                         "acceptedToGameFrameRatio": ratio,
                         "unobservedGameFrames": clock_frame_delta - len(times),
                         "driverAcceptedToGameFrameRatio": driver.get("acceptedToGameFrameRatio"),
                         "rawFloatMeasuredFrames": len(raw_selected), "boundaryRows": boundary_rows,
                         "rawFloatMinusAnchoredFrames": len(raw_selected) - len(times),
                         "driverMeasuredFrames": driver.get("measuredAcceptedFrames"),
                         "driverAcceptedFps": driver.get("acceptedFps"),
                         "anchoredMinusDriverFrames": len(times) - driver.get("measuredAcceptedFrames", len(times)),
                         "anchoredMinusDriverFps": accepted_fps - driver.get("acceptedFps", accepted_fps),
                         "firstAcceptedStamp": selected[0] if selected else None,
                         "lastAcceptedStamp": selected[-1] if selected else None,
                         "acceptedIntervalFps": (len(times) - 1) / (times[-1] - times[0]) if len(times) > 1 else None,
                         "acceptedIntervalMs": describe([(b - a) * 1000 for a, b in zip(times, times[1:])])}
        result["cameras"].append(camera_result)
    ratios = [camera["acceptedToGameFrameRatio"] for camera in result["cameras"]]
    result["cameraGameFrameRatios"] = {"min": min(ratios), "max": max(ratios), "mean": sum(ratios) / len(ratios)} if ratios else None
    result["notes"] = ["Accepted frames are counted by original render stamps using startSyncId < syncSampleId <= endSyncId.",
                       "Window anchors use nearest sync clocks within the explicit tolerance; decimal JSON ULP differences cannot move a boundary frame.",
                       "Driver game FPS uses its Stopwatch window; anchored game FPS uses the co-recorded capture clock window, which can differ by a frame phase.",
                       "Nominal AVI playback FPS, targetClockSlots and estimatedMissingTargetSlots are not capture-rate targets and are intentionally excluded.",
                       "Primary metric is accepted camera frames / capture-clock Unity frame delta over the same sync-ID window; game FPS is a separate throughput tradeoff.",
                       "Process CPU totals span all threads; they do not identify a GPU, main-thread or I/O bottleneck."]
    return result


def inspect(report_path, tolerance):
    report = load(report_path)
    if not report.get("captureDirectory"):
        return _inspect(report_path, tolerance)
    with CaptureReader(report["captureDirectory"]) as reader:
        reader.validate_references()
        return _inspect(report_path, tolerance, reader)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--matrix", action="append", default=[], type=Path)
    parser.add_argument("--benchmark", action="append", default=[], type=Path)
    parser.add_argument("--tolerance", type=float, default=1e-6)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    paths = list(args.benchmark)
    for matrix in args.matrix:
        paths.extend(sorted(matrix.glob("*/benchmark.json")))
    paths = sorted(set(path.resolve() for path in paths))
    if not paths:
        parser.error("No benchmark.json inputs were found")
    output = {"schema": "stationflow.research_window_read.v1", "createdUtc": datetime.now(timezone.utc).isoformat(),
              "readerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "anchorToleranceSeconds": args.tolerance, "cases": [], "errors": []}
    for path in paths:
        try:
            output["cases"].append(inspect(path, args.tolerance))
        except Exception as error:
            output["errors"].append({"benchmarkPath": str(path), "error": str(error)})
    output["success"] = not output["errors"]
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"success": output["success"], "cases": len(output["cases"]), "errors": output["errors"], "out": str(args.out)}))
    return 0 if output["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
