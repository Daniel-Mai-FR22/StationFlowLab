"""Portable, read-only StationFlow v4 capture reader (Python 3.10+, standard library).

CaptureReader expands current v4 references lazily. Its temporary
SQLite index contains shared clocks and state revisions, never expanded dense streams.
Use as a context manager so temporary indexes close promptly. Source files are never
changed. The CLI exports only to a newly created directory.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import re
import struct
from pathlib import Path, PurePosixPath
import sqlite3
import sys

V4 = "stationflow.station_capture.v4"
ENCODING = "references-and-state-revisions.v1"
AVATAR_STREAMS = ("trajectories.jsonl", "animations.jsonl", "decisions.jsonl", "events.jsonl", "skeleton-poses.bin")
SKELETON_MAGIC = b"SFLPOSE1"
SKELETON_SCHEMA = "stationflow.skeleton_definition.v1"
SKELETON_JOINTS = tuple("Hips LeftUpperLeg RightUpperLeg LeftLowerLeg RightLowerLeg LeftFoot RightFoot Spine Chest Neck Head LeftShoulder RightShoulder LeftUpperArm RightUpperArm LeftLowerArm RightLowerArm LeftHand RightHand LeftToes RightToes LeftEye RightEye Jaw LeftThumbProximal LeftThumbIntermediate LeftThumbDistal LeftIndexProximal LeftIndexIntermediate LeftIndexDistal LeftMiddleProximal LeftMiddleIntermediate LeftMiddleDistal LeftRingProximal LeftRingIntermediate LeftRingDistal LeftLittleProximal LeftLittleIntermediate LeftLittleDistal RightThumbProximal RightThumbIntermediate RightThumbDistal RightIndexProximal RightIndexIntermediate RightIndexDistal RightMiddleProximal RightMiddleIntermediate RightMiddleDistal RightRingProximal RightRingIntermediate RightRingDistal RightLittleProximal RightLittleIntermediate RightLittleDistal UpperChest".split())


class CaptureFormatError(ValueError):
    """Malformed, foreign, ambiguous or unresolved capture evidence."""


def _object_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise CaptureFormatError("Duplicate JSON key: " + key)
        result[key] = value
    return result


def _loads(text, strict=False):
    def reject(value):
        raise CaptureFormatError("Non-finite JSON number: " + value)
    try:
        return json.loads(text, object_pairs_hook=_object_pairs,
                          **({"parse_constant": reject} if strict else {}))
    except (ValueError, TypeError) as error:
        if isinstance(error, CaptureFormatError):
            raise
        raise CaptureFormatError("Invalid JSON: " + str(error)) from error


def _compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def _integer(value, label, minimum=1):
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum:
        raise CaptureFormatError(label + " must be an integer >= " + str(minimum))
    return value


def _finite(value, label):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise CaptureFormatError(label + " must be finite")
    return value


def _alias(path):
    if path.is_symlink():
        return True
    try:
        return bool(getattr(path.lstat(), "st_file_attributes", 0) & 0x400)
    except FileNotFoundError:
        return False


class CaptureReader:
    """Current-format facade. Relative paths use forward slashes and cannot escape root.

    `manifest` is the actual manifest. `read_json` and `iter_records` return owned
    expanded logical dictionaries. They do not disguise the capture version.
    `iter_raw_records` exposes stored records for independent format audits.
    """
    def __init__(self, root):
        self.root = Path(root).resolve()
        if not self.root.is_dir():
            raise CaptureFormatError("Capture directory is missing: " + str(self.root))
        self.manifest = self._json("manifest.json")
        if not isinstance(self.manifest, dict):
            raise CaptureFormatError("Manifest must be an object")
        schema = self.manifest.get("schema")
        if schema != V4:
            raise CaptureFormatError("Unknown capture schema: " + str(schema))
        self.run_id = self.manifest.get("runId")
        self._db = None
        self._prepared = False
        self._closed = False
        self.metadata = {}
        self.counts = {}
        self._agent_directories = {}
        self._camera_directories = {}
        if self.manifest.get("storageLayout") != "per_agent.v3":
            raise CaptureFormatError("v4 capture requires per_agent.v3")
        if self.manifest.get("recordEncoding") != ENCODING:
            raise CaptureFormatError("Unknown v4 recordEncoding")
        if not isinstance(self.run_id, str) or not self.run_id:
            raise CaptureFormatError("v4 capture has no runId")

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        if self._db is not None:
            self._db.close()
            self._db = None
        self._closed = True

    def __del__(self):
        db = getattr(self, "_db", None)
        if db is not None:
            try:
                db.close()
            except sqlite3.Error:
                pass

    def path(self, relative):
        if isinstance(relative, Path):
            if relative.is_absolute():
                try:
                    relative = relative.relative_to(self.root).as_posix()
                except ValueError as error:
                    raise CaptureFormatError("Path is outside capture") from error
            else:
                relative = relative.as_posix()
        if not isinstance(relative, str) or not relative or "\\" in relative or ":" in relative or "\x00" in relative:
            raise CaptureFormatError("Unsafe capture relative path: " + repr(relative))
        parts = relative.split("/")
        if any(part in ("", ".", "..") for part in parts) or PurePosixPath(relative).is_absolute():
            raise CaptureFormatError("Unsafe capture relative path: " + relative)
        path = self.root
        for part in parts:
            path = path / part
            if _alias(path):
                raise CaptureFormatError("Aliased capture path: " + relative)
        if not path.resolve().is_relative_to(self.root):
            raise CaptureFormatError("Path escapes capture: " + relative)
        return path

    def _json(self, relative, strict=True):
        path = self.path(relative)
        try:
            return _loads(path.read_text(encoding="utf-8-sig"), strict)
        except OSError as error:
            raise CaptureFormatError("Cannot read " + str(relative) + ": " + str(error)) from error

    def iter_raw_records(self, relative):
        if str(relative).endswith("/skeleton-poses.bin"):
            self._prepare()
            yield from self._skeleton_records(str(relative), expanded=False)
            return
        path = self.path(relative)
        try:
            with path.open(encoding="utf-8-sig") as stream:
                for number, line in enumerate(stream, 1):
                    if not line.strip():
                        continue
                    try:
                        value = _loads(line, True)
                        if not isinstance(value, dict):
                            raise CaptureFormatError("Record must be an object")
                        yield value
                    except CaptureFormatError as error:
                        raise CaptureFormatError(str(relative) + ":" + str(number) + ": " + str(error)) from error
        except OSError as error:
            raise CaptureFormatError("Cannot read " + str(relative) + ": " + str(error)) from error

    def _put(self, namespace, key, value):
        try:
            self._db.execute("INSERT INTO refs VALUES(?,?,?)", (namespace, str(key), _compact(value)))
        except sqlite3.IntegrityError as error:
            raise CaptureFormatError("Duplicate " + namespace + " reference: " + str(key)) from error
        self.counts[namespace] = self.counts.get(namespace, 0) + 1

    def _get(self, namespace, key):
        row = self._db.execute("SELECT payload FROM refs WHERE namespace=? AND id=?", (namespace, str(key))).fetchone()
        if row is None:
            raise CaptureFormatError("Missing " + namespace + " reference: " + str(key))
        return _loads(row[0], True)

    def _clock(self, sync_id):
        _integer(sync_id, "syncSampleId")
        return self._get("clock", sync_id)

    def _prepare(self):
        if self._closed:
            raise CaptureFormatError("CaptureReader is closed")
        if self._prepared:
            return
        self._db = sqlite3.connect("")
        self._db.execute("PRAGMA cache_size=-2048")
        self._db.execute("CREATE TABLE refs(namespace TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(namespace,id)) WITHOUT ROWID")
        try:
            self._prepare_normalized()
            self._db.commit()
            self._prepared = True
        except Exception:
            self.close()
            raise

    def _prepare_normalized(self):
        self.metadata = self._json("normalization.json")
        expected = {"schema": "stationflow.normalization.v1", "clockFile": "sync.jsonl",
                    "definitionsFile": "record-definitions.jsonl", "rostersFile": "rosters.jsonl",
                    "checkpointIndexFile": "state-checkpoints.jsonl", "checkpointInterval": 256,
                    "captureSchema": V4, "storageLayout": "per_agent.v3", "recordEncoding": ENCODING, "runId": self.run_id}
        for key, value in expected.items():
            if self.metadata.get(key) != value:
                raise CaptureFormatError("Unknown normalization metadata " + key)
        if self.manifest.get("normalizationFile", "normalization.json") != "normalization.json":
            raise CaptureFormatError("Unexpected normalizationFile")
        aliases = self.metadata.get("profileJourneyAliases")
        if not isinstance(aliases, dict) or any(not isinstance(k, str) or not isinstance(v, str) or not k or not v for k, v in aliases.items()):
            raise CaptureFormatError("profileJourneyAliases must be a string mapping")
        for row in self.iter_raw_records("record-definitions.jsonl"):
            if "policyId" in row:
                key = _integer(row["policyId"], "policyId")
                if set(row) != {"policyId", "policyParameters"} or not isinstance(row["policyParameters"], dict):
                    raise CaptureFormatError("Invalid policy definition")
                self._put("policy", key, row["policyParameters"])
            else:
                key = _integer(row.get("definitionId"), "definitionId")
                if not isinstance(row.get("type"), str) or not row["type"] or row.get("stampField") not in ("stamp", "captureStamp", "") or not isinstance(row.get("defaults"), dict):
                    raise CaptureFormatError("Invalid record definition")
                expected_stamp = "captureStamp" if "captureStamp" in row["defaults"] else "stamp" if "stamp" in row["defaults"] else ""
                if row["stampField"] != expected_stamp:
                    raise CaptureFormatError("Definition stampField disagrees with its defaults")
                self._put("definition", key, row)
        previous_id, previous_real, previous_frame = 0, -1.0, -1
        for row in self.iter_raw_records("sync.jsonl"):
            if row.get("kind") != "clock":
                continue
            stamp = row.get("stamp")
            if not isinstance(stamp, dict) or stamp.get("runId") != self.run_id:
                raise CaptureFormatError("Clock stamp is missing or belongs to a foreign run")
            key = _integer(stamp.get("syncSampleId"), "clock syncSampleId")
            real = _finite(stamp.get("realElapsedSeconds"), "clock realElapsedSeconds")
            frame = _integer(stamp.get("unityFrame"), "clock unityFrame", 0)
            for field in ("simulationSeconds", "stationClockSeconds", "timeScale"):
                _finite(stamp.get(field), "clock " + field)
            if not isinstance(stamp.get("paused"), bool) or not stamp.get("utc") or not stamp.get("simulationDate"):
                raise CaptureFormatError("Clock state/UTC/date is incomplete")
            if key <= previous_id or real < 0 or real < previous_real or frame < previous_frame:
                raise CaptureFormatError("Clock IDs/time/frames are not monotonic")
            previous_id, previous_real, previous_frame = key, real, frame
            self._put("clock", key, stamp)
        if not self.counts.get("clock"):
            raise CaptureFormatError("Capture contains no canonical clocks")
        if self.manifest.get("status") == "complete" and self.counts["clock"] != self.manifest.get("syncSamples"):
            raise CaptureFormatError("Canonical clock count differs from manifest")
        options = self.manifest.get("captureOptions") or {}
        avatar_enabled = any(options.get(k) for k in ("profiles", "trajectories", "animations", "decisions", "events", "skeletons"))
        if avatar_enabled:
            if self.manifest.get("agentsDirectory") != "agents" or self.manifest.get("agentIndexFile") != "agents/index.json":
                raise CaptureFormatError("Invalid agent directory/index declarations")
            index = self._json("agents/index.json")
            if index.get("runId") != self.run_id or index.get("schema") != "stationflow.agents_index.v1" or not isinstance(index.get("agents"), list):
                raise CaptureFormatError("Invalid or foreign agent index")
            ids, folded = set(), set()
            for entry in index["agents"]:
                agent = entry.get("agentId")
                relative = entry.get("directory")
                if not isinstance(agent, str) or not agent or agent in ids:
                    raise CaptureFormatError("Invalid or duplicate agent ID")
                if not isinstance(relative, str) or relative.casefold() in folded:
                    raise CaptureFormatError("Duplicate/colliding agent directory")
                match = re.fullmatch(r"agents/agent-([a-z0-9](?:[a-z0-9-]{0,54}[a-z0-9])?)-([0-9a-f]{16})", relative)
                if not match or match.group(2) != hashlib.sha256(agent.encode("utf-8")).hexdigest()[:16]:
                    raise CaptureFormatError("Unsafe or identity-mismatched agent directory")
                self.path(relative)
                ids.add(agent); folded.add(relative.casefold())
                self._agent_directories[relative] = agent
        else:
            ids = set()
            if self.manifest.get("agentsDirectory") or self.manifest.get("agentIndexFile") or (self.root / "agents").exists():
                raise CaptureFormatError("Agent files exist while avatar streams are disabled")
        for row in self.iter_raw_records("rosters.jsonl"):
            key = _integer(row.get("rosterId"), "rosterId")
            agents = row.get("agentIds")
            if set(row) != {"rosterId", "agentIds"} or not isinstance(agents, list) or any(not isinstance(a, str) for a in agents) or len(agents) != len(set(agents)) or not set(agents).issubset(ids):
                raise CaptureFormatError("Invalid, duplicate or foreign roster membership")
            self._put("roster", key, agents)
        self._index_skeleton_definitions(options)
        for relative, agent in self._agent_directories.items():
            self._index_agent_states(relative, agent)
            self._index_skeleton_bindings(relative, options)
        for camera in self.manifest.get("cameraResults") or []:
            relative = camera.get("directory")
            camera_id = camera.get("cameraId")
            if not isinstance(relative, str) or not isinstance(camera_id, str) or relative.casefold() in {r.casefold() for r in self._camera_directories} or camera_id in self._camera_directories.values():
                raise CaptureFormatError("Invalid or duplicate camera directory/identity")
            self.path(relative)
            if camera.get("cameraStateFile") != "camera-states.jsonl":
                raise CaptureFormatError("Camera state file declaration is missing")
            self._camera_directories[relative] = camera_id
            previous_id, previous_sync = 0, 0
            for row in self.iter_raw_records(relative + "/camera-states.jsonl"):
                state_id = _integer(row.get("cameraStateId"), "cameraStateId")
                sync_id = _integer(row.get("effectiveFromSyncSampleId"), "effectiveFromSyncSampleId")
                self._clock(sync_id)
                if state_id != previous_id + 1 or sync_id < previous_sync or not isinstance(row.get("camera"), dict):
                    raise CaptureFormatError("Camera state revisions are invalid or nonmonotonic")
                if set(row) != {"cameraStateId", "effectiveFromSyncSampleId", "camera"}:
                    raise CaptureFormatError("Unknown camera state record fields")
                self._put("camera:" + relative, state_id, row)
                previous_id, previous_sync = state_id, sync_id
            if camera.get("cameraStatesWritten") != previous_id:
                raise CaptureFormatError("Camera state count differs from manifest")
        self._validate_checkpoint_index()

    def _index_skeleton_definitions(self, options):
        if not isinstance(options.get("skeletons"), bool):
            raise CaptureFormatError("captureOptions.skeletons must be an explicit boolean")
        enabled = options["skeletons"]
        declarations = {"skeletonDefinitionsFile": "skeleton-definitions.jsonl", "skeletonPoseFileName": "skeleton-poses.bin",
                        "skeletonBindingsFileName": "skeleton-bindings.jsonl", "skeletonIndexFileName": "skeleton-index.jsonl",
                        "skeletonPoseFormat": "stationflow.skeleton_pose_binary.v1"}
        for key, expected in declarations.items():
            if key not in self.manifest or self.manifest[key] != (expected if enabled else ""):
                raise CaptureFormatError("Skeleton manifest declaration disagrees with toggle: " + key)
        path = self.path("skeleton-definitions.jsonl")
        if path.exists() != enabled:
            raise CaptureFormatError("Skeleton definitions file disagrees with skeletons toggle")
        if not enabled:
            return
        for row in self.iter_raw_records("skeleton-definitions.jsonl"):
            required = {"schema", "rigId", "jointNames", "parentIndices", "transformPaths", "coordinateSystem", "units", "positionEncoding"}
            if set(row) != required or row.get("schema") != SKELETON_SCHEMA or row.get("coordinateSystem") != "UnityWorld" or row.get("units") != "metres" or row.get("positionEncoding") != "float32":
                raise CaptureFormatError("Invalid skeleton definition contract")
            names, parents, paths = row.get("jointNames"), row.get("parentIndices"), row.get("transformPaths")
            if names != list(SKELETON_JOINTS) or not isinstance(parents, list) or len(parents) != len(names) or not isinstance(paths, list) or len(paths) != len(names):
                raise CaptureFormatError("Skeleton definition joint order/count differs from HumanBodyBones")
            seen_paths = set()
            payload = bytearray((SKELETON_SCHEMA + "\n").encode("utf-8"))
            payload += struct.pack("<i", len(names))
            for i, (name, parent, path) in enumerate(zip(names, parents, paths)):
                if not isinstance(parent, int) or isinstance(parent, bool) or not -1 <= parent < len(names) or parent == i or not isinstance(path, str) or "\x00" in path:
                    raise CaptureFormatError("Invalid skeleton parent or Transform path")
                if path:
                    if path in seen_paths:
                        raise CaptureFormatError("Duplicate skeleton Transform path")
                    seen_paths.add(path)
                elif parent != -1:
                    raise CaptureFormatError("Missing skeleton joint has a parent")
                if parent >= 0 and not paths[parent]:
                    raise CaptureFormatError("Skeleton parent is missing")
                chain, current = set(), i
                while current >= 0:
                    if current in chain:
                        raise CaptureFormatError("Skeleton hierarchy contains a cycle")
                    chain.add(current)
                    value = parents[current]
                    if not isinstance(value, int) or isinstance(value, bool) or not -1 <= value < len(names):
                        raise CaptureFormatError("Invalid skeleton parent index")
                    current = value
                for text, middle in ((name, parent), (path, None)):
                    encoded = text.encode("utf-8")
                    payload += struct.pack("<i", len(encoded)) + encoded
                    if middle is not None:
                        payload += struct.pack("<i", middle)
            expected_id = "rig-" + hashlib.sha256(payload).hexdigest()
            if row.get("rigId") != expected_id:
                raise CaptureFormatError("Skeleton rig ID differs from canonical definition SHA256")
            self._put("skeleton-rig", expected_id, row)

    def _index_skeleton_bindings(self, directory, options):
        names = ("skeleton-bindings.jsonl", "skeleton-poses.bin", "skeleton-index.jsonl")
        present = [self.path(directory + "/" + name).is_file() for name in names]
        if any(present) and (not options.get("skeletons") or not all(present)):
            raise CaptureFormatError("Disabled or incomplete skeleton stream: " + directory)
        if not any(present):
            return
        previous, previous_sync = 0, 0
        for row in self.iter_raw_records(directory + "/skeleton-bindings.jsonl"):
            if set(row) != {"bindingId", "rigId", "actorSlotId", "avatarName", "effectiveFromSyncSampleId"}:
                raise CaptureFormatError("Invalid skeleton binding fields")
            binding = _integer(row.get("bindingId"), "bindingId")
            sync_id = _integer(row.get("effectiveFromSyncSampleId"), "effectiveFromSyncSampleId")
            self._clock(sync_id)
            self._get("skeleton-rig", row.get("rigId"))
            if binding != previous + 1 or sync_id <= previous_sync or not isinstance(row.get("actorSlotId"), str) or not row["actorSlotId"] or not isinstance(row.get("avatarName"), str):
                raise CaptureFormatError("Skeleton binding sequence/identity is invalid")
            self._put("skeleton-binding:" + directory, binding, row)
            previous, previous_sync = binding, sync_id
        if not previous:
            raise CaptureFormatError("Materialized skeleton stream has no binding")

    def _skeleton_records(self, relative, expanded):
        directory = relative.rsplit("/", 1)[0]
        if directory not in self._agent_directories or not self.manifest.get("captureOptions", {}).get("skeletons"):
            raise CaptureFormatError("Unindexed or disabled skeleton stream")
        namespace = "skeleton-binding:" + directory
        checkpoints = iter(self.iter_raw_records(directory + "/skeleton-index.jsonl"))
        observation, last_sync, last_sample, last_time, active_binding = 0, 0, 0, -1.0, 0
        with self.path(relative).open("rb") as stream:
            if stream.read(8) != SKELETON_MAGIC or stream.read(8) != struct.pack("<II", 1, 16):
                raise CaptureFormatError("Invalid skeleton binary header")
            while True:
                offset = stream.tell()
                length_bytes = stream.read(4)
                if not length_bytes:
                    break
                if len(length_bytes) != 4:
                    raise CaptureFormatError("Truncated skeleton record length")
                length = struct.unpack("<I", length_bytes)[0]
                joints, mask_bytes = len(SKELETON_JOINTS), (len(SKELETON_JOINTS) + 7) // 8
                if length != 32 + mask_bytes + 12 * joints:
                    raise CaptureFormatError("Skeleton record byte length differs from schema")
                body = stream.read(length - 4)
                if len(body) != length - 4:
                    raise CaptureFormatError("Truncated skeleton pose record")
                sync_id, sample, binding, count, masks, time = struct.unpack_from("<qiiiif", body, 0)
                if count != joints or masks != mask_bytes or sync_id <= last_sync or sample <= last_sample or not math.isfinite(time) or time < last_time:
                    raise CaptureFormatError("Invalid skeleton count, clock, sample or time order")
                stamp = self._clock(sync_id)
                if struct.unpack("<f", struct.pack("<f", stamp["simulationSeconds"]))[0] != time:
                    raise CaptureFormatError("Skeleton time differs from float32 simulation clock")
                bind = self._get(namespace, _integer(binding, "bindingId"))
                rig = self._get("skeleton-rig", bind["rigId"])
                if binding != active_binding:
                    if binding != active_binding + 1 or bind["effectiveFromSyncSampleId"] != sync_id:
                        raise CaptureFormatError("Skeleton binding activation is not its first pose")
                    active_binding = binding
                if bind["effectiveFromSyncSampleId"] > sync_id:
                    raise CaptureFormatError("Skeleton pose references a future binding")
                next_binding = self._db.execute("SELECT payload FROM refs WHERE namespace=? AND id=?", (namespace, str(binding + 1))).fetchone()
                if next_binding and _loads(next_binding[0], True)["effectiveFromSyncSampleId"] <= sync_id:
                    raise CaptureFormatError("Skeleton pose references a stale binding")
                mask = body[28:28 + mask_bytes]
                if joints % 8 and mask[-1] & ~((1 << (joints % 8)) - 1):
                    raise CaptureFormatError("Skeleton validity mask has nonzero unused bits")
                flat = struct.unpack_from("<" + "f" * (joints * 3), body, 28 + mask_bytes)
                valid = [bool(mask[i // 8] & (1 << (i % 8))) for i in range(joints)]
                positions = [list(flat[i * 3:i * 3 + 3]) for i in range(joints)]
                for i, point in enumerate(positions):
                    if any(not math.isfinite(v) for v in point) or (valid[i] and not rig["transformPaths"][i]) or (not valid[i] and any(v != 0 for v in point)):
                        raise CaptureFormatError("Invalid skeleton joint position or validity mask")
                observation += 1
                if (observation - 1) % 256 == 0:
                    expected = {"observationIndex": observation, "syncSampleId": sync_id, "byteOffset": offset, "bindingId": binding}
                    if next(checkpoints, None) != expected:
                        raise CaptureFormatError("Skeleton checkpoint index does not address its exact pose")
                row = {"sampleId": sample, "time": time, "bindingId": binding, "rigId": bind["rigId"], "jointPositionsWorld": positions, "valid": valid}
                if expanded:
                    row.update(agentId=self._agent_directories[directory], captureStamp=stamp)
                else:
                    row.update(syncSampleId=sync_id)
                yield row
                last_sync, last_sample, last_time = sync_id, sample, time
        if next(checkpoints, None) is not None:
            raise CaptureFormatError("Skeleton checkpoint index contains unmatched entries")
        if not observation or active_binding != self.counts.get(namespace, 0):
            raise CaptureFormatError("Skeleton binding is unreferenced by a pose")

    def _raw_offsets(self, relative):
        with self.path(relative).open("rb") as stream:
            while True:
                offset = stream.tell()
                line = stream.readline()
                if not line:
                    return
                if line.strip():
                    value = _loads(line.decode("utf-8-sig"), True)
                    if not isinstance(value, dict):
                        raise CaptureFormatError("Indexed record must be an object")
                    yield offset, value

    def _index_agent_states(self, relative, agent):
        state_path = relative + "/state-changes.jsonl"
        if not self.path(state_path).exists():
            return
        current, prior = {}, {}
        forbidden = {"captureStamp", "stamp", "syncSampleId", "agentId", "stateId"}
        dense = {"trajectories": {"time", "sampleId", "position", "rotation", "velocity", "budget", "seatedSecondsRemaining"},
                 "animations": {"time", "sampleId", "normalizedTime", "nextNormalizedTime", "transitionProgress", "nativePlanarSpeed", "locomotionStrideWeight"}}
        for offset, row in self._raw_offsets(state_path):
            stream = row.get("stream")
            if stream not in dense or not self.manifest["captureOptions"].get(stream):
                raise CaptureFormatError("Unknown or disabled agent state stream")
            state_id = _integer(row.get("stateId"), "stateId")
            sync_id = _integer(row.get("fromSyncSampleId"), "fromSyncSampleId")
            observation = _integer(row.get("observationIndex"), "observationIndex")
            self._clock(sync_id)
            old = prior.get(stream)
            if state_id != (old[0] + 1 if old else 1) or old and (observation <= old[1] or sync_id < old[2]):
                raise CaptureFormatError("Agent state revisions are invalid or nonmonotonic")
            checkpoint = row.get("checkpoint")
            if not isinstance(checkpoint, bool) or checkpoint != ((observation - 1) % 256 == 0):
                raise CaptureFormatError("Invalid state checkpoint interval")
            payload = row.get("values" if checkpoint else "changes")
            expected = {"stream", "stateId", "fromSyncSampleId", "observationIndex", "checkpoint", "values" if checkpoint else "changes"}
            if set(row) != expected or not isinstance(payload, dict) or (not checkpoint and not payload) or forbidden.intersection(payload) or dense[stream].intersection(payload):
                raise CaptureFormatError("Invalid agent state payload")
            if not old and not checkpoint:
                raise CaptureFormatError("Initial agent state is not a checkpoint")
            if checkpoint:
                if old and set(payload) != set(current[stream]):
                    raise CaptureFormatError("State field set changed without a schema change")
                current[stream] = copy.deepcopy(payload)
            else:
                if not set(payload).issubset(current[stream]):
                    raise CaptureFormatError("State delta adds undeclared fields")
                current[stream].update(payload)
            stored = {"values": current[stream], "fromSyncSampleId": sync_id, "observationIndex": observation, "checkpoint": checkpoint, "byteOffset": offset}
            self._put("state:" + relative + ":" + stream, state_id, stored)
            if checkpoint:
                self._put("checkpoint", agent + ":" + stream + ":" + str(state_id), {"agentId": agent, "stream": stream, "stateId": state_id,
                          "fromSyncSampleId": sync_id, "observationIndex": observation, "path": state_path, "byteOffset": offset,
                          "densePath": relative + "/" + stream + ".jsonl"})
            prior[stream] = (state_id, observation, sync_id)

    def _at_offset(self, relative, offset):
        _integer(offset, "byteOffset", 0)
        with self.path(relative).open("rb") as stream:
            if offset:
                stream.seek(offset - 1)
                if stream.read(1) != b"\n":
                    raise CaptureFormatError("Index offset does not start a physical line")
            stream.seek(offset)
            line = stream.readline()
        if not line.strip():
            raise CaptureFormatError("Index references missing record")
        return _loads(line.decode("utf-8-sig"), True)

    def _validate_checkpoint_index(self):
        seen = set()
        for row in self.iter_raw_records("state-checkpoints.jsonl"):
            required = {"agentId", "stream", "stateId", "fromSyncSampleId", "observationIndex", "path", "byteOffset", "densePath", "denseByteOffset"}
            if set(row) != required:
                raise CaptureFormatError("Invalid state checkpoint index fields")
            key = str(row["agentId"]) + ":" + str(row["stream"]) + ":" + str(row["stateId"])
            if key in seen:
                raise CaptureFormatError("Duplicate state checkpoint index")
            seen.add(key)
            expected = self._get("checkpoint", key)
            if any(row[k] != value for k, value in expected.items()):
                raise CaptureFormatError("State checkpoint index differs from physical state record")
            state = self._at_offset(row["path"], row["byteOffset"])
            observed = self._at_offset(row["densePath"], row["denseByteOffset"])
            if not state.get("checkpoint") or observed.get("stateId") != row["stateId"] or observed.get("syncSampleId") != row["fromSyncSampleId"]:
                raise CaptureFormatError("Checkpoint dense/state offsets do not join")
            self._put("checkpoint-index", key, row)
        if len(seen) != self.counts.get("checkpoint", 0):
            raise CaptureFormatError("Missing state checkpoint index entries")

    def read_json(self, relative):
        self._prepare()
        value = self._json(relative)
        return self._expand_json(str(relative).replace("\\", "/"), value)

    def _profile(self, profile):
        if not isinstance(profile, dict):
            raise CaptureFormatError("Profile must be an object")
        profile = copy.deepcopy(profile)
        if "policyId" in profile:
            if "policyParameters" in profile:
                raise CaptureFormatError("Profile contains policy reference and repeated payload")
            profile["policyParameters"] = self._get("policy", _integer(profile.pop("policyId"), "policyId"))
        return profile

    def _typed(self, relative, row):
        definition = self._get("definition", _integer(row.get("definitionId"), "definitionId"))
        value = copy.deepcopy(definition["defaults"])
        value.update({key: item for key, item in row.items() if key not in ("definitionId", "syncSampleId", "rosterId")})
        stamp_field = definition["stampField"]
        if row.get("kind") == "clock":
            stamp = row.get("stamp")
            if stamp_field != "stamp" or stamp != self._clock(stamp.get("syncSampleId") if isinstance(stamp, dict) else None) or "syncSampleId" in row:
                raise CaptureFormatError("Clock definition or canonical stamp mismatch")
        elif stamp_field:
            if stamp_field in row or "stamp" in row or "captureStamp" in row:
                raise CaptureFormatError("Repeated full stamp in referenced record")
            value[stamp_field] = self._clock(row.get("syncSampleId"))
        elif "syncSampleId" in row:
            raise CaptureFormatError("Unstamped definition has a sync reference")
        if "rosterId" in row:
            if "activeJourneyIds" in row:
                raise CaptureFormatError("Roster payload is repeated beside reference")
            value["activeJourneyIds"] = self._get("roster", _integer(row["rosterId"], "rosterId"))
        directory = relative.rsplit("/", 1)[0] if "/" in relative else ""
        if directory in self._agent_directories:
            agent = self._agent_directories[directory]
            if "agentId" in row:
                raise CaptureFormatError("Per-agent row repeats or overrides contextual agent ID")
            if "agentId" in value:
                value["agentId"] = agent
        if "profileRef" in row:
            if directory not in self._agent_directories or row["profileRef"] != "profile.json#profile" or "profile" in row:
                raise CaptureFormatError("Invalid or conflicting contextual profile reference")
            value.pop("profileRef", None)
            value["profile"] = self.read_json(directory + "/profile.json")["profile"]
        elif "profile" in row:
            value["profile"] = self._profile(value["profile"])
        return value

    def _expand_json(self, relative, value):
        if relative in ("manifest.json", "normalization.json", "agents/index.json") or relative.endswith("/calibration.json"):
            return value
        if relative.endswith("/profile.json") and "profileRef" in value:
            raise CaptureFormatError("Recursive profile reference is invalid")
        if relative == "scenario.json" and "servicesFile" in value and "services" in value:
            raise CaptureFormatError("Timetable payload repeated beside its reference")
        if relative in ("scenario.json", "timetable.json") or relative.endswith("/profile.json"):
            if "definitionId" not in value:
                raise CaptureFormatError("Typed JSON record has no definitionId")
        if "definitionId" in value:
            value = self._typed(relative, value)
        if relative == "scenario.json" and "servicesFile" in value:
            if value["servicesFile"] != "timetable.json":
                raise CaptureFormatError("Invalid or duplicated timetable reference")
            value.pop("servicesFile")
            value["services"] = self.read_json("timetable.json")["services"]
        if relative.endswith("/profile.json"):
            directory = relative.rsplit("/", 1)[0]
            if directory not in self._agent_directories:
                raise CaptureFormatError("Profile directory is unregistered")
            profile = self._profile(value.get("profile"))
            journey = copy.deepcopy(value.get("journey"))
            if not isinstance(journey, dict):
                raise CaptureFormatError("Journey must be an object")
            for key, profile_key in self.metadata["profileJourneyAliases"].items():
                if key not in journey and profile_key in profile:
                    journey[key] = copy.deepcopy(profile[profile_key])
            agent = self._agent_directories[directory]
            if profile.get("agentId") != agent or journey.get("journeyId") != agent:
                raise CaptureFormatError("Profile/journey identity differs from agent directory")
            value.update(profile=profile, journey=journey)
        return value

    def iter_records(self, relative):
        self._prepare()
        relative = str(relative).replace("\\", "/")
        directory, _, name = relative.rpartition("/")
        if name == "skeleton-poses.bin":
            yield from self._skeleton_records(relative, expanded=True)
            return
        dense = directory in self._agent_directories and name in ("trajectories.jsonl", "animations.jsonl")
        raw = self._raw_offsets(relative) if dense else ((None, row) for row in self.iter_raw_records(relative))
        last_state_id, previous_sync = 0, 0
        for observation, (offset, row) in enumerate(raw, 1):
            if dense or directory in self._camera_directories and name == "frames.jsonl":
                sync_id = _integer(row.get("syncSampleId"), "syncSampleId")
                if sync_id <= previous_sync:
                    raise CaptureFormatError("Duplicate or backwards observation syncSampleId")
                previous_sync = sync_id
            if dense:
                state_id = _integer(row.get("stateId"), "stateId")
                last_state_id = state_id
                stream = name[:-6]
                state = self._get("state:" + directory + ":" + stream, state_id)
                if state["fromSyncSampleId"] > row.get("syncSampleId", 0) or state["observationIndex"] > observation:
                    raise CaptureFormatError("Dense row references future agent state")
                next_state = self._db.execute("SELECT payload FROM refs WHERE namespace=? AND id=?", ("state:" + directory + ":" + stream, str(state_id + 1))).fetchone()
                if next_state and _loads(next_state[0], True)["observationIndex"] <= observation:
                    raise CaptureFormatError("Dense row references stale agent state")
                if (observation - 1) % 256 == 0 and (not state["checkpoint"] or state["observationIndex"] != observation):
                    raise CaptureFormatError("Dense stream is missing a periodic full checkpoint")
                if (observation - 1) % 256 == 0:
                    checkpoint_key = self._agent_directories[directory] + ":" + stream + ":" + str(state_id)
                    if self._get("checkpoint-index", checkpoint_key)["denseByteOffset"] != offset:
                        raise CaptureFormatError("Checkpoint denseByteOffset points to the wrong observation index")
                if observation == state["observationIndex"] and row["syncSampleId"] != state["fromSyncSampleId"]:
                    raise CaptureFormatError("Dense row state activation sync differs")
                if any(key in row for key in ("captureStamp", "stamp", "agentId", "definitionId")) or set(state["values"]).intersection(row):
                    raise CaptureFormatError("Dense row repeats state/context fields")
                expected_dense = {"syncSampleId", "stateId", "time", "sampleId"} | ({"position", "rotation", "velocity", "budget", "seatedSecondsRemaining"} if stream == "trajectories" else {"normalizedTime", "nextNormalizedTime", "transitionProgress", "nativePlanarSpeed", "locomotionStrideWeight"})
                if set(row) != expected_dense:
                    raise CaptureFormatError("Dense observation fields differ from the declared schema")
                value = copy.deepcopy(state["values"])
                value.update({k: v for k, v in row.items() if k not in ("syncSampleId", "stateId")})
                value.update(captureStamp=self._clock(row.get("syncSampleId")), agentId=self._agent_directories[directory])
                yield value
            else:
                yield self._expand_record(relative, row)
        if dense:
            namespace = "state:" + directory + ":" + name[:-6]
            count = self.counts.get(namespace, 0)
            if count != last_state_id:
                raise CaptureFormatError("Agent state revision is unreferenced by its dense stream")

    def _expand_record(self, relative, row):
        directory, _, name = relative.rpartition("/")
        if directory in self._camera_directories and name == "frames.jsonl":
            if any(key in row for key in ("stamp", "camera", "definitionId")):
                raise CaptureFormatError("Camera frame repeats referenced metadata")
            state_id = _integer(row.get("cameraStateId"), "cameraStateId")
            state = self._get("camera:" + directory, state_id)
            sync_id = row.get("syncSampleId")
            stamp = self._clock(sync_id)
            if state["effectiveFromSyncSampleId"] > sync_id:
                raise CaptureFormatError("Camera frame references future state")
            next_state = self._db.execute("SELECT payload FROM refs WHERE namespace=? AND id=?", ("camera:" + directory, str(state_id + 1))).fetchone()
            if next_state and _loads(next_state[0], True)["effectiveFromSyncSampleId"] <= sync_id:
                raise CaptureFormatError("Camera frame references stale state")
            if self.manifest.get("captureCadence") == "each_unity_frame" and not {"captureIntervalRealSeconds", "captureFpsInstantaneous", "captureFpsRolling1s"}.issubset(row):
                raise CaptureFormatError("Camera frame lost measured FPS fields")
            value = {k: v for k, v in row.items() if k not in ("syncSampleId", "cameraStateId")}
            value.update(stamp=stamp, camera=state["camera"])
            return value
        if relative == "skeleton-definitions.jsonl" or name in ("skeleton-bindings.jsonl", "skeleton-index.jsonl"):
            return copy.deepcopy(row)
        return self._typed(relative, row)

    def validate_references(self):
        self._prepare()
        for relative in ("scenario.json", "timetable.json"):
            if self.path(relative).is_file():
                self.read_json(relative)
        for directory in self._agent_directories:
            if self.path(directory + "/profile.json").is_file():
                self.read_json(directory + "/profile.json")
        stream_counts = {}
        for relative in self.record_paths():
            stream_counts[relative] = sum(1 for _ in self.iter_records(relative))
        cameras = self.manifest.get("cameraResults") or []
        selected = self.manifest.get("selectedCameraIds")
        if not isinstance(selected, list) or len(selected) != len(set(selected)) or set(selected) != set(self._camera_directories.values()):
            raise CaptureFormatError("Selected and finalized camera identities differ")
        options = self.manifest.get("captureOptions") or {}
        if bool(cameras) and not options.get("video"):
            raise CaptureFormatError("Video references exist while video is disabled")
        for camera in cameras:
            relative = camera["directory"]
            calibration = self._json(relative + "/calibration.json")
            if calibration.get("cameraId") != camera["cameraId"] or calibration.get("width") != options.get("videoWidth") or calibration.get("height") != options.get("videoHeight"):
                raise CaptureFormatError("Camera calibration identity/dimensions differ")
            if stream_counts.get(relative + "/frames.jsonl", 0) != camera.get("framesWritten"):
                raise CaptureFormatError("Finalized camera frame count differs from sidecar")
        return {"normalized": True, "schema": V4, **self.counts, "streamRows": stream_counts}

    def record_paths(self):
        """Declared observation streams, excluding metadata/index JSONL."""
        candidates = ["sync.jsonl", "events.jsonl", "train-states.jsonl"]
        for relative in self._agent_directories:
            candidates.extend(relative + "/" + name for name in AVATAR_STREAMS)
        candidates.extend(relative + "/frames.jsonl" for relative in self._camera_directories)
        for relative in candidates:
            if self.path(relative).is_file():
                yield relative


def _write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def export_expanded(reader, destination, agent_id=None):
    """Create an explicit analysis view, never a renamed or downgraded capture."""
    destination = Path(destination).resolve()
    if destination.exists() or destination.is_relative_to(reader.root) or reader.root.is_relative_to(destination):
        raise CaptureFormatError("Export destination must be a new directory outside the capture")
    reader.validate_references()
    if agent_id is not None:
        entries = {entry["agentId"]: entry["directory"] for entry in reader.read_json("agents/index.json")["agents"]}
        if agent_id not in entries:
            raise CaptureFormatError("Agent is not registered: " + agent_id)
        directory = entries[agent_id]
        selected = [directory + "/" + name for name in ("profile.json", *AVATAR_STREAMS) if reader.path(directory + "/" + name).is_file()]
        selected.extend(name for name in ("scenario.json", "timetable.json", "skeleton-definitions.jsonl") if reader.path(name).is_file())
        if reader.path(directory + "/skeleton-bindings.jsonl").is_file():
            selected.append(directory + "/skeleton-bindings.jsonl")
        schema = "stationflow.agent_analysis_view.v1"
    else:
        selected = list(reader.record_paths())
        selected.extend(name for name in ("scenario.json", "timetable.json", "agents/index.json") if reader.path(name).is_file())
        if reader.path("skeleton-definitions.jsonl").is_file():
            selected.append("skeleton-definitions.jsonl")
        for directory in reader._agent_directories:
            if reader.path(directory + "/skeleton-bindings.jsonl").is_file():
                selected.append(directory + "/skeleton-bindings.jsonl")
            if reader.path(directory + "/profile.json").is_file():
                selected.append(directory + "/profile.json")
        for directory in reader._camera_directories:
            if reader.path(directory + "/calibration.json").is_file():
                selected.append(directory + "/calibration.json")
        schema = "stationflow.expanded_analysis_view.v1"
    destination.mkdir(parents=True)
    marker = destination / "EXPORT_INCOMPLETE.txt"
    marker.write_text("Do not consume until this marker has been removed.\n", encoding="utf-8")
    referenced_clocks = set()
    for relative in selected:
        name = PurePosixPath(relative).name
        target = relative.split("/", 2)[-1] if agent_id is not None and relative.startswith(directory + "/") else relative
        if name == "skeleton-poses.bin":
            target = target[:-4] + ".jsonl"
        output = destination / target
        output.parent.mkdir(parents=True, exist_ok=True)
        if name.endswith(".jsonl") or name == "skeleton-poses.bin":
            with output.open("w", encoding="utf-8", newline="\n") as stream:
                for row in reader.iter_records(relative):
                    stamp = row.get("captureStamp", row.get("stamp"))
                    if isinstance(stamp, dict):
                        referenced_clocks.add(stamp["syncSampleId"])
                    stream.write(_compact(row) + "\n")
        else:
            value = reader.read_json(relative)
            stamp = value.get("captureStamp") if isinstance(value, dict) else None
            if isinstance(stamp, dict):
                referenced_clocks.add(stamp["syncSampleId"])
            _write_json(output, value)
    if agent_id is not None:
        with (destination / "clocks.jsonl").open("w", encoding="utf-8", newline="\n") as stream:
            for sync_id in sorted(referenced_clocks):
                stream.write(_compact(reader._clock(sync_id)) + "\n")
    _write_json(destination / "analysis-view.json", {"schema": schema, "sourceRunId": reader.run_id,
                "sourceManifest": reader.manifest, "agentId": agent_id, "recordLayout": "expanded-logical-records",
                "mediaIncluded": False, "note": "Analysis view only; source capture remains authoritative. No normalized references remain in exported observation records."})
    marker.unlink()
    return {"directory": str(destination), "schema": schema, "agentId": agent_id, "mediaIncluded": False}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("inspect", help="Validate all references and print a compact inventory")
    export = commands.add_parser("export", help="Create a separate analysis view with resolved references")
    export.add_argument("--output", required=True, type=Path)
    export.add_argument("--agent", help="Export one logical agent, without video media")
    args = parser.parse_args(argv)
    try:
        with CaptureReader(args.capture) as reader:
            if args.command == "inspect":
                result = {"success": True, "runId": reader.run_id, **reader.validate_references()}
            else:
                result = {"success": True, **export_expanded(reader, args.output, args.agent)}
        print(json.dumps(result, ensure_ascii=True, indent=2))
        return 0
    except (CaptureFormatError, OSError, KeyError, TypeError) as error:
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=True), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())