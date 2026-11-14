"""Describe physical queue admission and scanner timing from a closed capture.

Read-only inputs. Complements the independent capture validator; no Unity calls.
Usage: python BuildRequests/summarize_station_arrival_fifo_evidence.py CAPTURE --output REPORT.json
Optional: --validator-report EXISTING.json, --play-report EXISTING.json.
"""
import argparse
import collections
import json
import math
from pathlib import Path
import statistics

from summarize_station_day_evidence import INPUTS, records, summarize


def planar(a, b):
    return math.hypot(a["x"] - b["x"], a["z"] - b["z"])


def distribution(values, suffix):
    if not values:
        return {"count": 0}
    ordered = sorted(values)
    return {"count": len(values), "minimum" + suffix: ordered[0],
            "median" + suffix: statistics.median(ordered), "maximum" + suffix: ordered[-1]}


def physical_summary(directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    if manifest.get("physicalArrivalQueues") is not True:
        raise ValueError("Capture does not declare physicalArrivalQueues; use the earlier summary for historical captures.")
    controls = {("gate", g["track"]): g for g in manifest["platformControls"]}
    controls.update({("coach", c["track"], c["coachNumber"]): c for c in manifest["coachControls"]})
    groups = {"gate": collections.defaultdict(list), "coach": collections.defaultdict(list)}
    examples = []
    joins = collections.Counter()
    scans, granted, passed = collections.defaultdict(list), {}, {}
    by_track = collections.defaultdict(lambda: {"passes": [], "joins": []})
    by_coach = collections.defaultdict(collections.Counter)
    closed = False
    for event in records(directory / "events.jsonl"):
        kind, jid, now = event.get("kind"), event.get("agentId"), event["time"]
        closed |= kind == "run_ended"
        if kind in ("coach_queue_joined", "coach_boarding_started", "coach_boarding_ready", "boarded"):
            by_coach[event["coachNumber"]][kind] += 1
            if kind == "boarded":
                by_coach[event["coachNumber"]]["representedBoarded"] += event["representedPassengers"]
        if kind in ("platform_queue_joined", "coach_queue_joined"):
            queue_type = "gate" if kind == "platform_queue_joined" else "coach"
            track = event["profile"]["trackNumber"]
            key = (queue_type, track) if queue_type == "gate" else (queue_type, track, event["coachNumber"])
            control = controls[key]
            groups[queue_type]["distance"].append(planar(event["position"], event["queueArrivalPosition"]))
            offset = groups[queue_type]["distance"][-1]
            joins[queue_type] += 1
            slot = event["queueSlotIndex"]
            target_matches = 0 <= slot < len(control["queueAnchors"]) and planar(
                event["queueArrivalPosition"], control["queueAnchors"][slot]) < .001
            predecessor = event.get("queuePredecessorJourneyId")
            separation = planar(event["position"], event["queuePredecessorPosition"]) if predecessor else None
            if separation is not None:
                groups[queue_type]["separation"].append(separation)
            issues = []
            if offset > control["arrivalTolerance"] + .00001:
                issues.append("join outside configured arrival tolerance")
            if separation is not None and separation + .00001 < control["minimumQueueSeparation"]:
                issues.append("join below configured predecessor separation")
            if not target_matches:
                issues.append("logged queue arrival target differs from saved anchor")
            for issue in issues:
                groups[queue_type]["issues"].append(issue)
            if issues and len(examples) < 15:
                examples.append({"time": now, "journeyId": jid, "queue": list(key), "issues": issues,
                                 "arrivalDistanceMeters": offset, "predecessorSeparationMeters": separation})
            if queue_type == "gate":
                by_track[track]["joins"].append(now)
        elif kind == "platform_ticket_scan_started":
            scans[jid].append(event)
        elif kind == "platform_access_granted":
            granted[jid] = event
        elif kind == "platform_access_passed":
            passed[jid] = event
            by_track[event["profile"]["trackNumber"]]["passes"].append(now)
    if not closed:
        raise ValueError("Capture has not logged run_ended; wait until the run closes.")

    complete_durations, first_start_durations, crosses, restarts = [], [], [], []
    weighted = collections.defaultdict(list)
    for jid, event in granted.items():
        starts = scans.get(jid, [])
        if not starts:
            continue
        duration = event["time"] - starts[-1]["time"]
        total = event["time"] - starts[0]["time"]
        complete_durations.append(duration)
        first_start_durations.append(total)
        weighted[event["representedPassengers"]].append(duration)
        if len(starts) > 1:
            nominal = controls[("gate", event["profile"]["trackNumber"])]["scanSecondsPerActor"]
            restarts.append({"journeyId": jid, "scanStarts": [e["time"] for e in starts],
                             "grantSeconds": event["time"], "extraSecondsBeyondNominal": total - nominal})
        if jid in passed:
            crosses.append(passed[jid]["time"] - event["time"])
    physical = {}
    for queue_type, values in groups.items():
        config = [c for key, c in controls.items() if key[0] == queue_type]
        physical[queue_type] = {"joinCount": joins[queue_type],
            "configuredArrivalTolerancesMeters": sorted({c["arrivalTolerance"] for c in config}),
            "configuredMinimumSeparationsMeters": sorted({c["minimumQueueSeparation"] for c in config}),
            "arrivalDistance": distribution(values["distance"], "Meters"),
            "predecessorSeparation": distribution(values["separation"], "Meters"),
            "descriptiveIssueCount": len(values["issues"]), "issues": dict(collections.Counter(values["issues"]))}
    return {"physicalArrivalQueues": True, "admission": physical, "admissionIssueExamples": examples,
        "scanner": {"configuredSecondsPerVisibleActor": sorted({g["scanSecondsPerActor"] for key, g in controls.items() if key[0] == "gate"}),
            "completedScanFromLatestStart": distribution(complete_durations, "Seconds"),
            "completedScanFromFirstStart": distribution(first_start_durations, "Seconds"),
            "scanDurationByRepresentedWeight": {str(k): distribution(v, "Seconds") for k, v in sorted(weighted.items())},
            "restartedGrantedJourneys": len(restarts), "restartExamples": restarts[:15],
            "grantToPass": distribution(crosses, "Seconds")},
        "coachServiceSecondsPerRepresentedPassenger": sorted({c["boardingSecondsPerPassenger"] for key, c in controls.items() if key[0] == "coach"}),
        "coachBoardingByNumber": {str(coach): dict(counts) for coach, counts in sorted(by_coach.items())},
        "gateWindows": {str(track): {"joins": len(row["joins"]), "passes": len(row["passes"]),
            "firstPassSeconds": min(row["passes"], default=None), "lastPassSeconds": max(row["passes"], default=None)}
            for track, row in sorted(by_track.items())},
        "limits": ["Admission measurements use logged physical actor/arrival/predecessor coordinates and manifest anchors.",
            "FIFO event-order reconstruction is in the base summary. This report does not certify unsampled first-contact ordering or full-density throughput.",
            "Scanner duration uses the latest start; first-start duration and restart list expose lost progress separately."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--validator-report", type=Path)
    parser.add_argument("--play-report", type=Path)
    parser.add_argument("--baseline-summary", type=Path)
    args = parser.parse_args()
    directory = args.capture.resolve()
    if args.output.resolve() in {directory / name for name in INPUTS}:
        parser.error("Output cannot overwrite a capture input.")
    physical = physical_summary(directory)
    result = summarize(directory)
    result["notes"][1] = "In physicalArrivalQueues captures, join-to-service begins at physical tail admission; pre-admission approach/closed-access waiting is not included."
    result["physicalArrivalFifo"] = physical
    if args.validator_report:
        report = json.loads(args.validator_report.read_text(encoding="utf-8-sig"))
        result["existingIndependentValidation"] = {"path": str(args.validator_report.resolve()),
            **{key: report.get(key) for key in ("directory", "success", "errorCount", "checks", "warnings")},
            "sameCapture": Path(report.get("directory", "")).resolve() == directory}
    if args.play_report:
        report = json.loads(args.play_report.read_text(encoding="utf-8-sig"))
        result["existingPlayReport"] = {"path": str(args.play_report.resolve()),
            **{key: report.get(key) for key in ("outputDirectory", "success", "completed", "errors", "warnings")}}
    if args.baseline_summary:
        baseline = json.loads(args.baseline_summary.read_text(encoding="utf-8-sig"))
        def comparison_row(report):
            counts = report["eventCounts"]
            return {"entered": report["counts"]["entered"], "boarded": report["boarding"]["events"],
                "representedBoarded": report["boarding"]["representedPeople"],
                "gateJoins": counts.get("platform_queue_joined", 0),
                "gatePasses": counts.get("platform_access_passed", 0),
                "coachJoins": counts.get("coach_queue_joined", 0),
                "peakGateActors": max(report["gateQueues"]["peakActorsByPhysicalQueue"].values(), default=0),
                "peakCoachActors": max(report["coachQueues"]["peakActorsByPhysicalQueue"].values(), default=0),
                "lastStartToGrantScan": report["completedStageDurations"].get("platformScanService"),
                "grantToPass": report["completedStageDurations"].get("platformGrantToPass"),
                "nativeNoProgressRetries": report["nativeNoProgressRetries"]["count"]}
        result["historicalComparison"] = {"baselinePath": str(args.baseline_summary.resolve()),
            "baselineCapture": baseline["capture"], "beforeRemoteRegistration": comparison_row(baseline),
            "physicalArrivalCapture": comparison_row(result),
            "scope": "Separate bounded native runs with changed queue/scanner logic; this is descriptive, not a controlled causal throughput experiment.",
            "notComparable": "Old join-to-service includes travel from remote registration. New join-to-service begins only at physical tail admission; those waits must not be compared as the same measurement."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output.resolve()), "counts": result["counts"],
                      "outcomes": result["terminalOutcomes"], "physicalArrivalFifo": physical}, indent=2))


if __name__ == "__main__":
    main()
