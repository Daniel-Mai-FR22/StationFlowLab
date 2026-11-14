"""Read-only sampled passing and hold-drift audit; never drives Unity."""
import argparse
import collections
import datetime
import heapq
import json
import math
from pathlib import Path


def rows(path):
    with path.open(encoding="utf-8-sig") as source:
        for line in source:
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                # A live writer can leave an incomplete trailing record.
                continue


def vec(row, name="position"):
    value = row.get(name, {})
    return tuple(value.get(axis, 0.0) for axis in ("x", "y", "z"))


def distance(a, b):
    return math.hypot(a[0] - b[0], a[2] - b[2])


def eligible(row):
    return (row.get("visibleActor", True) and not row.get("finished", False)
            and not row.get("isOnNativeLink", False)
            and not row.get("isSeated", False) and not row.get("seatId")
            and row.get("execution") not in {"SittingDown", "Seated", "StandingUp", "WalkingToSeat"}
            and abs(vec(row)[1] - .02) <= .20)


def hold_kind(row):
    # This flag excludes the carved-footprint removal/resume transition.
    if not row.get("isStationaryHeld", False):
        return None
    return row.get("activity", "") + " " + row.get("execution", "")


def audit(directory, output, status):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    if not manifest.get("stationaryHoldTelemetry"):
        raise ValueError("Actual stationary-hold telemetry is required; semantic labels are insufficient.")
    status_matches = Path(status.get("outputDirectory", "")).resolve() == directory.resolve()
    events = {event["sampleId"]: event for event in rows(directory / "events.jsonl")
              if event.get("kind") == "trajectory_sample"}
    batches = collections.defaultdict(list)
    for row in rows(directory / "trajectories.jsonl"):
        batches[row["sampleId"]].append(row)
    previous = {}
    episodes = {}
    states = collections.defaultdict(lambda: {"heldSamples": 0, "heldJourneys": set(),
        "passingPairs": 0,
        "pairsUnderHalfMetre": 0, "pairsUnderBodyDiameter": 0,
        "minimumCentreSeparation": None, "maximumEpisodeDrift": 0.0,
        "maximumConsecutiveDrift": 0.0, "nonzeroDriftChecks": 0, "driftChecks": 0,
        "settledDriftChecks": 0, "maximumSettledDrift": 0.0,
        "anchorChecks": 0, "maximumAnchorDisplacement": 0.0})
    closest = []
    ordinal = 0
    complete = skipped = checked_pairs = held_samples = drift_checks = 0
    hold_intervals = multi_sample_hold_intervals = 0
    total_under_half = total_under_body = 0
    minimum = None
    first_time = last_time = None
    max_episode = max_step = 0.0
    max_settled = 0.0
    settled_checks = 0
    anchor_checks = bad_anchor_checks = serial_anchor_changes = bad_serial_checks = 0
    maximum_anchor_displacement = 0.0
    anchor_examples = []
    drift_examples = []
    for sample_id in sorted(batches):
        batch = batches[sample_id]
        event = events.get(sample_id)
        ids = {row["agentId"] for row in batch}
        if not event or ids != set(event.get("activeJourneyIds", [])) or len(ids) != len(batch):
            skipped += 1
            continue
        timestamp = event["time"]
        if any(abs(row["time"] - timestamp) > 1e-5 for row in batch):
            skipped += 1
            continue
        complete += 1
        first_time = timestamp if first_time is None else first_time
        last_time = timestamp
        valid = [row for row in batch if eligible(row)]
        holds = [(row, hold_kind(row)) for row in valid if hold_kind(row)]
        current_hold_ids = {row["agentId"] for row, kind in holds}
        for journey in list(episodes):
            if journey not in current_hold_ids:
                episodes.pop(journey)
        moving = []
        for row in valid:
            if hold_kind(row):
                continue
            velocity = vec(row, "velocity")
            speed = math.hypot(velocity[0], velocity[2])
            prior = previous.get(row["agentId"])
            observed_speed = 0.0
            if prior and eligible(prior) and timestamp > prior["time"]:
                observed_speed = distance(vec(row), vec(prior)) / (timestamp - prior["time"])
            if max(speed, observed_speed) > .05:
                moving.append(row)
        for held, kind in holds:
            journey = held["agentId"]
            position = vec(held)
            metrics = states[kind]
            held_samples += 1
            metrics["heldSamples"] += 1
            metrics["heldJourneys"].add(journey)
            held_velocity = vec(held, "velocity")
            recorded_speed = math.hypot(held_velocity[0], held_velocity[2])
            serial = held.get("stationaryHoldSerial", 0)
            anchor = vec(held, "stationaryAnchor")
            anchor_drift = math.dist(position, anchor)
            anchor_checks += 1
            metrics["anchorChecks"] += 1
            metrics["maximumAnchorDisplacement"] = max(metrics["maximumAnchorDisplacement"], anchor_drift)
            maximum_anchor_displacement = max(maximum_anchor_displacement, anchor_drift)
            if serial <= 0:
                bad_serial_checks += 1
            if anchor_drift > .005:
                bad_anchor_checks += 1
                if len(anchor_examples) < 20:
                    anchor_examples.append({"journey": journey, "time": timestamp, "holdSerial": serial,
                        "anchorDisplacement": anchor_drift, "position": held["position"], "anchor": held["stationaryAnchor"]})
            episode = episodes.get(journey)
            if not episode or episode["serial"] != serial:
                episodes[journey] = {"kind": kind, "anchor": position, "previous": position,
                    "firstTime": timestamp, "samples": 1,
                    "settledAnchor": position if recorded_speed <= .05 else None,
                    "serial": serial, "recordedAnchor": anchor}
                hold_intervals += 1
            else:
                if math.dist(anchor, episode["recordedAnchor"]) > 1e-6:
                    serial_anchor_changes += 1
                drift_checks += 1
                metrics["driftChecks"] += 1
                if episode["samples"] == 1:
                    multi_sample_hold_intervals += 1
                episode["samples"] += 1
                drift = math.dist(position, episode["anchor"])
                step = math.dist(position, episode["previous"])
                max_episode = max(max_episode, drift)
                max_step = max(max_step, step)
                metrics["maximumEpisodeDrift"] = max(metrics["maximumEpisodeDrift"], drift)
                metrics["maximumConsecutiveDrift"] = max(metrics["maximumConsecutiveDrift"], step)
                if drift > 1e-6:
                    metrics["nonzeroDriftChecks"] += 1
                    if len(drift_examples) < 20:
                        drift_examples.append({"journey": journey, "kind": kind, "time": timestamp,
                            "episodeStart": episode["firstTime"], "drift": drift,
                            "firstRecordedVelocity": previous.get(journey, {}).get("velocity"),
                            "currentRecordedVelocity": held.get("velocity")})
                episode["previous"] = position
                if episode["settledAnchor"] is None and recorded_speed <= .05:
                    episode["settledAnchor"] = position
                elif episode["settledAnchor"] is not None:
                    settled_drift = math.dist(position, episode["settledAnchor"])
                    settled_checks += 1
                    metrics["settledDriftChecks"] += 1
                    metrics["maximumSettledDrift"] = max(metrics["maximumSettledDrift"], settled_drift)
                    max_settled = max(max_settled, settled_drift)
            for mover in moving:
                if abs(position[1] - vec(mover)[1]) > .15:
                    continue
                separation = distance(position, vec(mover))
                checked_pairs += 1
                metrics["passingPairs"] += 1
                metrics["minimumCentreSeparation"] = separation if metrics["minimumCentreSeparation"] is None else min(metrics["minimumCentreSeparation"], separation)
                minimum = separation if minimum is None else min(minimum, separation)
                if separation < .5:
                    total_under_half += 1
                    metrics["pairsUnderHalfMetre"] += 1
                if separation < .64:
                    total_under_body += 1
                    metrics["pairsUnderBodyDiameter"] += 1
                example = {"time": timestamp, "sampleId": sample_id,
                    "separation": separation, "nominalBodyGap": separation - .64,
                    "heldJourney": journey, "heldKind": kind, "heldPosition": held["position"],
                    "movingJourney": mover["agentId"], "movingActivity": mover["activity"],
                    "movingExecution": mover["execution"], "movingPosition": mover["position"]}
                ordinal += 1
                item = (-separation, ordinal, example)
                if len(closest) < 20:
                    heapq.heappush(closest, item)
                elif separation < -closest[0][0]:
                    heapq.heapreplace(closest, item)
        previous = {row["agentId"]: row for row in batch}
    for metrics in states.values():
        metrics["heldJourneys"] = len(metrics["heldJourneys"])
    native_evidence = None
    native_path = Path("Reports/TrainStationDaily/HallFountain/native-fountain-observations.json")
    if native_path.exists():
        try:
            native = json.loads(native_path.read_text(encoding="utf-8-sig"))
            if Path(native.get("outputDirectory", "")).resolve() == directory.resolve():
                native_evidence = {key: native.get(key) for key in (
                    "complete", "elapsed", "stationaryHoldChecks", "carvedHoldChecks",
                    "movingAvoidanceChecks", "obstacleModeChecks", "stationaryTransitionChecks",
                    "carvedInteriorChecks", "plannedStandingDetours", "maximumStationaryDisplacement",
                    "standingDetourEvidence", "errors")}
                native_evidence["source"] = str(native_path.resolve())
                native_evidence["classificationLimit"] = "Aggregate live observer evidence for this capture. Trajectories do not export the child planning-footprint state, so individual passing contacts cannot be classified as carving versus body-only without replay or added telemetry."
        except (json.JSONDecodeError, OSError):
            # A partial live observer write supplies no independent evidence.
            pass
    result = {"createdUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "capture": str(directory.resolve()), "phase": status.get("phase") if status_matches else "unmatched",
        "captureClosed": status_matches and status.get("phase") == "complete",
        "complete": status_matches and status.get("phase") == "complete" and status.get("completionReached", False),
        "nativeDayEvidence": {key: status.get(key) for key in (
            "elapsed", "completionReached", "success", "boarded", "exited", "unserved", "errors")} if status_matches else None,
        "success": total_under_half == 0 and max_episode <= .005 and bad_anchor_checks == 0 and serial_anchor_changes == 0 and bad_serial_checks == 0,
        "checks": checked_pairs + drift_checks, "completeSharedSamples": complete,
        "incompleteOrMismatchedSamplesExcluded": skipped, "firstSampleTime": first_time,
        "lastSampleTime": last_time, "nominalBodyRadius": .32,
        "minimumMovingToHeldCentreSeparation": minimum,
        "minimumNominalBodyGap": None if minimum is None else minimum - .64,
        "passingPairChecks": checked_pairs, "pairsUnderHalfMetre": total_under_half,
        "pairsUnderNominalBodyDiameter": total_under_body, "heldSamples": held_samples,
        "driftChecks": drift_checks, "maximumEpisodeDrift": max_episode,
        "sameSerialHoldIntervals": hold_intervals, "multiSampleSameSerialHoldIntervals": multi_sample_hold_intervals,
        "maximumConsecutiveHeldDrift": max_step, "byHoldKind": dict(states),
        "settledHoldDriftChecks": settled_checks, "maximumSettledHoldDisplacement": max_settled,
        "holdClassification": "actual isStationaryHeld telemetry, all eligible activities and queues",
        "anchorChecks": anchor_checks, "anchorDisplacementViolations": bad_anchor_checks,
        "maximumRecordedAnchorDisplacement": maximum_anchor_displacement,
        "invalidHoldSerialChecks": bad_serial_checks, "sameHoldSerialAnchorChanges": serial_anchor_changes,
        "nativePlanningEvidence": native_evidence,
        "anchorViolationExamples": anchor_examples,
        "closestPassingSamples": [entry[2] for entry in sorted(closest, key=lambda value: -value[0])],
        "nonzeroDriftExamples": drift_examples,
        "scope": "Independent trajectory audit using only complete shared sampleId/time groups checked against trajectory_sample activeJourneyIds. Actual isStationaryHeld telemetry classifies all eligible held actors, including queues; semantic labels do not classify holds. Every held row verifies its recorded stationaryAnchor within0.005m and positive stationaryHoldSerial. Same-journey/same-serial contiguous intervals verify root displacement and unchanged anchor; differing serials start a new hold. Movers require recorded native speed or same-journey sampled root speed >0.05m/s. Native gate links, seated/seat-reserved actors and calibrated seat transitions, finished/invisible actors and non-floor positions are excluded. Reported body gap subtracts two saved .32m navigation radii; it is a navigation cylinder approximation, not a skeletal collision test. Counts are repeated sampled pairs, not distinct contact incidents. Gaps between recorded frames are not certified. Success requires no centre separation below0.5m, sampled hold displacement<=0.005m, unchanged same-serial anchors, and valid hold serials."}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("phase", "complete", "success", "lastSampleTime", "checks",
        "minimumMovingToHeldCentreSeparation", "pairsUnderHalfMetre", "pairsUnderNominalBodyDiameter",
        "maximumEpisodeDrift", "maximumRecordedAnchorDisplacement", "anchorDisplacementViolations",
        "sameHoldSerialAnchorChanges", "invalidHoldSerialChecks")}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", type=Path)
    parser.add_argument("--output", type=Path, default=Path("Reports/TrainStationDaily/HallFountain/passing-clearance-validation.json"))
    args = parser.parse_args()
    status = json.loads(Path("Reports/TrainStationDaily/day-play-check.json").read_text(encoding="utf-8-sig"))
    audit(args.capture or Path(status["outputDirectory"]), args.output, status)
