"""Read-only behavioral evidence summary; complements, never replaces, the capture validators.

Usage: python BuildRequests/summarize_station_day_evidence.py CAPTURE --output REPORT.json
Optional: --validator-report EXISTING_VALIDATOR_REPORT.json (read only).
"""
import argparse
import collections
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import statistics


INPUTS = ("manifest.json", "events.jsonl", "decisions.jsonl", "trajectories.jsonl")


def records(path):
    with path.open(encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except ValueError as error:
                    raise ValueError(f"{path.name}:{line_number}: {error}") from error


def field(detail, name):
    match = re.search(r"(?:^|;\s*)" + re.escape(name) + r"=([^;]+)", detail or "")
    return match.group(1).strip() if match else ""


def stats(values):
    if not values:
        return {"count": 0, "maximumSeconds": None, "medianSeconds": None, "p95Seconds": None}
    ordered = sorted(values, key=lambda value: value[0])
    return {"count": len(values), "maximumSeconds": ordered[-1][0], "maximumJourneyId": ordered[-1][1],
            "medianSeconds": statistics.median(v[0] for v in ordered),
            "p95Seconds": ordered[min(len(ordered) - 1, int((len(ordered) - 1) * .95))][0]}


class QueueEvidence:
    def __init__(self):
        self.lines = collections.defaultdict(list)
        self.max_counts = collections.Counter()
        self.max_weights = collections.Counter()
        self.joins = {}
        self.fifo_mismatches = []
        self.terminal_cleanups = 0

    def join(self, key, jid, now, weights):
        line = self.lines[key]
        if jid in line:
            self.fifo_mismatches.append({"time": now, "journeyId": jid, "issue": "duplicate join", "queue": key})
            return
        line.append(jid)
        self.joins[jid] = now
        self.max_counts[key] = max(self.max_counts[key], len(line))
        self.max_weights[key] = max(self.max_weights[key], sum(weights.get(j, 0) for j in line))

    def head_check(self, key, jid, now, event):
        line = self.lines[key]
        if not line or line[0] != jid:
            self.fifo_mismatches.append({"time": now, "journeyId": jid, "event": event, "queue": key,
                                         "recordedHead": line[0] if line else None})

    def remove(self, key, jid):
        if jid in self.lines[key]:
            self.lines[key].remove(jid)

    def terminal(self, jid):
        for line in self.lines.values():
            if jid in line:
                line.remove(jid)
                self.terminal_cleanups += 1

    def report(self, end_time):
        return {"peakActorsByPhysicalQueue": dict(self.max_counts), "peakRepresentedPeopleByPhysicalQueue": dict(self.max_weights),
                "queuedAtCaptureEnd": {k: len(v) for k, v in self.lines.items() if v},
                "longestStillQueuedSeconds": max((end_time - self.joins[j] for line in self.lines.values() for j in line), default=0),
                "fifoMismatchCount": len(self.fifo_mismatches), "fifoMismatchExamples": self.fifo_mismatches[:10],
                "queueRemovalsInferredFromTerminalLifecycle": self.terminal_cleanups}


def summarize(directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    plans = {p["journeyId"]: p for p in manifest.get("scheduledJourneys", [])}
    weights = {jid: p["representedPassengers"] for jid, p in plans.items()}
    seat_activities = {seat["seatId"]: poi["seatedActivity"] for poi in manifest.get("pois", []) for seat in poi.get("seats", [])}
    gate, coach = QueueEvidence(), QueueEvidence()
    kinds, outcomes, outcome_weights, activities, interrupts, seated_starts, seated_completed = (collections.Counter() for _ in range(7))
    for activity in set(seat_activities.values()):
        seated_starts[activity] = seated_completed[activity] = 0
    entered, terminal, boarded, scans, grants, passes, coach_starts, ready = ({} for _ in range(8))
    durations = collections.defaultdict(list)
    seated_open = {}
    lifecycle_issues, progress_retries, failure_details = [], [], []
    board_chain_issues, sample_declaration_issues = [], []
    run_end, last_time, active_peak = None, 0, 0
    for event in records(directory / "events.jsonl"):
        kind, jid, now = event.get("kind"), event.get("agentId"), event["time"]
        kinds[kind] += 1
        last_time = max(last_time, now)
        detail = event.get("detail", "")
        if kind == "run_ended":
            run_end = now
        if kind == "trajectory_sample":
            ids = event.get("activeJourneyIds", [])
            active_peak = max(active_peak, len(ids))
            for sample_id in ids:
                if sample_id in terminal:
                    sample_declaration_issues.append({"time": now, "journeyId": sample_id})
            continue
        if not jid:
            continue
        plan = plans.get(jid, {})
        track = plan.get("trackNumber", field(detail, "track"))
        gate_key = f"track-{track}"
        coach_key = f"track-{track}/coach-{plan.get('coachNumber', event.get('coachNumber', field(detail, 'coach')))}"
        weights.setdefault(jid, event.get("representedPassengers", 0))
        if kind == "journey_entered":
            entered[jid] = event
        elif kind in ("journey_finished", "journey_unserved"):
            terminal[jid] = event
            outcome = "Unserved" if kind == "journey_unserved" else event.get("outcome", "Unknown")
            outcomes[outcome] += 1
            outcome_weights[outcome] += weights[jid]
            gate.terminal(jid)
            coach.terminal(jid)
            if outcome == "Failed":
                failure_details.append({"journeyId": jid, "time": now, "detail": detail, "position": event.get("position")})
        elif kind == "boarded":
            boarded[jid] = event
            if manifest.get("controlledPlatformAccess") and jid not in passes:
                board_chain_issues.append({"journeyId": jid, "issue": "no prior platform passage"})
            if manifest.get("coachControls") and (jid not in ready or not event.get("coachQueueValidated")):
                board_chain_issues.append({"journeyId": jid, "issue": "no validated coach-ready event"})
        elif kind == "platform_queue_joined":
            gate.join(gate_key, jid, now, weights)
        elif kind in ("platform_queue_left", "platform_queue_expired"):
            gate.remove(gate_key, jid)
        elif kind == "platform_ticket_scan_started":
            scans[jid] = now
        elif kind == "platform_access_granted":
            gate.head_check(gate_key, jid, now, kind)
            gate.remove(gate_key, jid)
            grants[jid] = now
            if jid in gate.joins:
                durations["platformJoinToGrant"].append((now - gate.joins[jid], jid))
            if jid in scans:
                durations["platformScanService"].append((now - scans[jid], jid))
        elif kind == "platform_access_passed":
            passes[jid] = now
            if jid in grants:
                durations["platformGrantToPass"].append((now - grants[jid], jid))
            if jid in gate.joins:
                durations["platformJoinToPass"].append((now - gate.joins[jid], jid))
        elif kind == "coach_queue_joined":
            coach.join(coach_key, jid, now, weights)
        elif kind == "coach_queue_left":
            coach.remove(coach_key, jid)
        elif kind == "coach_boarding_started":
            coach.head_check(coach_key, jid, now, kind)
            coach_starts[jid] = now
            if jid in coach.joins:
                durations["coachJoinToServiceStart"].append((now - coach.joins[jid], jid))
        elif kind == "coach_boarding_ready":
            coach.head_check(coach_key, jid, now, kind)
            ready[jid] = now
            if jid in coach_starts:
                durations["coachService"].append((now - coach_starts[jid], jid))
            if jid in coach.joins:
                durations["coachJoinToReady"].append((now - coach.joins[jid], jid))
        elif kind == "activity_completed":
            activities[detail.split(";")[0]] += 1
        elif kind == "activity_interrupted":
            interrupts[detail] += 1
            if "no progress" in detail.lower():
                progress_retries.append({"journeyId": jid, "time": now, "detail": detail})
        elif kind == "seat_state_changed":
            seat = field(detail, "seat")
            activity = seat_activities.get(seat, "Unknown")
            if " -> Seated" in detail:
                seated_starts[activity] += 1
                seated_open[jid] = (now, activity)
            elif "Seated ->" in detail and jid in seated_open:
                began, activity = seated_open.pop(jid)
                durations["seatedDwell/" + activity].append((now - began, jid))
        elif kind == "seat_released":
            if field(detail, "completed").lower() == "true":
                seated_completed[seat_activities.get(field(detail, "seat"), "Unknown")] += 1
            seated_open.pop(jid, None)

    post_terminal_rows, trajectory_rows, observed_seated = 0, 0, collections.defaultdict(set)
    last_sample = {}
    for row in records(directory / "trajectories.jsonl"):
        trajectory_rows += 1
        jid, now = row["agentId"], row["time"]
        last_sample[jid] = now
        if jid in terminal and now > terminal[jid]["time"] + 1e-6:
            post_terminal_rows += 1
        if row.get("isSeated"):
            observed_seated[row.get("seatedActivity", "Unknown")].add(jid)
    for jid, event in boarded.items():
        end = terminal.get(jid)
        if not end or end.get("outcome") != "Boarded" or abs(end["time"] - event["time"]) > 1e-4:
            lifecycle_issues.append({"journeyId": jid, "issue": "boarding lacks same-frame Boarded terminal event"})

    decisions, no_choices, last_streak, maximum_streak = 0, collections.Counter(), collections.Counter(), collections.Counter()
    for row in records(directory / "decisions.jsonl"):
        decisions += 1
        jid = row["agentId"]
        if row.get("selectedActivity") in (None, "None", ""):
            no_choices[jid] += 1
            last_streak[jid] += 1
            maximum_streak[jid] = max(maximum_streak[jid], last_streak[jid])
        else:
            last_streak[jid] = 0
    cohorts = collections.defaultdict(collections.Counter)
    for jid, event in entered.items():
        profile = event.get("profile") or {}
        key = f"{event.get('purpose')}|{profile.get('arrivalProfile')}|clipped={profile.get('clippedAtSimulationStart')}"
        cohorts[key]["entered"] += 1
        cohorts[key][terminal.get(jid, {}).get("outcome", "ActiveAtEnd")] += 1
    for failure in failure_details:
        failure["noChoiceDecisions"] = no_choices[failure["journeyId"]]
    for retry in progress_retries:
        retry["outcomeAtEnd"] = terminal.get(retry["journeyId"], {}).get("outcome", "ActiveAtEnd")
    by_purpose = collections.defaultdict(collections.Counter)
    for jid, event in terminal.items():
        purpose = entered.get(jid, {}).get("purpose", event.get("purpose", "Unknown"))
        outcome = "Unserved" if event["kind"] == "journey_unserved" else event.get("outcome", "Unknown")
        by_purpose[purpose][outcome] += 1
    return {
        "capture": str(directory), "generatedUtc": datetime.now(timezone.utc).isoformat(), "closedCapture": run_end is not None,
        "lastEventSeconds": last_time, "scope": "Descriptive event/trajectory evidence; run the independent frozen validators for integrity certification.",
        "notes": ["Visible actors represent weighted cohorts, not full-density collision volumes.",
                  "Queue durations include approach/closed-access waiting; completed episodes exclude still-waiting actors.",
                  "Despawn evidence means logged trajectory cessation and lifecycle completion, not independent rendered-image proof.",
                  "Queue terminal cleanup is inferred explicitly when no queue-left event was logged."],
        "counts": {"planned": len(plans), "entered": len(entered), "terminal": len(terminal), "activeAtEnd": len(set(entered)-set(terminal)),
                   "peakLoggedActive": active_peak, "decisions": decisions, "trajectoryRows": trajectory_rows},
        "terminalOutcomes": dict(outcomes), "terminalOutcomesByPurpose": dict(by_purpose),
        "terminalRepresentedPeople": dict(outcome_weights), "arrivalCohorts": dict(cohorts),
        "eventCounts": dict(kinds), "gateQueues": gate.report(last_time), "coachQueues": coach.report(last_time),
        "completedStageDurations": {k: stats(v) for k, v in durations.items()},
        "boarding": {"events": len(boarded), "representedPeople": sum(weights[j] for j in boarded),
                     "withPriorGatePass": sum(j in passes for j in boarded), "withPriorCoachReady": sum(j in ready for j in boarded),
                     "chainIssueCount": len(board_chain_issues), "chainIssues": board_chain_issues[:10]},
        "despawnEvidence": {"sameFrameTerminalIssueCount": len(lifecycle_issues), "samplesAfterTerminal": post_terminal_rows,
                            "activeDeclarationsAfterTerminal": len(sample_declaration_issues), "examples": lifecycle_issues[:10]},
        "completedActivities": dict(activities), "seatedActivities": {"stateEntries": dict(seated_starts), "completedReleases": dict(seated_completed),
                              "journeysWithSampledSeatedPose": {k: len(v) for k, v in observed_seated.items()}, "stillSeatedAtEnd": len(seated_open)},
        "nativeNoProgressRetries": {"count": len(progress_retries), "events": progress_retries},
        "interruptionReasons": dict(interrupts), "noChoiceDecisions": {"total": sum(no_choices.values()), "affectedJourneys": len(no_choices),
                                 "largestConsecutiveCounts": maximum_streak.most_common(10)}, "failedJourneys": failure_details}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--validator-report", type=Path)
    args = parser.parse_args()
    directory = args.capture.resolve()
    if args.output and args.output.resolve() in {directory / name for name in INPUTS}:
        parser.error("Output cannot overwrite a capture input.")
    report = summarize(directory)
    if args.validator_report:
        validation = json.loads(args.validator_report.read_text(encoding="utf-8-sig"))
        report["existingIndependentValidation"] = {"path": str(args.validator_report.resolve()),
             **{key: validation.get(key) for key in ("directory", "success", "errorCount", "checks", "warnings")}}
        report["existingIndependentValidation"]["sameCapture"] = Path(validation.get("directory", "")).resolve() == directory
    serialized = json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(serialized + "\n", encoding="utf-8")
        print(json.dumps({"output": str(args.output.resolve()), "closedCapture": report["closedCapture"], "counts": report["counts"],
              "outcomes": report["terminalOutcomes"], "boarding": report["boarding"], "despawn": report["despawnEvidence"]}, indent=2))
    else:
        print(serialized)


if __name__ == "__main__":
    main()
