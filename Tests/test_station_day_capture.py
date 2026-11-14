"""Adversarial lifecycle/capacity fixtures for the independent daily validator."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from test_station_decision_capture import fixture as policy_fixture
from validate_station_day_capture import DayAudit, CountSink
from native_gate_link_audit import audit_native_link_step
from test_native_gate_link_audit import fixture as native_link_fixture


def fixture():
    old, decisions, rows, _ = policy_fixture()
    needs = old["profiles"][0]["needs"]
    zero = dict(x=0, y=0, z=0)
    service = dict(serviceId="trip", trackNumber=1, seatedCapacity=220, expectedBoarding=60,
                   expectedAlighting=0, throughPassengers=0, arrivalOffsetSeconds=-10,
                   departureOffsetSeconds=200, doorDurationSeconds=8,
                   approachDurationSeconds=30, departureDurationSeconds=30)
    plans, profiles = [], []
    for index, (jid, entry, weight) in enumerate((("a", 0, 10), ("b", 1, 20), ("future", 100, 30))):
        plan = dict(journeyId=jid, serviceId="trip", trackNumber=1, representedPassengers=weight,
                    purpose="Boarding", arrivalProfile="Ordinary", plannedEntrySeconds=entry,
                    publishedDepartureSeconds=200, entranceId="north", groupId="", groupSize=1,
                    groupMemberIndex=0, walkSpeed=1.1, fastWalkSpeed=1.6, needs=needs)
        plans.append(plan)
        profiles.append(dict(old["profiles"][0], agentId=jid, representedPassengers=weight))
    manifest = dict(old, schema="stationflow.station_decision_day.v2", participantCount=3, actorSlotCount=1,
                     services=[service], scheduledJourneys=plans, profiles=profiles)
    events = [dict(time=0, kind="run_started", detail="fixture", agentId="", serviceId="")]
    trajectory = []
    decision_rows = []
    sample = 0
    for jid, entry, weight, outcome in (("a", 0, 10, "Boarded"), ("b", 1, 20, "Exited")):
        events.append(dict(time=entry, kind="journey_entered", agentId=jid, serviceId="trip", actorSlotId=0,
                           position=zero, walkSpeed=1.1, fastWalkSpeed=1.6, representedPassengers=weight))
        decision = copy.deepcopy(decisions[0])
        decision.update(agentId=jid, time=entry, actorSlotId=0)
        decision["context"]["nowSeconds"] = entry
        decision_rows.append(decision)
        for offset in (0, .2):
            sample += 1
            events.append(dict(time=entry+offset, kind="trajectory_sample", sampleId=sample, activeJourneyIds=[jid]))
            row = copy.deepcopy(rows[0])
            row.update(time=entry+offset, agentId=jid, actorSlotId=0, sampleId=sample, groupId="",
                       representedPassengers=weight, purpose="Boarding")
            trajectory.append(row)
        if outcome == "Boarded":
            events.append(dict(time=entry+.4, kind="boarded", agentId=jid, serviceId="trip", actorSlotId=0,
                               actualStatus="Boarding", actualCutoff=192, actualDeparture=200, capacity=220, occupancyAfter=weight,
                               representedPassengers=weight))
        events.append(dict(time=entry+.4, kind="journey_finished", agentId=jid, serviceId="trip", actorSlotId=0,
                           outcome=outcome, actualStatus="Boarding", actualCutoff=192,
                           actualDeparture=200, position=zero, representedPassengers=weight))
        sample += 1
        events.append(dict(time=entry+.4, kind="trajectory_sample", sampleId=sample, activeJourneyIds=[]))
    events.append(dict(time=1.4, kind="run_ended", detail="fixture", agentId="", serviceId=""))
    return manifest, decision_rows, trajectory, events


def validate(data, require_finished=False):
    base = Path(__file__).resolve().parent
    with tempfile.TemporaryDirectory(dir=base, prefix=".day-fixture-") as temp:
        path = Path(temp)
        assert path.resolve().parent == base
        for name, value in zip(("manifest.json", "decisions.jsonl", "trajectories.jsonl", "events.jsonl"), data):
            content = json.dumps(value) if name == "manifest.json" else "\n".join(json.dumps(r) for r in value)
            (path/name).write_text(content+"\n" if content else "", encoding="utf-8")
        audit = DayAudit(path, require_finished)
        result = audit.run()
        assert isinstance(audit.decisions, CountSink)
        assert len(audit.last_samples) <= len(audit.plans)
        return result


class DailyCaptureValidation(unittest.TestCase):
    def mobility_fixture(self, grouped=True):
        data = fixture()
        data[0]["mobilityModelVersion"] = "adult-mobility-v1"
        for i, (plan, profile) in enumerate(zip(data[0]["scheduledJourneys"], data[0]["profiles"])):
            mobility = dict(preferredWalkSpeed=1.5 if i == 0 else .6, preferredFastWalkSpeed=2 if i == 0 else .7)
            changes = dict(walkSpeed=.54, fastWalkSpeed=.63, mobilityProfile=mobility)
            if grouped: changes.update(groupId="party", groupSize=3, groupMemberIndex=i, plannedEntrySeconds=i)
            plan.update(changes); profile.update(changes)
        for row in data[2]: row["groupId"] = "party" if grouped else ""
        for event in data[3]:
            if event["kind"] == "journey_entered":
                plan = next(p for p in data[0]["scheduledJourneys"] if p["journeyId"] == event["agentId"])
                event.update(walkSpeed=.54, fastWalkSpeed=.63, mobilityProfile=copy.deepcopy(plan["mobilityProfile"]))
        return data

    def separate_mobility_party(self, data, timestamp=.05):
        data[3].append(dict(time=timestamp, kind="group_separated", agentId="a", serviceId="trip", groupId="party"))
        data[3].sort(key=lambda event: event["time"])

    def test_solo_saved_preferred_pace_bounds_actual_continuity(self):
        data = self.mobility_fixture(False)
        data[2][1]["position"] = dict(x=.7, y=0, z=0)
        self.assertTrue(validate(data)["success"], validate(data)["errors"])
        data[2][1]["position"] = dict(x=4, y=0, z=0)
        self.assertTrue(any("sampled-position jump" in e for e in validate(data)["errors"]))

    def test_party_retains_its_cap_until_independent_separation(self):
        data = self.mobility_fixture()
        data[2][1]["position"] = dict(x=.7, y=0, z=0)
        data[2][1]["usingGroupPace"] = False
        self.assertTrue(any("sampled-position jump" in e for e in validate(data)["errors"]))
        self.separate_mobility_party(data)
        self.assertTrue(validate(data)["success"], validate(data)["errors"])

    def test_mid_interval_separation_cannot_retroactively_widen_the_whole_step(self):
        data = self.mobility_fixture()
        data[2][1]["position"] = dict(x=.7, y=0, z=0)
        self.separate_mobility_party(data, .19)
        self.assertTrue(any("sampled-position jump" in e for e in validate(data)["errors"]))

    def test_separated_preference_applies_to_terminal_but_never_exempts_teleports(self):
        data = self.mobility_fixture()
        terminal = next(event for event in data[3] if event["kind"] == "journey_finished")
        terminal["position"] = dict(x=.7, y=0, z=0)
        self.assertTrue(any("terminal snapshot teleported" in e for e in validate(data)["errors"]))
        self.separate_mobility_party(data)
        self.assertTrue(validate(data)["success"], validate(data)["errors"])
        terminal["position"] = dict(x=4, y=0, z=0)
        self.assertTrue(any("terminal snapshot teleported" in e for e in validate(data)["errors"]))

    def test_companion_entering_after_separation_uses_its_own_first_sample_clock(self):
        data = self.mobility_fixture()
        for plan in (data[0]["scheduledJourneys"][1], data[0]["profiles"][1]):
            plan["mobilityProfile"].update(preferredWalkSpeed=1.5, preferredFastWalkSpeed=2)
        for event in data[3]:
            if event["kind"] == "journey_entered" and event.get("agentId") == "b":
                event["mobilityProfile"].update(preferredWalkSpeed=1.5, preferredFastWalkSpeed=2)
            if event["kind"] == "trajectory_sample" and event["time"] == 1: event["time"] = 1.15
        for row in data[2]:
            if row["agentId"] == "b":
                if row["time"] == 1: row["time"] = 1.15
                row["position"] = dict(x=.65, y=0, z=0)
        self.assertTrue(any("first sample teleports" in e for e in validate(data)["errors"]))
        self.separate_mobility_party(data)
        self.assertTrue(validate(data)["success"], validate(data)["errors"])
        data[2][2]["position"] = dict(x=4, y=0, z=0)
        self.assertTrue(any("first sample teleports" in e for e in validate(data)["errors"]))

    def test_mobility_cannot_enlarge_historical_capture_bounds(self):
        data = self.mobility_fixture(False)
        del data[0]["mobilityModelVersion"]
        data[2][1]["position"] = dict(x=.7, y=0, z=0)
        self.assertTrue(any("sampled-position jump" in e for e in validate(data)["errors"]))

    def test_saved_preferred_speeds_must_be_finite_bounded_and_immutable(self):
        for invalid in (float("nan"), float("inf"), 100, -1, 0):
            data = self.mobility_fixture()
            for plan in data[0]["scheduledJourneys"]: plan["mobilityProfile"]["preferredFastWalkSpeed"] = invalid
            self.assertFalse(validate(data)["success"])
        data = self.mobility_fixture()
        data[0]["profiles"][0]["mobilityProfile"] = dict(preferredWalkSpeed=1, preferredFastWalkSpeed=1.5)
        self.assertTrue(any("profile/plan mobility mismatch" in e for e in validate(data)["errors"]))
        data = self.mobility_fixture()
        data[3][1]["mobilityProfile"]["preferredFastWalkSpeed"] = 1.5
        self.assertTrue(any("entry changed immutable mobility" in e for e in validate(data)["errors"]))

    def test_native_link_keeps_profile_permissions_geometry_and_requested_speed_bounds(self):
        manifest, before, after, profile = native_link_fixture()
        audit = DayAudit(Path(__file__).parent)
        audit.plans["a"] = dict(groupId="party")
        audit.profiles["a"] = dict(profile, walkSpeed=.54, fastWalkSpeed=.63)
        audit.mobility_profiles["a"] = dict(preferredWalkSpeed=1.5, preferredFastWalkSpeed=2)
        self.assertFalse(audit_native_link_step(manifest, before, after, audit.speed_profile_at("a", after["time"]))["allowed"])
        audit.separated_groups["party"] = before["time"] - 1
        expanded = audit.speed_profile_at("a", after["time"])
        self.assertTrue(audit_native_link_step(manifest, before, after, expanded)["allowed"])
        invalid = copy.deepcopy(after); invalid["requestedNavigationSpeed"] = 2.1
        self.assertFalse(audit_native_link_step(manifest, before, invalid, expanded)["allowed"])
        invalid = copy.deepcopy(after); invalid["position"]["x"] += 3
        self.assertFalse(audit_native_link_step(manifest, before, invalid, expanded)["allowed"])
        invalid = copy.deepcopy(after); invalid["platformAccessGranted"] = False
        self.assertFalse(audit_native_link_step(manifest, before, invalid, expanded)["allowed"])

    def alighter_fixture(self, activity="Info"):
        data = fixture()
        manifest, decisions, rows, events = data
        manifest["services"][0].update(expectedBoarding=50, expectedAlighting=10)
        manifest["arrivalSettings"] = dict(alightingStopovers=dict(serviceVisitorFraction=.4,
            minimumVisits=1, maximumVisits=2, minimumBudgetSeconds=300, maximumBudgetSeconds=720))
        manifest["pois"].append(dict(poiId="station-service", activity=activity, position=dict(x=0,y=0,z=0)))
        for plan, profile in zip(manifest["scheduledJourneys"], manifest["profiles"]):
            values = dict(alightingServiceVisitor=plan["journeyId"] == "a",
                alightingServiceVisitLimit=1 if plan["journeyId"] == "a" else 0,
                alightingStopoverSeconds=400 if plan["journeyId"] == "a" else 0)
            plan.update(values); profile.update(values)
            if plan["journeyId"] == "a":
                plan.update(purpose="Alighting", arrivalProfile="Alighting")
                profile.update(purpose="Alighting")
        for decision in decisions:
            context = decision["context"]
            context.update(alightingJourney=decision["agentId"] == "a", requireInitialDisplay=False,
                           stationStopoverDeadlineSeconds=400 if decision["agentId"] == "a" else 0,
                           stationServicesRemaining=1 if decision["agentId"] == "a" else 0)
            if decision["agentId"] == "a":
                source, option = decision["inputOptions"][0], decision["options"][0]
                source.update(activity=activity, poiId="station-service", toPlatformSeconds=0, toExitSeconds=5,
                              seatTravelSeconds=6, seatingTransitionSeconds=4, seatedDurationSeconds=20)
                option.update(activity=activity, poiId="station-service", toPlatform=0, toExit=5,
                              seatTravel=6, seatingTransition=4, seatedDwell=20, slack=346)
                decision.update(selectedActivity=activity, selectedPoi="station-service")
        for row in rows:
            row.update(alightingStopoverDeadlineSeconds=400 if row["agentId"] == "a" else 0,
                       alightingServiceVisitsCompleted=0)
            if row["agentId"] == "a": row.update(purpose="Alighting", activity=activity, targetPoi="station-service")
        events[:] = [e for e in events if e["kind"] != "boarded"]
        for event in events:
            if event.get("agentId") == "a" and event["kind"] == "journey_finished": event["outcome"] = "Exited"
            if event["kind"] == "journey_entered":
                plan = next(p for p in manifest["scheduledJourneys"] if p["journeyId"] == event["agentId"])
                event.update({k:plan[k] for k in ("alightingServiceVisitor", "alightingServiceVisitLimit", "alightingStopoverSeconds")})
        return data

    def test_alighter_seated_service_uses_own_deadline_with_full_exit_cost(self):
        for activity in ("Info", "Shop", "Toilet"):
            data = self.alighter_fixture(activity)
            report = validate(data)
            self.assertTrue(report["success"], report["errors"])
            self.assertEqual(1, report["alighterPolicyAudit"]["positiveServiceBudgetChecks"])

    def test_departed_cancelled_train_does_not_change_alighter_service_budget(self):
        for status in ("Departed", "Cancelled", "Closed"):
            data = self.alighter_fixture("Shop")
            data[1][0]["context"]["trainStatus"] = status
            data[1][0]["observedStatus"] = status
            self.assertTrue(validate(data)["success"])

    def test_alighter_deadline_cannot_be_reset_or_taken_from_train_cutoff(self):
        data = self.alighter_fixture(); data[1][0]["context"]["stationStopoverDeadlineSeconds"] = 200
        self.assertTrue(any("actual entry plus immutable budget" in e for e in validate(data)["errors"]))
        data = self.alighter_fixture(); data[2][0]["alightingStopoverDeadlineSeconds"] = 401
        self.assertTrue(any("live alighter deadline" in e for e in validate(data)["errors"]))

    def test_alighter_seated_cost_slack_cannot_omit_exit_or_transitions(self):
        for offset in (5, 4, 20):
            data = self.alighter_fixture(); data[1][0]["options"][0]["slack"] += offset
            self.assertTrue(any("slack omits" in e for e in validate(data)["errors"]))

    def test_alighter_service_cannot_exceed_own_budget(self):
        data = self.alighter_fixture()
        data[1][0]["inputOptions"][0]["toExitSeconds"] = 400
        data[1][0]["options"][0].update(toExit=400, slack=-49)
        self.assertTrue(any("insufficient independent budget" in e for e in validate(data)["errors"]))

    def test_alighter_cannot_bypass_contract_with_missing_flag_or_false_boarder_identity(self):
        data = self.alighter_fixture(); del data[1][0]["context"]["alightingJourney"]
        self.assertTrue(any("evaluated alighting purpose" in e for e in validate(data)["errors"]))
        data = self.alighter_fixture(); data[1][1]["context"]["alightingJourney"] = True
        self.assertTrue(any("evaluated alighting purpose" in e for e in validate(data)["errors"]))

    def test_alighter_still_requires_route_capacity_and_matching_exit_cost(self):
        for field in ("reachable", "available"):
            data = self.alighter_fixture(); data[1][0]["inputOptions"][0][field] = False
            self.assertTrue(any("positive probability" in e for e in validate(data)["errors"]))
        data = self.alighter_fixture(); data[1][0]["options"][0]["toExit"] = 0
        self.assertTrue(any("inconsistent toExitSeconds" in e for e in validate(data)["errors"]))

    def test_alighter_quota_and_unique_completion_ledger_are_strict(self):
        data = self.alighter_fixture(); data[1][0]["context"]["stationServicesRemaining"] = 0
        self.assertTrue(any("remaining allowance" in e for e in validate(data)["errors"]))
        data = self.alighter_fixture()
        for time in (.05, .1):
            data[3].append(dict(time=time,kind="activity_completed",agentId="a",serviceId="trip",detail="Info; duration=10"))
        data[3].sort(key=lambda e:e["time"])
        report = validate(data)
        self.assertTrue(any("duplicate alighter service completion" in e for e in report["errors"]))
        self.assertTrue(any("above unique allowance" in e for e in report["errors"]))

    def test_alighter_exit_remains_feasible_after_expired_zero_budget(self):
        data = self.alighter_fixture("Exit")
        for p in (data[0]["scheduledJourneys"][0],data[0]["profiles"][0]):
            p.update(alightingServiceVisitor=False,alightingServiceVisitLimit=0,alightingStopoverSeconds=0)
        data[1][0]["context"].update(stationStopoverDeadlineSeconds=0,stationServicesRemaining=0)
        data[1][0]["options"][0]["slack"] = 0
        for row in data[2]:
            if row["agentId"] == "a": row["alightingStopoverDeadlineSeconds"] = 0
        data[3][1].update(alightingServiceVisitor=False,alightingServiceVisitLimit=0,alightingStopoverSeconds=0)
        self.assertTrue(validate(data)["success"])

    def test_alighter_platform_boarding_and_display_probabilities_rejected(self):
        for activity in ("Platform", "Board", "Display", "Cafe"):
            self.assertTrue(any("unsupported station activity" in e for e in validate(self.alighter_fixture(activity))["errors"]))

    def test_boarding_cost_checks_remain_unchanged_with_new_alighter_contract(self):
        data = self.alighter_fixture()
        source, option = data[1][1]["inputOptions"][0], data[1][1]["options"][0]
        source.update(seatTravelSeconds=0,seatingTransitionSeconds=0,seatedDurationSeconds=250)
        option.update(seatTravel=0,seatingTransition=0,seatedDwell=250,slack=-75)
        report = validate(data)
        self.assertTrue(any("seated visit selected despite insufficient total budget" in e for e in report["errors"]))

    def physical_fixture(self):
        data = self.controlled_fixture()
        data[0]["physicalArrivalQueues"] = True
        gate = data[0]["platformControls"][0]
        gate.pop("scanSecondsPerPassenger")
        gate.update(scanSecondsPerActor=.1, queueCapacity=2, arrivalTolerance=.22, minimumQueueSeparation=.7,
                    queueAnchors=[dict(x=0,y=0,z=0),dict(x=0,y=0,z=-.95)])
        joined = next(e for e in data[3] if e["kind"] == "platform_queue_joined")
        joined.update(position=dict(x=0,y=0,z=0),queueArrivalPosition=dict(x=0,y=0,z=0),
                      queueSlotIndex=0,queuePredecessorJourneyId="",queuePredecessorPosition=dict(x=0,y=0,z=0))
        return data

    def test_physical_arrival_and_short_actor_scan_preserve_weighted_capacity(self):
        report = validate(self.physical_fixture())
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["perServiceAdmissions"]["trip"]["boarded"],10)

    def test_remote_registration_is_rejected(self):
        data = self.physical_fixture()
        next(e for e in data[3] if e["kind"] == "platform_queue_joined")["position"]["x"] = 10
        self.assertTrue(any("remote queue reservation" in e for e in validate(data)["errors"]))

    def test_actor_scan_cannot_complete_before_configured_duration(self):
        data = self.physical_fixture()
        data[0]["platformControls"][0]["scanSecondsPerActor"] = 1
        self.assertTrue(any("scan completed too quickly" in e for e in validate(data)["errors"]))

    def test_registration_requires_saved_physical_tail_anchor(self):
        data = self.physical_fixture()
        next(e for e in data[3] if e["kind"] == "platform_queue_joined")["queueArrivalPosition"]["x"] = .2
        self.assertTrue(any("arrival point differs" in e for e in validate(data)["errors"]))

    def test_registration_rejects_wrong_fifo_predecessor(self):
        data = self.physical_fixture()
        next(e for e in data[3] if e["kind"] == "platform_queue_joined")["queuePredecessorJourneyId"] = "b"
        self.assertTrue(any("predecessor differs" in e for e in validate(data)["errors"]))

    def test_registration_rejects_physical_predecessor_overlap(self):
        data = self.physical_fixture()
        control = data[0]["platformControls"][0]
        event = next(e for e in data[3] if e["kind"] == "platform_queue_joined")
        event.update(queuePredecessorJourneyId="b",queuePredecessorPosition=dict(x=.3,y=0,z=0))
        audit = DayAudit(Path("unused")); audit.manifest = data[0]
        audit.check_physical_queue_join(event,["b"],control,"fixture")
        self.assertTrue(any("overlaps predecessor" in e for e in audit.errors))

    def controlled_fixture(self):
        data = fixture(); manifest, _, rows, events = data
        manifest.update(controlledPlatformAccess=True,
            platformControls=[dict(track=1, scanSecondsPerPassenger=.01, platform=dict(x=0,y=0,z=100))],
            coachAnchors=[dict(track=1, coachNumber=1, position=dict(x=0,y=0,z=0))])
        manifest["services"][0]["coachSeatCapacities"] = [220]
        seat = 1
        for p in manifest["scheduledJourneys"]:
            p.update(ticketId="ticket/"+p["journeyId"], coachNumber=1,
                     seatNumbers=list(range(seat, seat+p["representedPassengers"])))
            seat += p["representedPassengers"]
        for row in rows:
            row.update(coachNumber=1, platformAccessGranted=False, platformAccessPassed=row["agentId"]=="a" and row["time"]>=.18)
        for e in events:
            if e["kind"] == "boarded": e.update(platformAccessPassed=True, position=dict(x=0,y=0,z=0))
        extra = [dict(time=t,kind=kind,agentId="a",serviceId="trip",representedPassengers=10,
                      trainPresent=True,platformOpensAt=-100,actualCutoff=192)
                 for t, kind in ((.01,"platform_queue_joined"),(.02,"platform_ticket_scan_started"),(.15,"platform_access_granted"),(.18,"platform_access_passed"))]
        events.extend(extra); events.sort(key=lambda e:e["time"])
        return data

    def test_controlled_coach_ticket_and_weighted_scan(self):
        report = validate(self.controlled_fixture())
        self.assertTrue(report["success"], report["errors"])

    def test_controlled_duplicate_seat_is_rejected(self):
        data = self.controlled_fixture()
        data[0]["scheduledJourneys"][1]["seatNumbers"][0] = 1
        self.assertTrue(any("duplicate coach seat" in e for e in validate(data)["errors"]))

    def test_controlled_boarding_cannot_bypass_scan(self):
        data = self.controlled_fixture()
        data[3][:] = [e for e in data[3] if not e["kind"].startswith("platform_")]
        self.assertTrue(any("bypassed ticket control" in e for e in validate(data)["errors"]))

    def test_controlled_scan_accounts_for_cohort_weight(self):
        data = self.controlled_fixture()
        next(e for e in data[3] if e["kind"]=="platform_access_granted")["time"] = .03
        data[3].sort(key=lambda e:e["time"])
        self.assertTrue(any("too quickly" in e for e in validate(data)["errors"]))

    def test_controlled_grant_requires_present_train(self):
        data = self.controlled_fixture()
        next(e for e in data[3] if e["kind"]=="platform_access_granted")["trainPresent"] = False
        self.assertTrue(any("without berthed assigned train" in e for e in validate(data)["errors"]))

    def test_controlled_boarding_requires_correct_coach(self):
        data = self.controlled_fixture(); data[0]["coachAnchors"][0]["position"]["x"] = 10
        self.assertTrue(any("wrong coach" in e for e in validate(data)["errors"]))

    def coach_fixture(self):
        data = self.controlled_fixture()
        data[0]["coachControls"] = [dict(track=1,coachNumber=1,boardingSecondsPerPassenger=.01)]
        extra = [dict(time=t,kind=kind,agentId="a",serviceId="trip",representedPassengers=10,actualStatus="Boarding",actualCutoff=192)
                 for t,kind in ((.21,"coach_queue_joined"),(.22,"coach_boarding_started"),(.33,"coach_boarding_ready"),(.39,"coach_queue_left"))]
        data[3].extend(extra); data[3].sort(key=lambda e:e["time"])
        next(e for e in data[3] if e["kind"]=="boarded")["coachQueueValidated"] = True
        return data

    def test_coach_door_weighted_queue(self):
        report = validate(self.coach_fixture())
        self.assertTrue(report["success"], report["errors"])

    def test_coach_door_cannot_be_bypassed(self):
        data = self.coach_fixture(); data[3][:] = [e for e in data[3] if not e["kind"].startswith("coach_")]
        self.assertTrue(any("bypassed assigned coach queue" in e for e in validate(data)["errors"]))

    def test_coach_door_cannot_finish_too_early(self):
        data = self.coach_fixture(); next(e for e in data[3] if e["kind"]=="coach_boarding_ready")["time"] = .25
        data[3].sort(key=lambda e:e["time"])
        self.assertTrue(any("coach boarding completed too quickly" in e for e in validate(data)["errors"]))

    def test_empty_initial_window_is_valid_without_policy_decisions(self):
        data = fixture()
        data = (data[0], [], [], [data[3][0], dict(time=0, kind="trajectory_sample", sampleId=1, activeJourneyIds=[]),
                                 dict(time=0, kind="run_ended", agentId="", serviceId="")])
        self.assertTrue(validate(data)["success"], validate(data)["errors"])

    def test_valid_pool_reuse_future_roster_and_empty_groups(self):
        report = validate(fixture())
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["poolReuses"], 1)
        self.assertEqual(report["enteredJourneys"], 2)
        self.assertEqual(report["emptySampleGroups"], 2)
        self.assertEqual(report["perServiceAdmissions"]["trip"]["boarded"], 10)
        self.assertAlmostEqual(report["observedSampleSpanSeconds"], 1.4)
        self.assertAlmostEqual(report["actorSeconds"], .8)
        self.assertAlmostEqual(report["meanActiveActors"], .8/1.4)
        self.assertEqual(report["p95ActiveActors"], 1)

    def test_partial_day_cannot_claim_full_day(self):
        self.assertFalse(validate(fixture(), True)["success"])

    def test_platform_reapproach_is_active_native_travel_not_new_admission(self):
        data = fixture()
        data[3].insert(3, dict(time=.1, kind="platform_reapproach", agentId="a", serviceId="trip",
                               actorSlotId=0, representedPassengers=10, detail="Native return to assigned anchor"))
        report = validate(data)
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["enteredJourneys"], 2)
        self.assertEqual(report["boardingGateChecks"], 1)
        data[2][1]["position"]["x"] = 50
        self.assertFalse(validate(data)["success"], "A reapproach event must not exempt native position continuity")

    def test_platform_reapproach_cannot_revive_terminal_journey(self):
        data = fixture()
        index = next(i for i, event in enumerate(data[3]) if event["time"] >= 1)
        data[3].insert(index, dict(time=.5, kind="platform_reapproach", agentId="a", serviceId="trip",
                                  actorSlotId=0, representedPassengers=10))
        self.assertTrue(any("actor event outside active lifecycle" in e for e in validate(data)["errors"]))

    def test_plan_weights_must_conserve_demand(self):
        data = fixture(); data[0]["scheduledJourneys"][2]["representedPassengers"] += 1
        self.assertTrue(any("conserve demand" in e for e in validate(data)["errors"]))

    def test_capacity_overflow_rejected(self):
        data = fixture(); data[0]["services"][0]["seatedCapacity"] = 5
        self.assertFalse(validate(data)["success"])

    def test_no_early_spawn(self):
        data = fixture(); data[0]["scheduledJourneys"][0]["plannedEntrySeconds"] = 1
        self.assertTrue(any("before planned arrival" in e for e in validate(data)["errors"]))

    def test_unreleased_slot_cannot_be_reused(self):
        data = fixture(); data[3][:] = [e for e in data[3] if not (e["kind"] == "journey_finished" and e["agentId"] == "a")]
        self.assertTrue(any("slot reused" in e for e in validate(data)["errors"]))

    def test_pool_reuse_keeps_journey_identity(self):
        data = fixture(); data[2][2]["agentId"] = "a"
        self.assertTrue(any("after terminal" in e for e in validate(data)["errors"]))

    def test_declared_member_missing_from_sample_rejected(self):
        data = fixture(); del data[2][0]
        self.assertTrue(any("no trajectory rows" in e for e in validate(data)["errors"]))

    def test_sample_identity_cannot_drift(self):
        data = fixture(); data[2][0]["sampleId"] = 999
        self.assertTrue(any("sampleId" in e for e in validate(data)["errors"]))

    def test_same_timestamp_enter_sample_finish_sample_uses_event_order(self):
        data = fixture()
        for event in data[3]:
            if event["time"] == .4: event["time"] = .2  # terminal after sample at same clock
        self.assertTrue(validate(data)["success"], validate(data)["errors"])

    def test_boarding_requires_actual_gate(self):
        data = fixture(); next(e for e in data[3] if e["kind"] == "journey_finished")["actualStatus"] = "Closed"
        self.assertTrue(any("boarding gate" in e for e in validate(data)["errors"]))

    def test_terminal_cannot_hide_teleport(self):
        data = fixture(); next(e for e in data[3] if e["kind"] == "journey_finished")["position"] = dict(x=100, y=0, z=0)
        self.assertTrue(any("terminal snapshot teleported" in e for e in validate(data)["errors"]))

    def test_first_spawn_sample_uses_entry_time_not_global_time(self):
        data = fixture(); data[2][2]["position"] = dict(x=10, y=0, z=0)
        self.assertTrue(any("first sample teleports" in e for e in validate(data)["errors"]))

    def test_raw_native_velocity_warns_but_does_not_hide_teleport(self):
        data = fixture(); data[2][1]["velocity"] = dict(x=50, y=0, z=0)
        report = validate(data)
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["nativeVelocityWarnings"], 1)
        data[2][1]["position"]["x"] = 50
        self.assertFalse(validate(data)["success"])

    def test_probability_zero_cannot_be_selected(self):
        data = fixture(); data[1][0]["options"][0]["probability"] = 0
        self.assertTrue(any("positive-probability" in e for e in validate(data)["errors"]))

    def test_weighted_admission_ledger_cannot_underreport_load(self):
        data = fixture(); next(e for e in data[3] if e["kind"] == "boarded")["occupancyAfter"] = 1
        self.assertTrue(any("occupancy ledger" in e for e in validate(data)["errors"]))

    def test_missing_group_member_rejected(self):
        data = fixture(); data[0]["scheduledJourneys"][0].update(groupId="party", groupSize=2)
        self.assertTrue(any("incomplete group" in e for e in validate(data)["errors"]))

    def test_partial_party_entry_report_distinguishes_planned_from_entered(self):
        data = fixture()
        for i, p in enumerate(data[0]["scheduledJourneys"]):
            p.update(groupId="party", groupSize=3, groupMemberIndex=i, plannedEntrySeconds=i)
        for row in data[2]: row["groupId"] = "party"
        report = validate(data)
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["completeEnteredGroups"], 0)
        self.assertEqual(report["partialEnteredGroups"], 1)
        self.assertEqual(report["maximumGroupEntrySpreadSeconds"], 1)

    def test_unserved_future_identity_has_no_fake_actor(self):
        data = fixture(); data[3].insert(-1, dict(time=1.4, kind="journey_unserved", agentId="future", serviceId="trip", representedPassengers=30, actualStatus="Cancelled"))
        self.assertTrue(validate(data, True)["success"], validate(data, True)["errors"])

    def test_unentered_future_demand_cannot_be_discarded_early(self):
        data = fixture(); data[3].insert(-1, dict(time=1.4, kind="journey_unserved", agentId="future", serviceId="trip", representedPassengers=30))
        self.assertTrue(any("discarded before" in e for e in validate(data)["errors"]))

    def test_nonfinite_json_rejected(self):
        data = fixture(); data[2][0]["position"]["x"] = float("nan")
        self.assertFalse(validate(data)["success"])

    def test_train_interval_overlap_rejected(self):
        data = fixture(); second = dict(data[0]["services"][0], serviceId="conflict", expectedBoarding=0)
        data[0]["services"].append(second)
        self.assertTrue(any("track overlap" in e for e in validate(data)["errors"]))


if __name__ == "__main__":
    unittest.main()
