"""Fault-injection fixtures for the independent alighter capture auditor."""
import copy
import unittest

from validate_station_alighting_services import AlightingAudit, SeededRandom, replay_plan


def fixture():
    settings = dict(serviceVisitorFraction=.4, minimumVisits=1, maximumVisits=2,
                    minimumBudgetSeconds=300, maximumBudgetSeconds=720)
    selected, direct = None, None
    for i in range(100):
        p = dict(journeyId=f"fixture/A/{i}", groupId="", purpose=1, representedPassengers=5)
        visitor, quota, budget = replay_plan(p, settings, 9123)
        p.update(alightingServiceVisitor=visitor, alightingServiceVisitLimit=quota, alightingStopoverSeconds=budget)
        if visitor and selected is None:
            selected = p
        if not visitor and direct is None:
            direct = p
        if selected and direct:
            break
    profiles = [dict(agentId=p["journeyId"], purpose="Alighting", **{key: p[key] for key in
                ("alightingServiceVisitor", "alightingServiceVisitLimit", "alightingStopoverSeconds")}) for p in (selected, direct)]
    manifest = dict(seed=9123, arrivalSettings=dict(alightingStopovers=settings), scheduledJourneys=[selected, direct],
                    profiles=profiles, platformControls=[dict(ingressArea=3)])
    visitor, direct_id = selected["journeyId"], direct["journeyId"]
    deadline = 1 + selected["alightingStopoverSeconds"]
    def event(kind, jid=visitor, time=1, detail="", **kwargs):
        return dict(time=time, agentId=jid, kind=kind, purpose="Alighting", detail=detail, boarded=False,
                    profile=next((p for p in profiles if p["agentId"] == jid), {}), position=dict(x=0, y=.02, z=0), **kwargs)
    events = [event("journey_entered"), event("journey_entered", direct_id),
              event("counter_service_completed", time=30, detail="Info", actualStatus="Departed", actualDeparture=20),
              event("activity_completed", time=31, detail="Info; sampled dwell=10", actualStatus="Departed", actualDeparture=20),
              event("journey_finished", direct_id, time=80, detail="Exited; direct station exit"),
              event("journey_finished", time=90, detail="Exited; station services"),
              dict(time=100, kind="run_ended", agentId="")]
    context = dict(alightingJourney=True, requireInitialDisplay=False, stationStopoverDeadlineSeconds=deadline,
                   nowSeconds=2, stationServicesRemaining=selected["alightingServiceVisitLimit"], safetyMarginSeconds=12)
    costs = dict(travel=10, queue=0, dwell=10, seatTravel=0, seatingTransition=0, seatedDwell=0, toExit=40)
    option = dict(poiId="Info", activity="Info", probability=.9, toPlatform=0, slack=deadline - 2 - 72, **costs)
    raw = dict(poiId="Info", activity=5, available=True, reachable=True, toPlatformSeconds=0, toExitSeconds=40)
    decisions = [dict(time=2, agentId=visitor, decisionIndex=1, selectedActivity="Info", selectedPoi="Info",
                      context=context, options=[option], inputOptions=[raw], observedStatus="Departed")]
    def sample(jid=visitor, time=3, activity="Info", count=0):
        p = selected if jid == visitor else direct
        return dict(time=time, agentId=jid, sampleId=int(time), purpose="Alighting", activity=activity, execution="Using",
                    targetPoi=activity, alightingStopoverDeadlineSeconds=1 + p["alightingStopoverSeconds"],
                    alightingServiceVisitsCompleted=count, boarded=False, platformAccessGranted=False,
                    platformAccessPassed=False, nativeLinkDirection="", navigationAreaMask=-9,
                    position=dict(x=time / 2, y=.02, z=0))
    samples = [sample(), sample(time=32, activity="Exit", count=1), sample(direct_id, 4, "Exit")]
    return [manifest, events, decisions, samples]


def audit(data, require_outcomes=True):
    manifest, events, decisions, samples = data
    checker = AlightingAudit(manifest)
    for row in events:
        checker.consume_event(row)
    for row in decisions:
        checker.consume_decision(row)
    for row in samples:
        checker.consume_trajectory(row)
    return checker.result(require_outcomes=require_outcomes)


class AlightingCaptureTests(unittest.TestCase):
    def test_seeded_random_matches_known_system_random_samples(self):
        self.assertAlmostEqual(.7262432699679598, SeededRandom(0).sample())
        self.assertAlmostEqual(.24866858415709278, SeededRandom(1).sample())
        random = SeededRandom(2000000000)
        self.assertEqual([.10450909990095957, .9974449872958683, .7231875628806592], [random.sample() for _ in range(3)])

    def test_valid_closed_capture_records_direct_and_service_exits(self):
        result = audit(fixture())
        self.assertTrue(result["success"], result["errors"])
        self.assertEqual(1, result["observed"]["directExits"])
        self.assertEqual(1, result["observed"]["serviceVisitors"])
        self.assertEqual({"Info": 1}, result["observed"]["uniqueCompletedByType"])
        self.assertTrue(result["servicesAfterActualTrainDeparture"])

    def test_corrupted_seeded_profile_is_rejected(self):
        data = fixture(); data[0]["scheduledJourneys"][0]["alightingStopoverSeconds"] += 1
        self.assertFalse(audit(data)["success"])

    def test_deadline_must_start_at_actual_entry_not_planned_entry(self):
        data = fixture(); data[3][0]["alightingStopoverDeadlineSeconds"] += 1
        self.assertFalse(audit(data)["success"])

    def test_native_ingress_and_boarding_are_rejected(self):
        for key, value in (("nativeLinkDirection", "Ingress"), ("navigationAreaMask", -1), ("boarded", True),
                           ("platformAccessPassed", True), ("activity", "Board")):
            data = fixture(); data[3][0][key] = value
            self.assertFalse(audit(data)["success"], key)

    def test_full_service_cost_includes_exit_not_old_train(self):
        data = fixture(); data[2][0]["observedStatus"] = "Cancelled"
        self.assertTrue(audit(data)["success"])
        data[2][0]["options"][0]["toExit"] = 1000
        self.assertFalse(audit(data)["success"])

    def test_selected_service_requires_capacity_route_and_probability(self):
        for location, key, value in (("inputOptions", "reachable", False), ("inputOptions", "available", False),
                                     ("options", "probability", 0), ("inputOptions", "activity", 6)):
            data = fixture(); data[2][0][location][0][key] = value
            self.assertFalse(audit(data)["success"], key)

    def test_direct_exiter_cannot_use_service(self):
        data = fixture(); data[1][2]["agentId"] = data[0]["scheduledJourneys"][1]["journeyId"]
        self.assertFalse(audit(data)["success"])

    def test_duplicate_completed_service_rejected_even_with_two_visit_quota(self):
        data = fixture(); duplicate = copy.deepcopy(data[1][3]); duplicate["time"] = 33; data[1].insert(4, duplicate)
        self.assertFalse(audit(data)["success"])

    def test_censored_interrupted_visit_is_retained_without_forced_completion(self):
        data = fixture(); jid = data[0]["scheduledJourneys"][0]["journeyId"]
        data[1] = [e for e in data[1] if not (e.get("agentId") == jid and e.get("kind") in {"activity_completed", "journey_finished"})]
        data[3] = [s for s in data[3] if s["activity"] != "Exit" or s["agentId"] != jid]
        result = audit(data)
        self.assertTrue(result["success"], result["errors"])
        self.assertEqual(1, result["observed"]["censoredServiceVisits"])
        self.assertEqual(0, result["observed"]["serviceVisitorsExited"])

    def test_same_frame_completion_trajectory_boundary_is_allowed(self):
        data = fixture(); data[3][0]["time"] = 31
        self.assertTrue(audit(data)["success"])

    def test_interrupted_optional_visit_does_not_consume_completed_quota(self):
        data = fixture()
        jid = data[0]["scheduledJourneys"][0]["journeyId"]
        data[1] = [e for e in data[1] if e.get("kind") != "activity_completed" or e.get("agentId") != jid]
        data[1].insert(3, dict(time=31, agentId=jid, kind="activity_interrupted", purpose="Alighting",
                              detail="Info: independent budget exhausted", boarded=False))
        data[3][1]["alightingServiceVisitsCompleted"] = 0
        result = audit(data)
        self.assertTrue(result["success"], result["errors"])
        self.assertEqual(1, result["observed"]["interruptions"])

    def test_missing_aggregate_uses_and_closed_boundary_fail(self):
        data = fixture(); data[1] = [e for e in data[1] if e.get("kind") not in {"counter_service_completed", "run_ended"}]
        self.assertFalse(audit(data)["success"])


if __name__ == "__main__":
    unittest.main()
