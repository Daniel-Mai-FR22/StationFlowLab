"""Audit recorded roadside arrivals and continuous facade crossings without driving Unity."""
import argparse
import collections
import datetime
import json
import math
import statistics
from pathlib import Path


def rows(path):
    with path.open(encoding="utf-8-sig") as source:
        for line in source:
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                # A running capture can have an incomplete final line.
                continue


def point(value):
    if not isinstance(value, dict):
        raise ValueError("A recorded position must contain finite x/y/z coordinates.")
    result = tuple(float(value[axis]) for axis in ("x", "y", "z"))
    if not all(math.isfinite(axis) for axis in result):
        raise ValueError("A recorded position contains a nonfinite coordinate.")
    return result


def planar(a, b):
    return math.hypot(a[0] - b[0], a[2] - b[2])


def boarding(value):
    return value == "Boarding" or value == 0


def eligible(row):
    return (row.get("visibleActor", True) and not row.get("finished", False)
            and not row.get("isOnNativeLink", False) and not row.get("isSeated", False)
            and not row.get("seatId")
            and row.get("execution") not in {"SittingDown", "Seated", "StandingUp", "WalkingToSeat"})


def audit(directory, output, facade_z=-28.0, door_xs=(-24.0, -9.0, 0.0, 9.0, 24.0),
          maximum_gap=2.0, maximum_speed=3.0):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    errors = []
    def error(message):
        if len(errors) < 100:
            errors.append(message)

    catalog = manifest.get("entrancePortals")
    if not catalog:
        raise ValueError("The capture manifest must contain saved entrancePortals and spawnPoints.")
    portals = {}
    minimum_marker_facade = None
    for portal in catalog:
        name = portal.get("entranceId")
        if not name or name in portals:
            error("Saved entrance IDs are empty or duplicated.")
            continue
        weight = float(portal.get("weight", 0.0))
        if not math.isfinite(weight) or weight < 0.0:
            error("Saved entrance weight is invalid: " + name)
        anchors = [point(value) for value in portal.get("spawnPoints", [])]
        portals[name] = {"weight": weight, "anchors": anchors}
        if weight > 0.0:
            if not anchors:
                error("A positive entrance has no saved spawn points: " + name)
            for anchor in anchors:
                distance_facade = facade_z - anchor[2]
                minimum_marker_facade = distance_facade if minimum_marker_facade is None else min(minimum_marker_facade, distance_facade)
                if distance_facade < 30.0:
                    error("A positive entrance spawn point is less than30m outside the facade: " + name)
    positive = {name for name, portal in portals.items() if portal["weight"] > 0.0}
    if not positive:
        error("No positive weighted entrance is configured.")
    settings_weights = (manifest.get("arrivalSettings") or {}).get("entranceWeights") or []
    for item in settings_weights:
        name = item.get("entranceId")
        if name not in portals:
            error("Configured entrance weights refer to an unknown saved portal: " + str(name))
        elif float(item.get("weight", 0.0)) != portals[name]["weight"]:
            error("Arrival settings and saved portal weights disagree: " + name)

    plans = {plan["journeyId"]: plan for plan in manifest.get("scheduledJourneys", [])}
    planned_groups = collections.defaultdict(set)
    planned_counts = collections.Counter()
    for plan in plans.values():
        if not boarding(plan.get("purpose")):
            continue
        name = plan.get("entranceId")
        planned_counts[name] += 1
        if name not in positive or name == "entry-main":
            error("A scheduled boarder uses a zero/unknown/central-exit portal: " + plan["journeyId"])
        if plan.get("groupId"):
            planned_groups[plan["groupId"]].add(name)
    for name, entrances in planned_groups.items():
        if len(entrances) != 1:
            error("Scheduled companions have different entrances: " + name)

    entered = {}
    terminal = {}
    actual_groups = collections.defaultdict(set)
    actual_counts = collections.Counter()
    actual_facade_minimum = maximum_anchor_snap = maximum_anchor_height = None
    for event in rows(directory / "events.jsonl"):
        journey = event.get("agentId")
        if event.get("kind") == "journey_finished":
            terminal[journey] = event
        if event.get("kind") != "journey_entered" or not boarding(event.get("purpose")):
            continue
        if journey in entered:
            error("An immutable boarding journey entered more thanonce: " + str(journey))
            continue
        name = event.get("entranceId")
        position = point(event.get("position"))
        stamp = float(event["time"])
        actual_counts[name] += 1
        if name not in positive or name == "entry-main":
            error("An actual boarder entered through a zero/unknown/central-exit portal: " + str(journey))
        plan = plans.get(journey)
        if not plan or not boarding(plan.get("purpose")) or plan.get("entranceId") != name:
            error("Actual entry does not match its immutable boarding plan: " + str(journey))
        anchors = portals.get(name, {}).get("anchors", [])
        if anchors:
            anchor = min(anchors, key=lambda candidate: planar(position, candidate))
            snap = planar(position, anchor)
            height = abs(position[1] - anchor[1])
            maximum_anchor_snap = snap if maximum_anchor_snap is None else max(maximum_anchor_snap, snap)
            maximum_anchor_height = height if maximum_anchor_height is None else max(maximum_anchor_height, height)
            if snap > .3 or height > .15:
                error("Actual entry exceeds0.3m planar/0.15m height tolerance from its saved portal: " + str(journey))
        else:
            error("Actual entry has no matching saved spawn point: " + str(journey))
            snap = height = None
        distance_facade = facade_z - position[2]
        actual_facade_minimum = distance_facade if actual_facade_minimum is None else min(actual_facade_minimum, distance_facade)
        if distance_facade < 30.0:
            error("Actual boarding entry is less than30m outside the facade: " + str(journey))
        group = event.get("groupId") or ""
        if group:
            actual_groups[group].add(name)
        entered[journey] = {"journeyId": journey, "serviceId": event.get("serviceId"), "groupId": group,
            "assignedEntrance": name, "enteredTime": stamp, "entryPosition": event["position"],
            "entryFacadeDistance": distance_facade, "anchorPlanarSnap": snap, "anchorHeightSnap": height,
            "plannedEntryTime": plan.get("plannedEntrySeconds") if plan else None,
            "trajectorySamples": 0, "continuousEntryToFirstCrossing": True, "observedPathLengthBeforeHall": 0.0,
            "firstInwardCrossing": None, "inwardCrossings": 0, "outwardCrossings": 0,
            "previous": {"time": stamp, "position": event["position"], "execution": "Entered", "visibleActor": True}}
    for group, names in actual_groups.items():
        if len(names) != 1:
            error("Actually entered companions have different source portals: " + group)
    if not entered:
        error("No actual boarding entry has been recorded.")

    crossings = []
    excluded = collections.Counter()
    excluded_examples = []
    total_segments = total_samples = 0
    coincident_samples = 0
    last_time = None
    for row in rows(directory / "trajectories.jsonl"):
        journey = row.get("agentId")
        state = entered.get(journey)
        if state is None:
            continue
        stamp = float(row["time"])
        last_time = stamp if last_time is None else max(last_time, stamp)
        total_samples += 1
        state["trajectorySamples"] += 1
        prior = state["previous"]
        state["previous"] = row
        if not eligible(row) or not eligible(prior):
            reason = "seatNativeLinkOrInactiveTransition"
        else:
            delta = stamp - float(prior["time"])
            a, b = point(prior["position"]), point(row["position"])
            length = planar(a, b)
            if delta == 0.0 and math.dist(a, b) <= 1e-6:
                # Journey-entered and the first trajectory sample can share the same pose/time.
                # This duplicate is no travelled segment and does not break the observed chain.
                coincident_samples += 1
                continue
            if delta <= 0.0:
                reason = "nonIncreasingTime"
            elif delta > maximum_gap:
                reason = "sampleGapOver2Seconds"
            elif abs(a[1] - b[1]) > .25:
                reason = "differentFloorOrVerticalTransition"
            elif length > maximum_speed * delta + .25:
                reason = "implausibleWalkingJump"
            else:
                reason = None
        if reason:
            excluded[reason] += 1
            if state["firstInwardCrossing"] is None:
                state["continuousEntryToFirstCrossing"] = False
            if len(excluded_examples) < 20:
                excluded_examples.append({"journeyId": journey, "reason": reason,
                    "previousTime": prior.get("time"), "time": stamp,
                    "previousPosition": prior.get("position"), "position": row.get("position")})
            continue
        total_segments += 1
        direction = "inward" if a[2] < facade_z <= b[2] else "outward" if b[2] < facade_z <= a[2] else None
        if not direction:
            if state["firstInwardCrossing"] is None:
                state["observedPathLengthBeforeHall"] += length
            continue
        fraction = (facade_z - a[2]) / (b[2] - a[2])
        crossing_x = a[0] + fraction * (b[0] - a[0])
        crossing_y = a[1] + fraction * (b[1] - a[1])
        nearest_door = min(door_xs, key=lambda x: abs(crossing_x - x))
        crossing = {"journeyId": journey, "assignedEntrance": state["assignedEntrance"], "direction": direction,
            "time": float(prior["time"]) + fraction * delta, "previousTime": prior["time"], "nextTime": stamp,
            "segmentSeconds": delta, "segmentLength": length, "segmentPlanarSpeed": length / delta,
            "position": {"x": crossing_x, "y": crossing_y, "z": facade_z},
            "nearestPhysicalDoorX": nearest_door, "offsetFromNearestDoorCentre": abs(crossing_x - nearest_door)}
        crossings.append(crossing)
        state[direction + "Crossings"] += 1
        if direction == "inward" and state["firstInwardCrossing"] is None:
            state["observedPathLengthBeforeHall"] += length * fraction
            state["firstInwardCrossing"] = crossing
            state["timeFromEntryToHall"] = crossing["time"] - state["enteredTime"]

    first_crossings = [state["firstInwardCrossing"] for state in entered.values() if state["firstInwardCrossing"]]
    door_counts = collections.Counter(crossing["nearestPhysicalDoorX"] for crossing in first_crossings)
    flow_choices = collections.Counter((crossing["assignedEntrance"], crossing["nearestPhysicalDoorX"]) for crossing in first_crossings)
    lifecycle = collections.Counter()
    for journey, state in entered.items():
        state.pop("previous")
        ending = terminal.get(journey)
        state["lifecycleStatus"] = ending.get("outcome", "Finished") if ending else "ActiveOrPending"
        state["finishedTime"] = ending.get("time") if ending else None
        state["terminalReason"] = ending.get("detail") if ending else None
        state["lifecycleRightCensored"] = ending is None
        state["hallEntryObservation"] = ("Observed continuous facade crossing" if state["firstInwardCrossing"] else
            "No eligible inward crossing recorded before journey ended" if ending else "Right-censored before an observed hall entry")
        lifecycle[state["lifecycleStatus"]] += 1
    counts = [actual_counts[name] for name in sorted(positive)]
    mean = statistics.mean(counts) if counts else 0.0
    variance = statistics.pvariance(counts) if counts else 0.0
    positive_weight = sum(portals[name]["weight"] for name in positive)
    portal_statistics = []
    for name, portal in sorted(portals.items()):
        observed = [state for state in entered.values() if state["assignedEntrance"] == name]
        first = min(observed, key=lambda state: state["enteredTime"]) if observed else None
        portal_statistics.append({"entranceId": name, "weight": portal["weight"], "savedSpawnPoints": len(portal["anchors"]),
            "plannedBoarders": planned_counts[name], "actualEnteredBoarders": actual_counts[name],
            "configuredInboundShare": portal["weight"] / positive_weight if positive_weight else None,
            "observedInboundShare": actual_counts[name] / len(entered) if entered else None,
            "firstActualEntryTime": first["enteredTime"] if first else None,
            "firstActualEntryDistanceFromFacade": first["entryFacadeDistance"] if first else None,
            "minimumActualEntryDistanceFromFacade": min(state["entryFacadeDistance"] for state in observed) if observed else None})
    try:
        status = json.loads(Path("Reports/TrainStationDaily/day-play-check.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        status = {}
    matching_status = Path(status.get("outputDirectory", "")).resolve() == directory.resolve()
    result = {"createdUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "capture": str(directory.resolve()),
        "captureClosed": matching_status and status.get("phase") == "complete",
        "complete": matching_status and status.get("phase") == "complete" and status.get("completionReached", False),
        "success": False if errors else True if first_crossings else None, "errors": errors,
        "walkingEvidenceAvailable": bool(first_crossings),
        "facadeZ": facade_z, "physicalDoorXs": list(door_xs), "minimumRequiredOutsideDistance": 30.0,
        "minimumPositiveSavedSpawnDistanceFromFacade": minimum_marker_facade,
        "minimumActualEntryDistanceFromFacade": actual_facade_minimum,
        "maximumActualEntryPlanarSnap": maximum_anchor_snap, "maximumActualEntryHeightSnap": maximum_anchor_height,
        "plannedBoardingCounts": dict(planned_counts), "actualEnteredBoarders": len(entered),
        "plannedCompanionGroups": len(planned_groups), "actualCompanionGroups": len(actual_groups),
        "sourcePortalStatistics": portal_statistics,
        "positivePortalCountVariance": {"populationMean": mean, "populationVariance": variance,
            "populationStandardDeviation": math.sqrt(variance), "coefficientOfVariation": math.sqrt(variance) / mean if mean else None,
            "minimum": min(counts) if counts else None, "maximum": max(counts) if counts else None,
            "note": "Measured descriptive counts; equal per-portal flow is not required."},
        "recordedBoarderTrajectorySamples": total_samples, "eligibleContinuousSegments": total_segments,
        "coincidentPoseSamplesIgnored": coincident_samples,
        "excludedSegments": dict(excluded), "excludedSegmentExamples": excluded_examples, "lastSampleTime": last_time,
        "observedFirstInwardCrossings": len(first_crossings),
        "continuousEntryToHallJourneys": sum(state["firstInwardCrossing"] is not None and state["continuousEntryToFirstCrossing"] for state in entered.values()),
        "actualFirstDoorCounts": [{"doorX": x, "boarders": door_counts[x]} for x in door_xs],
        "observedDoorwayDiversity": sum(door_counts[x] > 0 for x in door_xs),
        "assignedPortalToChosenDoor": [{"assignedEntrance": name, "nearestPhysicalDoorX": x, "boarders": count}
            for (name, x), count in sorted(flow_choices.items())],
        "lifecycleCounts": dict(lifecycle), "crossings": crossings, "journeys": list(entered.values()),
        "rightCensoredLifecycles": sum(state["lifecycleRightCensored"] for state in entered.values()),
        "rightCensoredBeforeObservedHallEntry": sum(state["lifecycleRightCensored"] and not state["firstInwardCrossing"] for state in entered.values()),
        "endedWithoutObservedHallEntry": sum(not state["lifecycleRightCensored"] and not state["firstInwardCrossing"] for state in entered.values()),
        "nativeDayEvidence": {key: status.get(key) for key in ("elapsed", "completionReached", "success", "boarded", "exited", "unserved", "errors")} if matching_status else None,
        "scope": "Independent saved-manifest/event/trajectory audit. Boarding journey entry must match a positive assigned portal anchor within0.3m planar/0.15m height and be at least30m outside the z=-28 facade. Groups share a source portal. Actual mouths are measured from continuous same-journey recorded segments with0<delta<=2s, <=3m/s walking plus0.25m sample allowance, and no seat/native-link/inactive or vertical transition. Coincident pose records at identical timestamps are deduplicated without inventing a travelled segment. Crossing time/position use linear interpolation between adjacent eligible samples; nearest physical door x is descriptive, not an enforced destination. Teleport-like jumps and recording gaps are excluded rather than interpreted as a walk. No claim about between-frame paths, unseen journeys, exact stopped-root immobility, skeletal collisions or navigation ownership beyond the independently supplied native observer proof. Equal entrance counts and use of the source portal's nearest door are not pass conditions."}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("capture", "captureClosed", "complete", "success", "errors", "lastSampleTime",
        "actualEnteredBoarders", "minimumActualEntryDistanceFromFacade", "maximumActualEntryPlanarSnap",
        "observedFirstInwardCrossings", "continuousEntryToHallJourneys", "observedDoorwayDiversity", "lifecycleCounts", "actualFirstDoorCounts",
        "positivePortalCountVariance", "excludedSegments")}, ensure_ascii=False, indent=2))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", type=Path)
    parser.add_argument("--output", type=Path, default=Path("Reports/TrainStationDaily/HallFountain/roadside-arrivals-validation.json"))
    parser.add_argument("--facade-z", type=float, default=-28.0)
    parser.add_argument("--door-xs", type=float, nargs="+", default=[-24.0, -9.0, 0.0, 9.0, 24.0])
    args = parser.parse_args()
    capture = args.capture
    if capture is None:
        capture = Path(json.loads(Path("Reports/TrainStationDaily/day-play-check.json").read_text(encoding="utf-8-sig"))["outputDirectory"])
    audit(capture, args.output, args.facade_z, tuple(args.door_xs))
