"""Validate a closed portable Unity data-collection session and its authoritative clock joins."""
import argparse
import bisect
import hashlib
import importlib.util
import json
import math
import re
import struct
import subprocess
from pathlib import Path

_READER_SPEC = importlib.util.spec_from_file_location("stationflow_capture_reader", Path(__file__).resolve().parents[1] / "Assets/StreamingAssets/TrainStationData/read_capture.py")
_READER_MODULE = importlib.util.module_from_spec(_READER_SPEC)
_READER_SPEC.loader.exec_module(_READER_MODULE)
CaptureReader, CaptureFormatError = _READER_MODULE.CaptureReader, _READER_MODULE.CaptureFormatError


def matrix4(values):
    if not isinstance(values, list) or len(values) != 16 or not all(isinstance(v, (int, float)) and math.isfinite(v) for v in values):
        raise ValueError("A finite column-major 4x4 matrix is required")
    return [[values[c * 4 + r] for c in range(4)] for r in range(4)]


def multiply(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(len(b))) for c in range(len(b[0]))] for r in range(len(a))]


def inverse(a):
    n = len(a)
    rows = [list(row) + [float(r == c) for c in range(n)] for r, row in enumerate(a)]
    for c in range(n):
        pivot = max(range(c, n), key=lambda r: abs(rows[r][c]))
        if abs(rows[pivot][c]) < 1e-12:
            raise ValueError("Singular calibration matrix")
        rows[c], rows[pivot] = rows[pivot], rows[c]
        scale = rows[c][c]
        rows[c] = [v / scale for v in rows[c]]
        for r in range(n):
            if r != c:
                scale = rows[r][c]
                rows[r] = [x - scale * y for x, y in zip(rows[r], rows[c])]
    return [row[n:] for row in rows]


def max_error(a, b):
    return max(abs(x - y) for row_a, row_b in zip(a, b) for x, y in zip(row_a, row_b))


def validate_projection(payload, width, height, static=False):
    """Independent CPU matrix/intrinsics reprojection, using Unity -Z view and integer-centred top-left pixels."""
    view = matrix4(payload["worldToCameraMatrix" if static else "worldToCamera"])
    projection = matrix4(payload["projectionMatrix" if static else "projection"])
    inverse(matrix4(payload["gpuProjectionMatrix" if static else "gpuProjection"]))
    inv_view = inverse(view)
    inv_projection = inverse(projection)
    if max_error(inv_view, matrix4(payload["cameraToWorldMatrix" if static else "cameraToWorld"])) > 0.002:
        raise ValueError("Exported camera inverse does not invert the view matrix")
    if max_error(inv_projection, matrix4(payload["inverseProjectionMatrix" if static else "inverseProjection"])) > 0.002:
        raise ValueError("Exported inverse projection is inconsistent")
    for axis, index in zip(("x", "y", "z"), range(3)):
        if not math.isfinite(payload["position"][axis]) or abs(payload["position"][axis] - inv_view[index][3]) > 0.002:
            raise ValueError("Camera position differs from its inverse view matrix")
    if projection[0][0] <= 0 or projection[1][1] <= 0 or abs(projection[3][2] + 1) > 1e-5 or abs(projection[3][3]) > 1e-5:
        raise ValueError("Projection is not a forward perspective pinhole matrix")
    expected = [[width * projection[0][0] / 2, -width * projection[0][1] / 2, width * (1 - projection[0][2]) / 2 - 0.5],
                [-height * projection[1][0] / 2, height * projection[1][1] / 2, height * (1 + projection[1][2]) / 2 - 0.5], [0, 0, 1]]
    values = payload["intrinsicMatrix"]
    if len(values) != 9 or not all(math.isfinite(v) for v in values):
        raise ValueError("Finite row-major pixel intrinsics are required")
    intrinsic = [values[r * 3:(r + 1) * 3] for r in range(3)]
    if max_error(intrinsic, expected) > 0.002:
        raise ValueError("Pixel intrinsics differ from the rendered projection")
    v = payload["inverseIntrinsicMatrix"]
    if len(v) != 9 or not all(math.isfinite(x) for x in v):
        raise ValueError("Finite inverse pixel intrinsics are required")
    inv_intrinsic = [v[r * 3:(r + 1) * 3] for r in range(3)]
    if max_error(inverse(intrinsic), inv_intrinsic) > 0.002:
        raise ValueError("Inverse pixel intrinsics are inconsistent")
    d = [[1, 0, 0, 0], [0, -1, 0, 0], [0, 0, -1, 0], [0, 0, 0, 1]]
    cv_view = multiply(d, view)
    if max_error(cv_view, matrix4(payload["worldToCvCameraMatrix" if static else "worldToCvCamera"])) > 0.002:
        raise ValueError("Unity-to-CV extrinsic conversion is inconsistent")
    if max_error(inverse(cv_view), matrix4(payload["cvCameraToWorldMatrix" if static else "cvCameraToWorld"])) > 0.002:
        raise ValueError("CV camera inverse is inconsistent")
    q = payload["rotationQuaternion" if static else "rotation"]
    x, y, z, w = (q[k] for k in ("x", "y", "z", "w"))
    if not all(math.isfinite(v) for v in (x, y, z, w)) or abs(x*x + y*y + z*z + w*w - 1) > 0.001:
        raise ValueError("Camera rotation quaternion is not finite and unit length")
    forward = [2*(x*z + w*y), 2*(y*z - w*x), 1 - 2*(x*x + y*y)]
    if max(abs(forward[r] + inv_view[r][2]) for r in range(3)) > 0.001:
        raise ValueError("Camera rotation disagrees with the view forward axis")
    euler = payload["rotationEuler"]
    if not all(math.isfinite(euler[axis]) for axis in ("x", "y", "z")):
        raise ValueError("Camera Euler angles are not finite")
    pitch, yaw = math.radians(euler["x"]), math.radians(euler["y"])
    euler_forward = [math.sin(yaw)*math.cos(pitch), -math.sin(pitch), math.cos(yaw)*math.cos(pitch)]
    if max(abs(a - b) for a, b in zip(forward, euler_forward)) > 0.001:
        raise ValueError("Euler angles disagree with the quaternion forward axis")
    for key, scale, offset in (("horizontalFieldOfViewDegrees", projection[0][0], projection[0][2]), ("verticalFieldOfViewDegrees", projection[1][1], projection[1][2])):
        expected_fov = math.degrees(math.atan((1 + offset) / scale) - math.atan((-1 + offset) / scale))
        if not math.isfinite(payload[key]) or abs(payload[key] - expected_fov) > 0.002:
            raise ValueError("Camera FOV differs from its rendered projection")
    if payload.get("capturedPostProcessing") is not False:
        raise ValueError("Pinhole camera capture unexpectedly uses post processing")
    if not math.isfinite(payload["nearClip"]) or not math.isfinite(payload["farClip"]) or not 0 < payload["nearClip"] < payload["farClip"]:
        raise ValueError("Camera clipping distances are invalid")
    for x, y, z in ((0, 0, -10), (-2, 1, -15), (1, -1, -5)):
        world = multiply(inv_view, [[x], [y], [z], [1]])
        clip = multiply(projection, multiply(view, world))
        pixel = [(clip[0][0] / clip[3][0] + 1) * width / 2 - 0.5, (1 - clip[1][0] / clip[3][0]) * height / 2 - 0.5]
        cv = multiply(intrinsic, [[x / -z], [-y / -z], [1]])
        if max(abs(pixel[i] - cv[i][0]) for i in range(2)) > 0.002:
            raise ValueError("World/view/projection and camera intrinsics disagree on pixel reprojection")
    return 3


def records(path, reader=None):
    if not path.exists():
        return []
    if reader is not None:
        return list(reader.iter_records(path.relative_to(reader.root).as_posix()))
    with path.open(encoding="utf-8-sig") as stream:
        return [json.loads(line) for line in stream if line.strip()]


def avi_info(path):
    with path.open("rb") as f:
        fourcc, size, kind = struct.unpack("<4sI4s", f.read(12))
        if fourcc != b"RIFF" or kind != b"AVI " or size + 8 != path.stat().st_size:
            raise ValueError("Incomplete AVI RIFF header")
        result = {"frames": 0, "indexFrames": 0}

        def chunks(end, parent=None):
            while f.tell() + 8 <= end:
                tag, length = struct.unpack("<4sI", f.read(8))
                begin, after = f.tell(), f.tell() + length
                if after > end:
                    raise ValueError("AVI chunk exceeds its parent")
                if tag == b"LIST":
                    typ = f.read(4)
                    chunks(after, typ)
                elif tag == b"avih":
                    data = f.read(length)
                    result.update(microseconds=struct.unpack_from("<I", data)[0], declaredFrames=struct.unpack_from("<I", data, 16)[0],
                                  width=struct.unpack_from("<I", data, 32)[0], height=struct.unpack_from("<I", data, 36)[0])
                elif tag == b"00dc" and parent == b"movi":
                    if f.read(2) != b"\xff\xd8":
                        raise ValueError("Video frame has no JPEG start marker")
                    f.seek(after - 2)
                    if f.read(2) != b"\xff\xd9":
                        raise ValueError("Video frame has no JPEG end marker")
                    result["frames"] += 1
                elif tag == b"idx1":
                    if length % 16:
                        raise ValueError("Invalid AVI index size")
                    result["indexFrames"] = length // 16
                f.seek(after + (length & 1))

        chunks(size + 8)
        if not result["frames"] or result["frames"] != result.get("declaredFrames") or result["frames"] != result["indexFrames"]:
            raise ValueError("AVI frame/index/header counts differ")
        return result


def finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def nonnegative_integer(value):
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def measured_cadence_checks(manifest, timeline, camera_frames, require):
    """Validate measured cadence from original clocks and retained completion prefixes.

    A rate snapshot describes the finalized prefix available at that observation, not all
    render stamps eventually written by a delayed worker. Window membership is exact
    (t - 1, t]; tolerance applies only to arithmetic values, never to nullable fields.
    """
    if manifest.get("captureCadence") != "each_unity_frame":
        return {}
    nominal = manifest.get("nominalPlaybackFps")
    require(nonnegative_integer(nominal) and nominal > 0, "Measured cadence requires a positive nominalPlaybackFps")
    require(nominal == manifest["captureOptions"].get("videoFps"), "Nominal AVI playback FPS differs from the compatibility videoFps field")
    definition = manifest.get("actualVideoRateDefinition")
    require(isinstance(definition, str) and bool(definition.strip()), "Measured cadence definition is missing")
    frame_checks, snapshot_checks, camera_snapshot_checks = 0, 0, 0
    times_by_camera = {}
    results = {c["cameraId"]: c for c in manifest.get("cameraResults") or []}
    for camera_id, frames in camera_frames.items():
        times, previous = [], None
        for row in frames:
            real = row["stamp"].get("realElapsedSeconds")
            require(finite_number(real) and real >= 0, "Measured video timestamp is not finite/nonnegative: " + camera_id)
            if not finite_number(real):
                continue
            interval = None if previous is None else real - previous
            require(interval is None or interval > 0, "Measured video render timestamps are not strictly increasing: " + camera_id)
            require("captureIntervalRealSeconds" in row and "captureFpsInstantaneous" in row and "captureFpsRolling1s" in row,
                    "Measured video frame rate fields are missing: " + camera_id)
            if previous is None:
                require(row.get("captureIntervalRealSeconds") is None and row.get("captureFpsInstantaneous") is None,
                        "First measured video interval and instantaneous FPS must be null: " + camera_id)
            elif interval > 0:
                observed_interval, observed_fps = row.get("captureIntervalRealSeconds"), row.get("captureFpsInstantaneous")
                require(finite_number(observed_interval) and math.isclose(observed_interval, interval, rel_tol=1e-7, abs_tol=1e-9),
                        "Measured video capture interval differs from original render stamps: " + camera_id)
                require(finite_number(observed_fps) and math.isclose(observed_fps, 1 / interval, rel_tol=1e-7, abs_tol=1e-9),
                        "Measured instantaneous video FPS differs from original render stamps: " + camera_id)
            times.append(real)
            expected_rolling = len(times) - bisect.bisect_right(times, real - 1.0)
            observed_rolling = row.get("captureFpsRolling1s")
            require(finite_number(observed_rolling) and math.isclose(observed_rolling, expected_rolling, rel_tol=0, abs_tol=1e-8),
                    "Measured per-frame rolling video FPS differs from fixed one-second saved stamps: " + camera_id)
            previous = real
            frame_checks += 1
        times_by_camera[camera_id] = times
    previous_counts, previous_drops, previous_bucket = {}, {}, -1
    rate_rows = [row for row in timeline if row.get("kind") == "measured_frame_rates"]
    last_clock_real = max((row.get("stamp", {}).get("realElapsedSeconds", 0) for row in timeline if row.get("kind") == "clock"), default=0)
    require(last_clock_real < 1 or bool(rate_rows), "Measured cadence has no measured_frame_rates records after the first second")
    for row in rate_rows:
        now = row.get("stamp", {}).get("realElapsedSeconds")
        require(finite_number(now) and now >= 1, "Measured rate snapshot has an invalid observation time")
        if not finite_number(now):
            continue
        bucket = math.floor(now)
        require(bucket > previous_bucket, "Measured rate snapshots repeat or reverse a reporting second")
        previous_bucket = bucket
        payload = row.get("measuredFrameRates")
        require(isinstance(payload, dict), "Measured rate snapshot has no typed measuredFrameRates payload")
        if not isinstance(payload, dict):
            continue
        window = payload.get("windowSeconds")
        require(finite_number(window) and window == 1, "Measured rate snapshot window must be one fixed real second")
        game_fps = payload.get("gameFpsRolling1s")
        require(finite_number(game_fps) and game_fps >= 0, "Measured game FPS must be finite and nonnegative")
        cameras = payload.get("cameras")
        require(isinstance(cameras, list), "Measured rate camera payload must be a list")
        if not isinstance(cameras, list):
            continue
        valid_cameras = [c for c in cameras if isinstance(c, dict)]
        ids = [c.get("cameraId") for c in valid_cameras]
        require(len(valid_cameras) == len(cameras) and len(ids) == len(set(ids)) and set(ids) == set(results),
                "Measured rate snapshot camera IDs differ from finalized selected cameras")
        for camera in valid_cameras:
            cid = camera.get("cameraId")
            if cid not in results:
                continue
            written, dropped = camera.get("framesWritten"), camera.get("requestsDropped")
            valid_written = nonnegative_integer(written) and previous_counts.get(cid, 0) <= written <= len(camera_frames.get(cid, []))
            valid_dropped = nonnegative_integer(dropped) and previous_drops.get(cid, 0) <= dropped <= results[cid]["requestsDropped"]
            require(valid_written, "Measured rate finalized prefix count is invalid or backwards: " + cid)
            require(valid_dropped, "Measured rate dropped-request counter is invalid or backwards: " + cid)
            observed_fps = camera.get("videoFpsRolling1s")
            require(finite_number(observed_fps) and observed_fps >= 0, "Measured rolling camera FPS must be finite and nonnegative: " + cid)
            if valid_written:
                prefix = times_by_camera.get(cid, [])[:written]
                require(all(t <= now for t in prefix), "Measured rate finalized prefix contains future render stamps: " + cid)
                expected_fps = bisect.bisect_right(prefix, now) - bisect.bisect_right(prefix, now - 1.0)
                require(finite_number(observed_fps) and math.isclose(observed_fps, expected_fps, rel_tol=0, abs_tol=1e-8),
                        "Measured rolling camera FPS differs from its finalized prefix/window: " + cid)
                previous_counts[cid] = written
            if valid_dropped:
                previous_drops[cid] = dropped
            camera_snapshot_checks += 1
        snapshot_checks += 1
    return {"measuredVideoRateFrames": frame_checks, "measuredFrameRateSnapshots": snapshot_checks,
            "measuredCameraRateSnapshots": camera_snapshot_checks}


def filesystem_alias(path):
    # Windows directory junctions are reparse points, even when is_symlink() is false.
    try:
        return path.is_symlink() or bool(getattr(path.lstat(), "st_file_attributes", 0) & 0x400)
    except FileNotFoundError:
        return False


def profile_payload_present(payload):
    """Unity can expand an unassigned inline class into its constructor/default value object."""
    if payload is None:
        return False
    if not isinstance(payload, dict):
        return True
    policy_defaults = {"urgentWindowSeconds": 25, "optionalNeedWeight": 4, "informationNeedWeight": 3,
                       "repeatedActivityMultiplier": .15, "alightingInfoWeight": 1, "alightingShopWeight": 1,
                       "alightingToiletWeight": 1, "alightingExitWeight": .15}

    def meaningful(value, path):
        if path == ("policyParameters",) and isinstance(value, dict):
            return any(not (key in policy_defaults and finite_number(item) and math.isclose(item, policy_defaults[key], rel_tol=0, abs_tol=1e-7))
                       for key, item in value.items())
        if path == ("mobilityProfile", "groupPaceFactor") and finite_number(value) and value == 1:
            return False
        if isinstance(value, dict):
            return any(meaningful(item, path + (key,)) for key, item in value.items())
        if isinstance(value, list):
            return any(meaningful(item, path) for item in value)
        return bool(value)

    return meaningful(payload, ())


AVATAR_STREAMS = {"trajectories": "trajectories.jsonl", "animations": "animations.jsonl",
                  "decisions": "decisions.jsonl", "events": "events.jsonl", "skeletons": "skeleton-poses.bin"}
AVATAR_OPTIONS = ("profiles", *AVATAR_STREAMS)


def per_agent_data(folder, manifest, options, scenario, require, checks, reader):
    """Read current logical identities without treating reusable physical actor slots as identities."""
    data = {"rows": {name: [] for name in AVATAR_STREAMS.values()}, "profiles": [], "ids": set()}
    enabled = any(options.get(toggle) for toggle in AVATAR_OPTIONS)
    checks.update(agentLayout="per_agent.v3", registeredAgents=0, materializedAgents=0,
                  perAgentFiles=0, perAgentProfiles=0)
    require(manifest.get("storageLayout") == "per_agent.v3", "Unknown per-agent storage layout")
    require(manifest.get("agentsDirectory") == ("agents" if enabled else ""), "Agent directory declaration disagrees with avatar toggles")
    require(manifest.get("agentIndexFile") == ("agents/index.json" if enabled else ""), "Agent index declaration disagrees with avatar toggles")
    for name in ("profiles.json", "profiles.jsonl", "trajectories.jsonl", "animations.jsonl", "decisions.jsonl", "skeleton-poses.bin"):
        require(not (folder / name).exists(), "Flat avatar file duplicates the per-agent layout: " + name)
    require(not scenario.get("profiles") and not scenario.get("scheduledJourneys"), "Avatar arrays duplicated into per-agent scenario metadata")
    root = folder / "agents"
    if not enabled:
        require(not root.exists(), "Agent directory/index exists while all avatar options are disabled")
        return data
    root_safe = not filesystem_alias(root) and root.resolve().is_relative_to(folder)
    require(root_safe and root.is_dir(), "Agent directory is missing, aliased or escapes the capture")
    if not root_safe or not root.is_dir():
        return data
    file_identities = {}

    def safe_file(path):
        safe = not filesystem_alias(path) and path.resolve().is_relative_to(root) and path.is_file()
        require(safe, "Per-agent file is missing, aliased or outside agents: " + str(path.relative_to(folder)))
        if not safe:
            return False
        stat = path.stat()
        identity = (stat.st_dev, stat.st_ino)
        if stat.st_ino:
            require(identity not in file_identities, "Per-agent files share a physical alias: " + str(path.relative_to(folder)))
            if identity in file_identities:
                return False
            file_identities[identity] = path
        return True

    def json_file(path):
        if not safe_file(path):
            return None
        try:
            return reader.read_json(path.relative_to(folder).as_posix())
        except (OSError, ValueError) as error:
            require(False, "Invalid per-agent JSON " + str(path.relative_to(folder)) + ": " + str(error))
            return None

    index = json_file(root / "index.json")
    require(isinstance(index, dict), "Agent index must be a typed JSON object")
    if not isinstance(index, dict):
        return data
    require(index.get("schema") == "stationflow.agents_index.v1", "Unknown agent index schema")
    require(index.get("runId") == manifest["runId"], "Agent index belongs to a foreign capture run")
    require(isinstance(index.get("identityPolicy"), str) and bool(index["identityPolicy"].strip()), "Agent identity policy is missing")
    entries = index.get("agents")
    require(isinstance(entries, list), "Agent index has no explicit agents list")
    if not isinstance(entries, list):
        return data
    directories, valid_entries = set(), []
    for entry in entries:
        require(isinstance(entry, dict), "Agent index entry must be an object")
        if not isinstance(entry, dict):
            continue
        agent_id, relative = entry.get("agentId"), entry.get("directory")
        valid_id = isinstance(agent_id, str) and bool(agent_id.strip()) and all(ord(c) >= 32 for c in agent_id)
        require(valid_id, "Agent index contains an empty/invalid logical ID")
        if not valid_id:
            continue
        require(agent_id not in data["ids"], "Duplicate logical agent ID in index: " + agent_id)
        data["ids"].add(agent_id)
        if not isinstance(relative, str):
            require(False, "Agent index directory must be a string: " + agent_id)
            continue
        folded = relative.casefold()
        require(folded not in directories, "Agent directory case collision or alias in index: " + relative)
        if folded in directories:
            continue
        directories.add(folded)
        # The writer's readable ASCII directory retains a hash of the original ordinal UTF-8 logical ID.
        match = re.fullmatch(r"agents/(agent-([a-z0-9](?:[a-z0-9-]{0,54}[a-z0-9])?)-([0-9a-f]{16}))", relative)
        safe_relative = bool(match) and match.group(3) == hashlib.sha256(agent_id.encode("utf-8")).hexdigest()[:16]
        require(safe_relative, "Unsafe or identity-mismatched agent directory: " + relative)
        if not safe_relative:
            continue
        directory = folder / relative
        safe = not filesystem_alias(directory) and directory.resolve().is_relative_to(root)
        require(safe, "Agent directory is a symbolic alias or escapes agents: " + relative)
        require(not directory.exists() or directory.is_dir(), "Agent directory path names a file: " + relative)
        if safe and (not directory.exists() or directory.is_dir()):
            valid_entries.append((agent_id, directory))
    checks["registeredAgents"] = len(data["ids"])
    count = scenario.get("participantCount")
    require(nonnegative_integer(count) and count == len(data["ids"]), "Agent index does not register every planned logical journey")
    valid_names = {directory.name for _, directory in valid_entries}
    for child in root.iterdir():
        if child.name == "index.json":
            continue
        require(child.name in valid_names and child.is_dir() and not filesystem_alias(child), "Unregistered or aliased content in agents: " + child.name)
    allowed = {filename for toggle, filename in AVATAR_STREAMS.items() if options.get(toggle)}
    if options.get("profiles"):
        allowed.add("profile.json")
    if options.get("trajectories") or options.get("animations"):
        allowed.add("state-changes.jsonl")
    if options.get("skeletons"):
        allowed.update(("skeleton-bindings.jsonl", "skeleton-index.jsonl"))
    for agent_id, directory in valid_entries:
        profile_path = directory / "profile.json"
        if options.get("profiles"):
            packet = json_file(profile_path)
            require(isinstance(packet, dict), "Missing single-agent profile payload: " + agent_id)
            if isinstance(packet, dict):
                profile, journey = packet.get("profile"), packet.get("journey")
                require(isinstance(profile, dict) and profile.get("agentId") == agent_id, "Profile identity crosses its indexed agent directory: " + agent_id)
                require(isinstance(journey, dict) and journey.get("journeyId") == agent_id, "Journey identity crosses its indexed agent directory: " + agent_id)
                require(not any(key in packet for key in ("profiles", "journeys", "scheduledJourneys")), "Profile payload contains duplicate arrays: " + agent_id)
                data["profiles"].append((str(profile_path.relative_to(folder)), packet))
                checks["perAgentProfiles"] += 1
        if not directory.exists():
            continue  # Planned identities may not have spawned and have no enabled profile file.
        checks["materializedAgents"] += 1
        for path in directory.iterdir():
            valid = path.name in allowed and path.is_file() and not filesystem_alias(path)
            require(valid, "Disabled, unknown or aliased per-agent file: " + str(path.relative_to(folder)))
            if not valid:
                continue
            checks["perAgentFiles"] += 1
            if path.name in ("profile.json", "state-changes.jsonl", "skeleton-bindings.jsonl", "skeleton-index.jsonl"):
                continue
            if not safe_file(path):
                continue
            try:
                rows = records(path, reader)
            except (OSError, ValueError) as error:
                require(False, "Invalid per-agent stream " + str(path.relative_to(folder)) + ": " + str(error))
                continue
            for row in rows:
                require(isinstance(row, dict), "Per-agent stream record must be an object: " + str(path.relative_to(folder)))
                if not isinstance(row, dict):
                    continue
                require(row.get("agentId") == agent_id, "Cross-agent row contamination in " + str(path.relative_to(folder)))
                if profile_payload_present(row.get("profile")):
                    require(isinstance(row["profile"], dict) and row["profile"].get("agentId") == agent_id, "Nested event profile crosses its indexed agent: " + agent_id)
                data["rows"][path.name].append((str(path.relative_to(folder)), row))
    return data



def _validate(folder, decode, reader):
    folder = Path(folder).resolve()
    errors, checks, evidence = [], {}, []

    def require(condition, message):
        if not condition:
            errors.append(message)

    def evidence_file(path):
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        evidence.append({"file": str(path.relative_to(folder)), "bytes": path.stat().st_size, "sha256": digest.hexdigest()})

    manifest = reader.manifest
    require(manifest.get("schema") == "stationflow.station_capture.v4", "Capture schema must be current v4")
    checks["normalizedReferences"] = reader.validate_references()
    require(manifest.get("status") == "complete", "Session was not closed successfully")
    require(not manifest.get("errors"), "Manifest contains capture errors")
    run_id, options = manifest["runId"], manifest["captureOptions"]
    schema_metadata = manifest.get("dataSchema")
    checks["dataSchemaFilesChecked"] = 0
    if schema_metadata:
        require(bool(schema_metadata.get("schemaVersion")) and bool(schema_metadata.get("contractFingerprint")), "Data-schema version/fingerprint is missing")
        for key in ("htmlFile", "jsonFile"):
            relative = Path(schema_metadata[key])
            require(not relative.is_absolute() and (folder / relative).resolve().is_relative_to(folder), "Data-schema document path escapes the capture directory")
            path = folder / relative
            require(path.is_file(), "Packaged data-schema document is missing: " + str(relative))
            if path.is_file():
                checks["dataSchemaFilesChecked"] += 1
                if key == "jsonFile":
                    schema = json.loads(path.read_text(encoding="utf-8-sig"))
                    require(schema.get("schemaVersion") == schema_metadata["schemaVersion"], "Packaged data-schema version differs from the manifest")
                    require(schema.get("contractFingerprint") == schema_metadata["contractFingerprint"], "Packaged data-schema fingerprint differs from the manifest")
                    canonical = schema.get("canonicalContract")
                    require(isinstance(canonical, str) and hashlib.sha256((canonical or "").encode("utf-8")).hexdigest() == schema_metadata["contractFingerprint"], "Packaged data-schema canonical contract hash differs")
                else:
                    html = path.read_text(encoding="utf-8-sig")
                    require('<html lang="fr"' in html and 'name="data-contract-fingerprint" content="' + schema_metadata["contractFingerprint"] + '"' in html,
                            "French data-schema HTML language/fingerprint is inconsistent")
    for filename in ("manifest.json", "scenario.json", "README.txt", "sync.jsonl", "normalization.json", "record-definitions.jsonl", "rosters.jsonl", "state-checkpoints.jsonl"):
        require((folder / filename).exists(), "Missing mandatory file: " + filename)
    root_files = {"timetable": "timetable.json", "events": "events.jsonl"}
    for toggle, filename in root_files.items():
        require((folder / filename).exists() == bool(options.get(toggle)), "Toggle/file disagreement: " + toggle)
    require((folder / "train-states.jsonl").exists() == bool(options.get("timetable")), "Toggle/file disagreement: train-states")
    scenario = reader.read_json("scenario.json")
    if not options.get("profiles"):
        require(not scenario.get("profiles") and not scenario.get("scheduledJourneys"), "Profiles leaked into scenario metadata while their toggle was disabled")
    if not options.get("timetable"):
        require(not scenario.get("services"), "Timetable leaked into scenario metadata while its toggle was disabled")
    agents = per_agent_data(folder, manifest, options, scenario, require, checks, reader)
    timeline = records(folder / "sync.jsonl", reader)
    stamps, snapshots, previous_id, previous_real, previous_frame = {}, {}, 0, -1.0, -1
    for row in timeline:
        stamp = row.get("stamp") or {}
        require(stamp.get("runId") == run_id, "Foreign run ID in sync timeline")
        sample = stamp.get("syncSampleId", 0)
        if row.get("kind") == "clock":
            require(sample > previous_id, "Clock sample IDs are not strictly increasing")
            require(stamp["realElapsedSeconds"] >= previous_real, "Monotonic clock moved backwards")
            require(stamp["unityFrame"] >= previous_frame, "Unity frame number moved backwards")
            require(bool(stamp.get("utc")) and bool(stamp.get("simulationDate")), "Clock UTC/date is missing")
            previous_id, previous_real, previous_frame = sample, stamp["realElapsedSeconds"], stamp["unityFrame"]
            stamps[sample] = stamp
        else:
            require(stamps.get(sample) == stamp, "Marker references a missing or different clock sample")
            if row.get("kind") == "avatar_snapshot":
                ids = row.get("activeJourneyIds")
                require(isinstance(ids, list), "Avatar snapshot has no explicit active journey list")
                ids = ids or []
                require(len(ids) == len(set(ids)), "Avatar snapshot active journey list contains duplicates")
                if agents is not None:
                    require(set(ids).issubset(agents["ids"]), "Avatar snapshot references an unregistered logical agent")
                require(sample not in snapshots or snapshots[sample] == set(ids), "Two avatar snapshots disagree at the same clock sample")
                snapshots[sample] = set(ids)
    require(any(r.get("kind") == "capture_started" for r in timeline), "Capture start marker missing")
    require(any(r.get("kind") == "capture_stopped" for r in timeline), "Capture stop marker missing")
    require(len(stamps) == manifest.get("syncSamples"), "Manifest clock count differs from timeline")
    checks["clockSamples"] = len(stamps)
    avatar_data = {}
    for filename in ("events.jsonl", "decisions.jsonl", "trajectories.jsonl", "animations.jsonl", "skeleton-poses.bin", "train-states.jsonl"):
        rows = [] if filename in ("decisions.jsonl", "trajectories.jsonl", "animations.jsonl", "skeleton-poses.bin") else [(filename, row) for row in records(folder / filename, reader)]
        if filename == "events.jsonl":
            for label, row in rows:
                require(row.get("agentId") in (None, ""), "Root events must contain station events only")
                require(not profile_payload_present(row.get("profile")), "Root station event contains duplicated avatar profile data")
                require(set(row.get("activeJourneyIds") or []).issubset(agents["ids"]), "Station event references an unregistered logical agent")
        rows += agents["rows"].get(filename, [])
        if filename in ("trajectories.jsonl", "animations.jsonl", "skeleton-poses.bin"):
            avatar_data[filename] = {}
        for label, row in rows:
            stamp = row.get("captureStamp") or {}
            require(stamps.get(stamp.get("syncSampleId")) == stamp, label + " references a missing or different clock sample")
            if filename in avatar_data:
                sample_rows = avatar_data[filename].setdefault(stamp.get("syncSampleId"), {})
                agent_id = row.get("agentId")
                require(agent_id not in sample_rows, "Duplicate avatar data at one clock sample in " + label)
                sample_rows[agent_id] = row
            if filename == "events.jsonl" and not options.get("profiles"):
                require(not profile_payload_present(row.get("profile")), "Profile data leaked into an event while its toggle was disabled")
        checks[filename] = len(rows)
    profile_packets = agents["profiles"]
    for filename in ("timetable.json",):
        path = folder / filename
        if path.exists():
            profile_packets.append((filename, reader.read_json(filename)))
    for label, row in profile_packets:
        stamp = row.get("captureStamp") or {}
        require(stamps.get(stamp.get("syncSampleId")) == stamp, label + " references a missing or different clock sample")
    camera_results = {c["cameraId"]: c for c in manifest.get("cameraResults") or []}
    require(set(camera_results) == set(manifest.get("selectedCameraIds") or []), "Camera results and selection differ")
    ffmpeg = next((Path(__file__).resolve().parents[1] / "Tools/PythonPackages/imageio_ffmpeg/binaries").glob("ffmpeg*.exe"), None)
    exact_frame_joins, paused_exact_frame_joins, decoded, camera_batches, reprojections = 0, 0, [], {}, 0
    camera_frames = {}
    for camera_id, camera in camera_results.items():
        directory = folder / camera["directory"]
        calibration = reader.read_json((directory / "calibration.json").relative_to(folder).as_posix())
        require(calibration["cameraId"] == camera_id, "Calibration camera ID differs")
        require(calibration["width"] == options["videoWidth"] and calibration["height"] == options["videoHeight"], "Calibration capture dimensions differ")
        require(len(calibration["worldToCameraMatrix"]) == 16 and len(calibration["projectionMatrix"]) == 16, "Calibration matrix length differs")
        enhanced = calibration.get("calibrationVersion") == "stationflow.pinhole.v1"
        if enhanced:
            try:
                reprojections += validate_projection(calibration, options["videoWidth"], options["videoHeight"], static=True)
                require(calibration.get("cameraModel") == "ideal-pinhole", "Camera model is not pinhole")
                require(calibration.get("distortionCoefficients") == [0] * 5, "Pinhole camera reports nonzero distortion")
                require("left-handed" in calibration.get("coordinateSystem", "") and "pixel centre" in calibration.get("pixelMapping", ""), "Camera coordinate/pixel conventions are missing")
                require(bool(calibration.get("graphicsApi")) and isinstance(calibration.get("readbackRowsFlipped"), bool), "Camera graphics API / row normalization evidence is missing")
            except (KeyError, ValueError, TypeError) as error:
                errors.append(camera_id + " static calibration: " + str(error))
        frames = records(directory / "frames.jsonl", reader)
        camera_frames[camera_id] = frames
        require(len(frames) == camera["framesWritten"] and len(frames) > 0, "Camera did not produce the declared frame count: " + camera_id)
        segments, previous_request, previous_video_real, previous_video_unity = {}, -1, -1.0, -1
        for row in frames:
            stamp = row["stamp"]
            require(stamps.get(stamp.get("syncSampleId")) == stamp, "Video frame references a missing/different clock sample")
            require(stamp["realElapsedSeconds"] >= previous_video_real and stamp["unityFrame"] >= previous_video_unity, "Video capture stamps moved backwards")
            previous_video_real, previous_video_unity = stamp["realElapsedSeconds"], stamp["unityFrame"]
            if manifest.get("videoFrameAvatarSnapshots") and (options.get("trajectories") or options.get("animations") or options.get("skeletons")):
                sample = stamp["syncSampleId"]
                require(sample in snapshots, "Video frame has no declared avatar snapshot")
                expected = snapshots.get(sample, set())
                joined = sample in snapshots
                for toggle, filename in (("trajectories", "trajectories.jsonl"), ("animations", "animations.jsonl"), ("skeletons", "skeleton-poses.bin")):
                    if options.get(toggle):
                        actual = set(avatar_data[filename].get(sample, {}))
                        require(actual == expected, "Video frame active avatar set differs from " + filename)
                        joined = joined and actual == expected
                exact_frame_joins += int(joined)
                paused_exact_frame_joins += int(joined and stamp.get("paused", False))
            require(row["requestId"] > previous_request, "Video request IDs are not increasing")
            previous_request = row["requestId"]
            segment = segments.setdefault(row["segment"], [])
            require(row["videoFrame"] == len(segment), "Encoded video frame order is discontinuous")
            require(abs(row["videoTimeSeconds"] - row["videoFrame"] / options["videoFps"]) < 1e-9, "AVI nominal time is inconsistent")
            require(len(row["camera"]["worldToCamera"]) == 16, "Per-frame camera matrix is missing")
            camera_batches.setdefault(camera_id, {})[row["requestId"]] = stamp
            if enhanced:
                try:
                    reprojections += validate_projection(row["camera"], options["videoWidth"], options["videoHeight"])
                except (KeyError, ValueError, TypeError) as error:
                    errors.append(camera_id + " frame " + str(row["videoFrame"]) + " calibration: " + str(error))
            segment.append(row)
        require(len(segments) == camera["segments"], "Declared camera segment count differs")
        for number, rows in segments.items():
            path = directory / f"segment-{number:04}.avi"
            try:
                info = avi_info(path)
                require(info["frames"] == len(rows), "Video frame count differs from timestamp sidecar")
                require(info["width"] == options["videoWidth"] and info["height"] == options["videoHeight"], "AVI dimensions differ")
                if manifest.get("captureCadence") == "each_unity_frame":
                    nominal = manifest.get("nominalPlaybackFps")
                    require(finite_number(nominal) and nominal > 0 and abs(info["microseconds"] - 1_000_000 / nominal) <= 1,
                            "AVI header does not use the declared nominal playback FPS")
                if decode:
                    require(ffmpeg is not None, "FFmpeg decode requested but the validation tool is unavailable")
                    if ffmpeg:
                        result = subprocess.run([str(ffmpeg), "-v", "error", "-i", str(path), "-f", "null", "-"], capture_output=True, text=True, timeout=180)
                        require(result.returncode == 0 and not result.stderr.strip(), "Video decoder reported errors: " + result.stderr)
                        decoded.append({"file": str(path.relative_to(folder)), "exitCode": result.returncode, "decoderErrors": result.stderr.strip()})
            except (OSError, ValueError, struct.error) as error:
                errors.append(str(path.relative_to(folder)) + ": " + str(error))
        checks[camera_id] = {"frames": len(frames), "segments": len(segments), "dropped": camera["requestsDropped"]}
    checks.update(measured_cadence_checks(manifest, timeline, camera_frames, require))
    shared_batches = 0
    if len(camera_batches) > 1:
        for request in set.union(*(set(rows) for rows in camera_batches.values())):
            batch = [rows[request] for rows in camera_batches.values() if request in rows]
            if len(batch) > 1:
                require(all(stamp == batch[0] for stamp in batch), "Multi-camera request batch has different clock stamps")
                shared_batches += 1
        require(shared_batches > 0, "Multiple selected cameras have no synchronized frame batch")
        if all(c["requestsDropped"] == 0 for c in camera_results.values()):
            first = next(iter(camera_batches.values()))
            require(all(set(rows) == set(first) for rows in camera_batches.values()), "No-drop camera streams have different request sets")
    # Inspect before descending; rglob can follow a Windows junction into unrelated data.
    evidence_paths, pending = [], [folder]
    while pending:
        for path in pending.pop().iterdir():
            if filesystem_alias(path) or not path.resolve().is_relative_to(folder):
                require(False, "Aliased/outside evidence path: " + str(path.relative_to(folder)))
                continue
            evidence_paths.append(path)
            if path.is_dir():
                pending.append(path)
    for path in sorted(evidence_paths):
        if path.is_file():
            require(not path.name.endswith(".tmp"), "Unfinalized temporary evidence remains: " + path.name)
            evidence_file(path)
    checks["exactVideoFrameAvatarJoins"] = exact_frame_joins
    checks["pausedExactVideoFrameAvatarJoins"] = paused_exact_frame_joins
    checks["multiCameraSharedBatches"] = shared_batches
    checks["calibrationReprojections"] = reprojections
    return {"success": not errors, "capture": str(folder), "runId": run_id, "checks": checks,
            "decodeRequested": decode, "decodedVideos": decoded, "errors": errors, "files": evidence}


def validate(folder, decode=False):
    """Validate only the current normalized format, without writing expanded captures."""
    try:
        with CaptureReader(folder) as reader:
            return _validate(folder, decode, reader)
    except (CaptureFormatError, OSError, KeyError, TypeError, ValueError) as error:
        return {"success": False, "capture": str(Path(folder).resolve()), "decodeRequested": decode,
                "errors": [type(error).__name__ + ": " + str(error)], "checks": {}, "files": []}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--decode", action="store_true")
    args = parser.parse_args()
    try:
        report = validate(args.capture.resolve(), args.decode)
    except Exception as error:
        report = {"success": False, "capture": str(args.capture), "errors": [f"{type(error).__name__}: {error}"]}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "files"}, indent=2, ensure_ascii=False))
    raise SystemExit(0 if report["success"] else 1)
