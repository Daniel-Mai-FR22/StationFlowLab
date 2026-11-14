"""Validate a completed TrainStation DecisionLab log using only Python's stdlib.

Usage:
    python Tests/validate_station_decision_capture.py Captures/TrainStationDecisionLab/run-... --output Reports/TrainStationDecisionLab/capture-validation.json

Checks recorded decisions, identities, complete synchronized sample groups, boarding
gates and sampled trajectory continuity. Instantaneous native velocity spikes are
reported as quality warnings, without clamping data. This does not certify a per-frame
speed cap, human realism, camera visibility or that every passenger caught a train.
Stop Play and flush the capture before running; this is not an active-log monitor.
"""
from __future__ import annotations

import argparse
from bisect import bisect_left
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import statistics
import sys


ACTIVITIES = ("Display", "Cafe", "Toilet", "Shop", "Rest", "Info", "Platform", "Exit", "Board")
TRAIN_STATUSES = ("Scheduled", "Delayed", "Cancelled", "Boarding", "Closed", "Departed")
TERMINAL_STATES = {"Boarded", "Exited", "Failed"}
EXECUTION_STATES = {"Ready", "Walking", "Using", "Service", "WaitingForTrain", "ClearingCounter",
                    "WalkingToSeat", "SittingDown", "Seated", "StandingUp"} | TERMINAL_STATES
SEATED_ACTIVITIES = ("None", "Coffee", "Reading", "Rest")
SEAT_FIELDS = ("seatId", "seatedActivity", "isSeated", "seatedSecondsRemaining")
AGENT_EVENTS = {"announcement_received", "activity_completed", "activity_interrupted", "boarded", "journey_ended",
                "counter_service_completed", "counter_clearance_started", "seat_reserved", "seat_state_changed",
                "seat_released", "seated_activity_skipped", "platform_reapproach"}
EVENT_KINDS = AGENT_EVENTS | {"run_started", "run_ended", "pause", "Delay", "Cancellation"}
INPUT_FILES = ("manifest.json", "decisions.jsonl", "trajectories.jsonl", "events.jsonl")
TIME_TOLERANCE = 1e-4
PROBABILITY_TOLERANCE = 1e-6
SPEED_FACTOR = 1.5
POSITION_ALLOWANCE_METRES = .25


def reject_constant(value):
    raise ValueError(f"Non-finite JSON constant {value}")


def unique_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key {key!r}")
        result[key] = value
    return result


def finite_tree(value, location="$"):
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError(f"{location}: non-finite JSON number")
    if isinstance(value, dict):
        for key, item in value.items():
            finite_tree(item, f"{location}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            finite_tree(item, f"{location}[{index}]")


def decode(text):
    value = json.loads(text, parse_constant=reject_constant, object_pairs_hook=unique_keys)
    finite_tree(value)
    if not isinstance(value, dict):
        raise ValueError("Expected a JSON object")
    return value


def number(value):
    return type(value) in (int, float) and math.isfinite(value)


def enum_name(value, names):
    if isinstance(value, str) and value in names:
        return value
    if type(value) is int and 0 <= value < len(names):
        return names[value]
    return None


def vector(value):
    if isinstance(value, dict) and all(number(value.get(axis)) for axis in ("x", "y", "z")):
        return tuple(value[axis] for axis in ("x", "y", "z"))
    raise ValueError("Expected finite position/velocity object with x, y, z")


def distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


class Audit:
    def __init__(self, directory, require_finished=False):
        self.directory = directory
        self.require_finished = require_finished
        self.errors = []
        self.warnings = []
        self.error_count = 0
        self.checks = 0
        self.counts = Counter()
        self.profiles = {}
        self.services = {}
        self.pois = {}
        self.samples = defaultdict(list)
        self.events = []
        self.decisions = []
        self.sample_times = []
        self.manifest = {}
        self.maximum_step = 0.0
        self.maximum_measured_speed = 0.0
        self.maximum_sampled_speed = 0.0
        self.velocity_spike_count = 0
        self.velocity_spikes = []
        self.boarding_checks = 0
        self.seat_samples = 0
        self.seated_samples = 0

    def seat_extension(self, record, where, claims):
        """Optional additive v1 fields: old captures remain valid without them."""
        if not any(field in record for field in SEAT_FIELDS):
            self.check(record.get("execution") not in {"WalkingToSeat", "SittingDown", "Seated", "StandingUp"},
                       f"{where}: seat execution state lacks seat evidence")
            return
        self.seat_samples += 1
        self.check(all(field in record for field in SEAT_FIELDS), f"{where}: incomplete seat extension")
        seat_id = record.get("seatId")
        seated_activity = enum_name(record.get("seatedActivity"), SEATED_ACTIVITIES)
        self.check(isinstance(seat_id, str), f"{where}: seatId must be a string")
        self.check(seated_activity in SEATED_ACTIVITIES, f"{where}: invalid seatedActivity")
        self.check(type(record.get("isSeated")) is bool, f"{where}: isSeated must be Boolean")
        remaining = record.get("seatedSecondsRemaining")
        self.check(number(remaining) and remaining >= 0, f"{where}: invalid seatedSecondsRemaining")
        self.check(record.get("isSeated") == (record.get("execution") == "Seated"),
                   f"{where}: seated flag disagrees with execution state")
        if record.get("isSeated"):
            self.seated_samples += 1
        if record.get("execution") in {"WalkingToSeat", "SittingDown", "Seated", "StandingUp"}:
            self.check(bool(seat_id) and seated_activity not in {None, "None"}, f"{where}: seated actor lacks reserved seat/activity")
        if record.get("finished"):
            self.check(not seat_id and not record.get("isSeated"), f"{where}: terminal actor retains a seat")
        if isinstance(seat_id, str) and seat_id:
            self.check(seat_id not in claims, f"{where}: duplicate seat reservation {seat_id!r} in one sample group")
            claims.add(seat_id)

    def check(self, condition, message):
        self.checks += 1
        if not condition:
            self.error_count += 1
            if len(self.errors) < 100:
                self.errors.append(message)
        return bool(condition)

    def records(self, filename):
        path = self.directory / filename
        try:
            with path.open(encoding="utf-8-sig") as stream:
                for index, line in enumerate(stream, 1):
                    where = f"{filename}:{index}"
                    try:
                        value = decode(line)
                    except (ValueError, TypeError, OverflowError) as error:
                        self.check(False, f"{where}: {error}")
                        continue
                    self.counts[filename] += 1
                    yield where, value
        except OSError as error:
            self.check(False, f"{filename}: {error}")

    def needs(self, value, where):
        if not self.check(isinstance(value, dict), f"{where}: missing needs"):
            return
        for name in ("thirst", "toilet", "fatigue", "shopping", "information"):
            self.check(number(value.get(name)) and 0 <= value[name] <= 1, f"{where}.{name}: expected value in [0, 1]")

    def time(self, record, where):
        value = record.get("time")
        if not self.check(number(value) and value >= 0, f"{where}: invalid simulation timestamp"):
            return None
        return value

    def agent(self, record, where):
        value = record.get("agentId")
        self.check(value in self.profiles, f"{where}: unknown agentId {value!r}")
        return value

    def load_manifest(self):
        try:
            self.manifest = decode((self.directory / "manifest.json").read_text(encoding="utf-8-sig"))
            m = self.manifest
            self.check(m.get("schema") == "stationflow.station_decision_lab.v1", "manifest: unsupported schema")
            self.check(type(m.get("seed")) is int, "manifest: seed must be an integer")
            self.check(number(m.get("sampleRate")) and m["sampleRate"] > 0, "manifest: sampleRate must be positive")
            self.check(m.get("sharedRailRate") == 1, "manifest: railway and passenger seconds must use rate 1")
            self.check(number(m.get("congestionAllowance")) and m["congestionAllowance"] >= 1, "manifest: invalid congestionAllowance")
            for service in m["services"]:
                service_id = service["serviceId"]
                self.check(isinstance(service_id, str) and bool(service_id), "manifest: blank serviceId")
                self.check(service_id not in self.services, f"manifest: duplicate serviceId {service_id}")
                self.services[service_id] = service
                self.check(number(service.get("departureOffsetSeconds")) and number(service.get("doorDurationSeconds")) and service["doorDurationSeconds"] > 0, f"manifest: invalid times for {service_id}")
            for poi in m["pois"]:
                poi_id = poi["poiId"]
                self.check(isinstance(poi_id, str) and bool(poi_id), "manifest: blank poiId")
                self.check(poi_id not in self.pois, f"manifest: duplicate poiId {poi_id}")
                self.pois[poi_id] = poi
                self.check(poi.get("activity") in ACTIVITIES, f"manifest: invalid POI activity for {poi_id}")
                vector(poi["position"])
            for profile in m["profiles"]:
                agent_id = profile["agentId"]
                self.check(isinstance(agent_id, str) and bool(agent_id), "manifest: blank agentId")
                self.check(agent_id not in self.profiles, f"manifest: duplicate agentId {agent_id}")
                self.profiles[agent_id] = profile
                self.check(profile.get("serviceId") in self.services, f"manifest: unknown service for {agent_id}")
                self.check(type(profile.get("occurrence")) is int and profile["occurrence"] == 0, f"manifest: unexpected trip occurrence for {agent_id}")
                for field in ("walkSpeed", "fastWalkSpeed"):
                    self.check(number(profile.get(field)) and profile[field] > 0, f"manifest: {agent_id} has invalid {field}")
                for field in ("startDelay", "safetyMargin"):
                    self.check(number(profile.get(field)) and profile[field] >= 0, f"manifest: {agent_id} has invalid {field}")
                self.check(isinstance(profile.get("policyParameters"), dict), f"manifest: missing policy parameters for {agent_id}")
                self.check(type(profile.get("platformKnownInitially")) is bool, f"manifest: missing initial knowledge for {agent_id}")
                self.needs(profile.get("needs"), f"manifest.profiles[{agent_id}]")
                vector(profile["position"])
            self.check(bool(self.profiles), "manifest: no profiles")
            self.check(type(m.get("participantCount")) is int and m["participantCount"] == len(self.profiles), "manifest: participantCount disagrees with unique profiles")
            self.check(isinstance(m.get("scenarioEvents"), list), "manifest: missing scenario events")
        except (OSError, ValueError, KeyError, TypeError, OverflowError) as error:
            self.check(False, f"manifest: {error}")

    def load_events(self):
        previous_time = -1.0
        per_agent_time = {}
        terminal_events = Counter()
        for where, event in self.records("events.jsonl"):
            try:
                timestamp = self.time(event, where)
                if timestamp is None:
                    continue
                self.check(timestamp >= previous_time, f"{where}: event timestamp moved backwards")
                previous_time = timestamp
                kind = event.get("kind")
                self.check(kind in EVENT_KINDS, f"{where}: unknown event kind {kind!r}")
                self.check(isinstance(event.get("detail"), str), f"{where}: missing event detail")
                agent_id = event.get("agentId")
                service_id = event.get("serviceId")
                if kind in AGENT_EVENTS:
                    self.agent(event, where)
                    self.check(timestamp >= per_agent_time.get(agent_id, -1), f"{where}: agent event time moved backwards")
                    per_agent_time[agent_id] = timestamp
                    if agent_id in self.profiles:
                        self.check(service_id == self.profiles[agent_id]["serviceId"], f"{where}: event changed assigned service")
                if service_id:
                    self.check(service_id in self.services, f"{where}: unknown event service {service_id!r}")
                actual_status = event.get("actualStatus")
                cutoff, departure = event.get("actualCutoff"), event.get("actualDeparture")
                if service_id and actual_status is not None:
                    self.check(actual_status in TRAIN_STATUSES, f"{where}: invalid actualStatus")
                    self.check(number(cutoff) and number(departure) and cutoff < departure, f"{where}: invalid actual train times")
                if kind in {"Delay", "Cancellation"}:
                    self.check(bool(service_id) and not agent_id, f"{where}: disruption must identify a service, not an agent")
                    if kind == "Cancellation" and actual_status is not None:
                        self.check(actual_status == "Cancelled", f"{where}: cancellation snapshot is not Cancelled")
                if kind in {"boarded", "journey_ended"}:
                    terminal_events[agent_id] += 1
                    self.check(terminal_events[agent_id] == 1, f"{where}: repeated terminal event for {agent_id}")
                if kind == "platform_reapproach":
                    self.check(terminal_events[agent_id] == 0, f"{where}: platform reapproach after terminal journey")
                if kind == "boarded":
                    self.boarding_checks += 1
                    self.check(actual_status == "Boarding", f"{where}: boarding lacks actualStatus=Boarding evidence")
                    self.check(number(cutoff) and timestamp < cutoff, f"{where}: boarding was not strictly before actualCutoff")
                    self.check(number(departure) and number(cutoff) and cutoff < departure, f"{where}: boarding lacks valid actual departure/cutoff")
                self.events.append(event)
            except (ValueError, KeyError, TypeError, OverflowError) as error:
                self.check(False, f"{where}: {error}")
        starts = [event for event in self.events if event["kind"] == "run_started"]
        self.check(len(starts) == 1, "events: expected exactly one run_started event")
        if starts:
            self.check(starts[0]["time"] <= TIME_TOLERANCE, "events: run_started is not at simulation time zero")
        if not any(event["kind"] == "run_ended" for event in self.events):
            self.warnings.append("No run_ended marker; externally confirm Play stopped and log files were flushed.")

    def load_decisions(self):
        last_time, last_index = {}, {}
        for where, decision in self.records("decisions.jsonl"):
            try:
                agent_id = self.agent(decision, where)
                timestamp = self.time(decision, where)
                if timestamp is None:
                    continue
                self.check(timestamp >= last_time.get(agent_id, -1), f"{where}: agent decision time moved backwards")
                last_time[agent_id] = timestamp
                index = decision.get("decisionIndex")
                self.check(type(index) is int and index == last_index.get(agent_id, 0) + 1, f"{where}: non-contiguous decisionIndex")
                if type(index) is int:
                    last_index[agent_id] = index
                context = decision["context"]
                train_status = enum_name(context.get("trainStatus"), TRAIN_STATUSES)
                self.check(train_status in TRAIN_STATUSES, f"{where}: invalid evaluated train status")
                self.check(enum_name(context.get("previousActivity"), ACTIVITIES) in ACTIVITIES, f"{where}: invalid evaluated previous activity")
                self.check(number(context.get("nowSeconds")) and abs(context["nowSeconds"] - timestamp) <= TIME_TOLERANCE, f"{where}: policy clock differs from logged time")
                self.check(train_status == decision.get("observedStatus"), f"{where}: logged status differs from evaluated context")
                self.check(enum_name(context.get("previousActivity"), ACTIVITIES) == decision.get("previousActivity"), f"{where}: previousActivity differs from context")
                self.check(number(context.get("boardingCutoffSeconds")) and number(decision.get("observedCutoff")) and abs(context["boardingCutoffSeconds"] - decision["observedCutoff"]) <= TIME_TOLERANCE, f"{where}: cutoff differs from evaluated context")
                self.check(context.get("platformKnown") == decision.get("platformKnown"), f"{where}: knowledge differs from evaluated context")
                self.check(context.get("needs") == decision.get("needs"), f"{where}: needs differ from evaluated context")
                if "requireInitialDisplay" in context:
                    self.check(type(context["requireInitialDisplay"]) is bool, f"{where}: requireInitialDisplay must be Boolean")
                replanning = train_status in {"Cancelled", "Closed", "Departed"} or (
                    number(context.get("boardingCutoffSeconds")) and timestamp >= context["boardingCutoffSeconds"])
                display_required = context.get("requireInitialDisplay", False) and not replanning
                self.needs(decision.get("needs"), where)
                inputs, options = decision["inputOptions"], decision["options"]
                self.check(isinstance(inputs, list) and isinstance(options, list) and len(inputs) == len(options), f"{where}: input/evaluation option count differs")
                if not replanning and context.get("platformKnown") is True and not context.get("requireInitialDisplay", False):
                    direct_costs = []
                    for candidate in inputs:
                        candidate_activity = enum_name(candidate.get("activity"), ACTIVITIES)
                        if candidate_activity not in {"Platform", "Board"}: continue
                        if candidate_activity == "Board" and not (train_status == "Boarding" and
                            enum_name(context.get("previousActivity"), ACTIVITIES) in {"Platform", "Board"}): continue
                        costs = [candidate.get(k, 0) for k in ("travelSeconds", "queueSeconds", "durationSeconds",
                            "seatTravelSeconds", "seatingTransitionSeconds", "seatedDurationSeconds")]
                        onward, preference = candidate.get("toPlatformSeconds"), candidate.get("preference")
                        if candidate.get("available") is True and candidate.get("reachable") is True and number(preference) and preference > 0 and \
                            number(onward) and onward >= 0 and all(number(v) and v >= 0 for v in costs) and number(sum(costs)):
                            direct_costs.append(sum(costs))
                    if direct_costs and all(cost >= context["boardingCutoffSeconds"] - timestamp for cost in direct_costs):
                        replanning = True
                total = 0.0
                positives = 0
                chosen = []
                seen = set()
                for position, (source, option) in enumerate(zip(inputs, options)):
                    label = f"{where}.options[{position}]"
                    activity, poi_id = option.get("activity"), option.get("poiId")
                    key = (activity, poi_id)
                    self.check(key not in seen, f"{label}: duplicate candidate")
                    seen.add(key)
                    self.check(activity in ACTIVITIES and poi_id in self.pois, f"{label}: unknown activity/POI")
                    self.check(enum_name(source.get("activity"), ACTIVITIES) == activity and source.get("poiId") == poi_id, f"{label}: evaluated option does not match its input")
                    probability = option.get("probability")
                    if not self.check(number(probability) and 0 <= probability <= 1, f"{label}: invalid probability"):
                        continue
                    total += probability
                    positives += probability > 0
                    if display_required and activity != "Display":
                        self.check(probability == 0, f"{label}: initial display requirement gave another activity positive probability")
                    for field in ("reachable", "available"):
                        self.check(type(source.get(field)) is bool, f"{label}: missing input {field}")
                    preference = source.get("preference")
                    self.check(number(preference), f"{label}: missing input preference")
                    if not source.get("reachable") or not source.get("available") or not number(preference) or preference <= 0:
                        self.check(probability == 0, f"{label}: unreachable/unavailable/zero-preference choice has positive probability")
                    if replanning and activity not in {"Info", "Exit"}:
                        self.check(probability == 0, f"{label}: replanning must seek information or leave")
                    for input_name, output_name in (("travelSeconds", "travel"), ("queueSeconds", "queue"), ("durationSeconds", "dwell"), ("toPlatformSeconds", "toPlatform")):
                        a, b = source.get(input_name), option.get(output_name)
                        self.check(number(a) and a >= 0 and number(b) and abs(a - b) <= 1e-6, f"{label}: invalid or inconsistent {input_name}")
                    seat_costs = (("seatTravelSeconds", "seatTravel"), ("seatingTransitionSeconds", "seatingTransition"),
                                  ("seatedDurationSeconds", "seatedDwell"))
                    if any(a in source or b in option for a, b in seat_costs):
                        for input_name, output_name in seat_costs:
                            a, b = source.get(input_name), option.get(output_name)
                            self.check(number(a) and a >= 0 and number(b) and abs(a - b) <= 1e-6,
                                       f"{label}: invalid or inconsistent {input_name}")
                        if probability > 0 and not replanning:
                            cost_fields = ("travelSeconds", "queueSeconds", "durationSeconds", "seatTravelSeconds",
                                           "seatingTransitionSeconds", "seatedDurationSeconds")
                            costs = [source.get(field) for field in cost_fields]
                            onward = 0 if activity in {"Platform", "Board"} else source.get("toPlatformSeconds")
                            safety = context.get("safetyMarginSeconds")
                            if all(number(value) for value in costs + [onward, safety, context.get("boardingCutoffSeconds")]):
                                expected_slack = context["boardingCutoffSeconds"] - timestamp - sum(costs) - onward - safety
                                self.check(number(option.get("slack")) and abs(option["slack"] - expected_slack) <= 1e-5,
                                           f"{label}: slack omits or misstates seated visit costs")
                                if activity not in {"Platform", "Board"}:
                                    self.check(expected_slack >= -TIME_TOLERANCE, f"{label}: seated visit selected despite insufficient total budget")
                    if activity == decision.get("selectedActivity") and poi_id == decision.get("selectedPoi"):
                        chosen.append(probability)
                self.check(abs(total - (1 if positives else 0)) <= PROBABILITY_TOLERANCE, f"{where}: probabilities sum to {total}, expected {'1' if positives else '0'}")
                if decision.get("selectedActivity") == "None":
                    self.check(not positives and not decision.get("selectedPoi"), f"{where}: no choice despite positive probabilities, or nonempty selectedPoi")
                else:
                    self.check(len(chosen) == 1 and chosen[0] > 0, f"{where}: selected activity/POI lacks a unique positive-probability option")
                    if display_required:
                        self.check(decision["selectedActivity"] == "Display", f"{where}: normal journey skipped required initial display")
                self.decisions.append(decision)
            except (ValueError, KeyError, TypeError, OverflowError) as error:
                self.check(False, f"{where}: {error}")
        self.check(bool(self.decisions), "decisions: no valid records")

    def load_trajectories(self):
        group_time = None
        group_ids = set()
        group_seats = set()
        expected_ids = set(self.profiles)
        previous = {}

        def finish_group():
            self.check(group_ids == expected_ids, f"trajectories at {group_time}: sample group IDs differ; missing={sorted(expected_ids - group_ids)}, extra={sorted(group_ids - expected_ids)}")
            self.sample_times.append(group_time)

        for where, record in self.records("trajectories.jsonl"):
            try:
                timestamp = self.time(record, where)
                agent_id = self.agent(record, where)
                if timestamp is None or agent_id not in self.profiles:
                    continue
                if group_time is None:
                    group_time = timestamp
                elif timestamp != group_time:
                    finish_group()
                    self.check(timestamp > group_time, f"{where}: sample group time did not increase")
                    group_time, group_ids, group_seats = timestamp, set(), set()
                self.check(agent_id not in group_ids, f"{where}: duplicate agent within timestamp group")
                group_ids.add(agent_id)
                profile = self.profiles[agent_id]
                self.check(record.get("serviceId") == profile["serviceId"] and record.get("occurrence") == profile["occurrence"], f"{where}: assigned service/occurrence changed")
                self.check(record.get("activity") in ACTIVITIES and record.get("execution") in EXECUTION_STATES, f"{where}: invalid activity/execution state")
                self.seat_extension(record, where, group_seats)
                self.check(record.get("knownStatus") in TRAIN_STATUSES, f"{where}: invalid known train status")
                self.check(number(record.get("budget")), f"{where}: invalid budget")
                self.check(not record.get("targetPoi") or record["targetPoi"] in self.pois, f"{where}: unknown target POI")
                for field in ("finished", "boarded", "visibleActor"):
                    self.check(type(record.get(field)) is bool, f"{where}: {field} must be Boolean")
                position, velocity = vector(record["position"]), vector(record["velocity"])
                speed = distance(velocity, (0, 0, 0))
                limit = max(profile["walkSpeed"], profile["fastWalkSpeed"]) * SPEED_FACTOR
                self.maximum_measured_speed = max(self.maximum_measured_speed, speed)
                # Native velocity is an instantaneous solver output. The 5 Hz positions
                # cannot establish its frame duration or a hard per-frame speed cap.
                # Keep it unchanged and report quality exceedances separately from
                # hard sampled-position continuity failures below.
                if speed > limit + .25:
                    self.velocity_spike_count += 1
                    if len(self.velocity_spikes) < 20:
                        self.velocity_spikes.append({"record": where, "agentId": agent_id,
                            "time": timestamp, "rawSpeed": speed, "profileSanityBound": limit + .25,
                            "activity": record["activity"], "execution": record["execution"]})
                sample = {"time": timestamp, "position": position, "velocity": velocity,
                          "finished": record["finished"], "boarded": record["boarded"],
                          "execution": record["execution"], "visible": record["visibleActor"], "activity": record["activity"]}
                terminal = record["execution"] in TERMINAL_STATES
                self.check(record["finished"] == terminal, f"{where}: finished flag disagrees with terminal execution state")
                self.check(record["boarded"] == (record["execution"] == "Boarded"), f"{where}: boarded flag disagrees with execution state")
                if terminal:
                    self.check(not record.get("targetPoi"), f"{where}: terminal actor retains an activity target")
                if record["boarded"]:
                    self.check(record["activity"] == "Board" and not record["visibleActor"], f"{where}: boarded actor must have Board activity and be inactive")
                if record["execution"] == "Exited":
                    self.check(record["activity"] == "Exit" and not record["visibleActor"], f"{where}: Exited actor has no Exit activity or is still active")
                old = previous.get(agent_id)
                if old is not None:
                    dt = timestamp - old["time"]
                    self.check(dt > 0, f"{where}: per-agent trajectory timestamp did not increase")
                    moved = distance(position, old["position"])
                    self.maximum_step = max(self.maximum_step, moved)
                    if dt > 0:
                        self.maximum_sampled_speed = max(self.maximum_sampled_speed, moved / dt)
                    allowance = limit * max(dt, 0) + POSITION_ALLOWANCE_METRES
                    self.check(dt > 0 and moved <= allowance, f"{where}: sampled displacement {moved:.3f} m over {dt:.4f} s exceeds continuity bound {allowance:.3f} m")
                    if old["finished"]:
                        self.check(all(sample[field] == old[field] for field in ("finished", "boarded", "execution", "visible", "activity")), f"{where}: terminal state changed or actor reappeared")
                        self.check(moved <= .001 and speed <= .01, f"{where}: terminal actor continued moving")
                else:
                    initial_distance = distance(position, vector(profile["position"]))
                    self.check(initial_distance <= limit * timestamp + POSITION_ALLOWANCE_METRES, f"{where}: initial trajectory jumps from manifest position")
                previous[agent_id] = sample
                self.samples[agent_id].append(sample)
            except (ValueError, KeyError, TypeError, OverflowError) as error:
                self.check(False, f"{where}: {error}")
        if group_time is not None:
            finish_group()
        self.check(len(self.sample_times) >= 2, "trajectories: need at least two complete timestamp groups")
        self.check(set(self.samples) == expected_ids, "trajectories: retained participants differ from manifest")

    def cross_checks(self):
        terminal_by_agent = {event["agentId"]: event for event in self.events if event["kind"] in {"boarded", "journey_ended"}}
        for agent_id, samples in self.samples.items():
            times = [sample["time"] for sample in samples]
            terminal_samples = [sample for sample in samples if sample["finished"]]
            event = terminal_by_agent.get(agent_id)
            if terminal_samples:
                self.check(event is not None, f"{agent_id}: terminal trajectory lacks terminal event")
            if event is not None:
                t = event["time"]
                self.check(samples[-1]["time"] + TIME_TOLERANCE >= t, f"{agent_id}: no trajectory sample at/after terminal event")
                index = bisect_left(times, t - TIME_TOLERANCE)
                if index < len(samples):
                    after = samples[index]
                    self.check(after["finished"], f"{agent_id}: first sample after terminal event is not terminal")
                    self.check(after["boarded"] == (event["kind"] == "boarded"), f"{agent_id}: boarding event and trajectory disagree")
                    if index > 0:
                        before = samples[index - 1]
                        self.check(not before["finished"] or before["time"] >= t - TIME_TOLERANCE, f"{agent_id}: trajectory became terminal before event")
                        # Adjacent samples across the boarding event use the same profile motion bound.
                        # No claim is made about paths between samples.
                        limit = max(self.profiles[agent_id]["walkSpeed"], self.profiles[agent_id]["fastWalkSpeed"]) * SPEED_FACTOR
                        self.check(distance(after["position"], before["position"]) <= limit * (after["time"] - before["time"]) + POSITION_ALLOWANCE_METRES, f"{agent_id}: terminal transition exceeds sampled continuity bound")
                if event["kind"] == "boarded" and terminal_samples:
                    service = self.services[self.profiles[agent_id]["serviceId"]]
                    platforms = [poi for poi in self.pois.values() if poi["activity"] == "Platform" and poi.get("track") == service.get("trackNumber")]
                    self.check(bool(platforms), f"{agent_id}: no manifest platform for boarded service")
                    if platforms:
                        location = terminal_samples[0]["position"]
                        self.check(any(distance(location, vector(poi["position"])) <= poi.get("spreadRadius", 0) + 1.05 for poi in platforms), f"{agent_id}: boarding was away from assigned platform anchor")
            if self.require_finished:
                self.check(samples[-1]["finished"], f"{agent_id}: journey unfinished at capture end")
        for decision in self.decisions:
            event = terminal_by_agent.get(decision["agentId"])
            if event is not None:
                self.check(decision["time"] <= event["time"] + TIME_TOLERANCE, f"{decision['agentId']}: decision after journey ended")
        for event in self.events:
            terminal = terminal_by_agent.get(event.get("agentId"))
            if terminal is not None:
                self.check(event["time"] <= terminal["time"] + TIME_TOLERANCE, f"{event['agentId']}: activity event after journey ended")
        if self.sample_times:
            start, end = self.sample_times[0], self.sample_times[-1]
            for decision in self.decisions:
                self.check(start - TIME_TOLERANCE <= decision["time"] <= end + TIME_TOLERANCE, f"{decision['agentId']}: decision lies outside sampled interval")

    def run(self):
        self.load_manifest()
        if self.profiles and self.services and self.pois:
            self.load_events()
            self.load_decisions()
            self.load_trajectories()
            try:
                self.cross_checks()
            except (ValueError, KeyError, TypeError, OverflowError) as error:
                self.check(False, f"cross-file checks: {error}")
        if self.velocity_spike_count:
            self.warnings.append(f"{self.velocity_spike_count} instantaneous native velocity samples exceeded the profile sanity bound; raw maximum {self.maximum_measured_speed:.6f} m/s. Values were preserved. Sampled continuity is checked independently; this does not certify a per-frame speed cap.")
        final = [values[-1] for values in self.samples.values() if values]
        intervals = [b - a for a, b in zip(self.sample_times, self.sample_times[1:])]
        return {
            "schema": "stationflow.station_decision_lab.validation.v1",
            "directory": str(self.directory), "checkedUtc": datetime.now(timezone.utc).isoformat(),
            "success": self.error_count == 0, "errorCount": self.error_count,
            "errors": self.errors, "warnings": self.warnings, "checks": self.checks,
            "participants": len(self.profiles), "recordCounts": dict(self.counts),
            "sampleGroups": len(self.sample_times),
            "firstTime": self.sample_times[0] if self.sample_times else None,
            "lastTime": self.sample_times[-1] if self.sample_times else None,
            "medianSampleInterval": statistics.median(intervals) if intervals else None,
            "maximumSampleInterval": max(intervals) if intervals else None,
            "boardingEventsChecked": self.boarding_checks,
            "seatExtensionSamplesChecked": self.seat_samples,
            "seatedSamplesChecked": self.seated_samples,
            "finalBoarded": sum(sample["boarded"] for sample in final),
            "finalFinished": sum(sample["finished"] for sample in final),
            "requireAllFinished": self.require_finished,
            "maximumRawAgentVelocity": self.maximum_measured_speed,
            "rawVelocitySpikeCount": self.velocity_spike_count,
            "rawVelocitySpikes": self.velocity_spikes,
            "maximumSampledSpeed": self.maximum_sampled_speed,
            "maximumSampleDisplacement": self.maximum_step,
            "motionBound": {"profileSpeedFactor": SPEED_FACTOR, "positionAllowanceMetres": POSITION_ALLOWANCE_METRES},
            "scope": "Recorded data integrity and sampled continuity; instantaneous native speed exceedances are quality warnings. No per-frame speed cap, RGB, visibility, between-sample motion, human realism or prediction quality validation."
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("directory", type=Path, help="Completed run directory containing the four log files")
    parser.add_argument("--output", type=Path, help="Optional JSON validation report")
    parser.add_argument("--require-all-finished", action="store_true", help="Also fail if any journey is unfinished at the final sample")
    args = parser.parse_args()
    directory = args.directory.resolve()
    if args.output and args.output.resolve() in {directory / name for name in INPUT_FILES}:
        parser.error("The report must not overwrite an input log")
    report = Audit(directory, args.require_all_finished).run()
    serialized = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(serialized + "\n", encoding="utf-8")
    print(serialized)
    return 0 if report["success"] else 1


if __name__ == "__main__":
    sys.exit(main())
