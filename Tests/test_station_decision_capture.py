"""Schema regression tests for historical captures and real seated-visit invariants."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from validate_station_decision_capture import Audit


def fixture(seats=False):
    zero = {"x": 0, "y": 0, "z": 0}
    needs = dict(thirst=.8, toilet=.1, fatigue=.1, shopping=.1, information=.1)
    profile = dict(agentId="a", serviceId="trip", occurrence=0, walkSpeed=1.1, fastWalkSpeed=1.6,
                   startDelay=0, safetyMargin=10, policyParameters={}, platformKnownInitially=True,
                   needs=needs, position=zero)
    manifest = dict(schema="stationflow.station_decision_lab.v1", seed=1, sampleRate=5, sharedRailRate=1,
                    congestionAllowance=1, participantCount=1, profiles=[profile], scenarioEvents=[],
                    services=[dict(serviceId="trip", departureOffsetSeconds=200, doorDurationSeconds=8)],
                    pois=[dict(poiId="cafe", activity="Cafe", position=zero)])
    source = dict(activity="Cafe", poiId="cafe", travelSeconds=2, queueSeconds=3, durationSeconds=4,
                  toPlatformSeconds=5, reachable=True, available=True, preference=1)
    option = dict(activity="Cafe", poiId="cafe", travel=2, queue=3, dwell=4, toPlatform=5, probability=1, slack=176)
    if seats:
        source.update(seatTravelSeconds=6, seatingTransitionSeconds=4, seatedDurationSeconds=20)
        option.update(seatTravel=6, seatingTransition=4, seatedDwell=20, slack=146)
    context = dict(trainStatus="Scheduled", previousActivity="Display", nowSeconds=0,
                   boardingCutoffSeconds=200, platformKnown=True, safetyMarginSeconds=10, needs=needs)
    decision = dict(agentId="a", time=0, decisionIndex=1, context=context, observedStatus="Scheduled",
                    previousActivity="Display", observedCutoff=200, platformKnown=True, needs=needs,
                    inputOptions=[source], options=[option], selectedActivity="Cafe", selectedPoi="cafe")
    trajectories = []
    for timestamp in (0, .2):
        row = dict(agentId="a", time=timestamp, serviceId="trip", occurrence=0, activity="Cafe",
                   execution="Seated" if seats else "Using", knownStatus="Scheduled", budget=150,
                   targetPoi="cafe", finished=False, boarded=False, visibleActor=True, position=zero, velocity=zero)
        if seats:
            row.update(seatId="cafe-01", seatedActivity="Coffee", isSeated=True, seatedSecondsRemaining=20-timestamp)
        trajectories.append(row)
    events = [dict(time=0, kind="run_started", detail="fixture", agentId="", serviceId=""),
              dict(time=.2, kind="run_ended", detail="fixture", agentId="", serviceId="")]
    return manifest, [decision], trajectories, events


def validate(data):
    fixture_parent = Path(__file__).resolve().parent
    with tempfile.TemporaryDirectory(dir=fixture_parent, prefix=".capture-fixture-") as directory:
        root = Path(directory)
        assert root.resolve().parent == fixture_parent
        for name, value in zip(("manifest.json", "decisions.jsonl", "trajectories.jsonl", "events.jsonl"), data):
            text = json.dumps(value) if name == "manifest.json" else "\n".join(json.dumps(row) for row in value)
            (root/name).write_text(text+"\n", encoding="utf-8")
        return Audit(root).run()


class CaptureValidation(unittest.TestCase):
    def deadline_fixture(self, choice="Info", direct_cost=201):
        data = fixture()
        manifest, decisions, _, _ = data
        manifest["policyVersion"] = "conditional-semi-markov-v2"
        manifest["pois"].extend([dict(poiId="platform", activity="Platform", position=dict(x=0,y=0,z=0)),
                                  dict(poiId="help", activity=choice, position=dict(x=0,y=0,z=0))])
        direct = dict(activity="Platform", poiId="platform", travelSeconds=direct_cost, queueSeconds=0,
                      durationSeconds=0, toPlatformSeconds=0, reachable=True, available=True, preference=1,
                      seatTravelSeconds=0, seatingTransitionSeconds=0, seatedDurationSeconds=0)
        help_option = dict(direct, activity=choice, poiId="help", travelSeconds=30, toPlatformSeconds=200)
        def evaluation(source, probability):
            return dict(activity=source["activity"], poiId=source["poiId"], travel=source["travelSeconds"],
                        queue=0, dwell=0, toPlatform=source["toPlatformSeconds"], seatTravel=0,
                        seatingTransition=0, seatedDwell=0, probability=probability,
                        slack=190-source["travelSeconds"]-source["toPlatformSeconds"])
        decisions[0].update(inputOptions=[direct,help_option], options=[evaluation(direct,0),evaluation(help_option,1)],
                            selectedActivity=choice, selectedPoi="help")
        return data

    def test_inferred_deadline_miss_allows_help_without_changing_train_status(self):
        for choice in ("Info", "Exit"):
            with self.subTest(choice=choice):
                report = validate(self.deadline_fixture(choice))
                self.assertTrue(report["success"], report["errors"])

    def test_feasible_direct_route_cannot_hide_negative_optional_budget(self):
        report = validate(self.deadline_fixture(direct_cost=199))
        self.assertTrue(any("insufficient total budget" in e for e in report["errors"]))

    def test_unknown_platform_does_not_infer_deadline_miss(self):
        data = self.deadline_fixture()
        data[1][0]["context"]["platformKnown"] = False
        data[1][0]["platformKnown"] = False
        self.assertTrue(any("insufficient total budget" in e for e in validate(data)["errors"]))

    def test_unreachable_direct_route_does_not_infer_deadline_miss(self):
        data = self.deadline_fixture()
        data[1][0]["inputOptions"][0]["reachable"] = False
        self.assertTrue(any("insufficient total budget" in e for e in validate(data)["errors"]))

    def test_deadline_replanning_rejects_optional_cafe(self):
        report = validate(self.deadline_fixture("Cafe"))
        self.assertTrue(any("replanning must" in e for e in report["errors"]))

    def test_historical_schema_stays_valid(self):
        report = validate(fixture())
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["seatExtensionSamplesChecked"], 0)

    def test_native_platform_reapproach_preserves_continuity_checks(self):
        data = fixture()
        data[3].insert(1, dict(time=.1, kind="platform_reapproach", detail="Native return", agentId="a", serviceId="trip"))
        report = validate(data)
        self.assertTrue(report["success"], report["errors"])
        data[2][1]["position"] = dict(x=20, y=0, z=0)
        self.assertTrue(any("sampled displacement" in e for e in validate(data)["errors"]))

    def test_platform_reapproach_rejects_terminal_actor(self):
        data = fixture()
        data[3][1:1] = [dict(time=.05, kind="journey_ended", detail="Exit", agentId="a", serviceId="trip"),
                         dict(time=.1, kind="platform_reapproach", detail="Native return", agentId="a", serviceId="trip")]
        self.assertTrue(any("platform reapproach after terminal" in e for e in validate(data)["errors"]))

    def test_valid_seated_visit_cost_and_samples(self):
        report = validate(fixture(True))
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["seatedSamplesChecked"], 2)

    def test_missing_seat_id_rejected(self):
        data = fixture(True); data[2][0]["seatId"] = ""
        self.assertFalse(validate(data)["success"])

    def test_partial_seat_extension_rejected(self):
        data = fixture(True); del data[2][0]["isSeated"]
        self.assertFalse(validate(data)["success"])

    def test_transition_without_reservation_rejected(self):
        for state in ("WalkingToSeat", "SittingDown", "StandingUp"):
            with self.subTest(state=state):
                data = fixture(True)
                data[2][0].update(execution=state, isSeated=False, seatId="")
                self.assertTrue(any("lacks reserved seat" in e for e in validate(data)["errors"]))

    def test_duplicate_reservation_rejected(self):
        data = fixture(True)
        second = copy.deepcopy(data[0]["profiles"][0]); second["agentId"] = "b"
        data[0]["profiles"].append(second); data[0]["participantCount"] = 2
        rows = []
        for row in data[2]:
            rows.append(row); other = copy.deepcopy(row); other["agentId"] = "b"; rows.append(other)
        data = data[0], data[1], rows, data[3]
        report = validate(data)
        self.assertTrue(any("duplicate seat reservation" in e for e in report["errors"]))

    def test_cost_extension_mismatch_rejected(self):
        data = fixture(True); data[1][0]["options"][0]["seatedDwell"] = 0
        self.assertFalse(validate(data)["success"])

    def test_slack_that_omits_seated_cost_rejected(self):
        data = fixture(True); data[1][0]["options"][0]["slack"] = 176
        self.assertTrue(any("slack omits" in e for e in validate(data)["errors"]))

    def test_seated_visit_cannot_hide_teleport(self):
        data = fixture(True); data[2][1]["position"] = dict(x=10,y=0,z=0)
        self.assertTrue(any("sampled displacement" in e for e in validate(data)["errors"]))

    def test_velocity_spike_stays_warning(self):
        data = fixture(); data[2][1]["velocity"] = dict(x=7,y=0,z=0)
        report = validate(data)
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(report["rawVelocitySpikeCount"], 1)


if __name__ == "__main__":
    unittest.main()
