"""Validate the StationFlow paired RGB capture. Requires Python 3 and Pillow.

Usage:
    python Tests/validate_capture.py Captures/Presentation --require-pedestrian-motion --require-sensor-self-test

The validator checks file/label/calibration integrity and evidence of scene motion.
It does not infer pixel-level detection correctness, occlusion, realism, or ML utility.
Projected centers use 0.04 px tolerance in FOV. Outside FOV only, tolerance is
max(0.04 px, 5e-6 * abs(expected coordinate)): float32 transform rounding becomes
ill-conditioned near the pinhole plane for points thousands of pixels off image.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import sys

from PIL import Image, ImageDraw, ImageStat


def reject_constant(value):
    raise ValueError(f"Non-finite JSON numeric constant: {value}")


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"), parse_constant=reject_constant)


def assert_finite(value, location):
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError(f"{location}: non-finite number")
    if isinstance(value, dict):
        for key, item in value.items():
            assert_finite(item, f"{location}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            assert_finite(item, f"{location}[{index}]")


def close(a, b, tolerance=1e-4):
    return abs(a - b) <= tolerance


def projection_of_center(obj, frame, sensor, fisheye):
    center = obj["world_aabb_center"]
    matrix = frame["world_to_camera_optical_row_major"]
    local = [sum(matrix[row * 4 + col] * center[col] for col in range(3)) + matrix[row * 4 + 3] for row in range(3)]
    x, y, z = local  # Optical Y points down, matching PNG row indices.
    near, far = sensor["near_clip"], sensor["far_clip"]
    if not fisheye:
        if z < near or z > far:
            return None
        return sensor["cx"] + sensor["focal_pixels"] * x / z, sensor["cy"] + sensor["focal_pixels"] * y / z
    dominant = max(abs(x), abs(y), abs(z))
    if z < 0 or dominant < near or dominant > far:
        return None
    lateral = math.hypot(x, y)
    if lateral < 1e-7:
        return sensor["cx"], sensor["cy"]
    radius = sensor["focal_pixels"] * math.atan2(lateral, z)
    return sensor["cx"] + radius * x / lateral, sensor["cy"] + radius * y / lateral


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", default="Captures/Presentation", type=Path)
    parser.add_argument("--expected-tracks", type=int)
    parser.add_argument("--require-pedestrian-motion", action="store_true")
    parser.add_argument("--require-sensor-self-test", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    directory = args.directory.resolve()
    errors = []
    warnings = []
    image_count = Counter()
    unique_images = defaultdict(set)
    image_ranges = defaultdict(list)
    fov_object_counts = defaultdict(list)
    object_counts = []
    known_categories = {}
    previous_positions = {}
    moving_tracks = set()
    phase_counts = Counter()
    frame_ids = []
    timestamps = []

    def require(condition, message):
        if not condition:
            errors.append(message)

    def vector(value, size, location):
        require(isinstance(value, list) and len(value) == size, f"{location}: expected {size}-component array")
        return isinstance(value, list) and len(value) == size

    try:
        calibration = read_json(directory / "calibration.json")
        assert_finite(calibration, "calibration")
        require(calibration.get("schema") == "stationflow.synthetic_traffic.sensor.v1", "Unexpected calibration schema")
        fps = calibration["fps"]
        require(isinstance(fps, int) and fps > 0, "Capture FPS must be a positive integer")
        sensors = {name: calibration[name] for name in ("pinhole", "fisheye")}
        for name, sensor in sensors.items():
            require(sensor["width"] >= 16 and sensor["height"] >= 16, f"{name}: invalid image dimensions")
            require(close(sensor["cx"], sensor["width"] / 2) and close(sensor["cy"], sensor["height"] / 2), f"{name}: principal point differs from image center")
            require(0 < sensor["near_clip"] < sensor["far_clip"], f"{name}: invalid clipping planes")
            require(sensor["focal_pixels"] > 0, f"{name}: invalid focal length")
            vector(sensor["world_to_camera_optical_row_major"], 16, f"{name} extrinsics")
            quaternion = sensor["rotation_xyzw"]
            if vector(quaternion, 4, f"{name} quaternion"):
                require(close(sum(v * v for v in quaternion), 1, .002), f"{name}: rotation is not a unit quaternion")
        pinhole, fisheye = sensors["pinhole"], sensors["fisheye"]
        require(pinhole["model"] == "pinhole", "Pinhole model mismatch")
        require(fisheye["model"] == "equidistant_180", "Fisheye model mismatch")
        expected_focal = pinhole["height"] / (2 * math.tan(math.radians(pinhole["vertical_fov_degrees"]) / 2))
        require(close(pinhole["focal_pixels"], expected_focal, .002), "Pinhole focal length does not match FOV")
        k = pinhole["K_row_major"]
        if vector(k, 9, "pinhole K"):
            expected_k = [expected_focal, 0, pinhole["cx"], 0, expected_focal, pinhole["cy"], 0, 0, 1]
            require(all(close(a, b, .002) for a, b in zip(k, expected_k)), "Pinhole K matrix inconsistent")
        require(fisheye["width"] == fisheye["height"], "Fisheye image must be square")
        require(close(fisheye["focal_pixels"], fisheye["width"] / math.pi, .002), "Fisheye equidistant focal length must be size/pi")
        require(close(fisheye["valid_circle_radius_pixels"], fisheye["width"] / 2), "Fisheye valid-circle radius mismatch")
        require(pinhole["position_world"] == fisheye["position_world"] and pinhole["rotation_xyzw"] == fisheye["rotation_xyzw"], "Sensors are not co-located with matched orientation")
        require("not measured" in calibration.get("label_semantics", ""), "Calibration must disclose missing occlusion/visibility measurements")

        expected_tracks = args.expected_tracks
        run_report = None
        if (directory / "run-report.json").exists():
            run_report = read_json(directory / "run-report.json")
            assert_finite(run_report, "run-report")
            if expected_tracks is None:
                expected_tracks = run_report["people"] + run_report["vehicles"]
            require(run_report["redLightViolations"] == 0, "Runtime reports red-light violations")
            require(run_report["collisions"] == 0, "Runtime reports body overlaps")
        else:
            warnings.append("No run-report.json; expected object count inferred from first frame")

        with (directory / "frames.jsonl").open(encoding="utf-8-sig") as stream:
            for line_number, line in enumerate(stream, 1):
                if not line.strip():
                    errors.append(f"frames.jsonl:{line_number}: unexpected blank line")
                    continue
                try:
                    frame = json.loads(line, parse_constant=reject_constant)
                    assert_finite(frame, f"frame line {line_number}")
                    frame_id = frame["frame"]
                    where = f"frame {frame_id}"
                    require(frame.get("schema") == "stationflow.synthetic_traffic.frame.v1", f"{where}: unexpected schema")
                    require(frame_id == line_number - 1, f"{where}: indices must be contiguous from zero")
                    require(frame["seed"] == calibration["seed"], f"{where}: seed differs from calibration")
                    time = frame["timestamp_seconds"]
                    require(time >= 0, f"{where}: negative simulation timestamp")
                    if timestamps:
                        require(close(time - timestamps[-1], 1 / fps, 1e-5), f"{where}: timestamp interval {time - timestamps[-1]:.9f} differs from 1/fps")
                    timestamps.append(time)
                    frame_ids.append(frame_id)
                    phase_counts[frame["traffic_phase"]] += 1
                    matrix = frame["world_to_camera_optical_row_major"]
                    if not vector(matrix, 16, f"{where} extrinsics"):
                        continue
                    require(all(close(v, target) for v, target in zip(matrix[12:16], [0, 0, 0, 1])), f"{where}: invalid homogeneous matrix bottom row")
                    position = frame["camera_position_world"]
                    if vector(position, 3, f"{where} camera position"):
                        camera_origin = [sum(matrix[row * 4 + col] * position[col] for col in range(3)) + matrix[row * 4 + 3] for row in range(3)]
                        require(all(abs(v) < .002 for v in camera_origin), f"{where}: extrinsics do not map camera position to zero")
                    objects = frame["objects"]
                    object_counts.append(len(objects))
                    if expected_tracks is None:
                        expected_tracks = len(objects)
                    require(len(objects) == expected_tracks, f"{where}: {len(objects)} labels, expected {expected_tracks} active tracks")
                    ids = [obj["track_id"] for obj in objects]
                    require(len(ids) == len(set(ids)), f"{where}: duplicate track IDs")
                    per_sensor_count = Counter()
                    for obj in objects:
                        track_id = obj["track_id"]
                        location = f"{where}, track {track_id}"
                        category = obj["category"]
                        require(category in ("car", "pedestrian"), f"{location}: unexpected category {category}")
                        require(known_categories.setdefault(track_id, category) == category, f"{location}: category changed during track")
                        point = obj["position_world"]
                        velocity = obj["velocity_world_m_s"]
                        if not (vector(point, 3, f"{location} position") and vector(velocity, 3, f"{location} velocity")):
                            continue
                        if track_id in previous_positions:
                            displacement = math.dist(point, previous_positions[track_id])
                            # Ignore recycling teleports when establishing evidence of motion.
                            if .002 < displacement < 5 and math.sqrt(sum(v * v for v in velocity)) > .02:
                                moving_tracks.add(track_id)
                        previous_positions[track_id] = point
                        vector(obj["world_aabb_center"], 3, f"{location} AABB center")
                        if vector(obj["world_aabb_size"], 3, f"{location} AABB size"):
                            require(all(value > 0 for value in obj["world_aabb_size"]), f"{location}: degenerate world bounds")
                        for name, sensor in sensors.items():
                            projection = obj[name]
                            place = f"{location}, {name}"
                            require(projection["visibility"] == "not_measured", f"{place}: unsupported visibility claim")
                            require(projection["bounds_method"] == "sampled_world_aabb_amodal_approximation", f"{place}: unexpected bounds method")
                            require(projection["total_samples"] == 98, f"{place}: wrong number of AABB samples")
                            require(0 <= projection["projected_samples"] <= 98, f"{place}: invalid valid-sample count")
                            require(projection["clipped_by_projection_domain"] == (projection["projected_samples"] < 98), f"{place}: clipping-domain flag mismatch")
                            amodal = projection.get("sampled_amodal_bbox_xyxy") or []
                            clipped = projection.get("clipped_bbox_xyxy") or []
                            if projection["projected_samples"]:
                                if vector(amodal, 4, f"{place} amodal bounds"):
                                    require(amodal[0] <= amodal[2] and amodal[1] <= amodal[3], f"{place}: reversed amodal bounds")
                            else:
                                require(not amodal, f"{place}: unexpected bounds without projected samples")
                            if projection["in_sensor_fov"]:
                                per_sensor_count[name] += 1
                                if vector(clipped, 4, f"{place} clipped bounds"):
                                    width, height = sensor["width"], sensor["height"]
                                    require(0 <= clipped[0] <= clipped[2] <= width and 0 <= clipped[1] <= clipped[3] <= height, f"{place}: clipped bbox outside image")
                                    if len(amodal) == 4:
                                        expected_clip = [min(width, max(0, amodal[0])), min(height, max(0, amodal[1])), min(width, max(0, amodal[2])), min(height, max(0, amodal[3]))]
                                        require(all(close(a, b, .01) for a, b in zip(clipped, expected_clip)), f"{place}: clipped bounds inconsistent with amodal bounds")
                            else:
                                require(not clipped, f"{place}: nonempty clipped bounds outside FOV")
                            center = projection.get("center_px") or []
                            expected_center = projection_of_center(obj, frame, sensor, name == "fisheye")
                            if expected_center is None:
                                require(not center, f"{place}: center projects outside valid camera domain")
                            elif vector(center, 2, f"{place} image center"):
                                # Unity computes the pose transform in float32. Near-plane,
                                # far-offscreen rays magnify tiny differences between its
                                # quaternion transform and serialized matrix multiplication.
                                # Preserve strict subpixel checks for every in-FOV label.
                                tolerances = [.04 if projection["in_sensor_fov"] else max(.04, 5e-6 * abs(value)) for value in expected_center]
                                require(all(close(a, b, tolerance) for a, b, tolerance in zip(center, expected_center, tolerances)), f"{place}: label center disagrees with independent calibration projection")

                    for name, sensor in sensors.items():
                        fov_object_counts[name].append(per_sensor_count[name])
                        relative = frame[f"{name}_rgb"]
                        expected_name = f"{name}/frame_{frame_id:06d}.png"
                        require(relative == expected_name, f"{where}: unexpected image path {relative}")
                        image_path = (directory / relative).resolve()
                        require(image_path.is_relative_to(directory), f"{where}: image path escapes capture directory")
                        if not image_path.is_relative_to(directory):
                            continue
                        with Image.open(image_path) as image:
                            image.load()  # Decoding catches truncation and invalid PNG payloads.
                            require(image.format == "PNG", f"{where}, {name}: image is not PNG")
                            require(image.size == (sensor["width"], sensor["height"]), f"{where}, {name}: image dimensions disagree with calibration")
                            require(image.mode in ("RGB", "RGBA"), f"{where}, {name}: expected RGB or RGBA image")
                            rgb = image.convert("RGB")
                            unique_images[name].add(hashlib.sha256(rgb.tobytes()).hexdigest())
                            thumb = rgb.resize((128, 128))
                            gray = thumb.convert("L")
                            mask = None
                            if name == "fisheye":
                                for corner in [(0, 0), (rgb.width - 1, 0), (0, rgb.height - 1), (rgb.width - 1, rgb.height - 1)]:
                                    require(max(rgb.getpixel(corner)) <= 2, f"{where}: fisheye corner outside valid circle is not black")
                                mask = Image.new("L", thumb.size)
                                ImageDraw.Draw(mask).ellipse((3, 3, 124, 124), fill=255)
                            stats = ImageStat.Stat(gray, mask)
                            low, high = stats.extrema[0]
                            image_ranges[name].append(high - low)
                            require(high - low >= 20 and stats.stddev[0] >= 4, f"{where}, {name}: image lacks scene dynamic range; possible blank render")
                            pixels = thumb.tobytes()
                            magenta_pixels = sum(pixels[index] > 220 and pixels[index + 1] < 40 and pixels[index + 2] > 220 for index in range(0, len(pixels), 3))
                            require(magenta_pixels < 128 * 128 * .1, f"{where}, {name}: large missing-shader magenta region")
                            image_count[name] += 1
                except Exception as exception:
                    errors.append(f"frames.jsonl:{line_number}: {type(exception).__name__}: {exception}")
                if len(errors) > 1000:
                    errors.append("Stopped after more than 1000 validation failures")
                    break

        require(len(frame_ids) >= 2, "Need at least two frame pairs to validate motion and synchronization")
        require(len(moving_tracks) >= 2, f"Only {len(moving_tracks)} tracks have sampled non-teleport motion")
        moving_by_category = Counter(known_categories[track] for track in moving_tracks)
        if args.require_pedestrian_motion:
            require(moving_by_category["pedestrian"] >= 2, "Presentation sample needs at least two moving pedestrian tracks")
        for name in sensors:
            require(image_count[name] == len(frame_ids), f"{name}: decoded image count does not match metadata frames")
            require(len(unique_images[name]) >= 2, f"{name}: all decoded images are identical")
            actual_files = {path.name for path in (directory / name).glob("frame_*.png")}
            expected_files = {f"frame_{frame:06d}.png" for frame in frame_ids}
            require(actual_files == expected_files, f"{name}: PNG directory has missing or extra frame files")
            require(max(fov_object_counts[name], default=0) >= 2, f"{name}: fewer than two objects ever project into sensor FOV")
        summary = read_json(directory / "capture_summary.json")
        assert_finite(summary, "capture_summary")
        require(summary["frames"] == len(frame_ids), "Capture summary frame count mismatch")
        require(summary["fps"] == fps and summary["seed"] == calibration["seed"], "Capture summary FPS/seed mismatch")
        if timestamps:
            require(close(summary["last_timestamp_seconds"], timestamps[-1], 1e-6), "Capture summary last timestamp mismatch")
        if run_report is not None:
            require(run_report["frames"] == len(frame_ids), "Run report frame count mismatch")
            require(run_report["fps"] == fps and run_report["seed"] == calibration["seed"], "Run report FPS/seed mismatch")
        projection_report = directory / "sensor-validation" / "sensor_projection_checks.txt"
        if args.require_sensor_self_test or projection_report.exists():
            require(projection_report.exists() and projection_report.read_text(encoding="utf-8-sig").startswith("PASS:"), "Sensor GPU/numerical projection self-test report missing or not PASS")
        if (directory / "failure.txt").exists():
            errors.append("Runtime wrote failure.txt")
    except Exception as exception:
        errors.append(f"{type(exception).__name__}: {exception}")

    report = {
        "schema": "stationflow.capture_validation.v1",
        "result": "PASS" if not errors else "FAIL",
        "capture_directory": str(directory),
        "frames": len(frame_ids),
        "decoded_pngs": dict(image_count),
        "unique_images": {name: len(values) for name, values in unique_images.items()},
        "object_count_range": [min(object_counts), max(object_counts)] if object_counts else [],
        "moving_tracks": len(moving_tracks),
        "moving_tracks_by_category": dict(Counter(known_categories[track] for track in moving_tracks)),
        "traffic_phase_frames": dict(phase_counts),
        "simulation_time_range": [timestamps[0], timestamps[-1]] if timestamps else [],
        "in_fov_object_count_range": {name: [min(values), max(values)] for name, values in fov_object_counts.items() if values},
        "minimum_image_gray_dynamic_range": {name: min(values) for name, values in image_ranges.items() if values},
        "projection_center_tolerance_pixels": {
            "in_sensor_fov": 0.04,
            "outside_sensor_fov": "max(0.04, 5e-6 * abs(expected_coordinate))",
            "reason": "Float32 quaternion/matrix transform differences are amplified for offscreen points near the pinhole plane; finite, domain and image-bound checks remain unchanged.",
        },
        "error_count": len(errors),
        "errors": errors[:100],
        "warnings": warnings,
        "limitations": "Structural, numerical projection, image integrity and motion checks only. In-FOV counts and amodal AABBs are not visible-object counts. No pixel-level detection, occlusion, realism or model-training quality certification.",
    }
    output = args.output or directory / "validation-report.json"
    if output.parent.exists():
        output.write_text(json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
