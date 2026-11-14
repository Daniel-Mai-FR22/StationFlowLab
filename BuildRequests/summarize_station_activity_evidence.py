"""Read-only natural activity coverage from an unmodified station capture.

This is descriptive evidence, not an integrity validator. Policy selection is
logged before a POI claim and therefore is not by itself proof of execution.
Using/Seated trajectory states and seat transitions prove use; counter-complete
events prove fulfilled service but do not timestamp its start in older captures.
"""
import argparse
from bisect import bisect_right
import collections
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import re
import statistics


ACTIVITIES = ("Display", "Cafe", "Toilet", "Shop", "Rest", "Info", "Platform", "Exit", "Board")
INPUTS = ("manifest.json", "decisions.jsonl", "events.jsonl", "trajectories.jsonl")


def records(path):
    with path.open(encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except ValueError as error:
                    raise ValueError(f"{path.name}:{line_number}: {error}") from error


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def statistics_report(values):
    values = sorted(values)
    return {"count": len(values), "minimum": values[0] if values else None,
            "median": statistics.median(values) if values else None,
            "maximum": values[-1] if values else None}


def detail_activity(detail):
    return re.split(r"[;:]", detail or "", maxsplit=1)[0].strip()


def detail_field(detail, name):
    match = re.search(r"(?:^|;\s*)" + re.escape(name) + r"=([^;]+)", detail or "")
    return match.group(1).strip() if match else ""


def summarize(directory, label):
    directory = directory.resolve()
    hashes = {name: digest(directory / name) for name in INPUTS}
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    pois = {poi["poiId"]: poi for poi in manifest.get("pois", [])}
    seat_activities = {seat["seatId"]: poi.get("seatedActivity", "None")
                       for poi in pois.values() for seat in poi.get("seats", [])}
    by_journey = collections.defaultdict(list)
    decision_times = {}
    selections, options_positive, no_choices = (collections.Counter() for _ in range(3))
    selected_journeys, positive_journeys = (collections.defaultdict(set) for _ in range(2))
    reasons = collections.defaultdict(collections.Counter)
    known_active_costs = collections.defaultdict(lambda: collections.defaultdict(list))
    decisions = 0
    for record in records(directory / "decisions.jsonl"):
        decisions += 1
        jid, activity = record["agentId"], record.get("selectedActivity", "None")
        if activity in ACTIVITIES:
            selections[activity] += 1
            selected_journeys[activity].add(jid)
        else:
            no_choices[jid] += 1
        # Keep every decision as a boundary, including None. A later retry must
        # not be silently attached to a previous intent that already ended.
        by_journey[jid].append({"journeyId": jid, "activity": activity,
            "poi": record.get("selectedPoi", ""), "decisionIndex": record.get("decisionIndex"),
            "selectedAt": record["time"], "states": set(), "committed": False,
            "useStartedAt": None, "useStartEvidence": set(), "counterCompletedAt": None,
            "completedAt": None, "interruptedAt": None, "interruption": "",
            "seatedActivity": None, "seatedStartedAt": None, "seatedCompleted": False,
            "seatSkipped": "", "platformPassed": False, "coachStarted": False,
            "boarded": False, "toiletUseDuration": None, "toiletUseCompletedAt": None,
            "toiletUseInterruptedAt": None})
        known_active = (record.get("platformKnown") and
            not record.get("context", {}).get("requireInitialDisplay", False) and
            record.get("observedStatus") not in ("Closed", "Departed", "Cancelled"))
        for option in record.get("options", []):
            option_activity = option.get("activity", "Unknown")
            reasons[option_activity][option.get("reason", "")] += 1
            if option.get("probability", 0) > 0:
                options_positive[option_activity] += 1
                positive_journeys[option_activity].add(jid)
            if known_active:
                for field in ("slack", "travel", "queue", "dwell", "seatTravel", "seatingTransition", "seatedDwell", "toPlatform"):
                    if field in option:
                        known_active_costs[option_activity][field].append(option[field])
    for jid, episodes in by_journey.items():
        episodes.sort(key=lambda episode: episode["selectedAt"])
        decision_times[jid] = [episode["selectedAt"] for episode in episodes]
        for index, episode in enumerate(episodes[:-1]):
            episode["nextDecisionAt"] = episodes[index + 1]["selectedAt"]

    def episode_at(jid, now):
        index = bisect_right(decision_times.get(jid, []), now + 1e-5) - 1
        return by_journey[jid][index] if index >= 0 else None

    def mark_use(episode, now, evidence):
        episode["committed"] = True
        episode["useStartedAt"] = min(now, episode["useStartedAt"]) if episode["useStartedAt"] is not None else now
        episode["useStartEvidence"].add(evidence)

    event_counts = collections.Counter()
    endings = {}
    entered = {}
    active_peak = 0
    run_end, last_time = None, 0.0
    last_population_time, last_population_count, actor_seconds = 0.0, 0, 0.0
    unmatched = []
    seated_start_counts, seated_end_counts = (collections.Counter() for _ in range(2))
    wc_waiting, wc_leases, wc_fifo_issues = [], {}, []
    wc_max_waiting, wc_max_leases, wc_max_reported_waiting, wc_max_reported_uses = 0, 0, 0, 0
    wc_arrival_offsets, wc_predecessor_gaps = [], []
    relevant = {"activity_started", "activity_committed", "activity_completed", "activity_interrupted",
                "counter_service_started", "counter_service_completed", "seat_state_changed", "seat_released",
                "seated_activity_skipped", "platform_access_passed", "coach_boarding_started", "boarded",
                "toilet_queue_joined", "toilet_queue_left", "toilet_use_reserved", "toilet_use_started", "toilet_use_completed", "toilet_use_interrupted", "toilet_use_released"}
    for event in records(directory / "events.jsonl"):
        kind, jid, now = event.get("kind"), event.get("agentId", ""), event["time"]
        event_counts[kind] += 1
        last_time = max(last_time, now)
        if kind == "run_ended":
            run_end = now
        elif kind == "trajectory_sample":
            actor_seconds += (now - last_population_time) * last_population_count
            last_population_time, last_population_count = now, len(event.get("activeJourneyIds", []))
            active_peak = max(active_peak, last_population_count)
        elif kind == "journey_entered":
            entered[jid] = event
        elif kind in ("journey_finished", "journey_unserved"):
            endings[jid] = event
        if kind not in relevant or not jid:
            continue
        episode = episode_at(jid, now)
        detail = event.get("detail", "")
        wc_max_reported_waiting = max(wc_max_reported_waiting, event.get("toiletQueueCount", 0))
        wc_max_reported_uses = max(wc_max_reported_uses, event.get("toiletUseCount", 0))
        if kind == "toilet_queue_joined":
            if jid in wc_waiting:
                wc_fifo_issues.append({"time": now, "journeyId": jid, "issue": "duplicate WC join"})
            else:
                wc_waiting.append(jid)
            wc_max_waiting = max(wc_max_waiting, len(wc_waiting))
            position, arrival = event.get("position", {}), event.get("queueArrivalPosition", {})
            if position and arrival:
                wc_arrival_offsets.append(math.hypot(position["x"] - arrival["x"], position["z"] - arrival["z"]))
            if event.get("queuePredecessorJourneyId"):
                predecessor = event.get("queuePredecessorPosition", {})
                if position and predecessor:
                    wc_predecessor_gaps.append(math.hypot(position["x"] - predecessor["x"], position["z"] - predecessor["z"]))
        elif kind == "toilet_queue_left":
            if jid in wc_waiting:
                wc_waiting.remove(jid)
        elif kind == "toilet_use_reserved":
            if not wc_waiting or wc_waiting[0] != jid:
                wc_fifo_issues.append({"time": now, "journeyId": jid, "issue": "WC reserved out of arrival FIFO order", "head": wc_waiting[0] if wc_waiting else None})
            if jid in wc_waiting:
                wc_waiting.remove(jid)
            wc_leases[jid] = event.get("toiletUseSlotIndex", detail_field(detail, "useIndex"))
            wc_max_leases = max(wc_max_leases, len(wc_leases))
        elif kind == "toilet_use_released":
            wc_leases.pop(jid, None)
        if kind in ("toilet_queue_left", "toilet_use_released"):
            continue
        described_activity = detail_activity(detail)
        if episode is None or (kind.startswith("activity_") or kind.startswith("counter_service_")) and described_activity in ACTIVITIES and described_activity != episode["activity"]:
            unmatched.append({"kind": kind, "journeyId": jid, "time": now, "detail": detail,
                              "latestIntent": episode["activity"] if episode else None})
            continue
        if kind == "activity_committed":
            episode["committed"] = True
        elif kind in ("activity_started", "counter_service_started"):
            mark_use(episode, now, kind + " event")
        elif kind == "activity_completed":
            episode["committed"] = True
            episode["completedAt"] = now
        elif kind == "activity_interrupted":
            episode["interruptedAt"] = now
            episode["interruption"] = detail
        elif kind == "counter_service_completed":
            episode["committed"] = True
            episode["counterCompletedAt"] = now
        elif kind == "seat_state_changed" and " -> Seated" in detail:
            mark_use(episode, now, "seat transition event")
            episode["seatedStartedAt"] = now
            seated_activity = seat_activities.get(detail_field(detail, "seat"), "Unknown")
            episode["seatedActivity"] = seated_activity
            seated_start_counts[seated_activity] += 1
        elif kind == "seat_released" and detail_field(detail, "completed").lower() == "true":
            episode["seatedCompleted"] = True
            seated_end_counts[seat_activities.get(detail_field(detail, "seat"), "Unknown")] += 1
        elif kind == "seated_activity_skipped":
            episode["seatSkipped"] = detail
        elif kind == "platform_access_passed":
            episode["platformPassed"] = True
            mark_use(episode, now, "validated platform passage")
        elif kind == "coach_boarding_started":
            episode["coachStarted"] = True
        elif kind == "boarded":
            episode["boarded"] = True
        elif kind in ("toilet_queue_joined", "toilet_use_reserved"):
            episode["committed"] = True
        elif kind == "toilet_use_started":
            mark_use(episode, now, "toilet_use_started event at actual use point")
            duration = detail_field(detail, "duration")
            typed_duration = event.get("toiletUseDurationSeconds", 0)
            episode["toiletUseDuration"] = typed_duration if typed_duration > 0 else float(duration) if duration else None
        elif kind == "toilet_use_completed":
            episode["toiletUseCompletedAt"] = now
        elif kind == "toilet_use_interrupted":
            episode["toiletUseInterruptedAt"] = now

    state_journeys = collections.defaultdict(set)
    seated_journeys = collections.defaultdict(set)
    trajectory_rows = 0
    for row in records(directory / "trajectories.jsonl"):
        trajectory_rows += 1
        jid, now = row["agentId"], row["time"]
        activity, state = row.get("activity"), row.get("execution")
        state_journeys[(activity, state)].add(jid)
        if row.get("isSeated"):
            seated_journeys[row.get("seatedActivity", "Unknown")].add(jid)
        episode = episode_at(jid, now)
        if episode is None or activity != episode["activity"]:
            continue
        episode["states"].add(state)
        # Ready retains the old CurrentActivity after completion and therefore
        # does not indicate a new visit. Require its claimed POI for commitment.
        if row.get("targetPoi") == episode["poi"] and state not in ("Ready", "Regrouping", "Scheduled"):
            episode["committed"] = True
        if state in ("Using", "WCUsing"):
            mark_use(episode, now, "Using trajectory sample")
        if row.get("isSeated"):
            mark_use(episode, now, "Seated trajectory sample")
            episode["seatedActivity"] = row.get("seatedActivity", "Unknown")

    flat = [episode for episodes in by_journey.values() for episode in episodes if episode["activity"] in ACTIVITIES]
    activity_report = {}
    for activity in ACTIVITIES:
        visits = [episode for episode in flat if episode["activity"] == activity]
        interrupted = collections.Counter(visit["interruption"] for visit in visits if visit["interruptedAt"] is not None)
        # Board is the final executor transition, usually not a policy choice.
        board_events = event_counts["boarded"] if activity == "Board" else 0
        activity_report[activity] = {
            "policySelections": selections[activity], "selectedDistinctJourneys": len(selected_journeys[activity]),
            "positiveProbabilityOptions": options_positive[activity], "feasibleDistinctJourneys": len(positive_journeys[activity]),
            "committedEpisodesEvidenced": sum(visit["committed"] for visit in visits),
            "actualUseStartEpisodesEvidenced": sum(visit["useStartedAt"] is not None for visit in visits),
            "counterFulfilledEpisodes": sum(visit["counterCompletedAt"] is not None for visit in visits),
            "fulfilledUseEpisodesEvidenced": sum(visit["useStartedAt"] is not None or visit["counterCompletedAt"] is not None or visit["completedAt"] is not None for visit in visits),
            "activityCompletedEpisodes": sum(visit["completedAt"] is not None for visit in visits),
            "completedWithoutSampledStartEpisodes": sum(visit["completedAt"] is not None and visit["useStartedAt"] is None for visit in visits),
            "activityInterruptedEpisodes": sum(visit["interruptedAt"] is not None for visit in visits),
            "unfinishedSelectedEpisodesAtCaptureEnd": sum(visit["completedAt"] is None and visit["interruptedAt"] is None and
                not visit["boarded"] and "nextDecisionAt" not in visit and visit["journeyId"] not in endings for visit in visits),
            "noExecutionEvidenceEpisodes": sum(not visit["committed"] and visit["interruptedAt"] is None for visit in visits),
            "actualSeatedEpisodes": sum(visit["seatedActivity"] is not None for visit in visits),
            "validatedPlatformPassages": sum(visit["platformPassed"] for visit in visits),
            "coachBoardingServiceStarts": sum(visit["coachStarted"] for visit in visits),
            "toiletUseCompletedEpisodes": sum(visit["toiletUseCompletedAt"] is not None for visit in visits),
            "toiletUseInterruptedEpisodes": sum(visit["toiletUseInterruptedAt"] is not None for visit in visits),
            "boardedEvents": board_events,
            "distinctJourneysObservedByExecutionState": {state: len(jids) for (observed_activity, state), jids in state_journeys.items() if observed_activity == activity},
            "interruptionReasons": dict(interrupted), "optionEvaluationReasons": dict(reasons[activity]),
            "knownPlatformNonInitialLiveDecisionCostSeconds": {field: statistics_report(values) for field, values in known_active_costs[activity].items()}}
    for episode in flat:
        episode["states"] = sorted(episode["states"])
        episode["useStartEvidence"] = sorted(episode["useStartEvidence"])
        end = endings.get(episode["journeyId"])
        episode["journeyOutcomeAtCaptureEnd"] = end.get("outcome", "Unserved") if end else "ActiveAtEnd"
    after_hashes = {name: digest(directory / name) for name in INPUTS}
    if hashes != after_hashes:
        raise ValueError("Capture input changed while summarizing; no frozen evidence report produced.")
    wc_durations = [episode["toiletUseDuration"] for episode in flat if episode["toiletUseDuration"] is not None]
    wc_elapsed = [episode["toiletUseCompletedAt"] - episode["useStartedAt"] for episode in flat
                  if episode["toiletUseCompletedAt"] is not None and episode["useStartedAt"] is not None]
    actor_seconds += max(0.0, last_time - last_population_time) * last_population_count
    return {"generatedUtc": datetime.now(timezone.utc).isoformat(), "label": label, "capture": str(directory),
        "scope": "Natural activity selection and execution evidence; descriptive, not a substitute for integrity validation.",
        "closedCapture": run_end is not None, "lastEventSeconds": last_time, "runEndedSeconds": run_end,
        "operatingDate": manifest.get("operatingDate"), "arrivalSettings": manifest.get("arrivalSettings"),
        "plannedJourneyActors": len(manifest.get("scheduledJourneys", [])), "actorSlotCount": manifest.get("actorSlotCount"),
        "enteredActors": len(entered), "peakConcurrentActors": active_peak,
        "timeWeightedMeanConcurrentActors": actor_seconds / last_time if last_time > 0 else 0,
        "actorSecondsObserved": actor_seconds,
        "decisionCount": decisions, "noChoiceDecisions": sum(no_choices.values()), "trajectoryRows": trajectory_rows,
        "eventCounts": dict(event_counts), "activities": activity_report,
        "toiletPhysicalQueue": {"joinEvents": event_counts["toilet_queue_joined"], "reserveEvents": event_counts["toilet_use_reserved"],
            "actualUseStartedEvents": event_counts["toilet_use_started"], "useCompletedEvents": event_counts["toilet_use_completed"],
            "useInterruptedEvents": event_counts["toilet_use_interrupted"], "releaseEvents": event_counts["toilet_use_released"],
            "peakWaitingActorsFromEvents": wc_max_waiting, "peakReservedAndOccupiedPlacesFromEvents": wc_max_leases,
            "peakReportedWaitingActors": wc_max_reported_waiting, "peakReportedOccupiedPlaces": wc_max_reported_uses,
            "fifoMismatchCount": len(wc_fifo_issues), "fifoMismatchExamples": wc_fifo_issues[:20],
            "physicalJoinOffsetMeters": statistics_report(wc_arrival_offsets), "predecessorGapMeters": statistics_report(wc_predecessor_gaps),
            "waitingJourneyIdsAtCaptureEnd": wc_waiting, "useLeasesAtCaptureEnd": wc_leases},
        "toiletUseDurationSeconds": {"sampled": statistics_report(wc_durations), "completedElapsed": statistics_report(wc_elapsed),
            "allObservedSampledDurationsWithin120To240": all(120 <= duration <= 240 for duration in wc_durations) if wc_durations else None},
        "seatedUse": {activity: {"startedEvents": seated_start_counts[activity], "completedEvents": seated_end_counts[activity],
            "distinctJourneysObservedSeated": len(seated_journeys[activity])} for activity in ("Coffee", "Reading", "Rest")},
        "unmatchedRelevantEventCount": len(unmatched), "unmatchedRelevantEventExamples": unmatched[:20],
        "notes": ["A policy choice precedes POI claim; selected intent alone does not prove an executed activity.",
            "First Using trajectory timestamp is sampled evidence, not the exact unsampled runtime transition.",
            "Service trajectory state is a visit request, not proof that the passenger is already being served at the counter.",
            "Older captures prove counter fulfillment via counter_service_completed without a service-start timestamp.",
            "Platform completion is validated passage then coach boarding, rather than an activity_completed event.",
            "Board normally transitions directly in the executor; no Board policy selection is expected.",
            "An activity still running when the capture ends is censored, not failed.",
            "Visible actor counts are independent of represented passenger weights."],
        "inputSha256": hashes, "inputsUnchangedDuringAnalysis": True, "episodes": flat}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--label", default="natural activity evidence")
    args = parser.parse_args()
    output = args.output.resolve()
    if output == args.capture.resolve() or args.capture.resolve() in output.parents:
        parser.error("Output must be outside capture inputs.")
    result = summarize(args.capture, args.label)
    if not result["closedCapture"]:
        parser.error("Capture must be closed (run_ended event) to summarize frozen evidence.")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "seconds": result["lastEventSeconds"],
        "coverage": {activity: {key: values[key] for key in ("policySelections", "actualUseStartEpisodesEvidenced", "counterFulfilledEpisodes", "activityCompletedEpisodes", "validatedPlatformPassages", "boardedEvents")}
                     for activity, values in result["activities"].items()},
        "inputsUnchanged": result["inputsUnchangedDuringAnalysis"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
