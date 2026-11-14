"""Streaming validation of a completed finite-day capture (v2).

Retains O(journeys + services + active actors), never every trajectory row.
Run only after Play stops and logs flush. Weighted actors conserve demand but
are not evidence for congestion at full physical passenger density.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import sys

from validate_station_decision_capture import (
    Audit, ACTIVITIES, TRAIN_STATUSES, EXECUTION_STATES, TERMINAL_STATES,
    decode, distance, enum_name, number, vector, TIME_TOLERANCE,
    EVENT_KINDS, PROBABILITY_TOLERANCE,
)
from native_gate_link_audit import audit_native_link_step

PURPOSES = ("Boarding", "Alighting")
ARRIVAL_PROFILES = ("EarlyService", "Ordinary", "LastMinute", "Alighting")
STATUSES = ("Scheduled", "WaitingForEntry", "Active", "Boarded", "Exited", "Unserved", "Failed")
ALIGHTER_SERVICES = {"Info", "Shop", "Toilet"}
STOPOVER_FIELDS = ("alightingServiceVisitor", "alightingServiceVisitLimit", "alightingStopoverSeconds")
STOPOVER_CLOCK_TOLERANCE = .003  # Float entry + budget accumulated at the native simulation clock.
INPUTS = ("manifest.json", "events.jsonl", "decisions.jsonl", "trajectories.jsonl")
DAY_EVENTS = EVENT_KINDS | {"journey_entered", "journey_finished", "journey_unserved", "group_separated",
                          "trajectory_sample", "capacity_denied", "dispatch_change_rejected", "platform_reapproach",
                          "platform_queue_joined", "platform_queue_left", "platform_queue_expired",
                          "platform_ticket_scan_started", "platform_access_granted", "platform_access_passed",
                          "coach_queue_joined", "coach_queue_left", "coach_boarding_started", "coach_boarding_ready",
                          "toilet_queue_joined", "toilet_queue_left", "toilet_use_reserved", "toilet_use_started",
                          "toilet_use_completed", "toilet_use_interrupted", "toilet_use_released",
                          "platform_holding_claimed", "platform_holding_released"}
GATE_STATES = {"GateApproach", "GateWaiting", "GateQueue", "GateCrossing", "CoachApproach", "CoachQueue"}
TOILET_STATES = {"WCApproach", "WCQueue", "WCToUse", "WCUsing", "WCClearing"}
# Daily-only recovery retains the exclusive Display stop while native movement
# returns a displaced reader. The Lab v1 validator keeps its original schema.
EXECUTION_STATES = EXECUTION_STATES | {"ReturningToDisplay"}


class CountSink:
    """Use the existing policy audit without retaining its decision records."""
    def __init__(self, allow_empty=False): self.count, self.allow_empty = 0, allow_empty
    def append(self, _): self.count += 1
    def __bool__(self): return self.count > 0 or self.allow_empty


class DayAudit(Audit):
    def __init__(self, directory, require_day_finished=False):
        super().__init__(Path(directory))
        self.require_day_finished = require_day_finished
        # Unlike the static24 cohort, a valid daily window may contain only empty
        # samples or exiting alighters. It need not have a policy decision yet.
        self.decisions = CountSink(allow_empty=True)
        self.plans, self.entered, self.terminal = {}, {}, {}
        self.ledger = defaultdict(Counter)
        self.last_samples = {}
        self.first_samples = set()
        self.max_active = self.max_represented_active = 0
        self.sample_groups = self.empty_groups = self.pool_reuses = 0
        self.run_end = None
        self.sample_events = False
        self.event_kinds = Counter()
        self.max_gap = self.max_entry_delay = 0.0
        self.occupancy_seconds = Counter()
        self.sample_span = self.actor_seconds = self.represented_seconds = 0.0
        self.profile_counts = Counter()
        self.hourly_entry_counts = Counter()
        self.terminal_counts = Counter()
        self.last_global_time = 0.0
        self.gate_scans, self.gate_grants, self.gate_passes = {}, {}, {}
        self.coach_starts, self.coach_ready = {}, {}
        self.native_link_flagged_samples = self.native_link_steps = self.native_link_allowed = 0
        self.native_link_rejected = self.native_link_exceptions = 0
        self.native_link_distance = self.native_link_exception_distance = 0.0
        self.native_link_max_step = self.native_link_max_speed = 0.0
        self.native_link_examples = []
        self.native_link_sample_interval_allowances = 0
        self.stopover_contract = False
        self.alighter_completions = defaultdict(list)
        self.alighter_last_decision_time, self.alighter_last_decision_index = {}, {}
        self.alighter_decision_count = self.alighter_budget_checks = self.alighter_deadline_checks = 0
        self._route_alighter_decisions = False
        self.mobility_contract = False
        self.mobility_profiles, self.separated_groups = {}, {}

    def speed_profile_at(self, jid, timestamp):
        """Use immutable solo preferences only after an independently logged party separation.

        Historical profiles keep the exact original speed caps. A runtime flag,
        requested agent speed or raw velocity cannot enlarge an ordinary bound.
        """
        profile = self.profiles[jid]
        mobility = self.mobility_profiles.get(jid)
        gid = self.plans[jid].get("groupId")
        separated = self.separated_groups.get(gid)
        if mobility and (not gid or separated is not None and timestamp >= separated):
            return dict(profile, walkSpeed=max(profile["walkSpeed"], mobility["preferredWalkSpeed"]),
                        fastWalkSpeed=max(profile["fastWalkSpeed"], mobility["preferredFastWalkSpeed"]))
        return profile

    def ordinary_distance_allowance(self, jid, start, end):
        """Integrate the saved cap across a separation inside the sample interval."""
        end = max(start, end)
        separated = self.separated_groups.get(self.plans[jid].get("groupId"))
        splits = (start, separated, end) if jid in self.mobility_profiles and separated is not None and start < separated < end else (start, end)
        allowance = .25
        for before, after in zip(splits, splits[1:]):
            profile = self.speed_profile_at(jid, before)
            allowance += max(profile["walkSpeed"], profile["fastWalkSpeed"]) * 1.5 * (after-before)
        return allowance

    def records(self, filename):
        """Route only the additive alighter policy to its own strict clock audit.

        The inherited policy validator still receives every boarding decision
        unmodified. All rows are parsed/count-checked once by Audit.records.
        Historical captures without the additive stopover contract keep the
        original policy audit.
        """
        for where, record in super().records(filename):
            if filename == "decisions.jsonl" and self._route_alighter_decisions:
                plan = self.plans.get(record.get("agentId"), {})
                alighter = enum_name(plan.get("purpose"), PURPOSES) == "Alighting"
                context = record.get("context") or {}
                if self.stopover_contract or isinstance(context, dict) and "alightingJourney" in context:
                    self.check(isinstance(context, dict) and type(context.get("alightingJourney")) is bool and
                               context.get("alightingJourney") == alighter,
                               f"{where}: evaluated alighting purpose differs from immutable journey")
                    if alighter:
                        self.check(self.stopover_contract, f"{where}: alighter policy lacks saved stopover settings")
                        self.check_alighter_decision(record, where)
                        continue
            yield where, record

    def load_decisions(self):
        self._route_alighter_decisions = True
        try:
            super().load_decisions()
        finally:
            self._route_alighter_decisions = False

    def check_alighter_decision(self, decision, where):
        try:
            jid, timestamp = self.agent(decision, where), self.time(decision, where)
            if timestamp is None or jid not in self.plans: return
            plan = self.plans[jid]
            self.check(timestamp >= self.alighter_last_decision_time.get(jid, -1), f"{where}: agent decision time moved backwards")
            self.alighter_last_decision_time[jid] = timestamp
            index = decision.get("decisionIndex")
            self.check(type(index) is int and index == self.alighter_last_decision_index.get(jid, 0) + 1,
                       f"{where}: non-contiguous decisionIndex")
            if type(index) is int: self.alighter_last_decision_index[jid] = index
            context = decision["context"]
            self.check(isinstance(context, dict), f"{where}: invalid alighter decision context")
            status, previous = enum_name(context.get("trainStatus"), TRAIN_STATUSES), enum_name(context.get("previousActivity"), ACTIVITIES)
            self.check(status in TRAIN_STATUSES and status == decision.get("observedStatus"), f"{where}: invalid/inconsistent evaluated train status")
            self.check(previous in ACTIVITIES and previous == decision.get("previousActivity"), f"{where}: previousActivity differs from context")
            self.check(number(context.get("nowSeconds")) and abs(context["nowSeconds"] - timestamp) <= TIME_TOLERANCE,
                       f"{where}: policy clock differs from logged time")
            self.check(number(context.get("boardingCutoffSeconds")) and number(decision.get("observedCutoff")) and
                       abs(context["boardingCutoffSeconds"] - decision["observedCutoff"]) <= TIME_TOLERANCE,
                       f"{where}: observed train cutoff telemetry differs from context")
            self.check(context.get("platformKnown") == decision.get("platformKnown"), f"{where}: knowledge differs from evaluated context")
            self.check(context.get("needs") == decision.get("needs"), f"{where}: needs differ from evaluated context")
            self.needs(decision.get("needs"), where)
            self.check(context.get("alightingJourney") is True and context.get("requireInitialDisplay") is False,
                       f"{where}: alighter lacks independent policy or was forced to initial Display")
            entry = self.entered.get(jid, {}).get("time")
            budget, deadline = plan.get("alightingStopoverSeconds"), context.get("stationStopoverDeadlineSeconds")
            valid_clock = number(entry) and number(budget) and number(deadline)
            self.check(valid_clock and abs(deadline - entry - budget) <= STOPOVER_CLOCK_TOLERANCE,
                       f"{where}: alighter deadline differs from actual entry plus immutable budget")
            completed = [activity for time, activity in self.alighter_completions[jid] if time <= timestamp + TIME_TOLERANCE]
            quota, remaining_visits = plan.get("alightingServiceVisitLimit"), context.get("stationServicesRemaining")
            self.check(type(remaining_visits) is int and type(quota) is int and remaining_visits == max(0, quota - len(completed)),
                       f"{where}: alighter remaining allowance differs from unique completion ledger")
            inputs, options = decision["inputOptions"], decision["options"]
            self.check(isinstance(inputs, list) and isinstance(options, list) and len(inputs) == len(options),
                       f"{where}: input/evaluation option count differs")
            total, positives, chosen, seen = 0., 0, [], set()
            for position, (source, option) in enumerate(zip(inputs, options)):
                label = f"{where}.options[{position}]"
                activity, poi = option.get("activity"), option.get("poiId")
                key = (activity, poi)
                self.check(key not in seen, f"{label}: duplicate candidate")
                seen.add(key)
                self.check(activity in ACTIVITIES and poi in self.pois, f"{label}: unknown activity/POI")
                self.check(enum_name(source.get("activity"), ACTIVITIES) == activity and source.get("poiId") == poi,
                           f"{label}: evaluated option does not match its input")
                probability = option.get("probability")
                if not self.check(number(probability) and 0 <= probability <= 1, f"{label}: invalid probability"): continue
                total += probability; positives += probability > 0
                for field in ("available", "reachable"):
                    self.check(type(source.get(field)) is bool, f"{label}: missing input {field}")
                preference = source.get("preference")
                self.check(number(preference), f"{label}: missing input preference")
                if not source.get("available") or not source.get("reachable") or not number(preference) or preference <= 0:
                    self.check(probability == 0, f"{label}: unavailable/unreachable/zero-preference choice has positive probability")
                pairs = (("travelSeconds", "travel"), ("queueSeconds", "queue"), ("durationSeconds", "dwell"),
                         ("toPlatformSeconds", "toPlatform"), ("toExitSeconds", "toExit"), ("seatTravelSeconds", "seatTravel"),
                         ("seatingTransitionSeconds", "seatingTransition"), ("seatedDurationSeconds", "seatedDwell"))
                for input_name, output_name in pairs:
                    a, b = source.get(input_name), option.get(output_name)
                    self.check(number(a) and a >= 0 and number(b) and abs(a - b) <= 1e-6,
                               f"{label}: invalid or inconsistent {input_name}")
                self.check(source.get("toPlatformSeconds") == 0, f"{label}: platform ETA leaked into alighter station budget")
                if probability > 0:
                    self.check(activity in ALIGHTER_SERVICES | {"Exit"}, f"{label}: alighter selected boarding/platform or unsupported station activity")
                    if activity == "Exit":
                        self.check(option.get("slack") == 0, f"{label}: Exit must remain feasible independently of the expired stopover clock")
                    elif activity in ALIGHTER_SERVICES:
                        self.check(plan.get("alightingServiceVisitor") is True and type(remaining_visits) is int and remaining_visits > 0 and activity not in completed,
                                   f"{label}: service selected without remaining unique allowance")
                        costs = [source.get(k) for k in ("travelSeconds", "queueSeconds", "durationSeconds", "seatTravelSeconds",
                                                        "seatingTransitionSeconds", "seatedDurationSeconds", "toExitSeconds")]
                        safety = context.get("safetyMarginSeconds")
                        self.check(number(safety) and safety >= 0, f"{label}: invalid stopover safety reserve")
                        if valid_clock and all(number(v) and v >= 0 for v in costs) and number(safety):
                            expected_slack = deadline - timestamp - sum(costs) - safety
                            self.alighter_budget_checks += 1
                            self.check(number(option.get("slack")) and abs(option["slack"] - expected_slack) <= 1e-5,
                                       f"{label}: alighter slack omits or misstates service, seating or Exit costs")
                            self.check(expected_slack >= -TIME_TOLERANCE, f"{label}: alighter service selected despite insufficient independent budget")
                if activity == decision.get("selectedActivity") and poi == decision.get("selectedPoi"): chosen.append(probability)
            self.check(abs(total - (1 if positives else 0)) <= PROBABILITY_TOLERANCE, f"{where}: probabilities do not normalize")
            if decision.get("selectedActivity") == "None":
                self.check(not positives and not decision.get("selectedPoi"), f"{where}: no choice despite positive probabilities, or nonempty selectedPoi")
            else:
                self.check(len(chosen) == 1 and chosen[0] > 0, f"{where}: selected activity/POI lacks unique positive probability")
            self.alighter_decision_count += 1
            self.decisions.append(decision)
        except (ValueError, KeyError, TypeError, OverflowError) as error:
            self.check(False, f"{where}: {error}")

    def load_manifest(self):
        try:
            self.manifest = decode((self.directory / "manifest.json").read_text(encoding="utf-8-sig"))
            m = self.manifest
            self.mobility_contract = m.get("mobilityModelVersion") == "adult-mobility-v1"
            if m.get("mobilityModelVersion") is not None:
                self.check(self.mobility_contract, "manifest: unsupported mobility model version")
            self.check(m.get("schema") == "stationflow.station_decision_day.v2", "manifest: expected finite-day v2 schema")
            self.check(type(m.get("seed")) is int, "manifest: seed must be integer")
            self.check(m.get("sharedRailRate") == 1, "manifest: shared rail rate must equal1")
            self.check(number(m.get("sampleRate")) and m["sampleRate"] > 0, "manifest: invalid sampleRate")
            self.check(number(m.get("congestionAllowance")) and m["congestionAllowance"] >= 1, "manifest: invalid congestion allowance")
            settings = (m.get("arrivalSettings") or {}).get("alightingStopovers")
            self.stopover_contract = isinstance(settings, dict)
            if settings is not None:
                self.check(self.stopover_contract, "manifest: invalid alighter stopover settings")
            if self.stopover_contract:
                self.check(number(settings.get("serviceVisitorFraction")) and 0 <= settings["serviceVisitorFraction"] <= 1,
                           "manifest: invalid alighter visitor fraction")
                low, high = settings.get("minimumVisits"), settings.get("maximumVisits")
                self.check(type(low) is int and type(high) is int and 1 <= low <= high <= 2,
                           "manifest: invalid alighter service allowance")
                low, high = settings.get("minimumBudgetSeconds"), settings.get("maximumBudgetSeconds")
                self.check(number(low) and number(high) and 0 < low <= high, "manifest: invalid independent alighter budget bounds")
            if "nativeGateLinkTelemetry" in m:
                self.check(type(m["nativeGateLinkTelemetry"]) is bool, "manifest: nativeGateLinkTelemetry must be boolean")
            if "stationaryHoldTelemetry" in m:
                self.check(type(m["stationaryHoldTelemetry"]) is bool, "manifest: stationaryHoldTelemetry must be boolean")
            if "physicalArrivalQueues" in m:
                self.check(type(m["physicalArrivalQueues"]) is bool, "manifest: physicalArrivalQueues must be boolean")
            if m.get("physicalArrivalQueues") is True:
                self.check(m.get("controlledPlatformAccess") is True, "manifest: physical arrival queues lack ticket controls")
                for control in m.get("platformControls", []) + m.get("coachControls", []):
                    anchors = control.get("queueAnchors")
                    self.check(isinstance(anchors, list) and len(anchors) == control.get("queueCapacity") and bool(anchors),
                               "manifest: physical queue lacks complete saved anchors")
                    for anchor in anchors or []: vector(anchor)
                    self.check(number(control.get("arrivalTolerance")) and 0 < control["arrivalTolerance"] <= .5,
                               "manifest: invalid physical arrival tolerance")
                    self.check(number(control.get("minimumQueueSeparation")) and .5 <= control["minimumQueueSeparation"] <= 1.2,
                               "manifest: invalid physical predecessor separation")
                for gate in m.get("platformControls", []):
                    self.check(number(gate.get("scanSecondsPerActor")) and gate["scanSecondsPerActor"] > 0,
                               "manifest: physical gate lacks a positive per-actor scan duration")
            if m.get("nativeGateLinkTelemetry") is True:
                self.check(m.get("controlledPlatformAccess") is True, "manifest: native gate telemetry lacks controlled platform access")
                controls = m.get("platformControls", [])
                self.check(bool(controls), "manifest: native gate telemetry lacks saved link geometry")
                self.check(len({g["track"] for g in controls}) == len(controls), "manifest: duplicate native gate track")
                self.check(len({g["ingressArea"] for g in controls}) == len(controls), "manifest: duplicate ingress permission area")
                for g in controls:
                    self.check(type(g.get("ingressArea")) is int and g["ingressArea"] in (3,5,6,7), "manifest: invalid ingress permission area")
                    for direction in ("ingress", "egress"):
                        start, end = vector(g[direction+"Start"]), vector(g[direction+"End"])
                        width = g[direction+"Width"]
                        self.check(number(width) and 0 < width <= 3, "manifest: invalid native gate width")
                        self.check(0 < distance(start,end) <= 4 and abs(start[1]-end[1]) <= .03 and abs(start[0]-end[0]) <= .03,
                                   "manifest: invalid native gate link geometry")
                        self.check(end[2] > start[2] if direction == "ingress" else end[2] < start[2], "manifest: reversed native gate link")
            for s in m["services"]:
                sid = s["serviceId"]
                self.check(bool(sid) and sid not in self.services, f"manifest: duplicate/blank service {sid}")
                self.services[sid] = s
                for field in ("seatedCapacity", "expectedBoarding", "expectedAlighting", "throughPassengers"):
                    self.check(type(s.get(field)) is int and s[field] >= (1 if field == "seatedCapacity" else 0), f"{sid}: invalid {field}")
                cap, through = s["seatedCapacity"], s["throughPassengers"]
                self.check(through+s["expectedBoarding"] <= cap and through+s["expectedAlighting"] <= cap,
                           f"{sid}: planned load exceeds capacity")
                start = s["arrivalOffsetSeconds"]-s["approachDurationSeconds"]
                end = s["departureOffsetSeconds"]+s["departureDurationSeconds"]
                self.check(start < end and s["arrivalOffsetSeconds"]+2*s["doorDurationSeconds"] < s["departureOffsetSeconds"],
                           f"{sid}: invalid occupation interval")
            by_track = defaultdict(list)
            for sid, s in self.services.items():
                by_track[s["trackNumber"]].append((s["arrivalOffsetSeconds"]-s["approachDurationSeconds"],
                    s["departureOffsetSeconds"]+s["departureDurationSeconds"], sid))
            clearance = m.get("minimumTrackClearanceSeconds", 0)
            for entries in by_track.values():
                entries.sort()
                for a, b in zip(entries, entries[1:]):
                    self.check(a[1]+clearance <= b[0]+TIME_TOLERANCE, f"manifest: track overlap {a[2]} / {b[2]}")
            for p in m.get("pois", []):
                self.check(p["poiId"] not in self.pois, "manifest: duplicate POI")
                self.pois[p["poiId"]] = p
                vector(p["position"])
            groups, planned = defaultdict(list), defaultdict(Counter)
            allocated_seats, ticket_ids = set(), set()
            plans = m.get("scheduledJourneys") or m["profiles"]
            raw_profiles = {p["agentId"]: p for p in m["profiles"]}
            self.check(len(raw_profiles) == len(m["profiles"]), "manifest: duplicate profile IDs")
            for raw in plans:
                p = dict(raw)
                p.setdefault("journeyId", p.get("agentId"))
                p.setdefault("trackNumber", p.get("track"))
                p.setdefault("entranceId", p.get("entryId"))
                jid, sid = p["journeyId"], p["serviceId"]
                self.check(isinstance(jid, str) and bool(jid) and jid not in self.plans, f"manifest: duplicate/blank journey {jid}")
                self.check(sid in self.services, f"{jid}: unknown service")
                self.plans[jid] = p
                purpose = enum_name(p.get("purpose"), PURPOSES)
                profile = enum_name(p.get("arrivalProfile"), ARRIVAL_PROFILES)
                self.check(purpose is not None and profile is not None, f"{jid}: invalid purpose/profile")
                weight = p.get("representedPassengers")
                self.check(type(weight) is int and weight > 0, f"{jid}: invalid representedPassengers")
                self.check(number(p.get("plannedEntrySeconds")) and p["plannedEntrySeconds"] >= 0, f"{jid}: invalid entry time")
                self.check(p.get("trackNumber") == self.services[sid]["trackNumber"], f"{jid}: wrong planned track")
                self.check(abs(p["publishedDepartureSeconds"]-self.services[sid]["departureOffsetSeconds"]) <= TIME_TOLERANCE,
                           f"{jid}: published departure differs from saved timetable")
                for field in ("walkSpeed", "fastWalkSpeed"):
                    self.check(number(p.get(field)) and p[field] > 0, f"{jid}: invalid {field}")
                self.needs(p.get("needs"), jid)
                if m.get("controlledPlatformAccess"):
                    composition = self.services[sid].get("coachSeatCapacities", [])
                    coach, seats, ticket = p.get("coachNumber"), p.get("seatNumbers", []), p.get("ticketId")
                    self.check(bool(composition) and sum(composition) == self.services[sid]["seatedCapacity"], f"{sid}: inconsistent coach capacity")
                    self.check(type(coach) is int and 1 <= coach <= len(composition), f"{jid}: invalid assigned coach")
                    self.check(isinstance(ticket, str) and bool(ticket) and ticket not in ticket_ids, f"{jid}: duplicate/blank ticket")
                    ticket_ids.add(ticket)
                    self.check(len(seats) == weight and len(set(seats)) == weight, f"{jid}: ticket seats differ from cohort weight")
                    for seat in seats:
                        self.check(type(seat) is int and 1 <= seat <= composition[coach-1], f"{jid}: invalid seat")
                        key = (sid, purpose, coach, seat)
                        self.check(key not in allocated_seats, f"{jid}: duplicate coach seat")
                        allocated_seats.add(key)
                planned[sid][purpose] += weight
                if purpose == "Boarding": self.profile_counts[profile] += 1
                if p.get("groupId"): groups[p["groupId"]].append(p)
                else: self.check(p.get("groupSize", 1) == 1, f"{jid}: solo has groupSize !=1")
                self.check(jid in raw_profiles, f"{jid}: missing stable profile")
                stable = raw_profiles.get(jid, {})
                if self.mobility_contract:
                    mobility = p.get("mobilityProfile")
                    valid = isinstance(mobility, dict) and number(mobility.get("preferredWalkSpeed")) and number(mobility.get("preferredFastWalkSpeed")) and \
                        .45 - .00001 <= mobility["preferredWalkSpeed"] <= 1.55 + .00001 and \
                        mobility["preferredWalkSpeed"] < mobility["preferredFastWalkSpeed"] <= 2.2 + .00001
                    self.check(valid, f"{jid}: invalid finite saved adult mobility speed bounds")
                    self.check(mobility == stable.get("mobilityProfile"), f"{jid}: immutable profile/plan mobility mismatch")
                    if valid: self.mobility_profiles[jid] = mobility
                for field in ("serviceId", "representedPassengers"):
                    self.check(stable.get(field) == p.get(field), f"{jid}: profile/plan {field} mismatch")
                if "groupId" in stable:
                    self.check(stable["groupId"] == p.get("groupId"), f"{jid}: profile/plan group mismatch")
                if self.stopover_contract:
                    visitor, quota, budget = (p.get(field) for field in STOPOVER_FIELDS)
                    self.check(type(visitor) is bool and type(quota) is int and number(budget), f"{jid}: invalid generated alighter profile")
                    if purpose == "Alighting" and visitor is True:
                        self.check(type(quota) is int and settings["minimumVisits"] <= quota <= settings["maximumVisits"] and
                                   number(budget) and settings["minimumBudgetSeconds"] <= budget <= settings["maximumBudgetSeconds"],
                                   f"{jid}: alighter quota/budget outside configured bounds")
                    else:
                        self.check(visitor is False and quota == 0 and budget == 0, f"{jid}: direct exiter/boarder gained optional stopovers")
                    for field in STOPOVER_FIELDS:
                        self.check(stable.get(field) == p.get(field), f"{jid}: immutable profile/plan {field} mismatch")
                self.profiles[jid] = dict(p, **raw_profiles.get(jid, {}))
                self.profiles[jid]["agentId"] = jid
            self.check(m.get("participantCount") == len(self.plans), "manifest: participantCount differs from planned journeys")
            for sid, s in self.services.items():
                in_window = s["departureOffsetSeconds"] > 0
                self.check(planned[sid]["Boarding"] == (s["expectedBoarding"] if in_window else 0), f"{sid}: planned boarding weight does not conserve demand")
                self.check(planned[sid]["Alighting"] == (s["expectedAlighting"] if in_window else 0), f"{sid}: planned alighting weight does not conserve demand")
            for gid, members in groups.items():
                first = members[0]
                self.check(2 <= len(members) <= 4 and all(p["groupSize"] == len(members) for p in members), f"{gid}: incomplete group")
                self.check({p["groupMemberIndex"] for p in members} == set(range(len(members))), f"{gid}: duplicate/missing group member")
                for field in ("serviceId", "purpose", "entranceId", "arrivalProfile") + (("coachNumber",) if m.get("controlledPlatformAccess") else ()):
                    self.check(len({p[field] for p in members}) == 1, f"{gid}: inconsistent {field}")
                self.check(max(p["plannedEntrySeconds"] for p in members)-min(p["plannedEntrySeconds"] for p in members) <= 5,
                           f"{gid}: planned group spread exceeds5s")
            self.check(bool(self.plans), "manifest: empty journey roster")
        except (OSError, ValueError, KeyError, TypeError, OverflowError, IndexError, StopIteration) as error:
            self.check(False, f"manifest: {error}")

    def check_physical_queue_join(self, event, line, control, where):
        if self.manifest.get("physicalArrivalQueues") is not True: return
        slot = event.get("queueSlotIndex")
        anchors = control["queueAnchors"]
        if not self.check(type(slot) is int and 0 <= slot < len(anchors), f"{where}: invalid physical arrival slot"): return
        actor = vector(event["position"])
        arrival = vector(event["queueArrivalPosition"])
        self.check(distance(arrival, vector(anchors[slot])) <= .01, f"{where}: arrival point differs from saved queue slot")
        self.check(math.hypot(actor[0]-arrival[0],actor[2]-arrival[2]) <= control["arrivalTolerance"]+.01 and
                   abs(actor[1]-arrival[1]) <= 1.2, f"{where}: remote queue reservation before physical arrival")
        predecessor = event.get("queuePredecessorJourneyId")
        expected = line[-1] if line else ""
        self.check(predecessor == expected, f"{where}: physical predecessor differs from FIFO tail")
        if expected:
            previous = vector(event["queuePredecessorPosition"])
            self.check(math.hypot(actor[0]-previous[0],actor[2]-previous[2])+.01 >= control["minimumQueueSeparation"],
                       f"{where}: physical queue admission overlaps predecessor")

    def load_events(self):
        previous, slots, last_sample_id = -1.0, {}, 0
        previous_sample = None
        admitted = Counter()
        board_events = set()
        gate_lines, coach_lines = defaultdict(list), defaultdict(list)
        for where, e in self.records("events.jsonl"):
            try:
                t = self.time(e, where)
                if t is None: continue
                self.check(t+TIME_TOLERANCE >= previous, f"{where}: event clock moved backwards")
                previous = t
                kind, jid = e.get("kind"), e.get("agentId") or e.get("journeyId")
                self.check(kind in DAY_EVENTS, f"{where}: unknown event kind {kind}")
                self.event_kinds[kind] += 1
                if kind.startswith("toilet_"):
                    self.check(len(self.manifest.get("toiletControls", [])) == 1, f"{where}: WC event without saved WC control")
                    self.check(jid in self.entered and jid not in self.terminal, f"{where}: WC event outside active journey")
                if kind == "run_ended": self.run_end = t
                if kind == "trajectory_sample":
                    self.sample_events = True
                    self.check(type(e.get("sampleId")) is int and e["sampleId"] == last_sample_id+1,
                               f"{where}: sampleId must increase contiguously")
                    last_sample_id = e.get("sampleId", last_sample_id)
                    wanted = e.get("activeJourneyIds")
                    self.check(isinstance(wanted, list), f"{where}: sample lacks active cohort")
                    if isinstance(wanted, list):
                        self.check(len(wanted) == len(set(wanted)), f"{where}: duplicate sample identity")
                        self.check(set(wanted) == set(slots.values()), f"{where}: declared active cohort differs from ordered lifecycle")
                        represented = sum(self.plans[j]["representedPassengers"] for j in set(wanted) if j in self.plans)
                        if previous_sample is not None:
                            last_t, last_count, last_represented = previous_sample
                            dt = max(0.0, t-last_t)
                            self.max_gap = max(self.max_gap, dt)
                            self.sample_span += dt
                            self.actor_seconds += dt*last_count
                            self.represented_seconds += dt*last_represented
                            self.occupancy_seconds[last_count] += dt
                        previous_sample = (t, len(wanted), represented)
                    continue
                if not jid: continue
                if not self.check(jid in self.plans, f"{where}: unknown journey {jid}"): continue
                p = self.plans[jid]
                self.check(e.get("serviceId") == p["serviceId"], f"{where}: service assignment changed")
                if self.stopover_contract and enum_name(p["purpose"], PURPOSES) == "Alighting":
                    self.check(kind != "boarded" and not e.get("boarded"), f"{where}: alighter boarded as outbound demand")
                    if kind == "activity_completed":
                        activity = e.get("detail", "").split(";", 1)[0]
                        self.check(activity in ALIGHTER_SERVICES | {"Exit"}, f"{where}: alighter completed unsupported station activity")
                        if activity in ALIGHTER_SERVICES:
                            prior = self.alighter_completions[jid]
                            self.check(p["alightingServiceVisitor"] is True and activity not in {a for _, a in prior},
                                       f"{where}: direct exiter or duplicate alighter service completion")
                            prior.append((t, activity))
                            self.check(len(prior) <= p["alightingServiceVisitLimit"], f"{where}: alighter completed above unique allowance")
                if "representedPassengers" in e:
                    self.check(e["representedPassengers"] == p["representedPassengers"], f"{where}: represented weight changed")
                if self.manifest.get("coachControls") and kind.startswith("coach_"):
                    self.check(jid in self.gate_passes, f"{where}: coach queue without validated platform passage")
                    line = coach_lines[(p["serviceId"], p["coachNumber"])]
                    if kind == "coach_queue_joined":
                        self.check(jid not in line, f"{where}: duplicate coach queue entry")
                        control = next(c for c in self.manifest["coachControls"] if c["track"] == p["trackNumber"] and c["coachNumber"] == p["coachNumber"])
                        self.check_physical_queue_join(e,line,control,where)
                        line.append(jid)
                    elif kind == "coach_queue_left" and jid in line: line.remove(jid)
                    if kind == "coach_boarding_started": self.coach_starts[jid] = t
                    elif kind == "coach_boarding_ready":
                        self.check(bool(line) and line[0] == jid, f"{where}: coach boarding violated FIFO order")
                        door = next(c for c in self.manifest["coachControls"] if c["track"] == p["trackNumber"] and c["coachNumber"] == p["coachNumber"])
                        self.check(jid in self.coach_starts and t - self.coach_starts.get(jid, t) + TIME_TOLERANCE >= door["boardingSecondsPerPassenger"] * p["representedPassengers"], f"{where}: coach boarding completed too quickly")
                        self.check(e.get("actualStatus") == "Boarding" and t < e["actualCutoff"], f"{where}: coach boarding outside open doors/cutoff")
                        self.coach_ready[jid] = t
                if self.manifest.get("controlledPlatformAccess") and kind.startswith("platform_"):
                    self.check(enum_name(p["purpose"], PURPOSES) == "Boarding", f"{where}: alighter forced through ingress control")
                    line = gate_lines[p["trackNumber"]]
                    if kind == "platform_queue_joined":
                        self.check(jid not in line, f"{where}: duplicate platform queue entry")
                        control = next(g for g in self.manifest["platformControls"] if g["track"] == p["trackNumber"])
                        self.check_physical_queue_join(e,line,control,where)
                        line.append(jid)
                    elif kind in {"platform_queue_left", "platform_queue_expired"} and jid in line: line.remove(jid)
                    if kind == "platform_ticket_scan_started": self.gate_scans[jid] = t
                    elif kind == "platform_access_granted":
                        self.check(bool(line) and line[0] == jid, f"{where}: platform control violated FIFO order")
                        if jid in line: line.remove(jid)
                        self.check(jid in self.gate_scans and jid not in self.gate_grants, f"{where}: grant without unique completed scan")
                        scanner = next(g for g in self.manifest["platformControls"] if g["track"] == p["trackNumber"])
                        duration = scanner["scanSecondsPerActor"] if "scanSecondsPerActor" in scanner else scanner["scanSecondsPerPassenger"] * p["representedPassengers"]
                        self.check(number(duration) and duration > 0 and t - self.gate_scans.get(jid, t) + TIME_TOLERANCE >= duration,
                                   f"{where}: scan completed too quickly for its configured duration")
                        self.check(e.get("trainPresent") is True and e["platformOpensAt"] <= t < e["actualCutoff"], f"{where}: gate grant outside access window or without berthed assigned train")
                        self.gate_grants[jid] = t
                    elif kind == "platform_access_passed":
                        self.check(jid in self.gate_grants and jid not in self.gate_passes and t >= self.gate_grants.get(jid, t), f"{where}: crossing without unique gate grant")
                        self.gate_passes[jid] = t
                if kind == "journey_entered":
                    self.check(jid not in self.entered and jid not in self.terminal, f"{where}: journey entered more than once or after terminal")
                    self.check(t+TIME_TOLERANCE >= p["plannedEntrySeconds"], f"{where}: journey entered before planned arrival")
                    slot = e.get("actorSlotId")
                    self.check((isinstance(slot, str) and bool(slot)) or (type(slot) is int and slot >= 0), f"{where}: missing pool-slot identity")
                    self.check(slot not in slots, f"{where}: pool slot reused while still occupied")
                    if slot in {v.get("actorSlotId") for v in self.terminal.values()}: self.pool_reuses += 1
                    slots[slot] = jid
                    self.entered[jid] = e
                    profile = e.get("profile") or e
                    self.check(isinstance(profile, dict), f"{where}: entry lacks actual profile")
                    if isinstance(profile, dict):
                        if self.mobility_contract:
                            self.check(profile.get("mobilityProfile") == p.get("mobilityProfile"),
                                       f"{where}: actual entry changed immutable mobility profile")
                        if self.stopover_contract:
                            for field in STOPOVER_FIELDS:
                                self.check(profile.get(field) == p.get(field), f"{where}: actual entry changed immutable {field}")
                        self.profiles[jid].update(profile)
                        self.profiles[jid]["position"] = e["position"]
                        vector(e["position"])
                        for field in ("walkSpeed", "fastWalkSpeed"):
                            self.check(abs(profile[field]-p[field]) <= TIME_TOLERANCE, f"{where}: immutable {field} changed")
                    self.max_entry_delay = max(self.max_entry_delay, t-p["plannedEntrySeconds"])
                    self.hourly_entry_counts[str(int(t//3600))] += p["representedPassengers"]
                elif kind in {"journey_finished", "journey_unserved"}:
                    self.check(jid not in self.terminal, f"{where}: repeated terminal journey")
                    status = "Unserved" if kind == "journey_unserved" else e.get("outcome")
                    self.check(status in {"Boarded", "Exited", "Unserved", "Failed"}, f"{where}: invalid terminal outcome")
                    self.check(jid in self.entered or status in {"Unserved", "Failed"}, f"{where}: successful terminal without entry")
                    if jid not in self.entered:
                        self.check(t+TIME_TOLERANCE >= p["plannedEntrySeconds"] or e.get("actualStatus") == "Cancelled",
                                   f"{where}: future journey discarded before its planned entry")
                    slot = e.get("actorSlotId")
                    if jid in self.entered:
                        self.check(slot == self.entered[jid].get("actorSlotId") and slots.get(slot) == jid, f"{where}: terminal released another slot")
                        slots.pop(slot, None)
                    self.terminal[jid] = e
                    self.terminal_counts[status] += 1
                    if status == "Boarded":
                        self.check(enum_name(p["purpose"], PURPOSES) == "Boarding", f"{where}: alighter boarded as outbound demand")
                        self.check(e.get("actualStatus") == "Boarding" and number(e.get("actualCutoff")) and t < e["actualCutoff"],
                                   f"{where}: boarding outside actual boarding gate")
                        self.ledger[p["serviceId"]]["boarded"] += p["representedPassengers"]
                        self.boarding_checks += 1
                elif kind in {"boarded", "journey_ended"}:
                    # Detailed activity outcome precedes the single lifecycle finalization.
                    self.check(jid in self.entered and jid not in self.terminal, f"{where}: outcome outside active lifecycle")
                    if kind == "boarded":
                        if self.manifest.get("controlledPlatformAccess"):
                            self.check(jid in self.gate_passes and e.get("platformAccessPassed") is True, f"{where}: boarding bypassed ticket control")
                            if self.manifest.get("coachControls"):
                                self.check(jid in self.coach_ready and e.get("coachQueueValidated") is True, f"{where}: boarding bypassed assigned coach queue")
                            anchor = next(a for a in self.manifest["coachAnchors"] if a["track"] == p["trackNumber"] and a["coachNumber"] == p["coachNumber"])
                            self.check(distance(vector(e["position"]), vector(anchor["position"])) <= 1.5, f"{where}: boarding at wrong coach")
                        self.check(jid not in board_events, f"{where}: repeated boarding admission")
                        board_events.add(jid)
                        sid = p["serviceId"]
                        admitted[sid] += p["representedPassengers"]
                        self.check(e.get("actualStatus") == "Boarding" and number(e.get("actualCutoff")) and t < e["actualCutoff"],
                                   f"{where}: boarding event outside actual gate")
                        lead = max(self.services[sid]["doorDurationSeconds"], self.services[sid].get("boardingCutoffLeadSeconds", 0))
                        self.check(number(e.get("actualDeparture")) and number(e.get("actualCutoff")) and
                                   abs(e["actualDeparture"]-e["actualCutoff"]-lead) <= .02,
                                   f"{where}: actual cutoff lead differs from declared boarding rule")
                        self.check(e.get("capacity") == self.services[sid]["seatedCapacity"], f"{where}: capacity snapshot differs from manifest")
                        self.check(e.get("occupancyAfter") == admitted[sid]+self.services[sid]["throughPassengers"],
                                   f"{where}: boarding occupancy ledger does not conserve weighted admission")
                elif kind == "group_separated":
                    # The group's canonical first member may already be terminal or still outside.
                    self.check(bool(p.get("groupId")) and e.get("groupId") == p.get("groupId"), f"{where}: separation lacks stable group identity")
                    if p.get("groupId") and e.get("groupId") == p.get("groupId"):
                        self.separated_groups.setdefault(p["groupId"], t)
                else:
                    self.check(jid in self.entered and jid not in self.terminal, f"{where}: actor event outside active lifecycle")
            except (ValueError, KeyError, TypeError, OverflowError, IndexError, StopIteration) as error:
                self.check(False, f"{where}: {error}")
        self.check(self.event_kinds["run_started"] == 1, "events: expected one run_started")
        if self.run_end is None: self.warnings.append("No run_ended; completion/flush must be confirmed externally.")
        for sid, ledger in self.ledger.items():
            s = self.services[sid]
            self.check(ledger["boarded"]+s["throughPassengers"] <= s["seatedCapacity"], f"{sid}: admitted weighted boarding exceeds seated capacity")
            self.check(ledger["boarded"] <= s["expectedBoarding"], f"{sid}: admitted more than planned demand")
            self.check(admitted[sid] == ledger["boarded"], f"{sid}: admission events and terminal outcomes disagree")
        self.check(board_events == {jid for jid, e in self.terminal.items() if e.get("outcome") == "Boarded"},
                   "events: every boarded journey needs exactly one admission and one terminal event")
        if self.require_day_finished:
            self.check(len(self.terminal) == len(self.plans), "day: scheduled journeys remain unfinished")

    def agent(self, record, where):
        jid = super().agent(record, where)
        if jid in self.plans:
            t = record.get("time")
            self.check(jid in self.entered, f"{where}: record for never-entered journey")
            if jid in self.entered:
                self.check(number(t) and t+TIME_TOLERANCE >= self.entered[jid]["time"], f"{where}: record before entry")
                self.check(record.get("actorSlotId") == self.entered[jid].get("actorSlotId"), f"{where}: actor-slot identity changed")
            if jid in self.terminal:
                self.check(number(t) and t <= self.terminal[jid]["time"]+TIME_TOLERANCE, f"{where}: record after terminal")
        return jid

    def sample_declarations(self):
        # Second streaming pass, no O(samples x actors) retained metadata.
        for _, event in self.records_without_count("events.jsonl"):
            if event.get("kind") == "trajectory_sample": yield event

    def records_without_count(self, name):
        before = self.counts[name]
        for item in self.records(name): yield item
        self.counts[name] = before

    def load_trajectories(self):
        declarations = iter(self.sample_declarations()) if self.sample_events else None
        declaration = next(declarations, None) if declarations else None
        group_time, ids, seats, slots = None, set(), set(), set()
        lifecycle = sorted([(e["time"], 0, jid) for jid, e in self.entered.items()] +
                           [(e["time"], 1, jid) for jid, e in self.terminal.items()])
        cursor, active = 0, set()
        group_sample_id = None

        def close_group():
            nonlocal declaration, cursor
            if group_time is None: return
            self.sample_groups += 1
            while cursor < len(lifecycle) and lifecycle[cursor][0] <= group_time+TIME_TOLERANCE:
                _, kind, jid = lifecycle[cursor]
                if kind == 0: active.add(jid)
                else: active.discard(jid)
                cursor += 1
            if declarations is not None:
                while declaration and declaration["time"] < group_time-TIME_TOLERANCE:
                    self.check(not declaration.get("activeJourneyIds"), "sample: nonempty declared group has no trajectory rows")
                    self.empty_groups += 1
                    declaration = next(declarations, None)
                self.check(declaration is not None and abs(declaration["time"]-group_time) <= TIME_TOLERANCE, "sample: rows lack matching declaration")
                if declaration:
                    wanted = declaration.get("activeJourneyIds", [])
                    self.check(len(wanted) == len(set(wanted)), "sample: duplicate declared identity")
                    self.check(ids == set(wanted), "sample: trajectory membership differs from declared active cohort")
                    self.check(group_sample_id == declaration.get("sampleId"), "sample: row sampleId differs from declaration")
                    declaration = next(declarations, None)
            else:
                self.check(ids == active, f"sample {group_time}: active-only membership differs from lifecycle")
            self.max_active = max(self.max_active, len(ids))
            self.max_represented_active = max(self.max_represented_active, sum(self.plans[j]["representedPassengers"] for j in ids if j in self.plans))

        for where, r in self.records("trajectories.jsonl"):
            try:
                jid, t = self.agent(r, where), self.time(r, where)
                if t is None or jid not in self.plans: continue
                if group_time != t:
                    if group_time is not None:
                        self.check(t > group_time, f"{where}: sample timestamp did not increase")
                        gap = t-group_time
                        if not self.sample_events: self.max_gap = max(self.max_gap, gap)
                    close_group()
                    group_time, ids, seats, slots = t, set(), set(), set()
                    group_sample_id = r.get("sampleId")
                self.check(r.get("sampleId") == group_sample_id, f"{where}: sampleId changes within timestamp group")
                self.check(jid not in ids, f"{where}: duplicate journey in sample")
                ids.add(jid)
                slot = r.get("actorSlotId")
                self.check(slot not in slots, f"{where}: duplicate pool actor in sample")
                slots.add(slot)
                self.check(r.get("serviceId") == self.plans[jid]["serviceId"] and r.get("occurrence") == 0, f"{where}: trip identity changed")
                for field in ("representedPassengers", "groupId"):
                    self.check(r.get(field) == self.plans[jid].get(field), f"{where}: immutable {field} changed")
                self.check(enum_name(r.get("purpose"), PURPOSES) == enum_name(self.plans[jid].get("purpose"), PURPOSES), f"{where}: journey purpose changed")
                if self.stopover_contract and enum_name(self.plans[jid].get("purpose"), PURPOSES) == "Alighting":
                    plan = self.plans[jid]
                    entry = self.entered.get(jid, {}).get("time")
                    deadline, budget = r.get("alightingStopoverDeadlineSeconds"), plan.get("alightingStopoverSeconds")
                    self.alighter_deadline_checks += 1
                    self.check(number(entry) and number(deadline) and number(budget) and
                               abs(deadline - entry - budget) <= STOPOVER_CLOCK_TOLERANCE,
                               f"{where}: live alighter deadline differs from actual entry plus immutable budget")
                    self.check(r.get("activity") in ALIGHTER_SERVICES | {"Exit"} and not r.get("platformAccessGranted") and
                               not r.get("platformAccessPassed") and r.get("nativeLinkDirection") != "Ingress",
                               f"{where}: alighter boarded/reentered a platform or chose unsupported station activity")
                    if self.manifest.get("nativeGateLinkTelemetry") is True:
                        mask, ingress_mask = r.get("navigationAreaMask"), 0
                        for gate in self.manifest["platformControls"]: ingress_mask |= 1 << gate["ingressArea"]
                        self.check(type(mask) is int and mask & ingress_mask == 0,
                                   f"{where}: native ingress permission enabled for an alighter")
                    completed = self.alighter_completions[jid]
                    strict = sum(time < t - TIME_TOLERANCE for time, _ in completed)
                    inclusive = sum(time <= t + TIME_TOLERANCE for time, _ in completed)
                    count = r.get("alightingServiceVisitsCompleted")
                    self.check(type(count) is int and strict <= count <= inclusive and count <= plan["alightingServiceVisitLimit"],
                               f"{where}: live alighter completed counter differs from unique event ledger/allowance")
                self.check(r.get("execution") in EXECUTION_STATES | {"Regrouping", "WaitingForGroup"} | GATE_STATES | TOILET_STATES, f"{where}: unknown execution")
                if r.get("execution") in TOILET_STATES:
                    self.check(r.get("activity") == "Toilet" and len(self.manifest.get("toiletControls", [])) == 1,
                               f"{where}: WC execution requires Toilet activity and a saved WC control")
                if r.get("execution") == "ReturningToDisplay":
                    poi = self.pois.get(r.get("targetPoi"), {})
                    self.check(r.get("activity") == "Display" and poi.get("activity") == "Display" and
                               bool(poi.get("standingPoints")),
                               f"{where}: Display return requires Display activity and a saved standing area")
                self.check(r.get("activity") in ACTIVITIES and r.get("knownStatus") in TRAIN_STATUSES, f"{where}: invalid activity/status")
                self.check(not r.get("finished") and not r.get("boarded") and r.get("visibleActor") is True, f"{where}: inactive/terminal actor in active sample")
                pos, vel = vector(r["position"]), vector(r["velocity"])
                if self.manifest.get("stationaryHoldTelemetry") is True:
                    held, serial = r.get("isStationaryHeld"), r.get("stationaryHoldSerial")
                    self.check(type(held) is bool, f"{where}: stationary hold flag must be boolean")
                    self.check(type(serial) is int and serial >= 0, f"{where}: invalid stationary hold serial")
                    anchor = vector(r.get("stationaryAnchor"))
                    if held is True:
                        self.check(type(serial) is int and serial > 0, f"{where}: active hold needs a positive serial")
                        self.check(distance(pos, anchor) <= .005,
                                   f"{where}: stationary body differs from its explicit anchor by more than0.005m")
                if self.manifest.get("controlledPlatformAccess"):
                    p = self.plans[jid]
                    self.check(r.get("coachNumber") == p["coachNumber"], f"{where}: assigned coach changed")
                    gate = next(g for g in self.manifest["platformControls"] if g["track"] == p["trackNumber"])
                    if enum_name(p["purpose"], PURPOSES) == "Boarding" and pos[2] > gate["platform"]["z"] + .1 and abs(pos[0] - gate.get("center", gate["platform"])["x"]) < 3.95:
                        self.check(r.get("platformAccessGranted") is True or r.get("platformAccessPassed") is True, f"{where}: unauthorized physical platform entry")
                speed = math.sqrt(sum(x*x for x in vel))
                profile = self.speed_profile_at(jid, t)
                bound_speed = max(profile["walkSpeed"], profile["fastWalkSpeed"])*1.5
                self.maximum_measured_speed = max(self.maximum_measured_speed, speed)
                if speed > bound_speed:
                    self.velocity_spike_count += 1
                    if len(self.velocity_spikes) < 10: self.velocity_spikes.append(dict(where=where, journeyId=jid, time=t, speed=speed))
                previous = self.last_samples.get(jid)
                if self.manifest.get("nativeGateLinkTelemetry") is True:
                    self.check(type(r.get("isOnNativeLink")) is bool, f"{where}: native-link flag must be boolean")
                if jid in self.mobility_profiles and r.get("isOnNativeLink") is True:
                    requested = r.get("requestedNavigationSpeed")
                    self.check(number(requested) and 0 < requested <= max(profile["walkSpeed"], profile["fastWalkSpeed"]) + .01,
                               f"{where}: native requested speed exceeds the saved current solo/party profile")
                if r.get("isOnNativeLink") is True: self.native_link_flagged_samples += 1
                native = audit_native_link_step(self.manifest, previous, r,
                    dict(profile, trackNumber=self.plans[jid]["trackNumber"], purpose=enum_name(self.plans[jid]["purpose"], PURPOSES)))
                if native["candidate"]:
                    self.native_link_steps += 1
                    for error in native["errors"]: self.check(False, f"{where}: native gate link: {error}")
                    if native["allowed"] and native["details"]["direction"] == "Ingress":
                        on_link_time = min(sample["time"] for sample in (previous, r)
                                           if sample and sample.get("isOnNativeLink") is True)
                        independently_granted = jid in self.gate_grants and self.gate_grants.get(jid, t) <= on_link_time+TIME_TOLERANCE
                        self.check(independently_granted, f"{where}: native ingress occurred before its independently logged grant")
                        native["allowed"] = independently_granted
                    if native["allowed"]:
                        self.native_link_allowed += 1
                        detail = native["details"]
                        self.native_link_distance += detail["stepMetres"]
                        self.native_link_max_step = max(self.native_link_max_step, detail["stepMetres"])
                        self.native_link_max_speed = max(self.native_link_max_speed, detail["sampledSpeed"])
                        self.native_link_sample_interval_allowances += detail["usedSampleIntervalGeometryAllowance"]
                    else: self.native_link_rejected += 1
                if previous:
                    dt = t-previous["time"]
                    step = distance(pos, vector(previous["position"]))
                    self.check(dt > 0, f"{where}: journey sample clock did not increase")
                    self.maximum_step = max(self.maximum_step, step)
                    if dt > 0: self.maximum_sampled_speed = max(self.maximum_sampled_speed, step/dt)
                    ordinary_continuity = step <= self.ordinary_distance_allowance(jid, previous["time"], t)
                    self.check(ordinary_continuity or native["allowed"], f"{where}: sampled-position jump {step:.3f}m/{dt:.3f}s")
                    if not ordinary_continuity and native["allowed"]:
                        self.native_link_exceptions += 1
                        self.native_link_exception_distance += step
                        if len(self.native_link_examples) < 10:
                            self.native_link_examples.append(dict(where=where, journeyId=jid, time=t, **native["details"]))
                elif jid in self.entered:
                    dt = t-self.entered[jid]["time"]
                    self.check(distance(pos, vector(profile["position"])) <= self.ordinary_distance_allowance(jid, self.entered[jid]["time"], t), f"{where}: first sample teleports from entrance")
                self.seat_extension(r, where, seats)
                self.last_samples[jid] = r
                self.first_samples.add(jid)
                self.last_global_time = max(self.last_global_time, t)
            except (ValueError, KeyError, TypeError, OverflowError, IndexError, StopIteration) as error:
                self.check(False, f"{where}: {error}")
        close_group()
        while declaration:
            self.check(not declaration.get("activeJourneyIds"), "sample: final nonempty declaration lacks rows")
            self.empty_groups += 1
            declaration = next(declarations, None)
        if not self.sample_events: self.warnings.append("No trajectory_sample declarations: empty active cohorts cannot be audited.")
        for jid, terminal in self.terminal.items():
            if jid not in self.entered: continue
            position = terminal.get("position")
            previous = self.last_samples.get(jid) or self.entered[jid]
            if position is not None:
                dt = terminal["time"]-previous["time"]
                self.check(distance(vector(position), vector(previous["position"])) <= self.ordinary_distance_allowance(jid, previous["time"], terminal["time"]), f"{jid}: terminal snapshot teleported")
            else:
                self.check(position is not None, f"{jid}: terminal lacks final physical snapshot")
        limit = self.manifest.get("actorSlotCount")
        if type(limit) is int: self.check(self.max_active <= limit, "sample: active pool limit exceeded")
        if self.velocity_spike_count: self.warnings.append(f"{self.velocity_spike_count} native instantaneous velocity spikes; sampled continuity is separately checked.")
        if self.native_link_exceptions:
            self.warnings.append(f"{self.native_link_exceptions} sampled steps exceeded ordinary walking continuity and passed the explicit saved native gate-link audit; positions and raw velocities were preserved.")

    def run(self):
        self.load_manifest()
        if self.plans and self.services:
            self.load_events()
            self.load_decisions()
            self.load_trajectories()
        terminal_weight = sum(self.plans[j]["representedPassengers"] for j in self.terminal)
        active_ids = set(self.entered)-set(self.terminal)
        planned_groups = defaultdict(list)
        for jid, p in self.plans.items():
            if p.get("groupId"): planned_groups[p["groupId"]].append(jid)
        group_spreads = []
        for gid, ids in planned_groups.items():
            times = [self.entered[jid]["time"] for jid in ids if jid in self.entered]
            if times:
                group_spreads.append(dict(groupId=gid, plannedMembers=len(ids), enteredMembers=len(times),
                                         complete=len(times) == len(ids), entrySpreadSeconds=max(times)-min(times)))
        group_spreads.sort(key=lambda g: g["entrySpreadSeconds"], reverse=True)
        accumulated, p95_occupancy = 0.0, 0
        for count, seconds in sorted(self.occupancy_seconds.items()):
            accumulated += seconds
            if accumulated >= self.sample_span*.95:
                p95_occupancy = count
                break
        return dict(success=self.error_count == 0, checkedUtc=datetime.now(timezone.utc).isoformat(),
            directory=str(self.directory.resolve()), checks=self.checks, errorCount=self.error_count,
            errors=self.errors, warnings=self.warnings, rows=dict(self.counts), eventKinds=dict(self.event_kinds),
            services=len(self.services), plannedJourneys=len(self.plans), enteredJourneys=len(self.entered),
            finishedJourneys=len(self.terminal), activeAtEnd=len(active_ids), terminalOutcomes=dict(self.terminal_counts),
            plannedRepresentedPeople=sum(p["representedPassengers"] for p in self.plans.values()),
            terminalRepresentedPeople=terminal_weight, activeRepresentedPeople=sum(self.plans[j]["representedPassengers"] for j in active_ids),
            sampleGroups=self.sample_groups, emptySampleGroups=self.empty_groups, peakActiveActors=self.max_active,
            peakRepresentedActivePeople=self.max_represented_active, poolReuses=self.pool_reuses,
            observedSampleSpanSeconds=self.sample_span, actorSeconds=self.actor_seconds,
            representedPersonSeconds=self.represented_seconds,
            meanActiveActors=self.actor_seconds/self.sample_span if self.sample_span else 0,
            meanRepresentedActivePeople=self.represented_seconds/self.sample_span if self.sample_span else 0,
            p95ActiveActors=p95_occupancy,
            completeEnteredGroups=sum(g["complete"] for g in group_spreads),
            partialEnteredGroups=sum(not g["complete"] for g in group_spreads),
            maximumGroupEntrySpreadSeconds=max((g["entrySpreadSeconds"] for g in group_spreads), default=0),
            largestGroupEntrySpreads=group_spreads[:10],
            maximumEntryDeferralSeconds=self.max_entry_delay, boardingGateChecks=self.boarding_checks,
            perServiceAdmissions={sid: dict(v) for sid, v in self.ledger.items()},
            arrivalProfileActors=dict(self.profile_counts), actualEntryRepresentedPeopleByElapsedHour=dict(self.hourly_entry_counts),
            maximumSampleGapSeconds=self.max_gap, maximumSampledSpeed=self.maximum_sampled_speed,
            maximumNativeVelocity=self.maximum_measured_speed, nativeVelocityWarnings=self.velocity_spike_count,
            nativeVelocityExamples=self.velocity_spikes, seatedSamplesChecked=self.seated_samples,
            alighterPolicyAudit=dict(enabled=self.stopover_contract, decisions=self.alighter_decision_count,
                positiveServiceBudgetChecks=self.alighter_budget_checks, actualEntryDeadlineSamples=self.alighter_deadline_checks,
                uniqueCompletedServices=sum(map(len, self.alighter_completions.values())),
                scope="Optional station services use the immutable actual-entry stopover deadline, full travel/queue/dwell/seating/Exit costs and unique completed allowance. Exit remains feasible after that clock expires; observed train status is telemetry. Boarding policies and native continuity/link bounds remain unchanged."),
            nativeGateLinkAudit=dict(telemetryEnabled=self.manifest.get("nativeGateLinkTelemetry") is True,
                flaggedSamples=self.native_link_flagged_samples, checkedSteps=self.native_link_steps,
                allowedSteps=self.native_link_allowed, rejectedSteps=self.native_link_rejected,
                ordinaryContinuityExceptions=self.native_link_exceptions,
                adjacentSampleIntervalGeometryAllowances=self.native_link_sample_interval_allowances,
                allowedDistanceMetres=self.native_link_distance, exceptionDistanceMetres=self.native_link_exception_distance,
                maximumAllowedStepMetres=self.native_link_max_step, maximumAllowedSampledSpeed=self.native_link_max_speed,
                examples=self.native_link_examples,
                scope="Only explicitly flagged owned native links, saved finite lane geometry, one-way direction, independent ingress grants and area permissions. Only an adjacent unflagged point near its proper approach/exit endpoint may extend in 2D by min(requestedSpeed*sampleInterval,.5m), with shared Euclidean lateral/longitudinal excess. Flagged geometry, floor and native speed/snap bounds remain fixed. Ordinary, initial and terminal bounds remain unchanged."),
            memoryContract="O(planned journeys + services + previous sample per entered journey), no trajectory-row accumulation",
            scope="Finite JSON, lifecycle, weighted demand/capacity, policy probabilities and sampled physical continuity. Not a full-day live test or full-density congestion validation.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--require-day-finished", action="store_true")
    args = parser.parse_args()
    root = args.directory.resolve()
    if args.output and args.output.resolve() in {root / n for n in INPUTS}:
        parser.error("Report cannot overwrite capture inputs")
    report = DayAudit(root, args.require_day_finished).run()
    serialized = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(serialized+"\n", encoding="utf-8")
    print(serialized)
    return 0 if report["success"] else 1


if __name__ == "__main__":
    sys.exit(main())
