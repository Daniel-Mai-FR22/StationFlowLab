"""Independently audit alighter station visits in an immutable, closed Daily capture.

Usage: python BuildRequests/validate_station_alighting_services.py CAPTURE --output REPORT.json
Open visits at the bounded capture end are censored, not failed or forced to use
their maximum allowance. Native recorded motion is simulator evidence, not a
claim that directions have been inferred from images.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path


INPUTS = ("manifest.json", "events.jsonl", "decisions.jsonl", "trajectories.jsonl")
SERVICES = {"Info", "Shop", "Toilet"}
ACTIVITY_NUMBERS = {"Info": 5, "Shop": 3, "Toilet": 2}
TIME_TOLERANCE = .003


def records(path):
    with path.open(encoding="utf-8-sig") as stream:
        for number, line in enumerate(stream, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except ValueError as error:
                    raise ValueError(f"{path.name}:{number}: {error}") from error


def file_hash(path):
    hashed = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hashed.update(block)
    return hashed.hexdigest()


def finite(value):
    return isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value)


def close(a, b):
    return finite(a) and finite(b) and abs(a - b) <= TIME_TOLERANCE


def flat(a, b):
    return math.hypot(a["x"] - b["x"], a["z"] - b["z"])


def signed32(value):
    value &= 0xffffffff
    return value if value < 0x80000000 else value - 0x100000000


def stable_hash(value):
    # C# hashes UTF-16 chars, rather than Python Unicode code points.
    hashed = 2166136261
    encoded = value.encode("utf-16-le", errors="surrogatepass")
    for offset in range(0, len(encoded), 2):
        char = encoded[offset] | encoded[offset + 1] << 8
        hashed = ((hashed ^ char) * 16777619) & 0xffffffff
    return signed32(hashed)


class SeededRandom:
    """Independent implementation of seeded System.Random's legacy algorithm.

    Unity's System.Random(int) uses this 56-element subtractive generator. The
    implementation is deliberately local so it does not invoke the simulator.
    """
    MAX = 2147483647

    def __init__(self, seed):
        subtraction = self.MAX if seed == -2147483648 else abs(seed)
        mj, mk = 161803398 - subtraction, 1
        self.values = [0] * 56
        self.values[55] = mj
        for i in range(1, 55):
            ii = (21 * i) % 55
            self.values[ii] = mk
            mk = signed32(mj - mk)
            if mk < 0:
                mk += self.MAX
            mj = self.values[ii]
        for _ in range(4):
            for i in range(1, 56):
                self.values[i] = signed32(self.values[i] - self.values[1 + (i + 30) % 55])
                if self.values[i] < 0:
                    self.values[i] += self.MAX
        self.next, self.next_pair = 0, 21

    def sample(self):
        self.next = self.next + 1 if self.next < 55 else 1
        self.next_pair = self.next_pair + 1 if self.next_pair < 55 else 1
        value = signed32(self.values[self.next] - self.values[self.next_pair])
        if value == self.MAX:
            value -= 1
        if value < 0:
            value += self.MAX
        self.values[self.next] = value
        return value / self.MAX


def replay_plan(plan, settings, seed):
    identity = plan.get("groupId") or plan["journeyId"]
    random = SeededRandom(signed32(seed ^ stable_hash("alighting-stopover|" + identity) ^ 1299721))
    selected = random.sample() < settings["serviceVisitorFraction"]
    if not selected:
        return False, 0, 0.
    low, high = settings["minimumVisits"], settings["maximumVisits"]
    quota = low + int(random.sample() * (high + 1 - low))
    budget = settings["minimumBudgetSeconds"] + random.sample() * (
        settings["maximumBudgetSeconds"] - settings["minimumBudgetSeconds"])
    return True, quota, budget


class AlightingAudit:
    def __init__(self, manifest):
        self.manifest = manifest
        self.errors, self.checks, self.error_count = [], 0, 0
        self.plans = {}
        self.alighters, self.entered, self.finished = {}, {}, {}
        self.completed = defaultdict(list)
        self.interrupted = defaultdict(list)
        self.selected = defaultdict(list)
        self.actual = defaultdict(list)
        self.motion = {}
        self.after_departure = []
        self.events, self.decisions, self.samples = Counter(), Counter(), Counter()
        self.service_actual, self.service_completed = Counter(), Counter()
        self.closed_at = None
        self.last_time = 0.
        self.deadline_checks = self.cost_checks = self.link_checks = 0
        self.settings = (manifest.get("arrivalSettings") or {}).get("alightingStopovers")
        self.gate_ingress_mask = 0
        for gate in manifest.get("platformControls", []):
            self.gate_ingress_mask |= 1 << gate["ingressArea"]
        self._manifest()

    def check(self, condition, message):
        self.checks += 1
        if not condition:
            self.error_count += 1
            if len(self.errors) < 100:
                self.errors.append(message)

    def _manifest(self):
        self.check(isinstance(self.settings, dict), "Missing saved alightingStopovers settings")
        if not isinstance(self.settings, dict):
            self.settings = {}
        required = ("serviceVisitorFraction", "minimumVisits", "maximumVisits", "minimumBudgetSeconds", "maximumBudgetSeconds")
        self.check(all(finite(self.settings.get(k)) for k in required), "Invalid/missing saved stopover settings")
        valid_settings = all(finite(self.settings.get(k)) for k in required)
        if valid_settings:
            fraction = self.settings["serviceVisitorFraction"]
            self.check(0 <= fraction <= 1, "Visitor fraction outside [0,1]")
            self.check(1 <= self.settings["minimumVisits"] <= self.settings["maximumVisits"] <= 2,
                       "Configured service allowance outside 1–2 visits")
            self.check(300 <= self.settings["minimumBudgetSeconds"] <= self.settings["maximumBudgetSeconds"] <= 720,
                       "Configured independent budget outside 5–12 minutes")
        profiles = {p.get("agentId"): p for p in self.manifest.get("profiles", [])}
        for plan in self.manifest.get("scheduledJourneys", []):
            jid = plan.get("journeyId")
            self.check(bool(jid) and jid not in self.plans, f"Duplicate/missing generated journey identity: {jid}")
            self.plans[jid] = plan
            is_alighter = plan.get("purpose") in (1, "Alighting")
            if not is_alighter:
                self.check(not plan.get("alightingServiceVisitor") and plan.get("alightingServiceVisitLimit", 0) == 0 and
                           plan.get("alightingStopoverSeconds", 0) == 0, f"{jid}: boarding profile gained alighting visits")
                continue
            self.alighters[jid] = plan
            selected = plan.get("alightingServiceVisitor")
            quota, budget = plan.get("alightingServiceVisitLimit"), plan.get("alightingStopoverSeconds")
            self.check(type(selected) is bool, f"{jid}: invalid generated selection flag")
            self.check(isinstance(quota, int) and not isinstance(quota, bool), f"{jid}: invalid generated visit quota")
            self.check(finite(budget), f"{jid}: invalid generated independent budget")
            if selected:
                self.check(isinstance(quota, int) and 1 <= quota <= 2 and finite(budget) and 300 <= budget <= 720,
                           f"{jid}: visitor quota/budget outside saved design bounds")
            else:
                self.check(quota == 0 and budget == 0, f"{jid}: direct exiter has optional visits or dwell budget")
            if valid_settings:
                expected = replay_plan(plan, self.settings, self.manifest["seed"])
                self.check(selected == expected[0] and quota == expected[1] and close(budget, expected[2]),
                           f"{jid}: seeded selection/quota/budget differs from independent replay")
            profile = profiles.get(jid, {})
            self.check(profile.get("purpose") == "Alighting", f"{jid}: immutable profile purpose differs from plan")
            for key in ("alightingServiceVisitor", "alightingServiceVisitLimit", "alightingStopoverSeconds"):
                self.check(profile.get(key) == plan.get(key), f"{jid}: profile and plan disagree on {key}")
        self.check(bool(self.alighters), "No generated alighting journeys")
        parties = defaultdict(list)
        for jid, plan in self.alighters.items():
            parties[plan.get("groupId") or jid].append(plan)
        for identity, members in parties.items():
            expected = tuple(members[0].get(k) for k in ("alightingServiceVisitor", "alightingServiceVisitLimit", "alightingStopoverSeconds"))
            self.check(all(tuple(p.get(k) for k in ("alightingServiceVisitor", "alightingServiceVisitLimit", "alightingStopoverSeconds")) == expected
                           for p in members), f"{identity}: party stopover profile differs between companions")

    def consume_event(self, event):
        now, jid, kind = event["time"], event.get("agentId"), event.get("kind")
        self.last_time = max(self.last_time, now)
        if kind == "run_ended":
            self.closed_at = now
        if jid not in self.alighters:
            return
        self.events[kind] += 1
        plan = self.alighters[jid]
        self.check(event.get("purpose") == "Alighting", f"{jid}: event changed immutable purpose")
        self.check(not event.get("boarded") and kind != "boarded" and not kind.startswith("coach_boarding") and
                   kind not in {"platform_access_granted", "platform_access_passed", "platform_ticket_scan_started", "platform_queue_joined", "coach_queue_joined"},
                   f"{jid}: alighter boarded or entered a controlled platform")
        if kind == "journey_entered":
            self.check(jid not in self.entered, f"{jid}: duplicated actual entry")
            self.entered[jid] = now
            profile = event.get("profile") or {}
            for key in ("alightingServiceVisitor", "alightingServiceVisitLimit", "alightingStopoverSeconds"):
                self.check(profile.get(key) == plan.get(key), f"{jid}: entry profile altered {key}")
        elif kind == "journey_finished":
            self.finished[jid] = event
            self.check(not event.get("detail", "").startswith("Boarded;"), f"{jid}: terminal boarded outcome")
        elif kind == "activity_completed":
            activity = event.get("detail", "").split(";", 1)[0]
            self.check(activity in SERVICES | {"Exit"}, f"{jid}: forbidden completed activity {activity}")
            if activity in SERVICES:
                previous = [v["activity"] for v in self.completed[jid]]
                self.check(plan.get("alightingServiceVisitor") and activity not in previous,
                           f"{jid}: direct exiter or duplicate station service completed: {activity}")
                self.completed[jid].append(dict(time=now, activity=activity, position=event.get("position")))
                self.service_completed[activity] += 1
                self.check(len(self.completed[jid]) <= plan.get("alightingServiceVisitLimit", 0), f"{jid}: completed above unique quota")
        elif kind == "activity_interrupted":
            activity = event.get("detail", "").split(":", 1)[0]
            self.interrupted[jid].append(dict(time=now, activity=activity, reason=event.get("detail")))
        activity = event.get("detail", "") if kind == "counter_service_completed" else "Toilet" if kind == "toilet_use_started" else None
        if activity in SERVICES:
            self.check(plan.get("alightingServiceVisitor"), f"{jid}: direct exiter used {activity}")
            self.actual[jid].append(dict(time=now, activity=activity, event=kind, position=event.get("position")))
            self.service_actual[activity] += 1
        if (activity in SERVICES or kind == "activity_completed" and event.get("detail", "").split(";", 1)[0] in SERVICES) and \
                event.get("actualStatus") == "Departed" and now >= event.get("actualDeparture", math.inf):
            self.after_departure.append(dict(journeyId=jid, time=now, event=kind, activity=activity or event["detail"].split(";", 1)[0],
                                            actualDeparture=event["actualDeparture"], actualStatus=event["actualStatus"]))

    def consume_decision(self, decision):
        jid = decision.get("agentId")
        if jid not in self.alighters:
            return
        plan, context, now = self.alighters[jid], decision.get("context") or {}, decision["time"]
        activity, poi = decision.get("selectedActivity"), decision.get("selectedPoi")
        self.decisions[activity] += 1
        label = f"{jid} decision {decision.get('decisionIndex')} at {now:.3f}"
        self.check(context.get("alightingJourney") is True and context.get("requireInitialDisplay") is False,
                   label + ": missing independent alighter context or forced Display")
        self.check(jid in self.entered, label + ": decision without actual entry")
        if jid not in self.entered:
            return
        deadline = self.entered[jid] + plan.get("alightingStopoverSeconds", 0)
        self.check(close(context.get("stationStopoverDeadlineSeconds"), deadline) and close(context.get("nowSeconds"), now),
                   label + ": clock/deadline differs from actual entry plus generated budget")
        completed_before = [v for v in self.completed[jid] if v["time"] <= now + TIME_TOLERANCE]
        self.check(context.get("stationServicesRemaining") == max(0, plan.get("alightingServiceVisitLimit", 0) - len(completed_before)),
                   label + ": remaining allowance disagrees with completed unique services")
        self.check(activity in SERVICES | {"Exit", "None"}, label + f": forbidden selected activity {activity}")
        if activity not in SERVICES:
            return
        self.check(plan.get("alightingServiceVisitor") and context.get("stationServicesRemaining", 0) > 0,
                   label + ": service selected without positive allowance")
        self.check(activity not in [v["activity"] for v in completed_before], label + ": completed service selected again")
        option = next((p for p in decision.get("options", []) if p.get("poiId") == poi and p.get("activity") == activity), None)
        raw = next((p for p in decision.get("inputOptions", []) if p.get("poiId") == poi), None)
        self.check(option is not None and raw is not None, label + ": selected option missing from recorded evaluation/input")
        if option is None or raw is None:
            return
        self.check(raw.get("activity") in (activity, ACTIVITY_NUMBERS[activity]), label + ": selected activity differs from raw policy input")
        self.check(raw.get("available") is True and raw.get("reachable") is True and finite(option.get("probability")) and option["probability"] > 0,
                   label + ": unavailable/unreachable/nonpositive service selected")
        keys = ("travel", "queue", "dwell", "seatTravel", "seatingTransition", "seatedDwell", "toExit")
        self.check(all(finite(option.get(key)) and option[key] >= 0 for key in keys), label + ": invalid full service/Exit cost")
        if not all(finite(option.get(key)) for key in keys):
            return
        reserve = context.get("safetyMarginSeconds")
        self.check(finite(reserve) and reserve >= 0, label + ": invalid Exit reserve")
        if not finite(reserve):
            return
        cost, remaining = sum(option[key] for key in keys) + reserve, deadline - now
        self.cost_checks += 1
        self.check(remaining > 0 and cost <= remaining + TIME_TOLERANCE,
                   label + ": service cost plus native Exit ETA exceeds independent positive budget")
        self.check(close(option.get("slack"), remaining - cost), label + ": recorded slack differs from independently summed cost")
        self.check(option.get("toPlatform") == 0 and raw.get("toPlatformSeconds") == 0 and close(option.get("toExit"), raw.get("toExitSeconds")),
                   label + ": train/platform cost leaked into alighter service budget")
        self.selected[jid].append(dict(time=now, activity=activity, poiId=poi, costSeconds=cost, remainingSeconds=remaining,
                                       exitTravelSeconds=option["toExit"], observedTrainStatus=decision.get("observedStatus")))

    def consume_trajectory(self, sample):
        jid = sample.get("agentId")
        if jid not in self.alighters:
            return
        plan, now = self.alighters[jid], sample["time"]
        self.samples[sample.get("activity")] += 1
        label = f"{jid} sample {sample.get('sampleId')} at {now:.3f}"
        self.check(sample.get("purpose") == "Alighting" and not sample.get("boarded") and
                   sample.get("activity") in SERVICES | {"Exit"}, label + ": invalid purpose/boarding/activity")
        self.check(not sample.get("platformAccessGranted") and not sample.get("platformAccessPassed") and
                   sample.get("nativeLinkDirection") != "Ingress", label + ": controlled ingress/reentry observed")
        self.link_checks += 1
        mask = sample.get("navigationAreaMask")
        self.check(isinstance(mask, int) and mask & self.gate_ingress_mask == 0, label + ": native ingress areas enabled for alighter")
        if jid in self.entered:
            self.deadline_checks += 1
            self.check(close(sample.get("alightingStopoverDeadlineSeconds"), self.entered[jid] + plan.get("alightingStopoverSeconds", 0)),
                       label + ": live deadline differs from actual entry plus immutable plan budget")
        else:
            self.check(False, label + ": trajectory without actual entry")
        strict = len([v for v in self.completed[jid] if v["time"] < now - TIME_TOLERANCE])
        inclusive = len([v for v in self.completed[jid] if v["time"] <= now + TIME_TOLERANCE])
        counter = sample.get("alightingServiceVisitsCompleted")
        self.check(isinstance(counter, int) and strict <= counter <= inclusive and counter <= plan.get("alightingServiceVisitLimit", 0),
                   label + ": completed counter disagrees with event ledger/quota")
        position = sample.get("position")
        if not isinstance(position, dict) or not all(finite(position.get(k)) for k in ("x", "y", "z")):
            self.check(False, label + ": invalid recorded native position")
            return
        motion = self.motion.setdefault(jid, dict(firstTime=now, first=position, lastTime=now, last=position,
                                                  sampledDistance=0., maximumDisplacement=0., services={}))
        motion["sampledDistance"] += flat(motion["last"], position)
        motion["maximumDisplacement"] = max(motion["maximumDisplacement"], flat(motion["first"], position))
        motion["lastTime"], motion["last"] = now, position
        motion["lastActivity"], motion["lastExecution"] = sample.get("activity"), sample.get("execution")
        if sample.get("activity") in SERVICES and sample.get("targetPoi"):
            evidence = motion["services"].setdefault(sample["activity"], dict(firstTime=now, first=position, lastTime=now,
                                                                             last=position, poiId=sample["targetPoi"], states=Counter()))
            evidence["lastTime"], evidence["last"] = now, position
            evidence["states"][sample.get("execution")] += 1

    def result(self, require_outcomes=True):
        self.check(self.closed_at is not None, "Capture has no run_ended boundary")
        self.check(bool(self.entered), "No actual alighter entries")
        direct_exits = [jid for jid, event in self.finished.items() if not self.alighters[jid].get("alightingServiceVisitor") and
                        event.get("detail", "").startswith("Exited;")]
        service_exits = [jid for jid, event in self.finished.items() if self.actual.get(jid) and event.get("detail", "").startswith("Exited;")]
        if require_outcomes:
            self.check(bool(direct_exits), "No observed direct alighter exit in this bounded capture")
            self.check(bool(self.actual), "No actual alighter station service observed in this bounded capture")
        for jid, visits in self.actual.items():
            for visit in visits:
                self.check(any(choice["activity"] == visit["activity"] and choice["time"] <= visit["time"] + TIME_TOLERANCE
                               for choice in self.selected[jid]), f"{jid}: actual {visit['activity']} use has no preceding feasible recorded selection")
        for jid, visits in self.completed.items():
            for visit in visits:
                self.check(any(use["activity"] == visit["activity"] and use["time"] <= visit["time"] + TIME_TOLERANCE
                               for use in self.actual.get(jid, [])), f"{jid}: completed {visit['activity']} without observed counter/WC use")
        parties = {p.get("groupId") or jid: p for jid, p in self.alighters.items()}
        selected_plans = [p for p in self.alighters.values() if p.get("alightingServiceVisitor")]
        selected_parties = [p for p in parties.values() if p.get("alightingServiceVisitor")]
        weight = lambda plans: sum(p.get("representedPassengers", 1) for p in plans)
        total_weight, selected_weight = weight(self.alighters.values()), weight(selected_plans)
        examples = []
        ids_by_type = {activity: sorted(jid for jid, uses in self.actual.items() if any(v["activity"] == activity for v in uses))
                       for activity in sorted(SERVICES)}
        for jid in sorted(self.actual):
            motion = self.motion.get(jid, {})
            examples.append(dict(journeyId=jid, planBudgetSeconds=self.alighters[jid].get("alightingStopoverSeconds"),
                                 visitLimit=self.alighters[jid].get("alightingServiceVisitLimit"), actualEntrySeconds=self.entered.get(jid),
                                 decisions=self.selected[jid], actualUse=self.actual[jid], completed=self.completed[jid],
                                 interruptions=self.interrupted[jid], terminal=self.finished.get(jid, {}).get("detail"),
                                 sampledMotion=motion))
        open_visits = []
        for jid, visits in self.selected.items():
            motion = self.motion.get(jid, {})
            if jid not in self.finished and visits and motion.get("lastActivity") in SERVICES:
                open_visits.append(dict(journeyId=jid, lastSelected=visits[-1], completed=self.completed[jid],
                                        lastObserved=motion.get("lastTime"), lastExecution=motion.get("lastExecution")))
        return dict(success=not self.error_count, checks=self.checks, errorCount=self.error_count, errors=self.errors,
                    closedAtSeconds=self.closed_at, configuredStopovers=self.settings,
                    generated=dict(alighters=len(self.alighters), selectedVisitors=len(selected_plans), parties=len(parties),
                                   selectedParties=len(selected_parties), actorVisitorFraction=len(selected_plans) / len(self.alighters) if self.alighters else None,
                                   partyVisitorFraction=len(selected_parties) / len(parties) if parties else None,
                                   representedAlighters=total_weight, representedVisitors=selected_weight,
                                   representedVisitorFraction=selected_weight / total_weight if total_weight else None),
                    observed=dict(entered=len(self.entered), finished=len(self.finished), directExits=len(direct_exits),
                                  serviceVisitors=len(self.actual), serviceVisitorsExited=len(service_exits),
                                  selectedServiceDecisions=sum(map(len, self.selected.values())), actualUsesByType=dict(self.service_actual),
                                  uniqueCompletedByType=dict(self.service_completed), interruptions=sum(map(len, self.interrupted.values())),
                                  activeEnteredAtEnd=len(self.entered.keys() - self.finished.keys()), censoredServiceVisits=len(open_visits)),
                    deadlineChecks=self.deadline_checks, fullCostChecks=self.cost_checks, noIngressChecks=self.link_checks,
                    actualUseJourneyIdsByType=ids_by_type, postDepartureServiceEvidenceCount=len(self.after_departure),
                    directExitExamples=direct_exits[:8], serviceExamples=examples[:12],
                    servicesAfterActualTrainDeparture=self.after_departure[:20], censoredVisitExamples=open_visits[:12],
                    eventKinds=dict(self.events), selectedActivities=dict(self.decisions), trajectoryActivities=dict(self.samples),
                    scope="Independent seeded profile replay, actual-entry stopover clock, full service plus native Exit cost, unique completed allowance, no boarding/ingress, and observed outcomes. Interrupted/censored visits are retained; a selected visitor need not exhaust its maximum quota.")


def audit_capture(directory):
    directory = Path(directory).resolve()
    paths = [directory / name for name in INPUTS]
    before = {p.name: file_hash(p) for p in paths}
    manifest = json.loads(paths[0].read_text(encoding="utf-8-sig"))
    audit = AlightingAudit(manifest)
    for event in records(paths[1]):
        audit.consume_event(event)
    for decision in records(paths[2]):
        audit.consume_decision(decision)
    for trajectory in records(paths[3]):
        audit.consume_trajectory(trajectory)
    report = audit.result()
    report.update(directory=str(directory), checkedUtc=datetime.now(timezone.utc).isoformat(), inputHashes=before,
                  inputsPreserved=all(file_hash(p) == before[p.name] for p in paths))
    report["success"] &= report["inputsPreserved"]
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.output and args.directory.resolve() in args.output.resolve().parents:
        parser.error("Place the report outside the immutable capture directory")
    try:
        report = audit_capture(args.directory)
    except (OSError, ValueError, KeyError, TypeError) as error:
        report = dict(success=False, errors=[str(error)], errorCount=1)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("success", "checks", "errorCount", "errors", "generated", "observed") if k in report}, ensure_ascii=False))
    return 0 if report["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
