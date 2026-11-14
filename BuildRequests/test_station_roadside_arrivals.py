"""Checks for measured crossings, discontinuity rejection and censored evidence."""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from audit_station_roadside_arrivals import audit


class RoadsideEvidenceTests(unittest.TestCase):
    def fixture(self, trajectories, finished=False):
        fixture_root = (Path(__file__).resolve().parent / "RoadsideAuditFixtures").resolve()
        fixture_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=fixture_root) as temporary:
            capture = Path(temporary).resolve()
            # Verify the temporary cleanup target remains in the explicit workspace fixture root.
            if not capture.is_relative_to(fixture_root):
                raise ValueError("A temporary fixture escaped its workspace root.")
            ids = ["entry-west", "entry-mid-west", "entry-main", "entry-mid-east", "entry-east"]
            xs = [-24, -9, 0, 9, 24]
            portals = [{"entranceId": name, "weight": 0 if name == "entry-main" else 1,
                "spawnPoints": [{"x": x, "y": .02, "z": -60}]} for name, x in zip(ids, xs)]
            identity = "test/service/B/001"
            manifest = {"entrancePortals": portals, "scheduledJourneys": [{"journeyId": identity,
                "purpose": 0, "entranceId": "entry-west", "groupId": "", "plannedEntrySeconds": 0}]}
            events = [{"kind": "journey_entered", "agentId": identity, "purpose": "Boarding",
                "entranceId": "entry-west", "time": 0, "position": {"x": -24, "y": .02, "z": -60}}]
            if finished:
                events.append({"kind": "journey_finished", "agentId": identity, "time": 25,
                    "outcome": "Exited", "detail": "Exited after station visit"})
            samples = [{"agentId": identity, "time": time, "position": {"x": -24, "y": .02, "z": z},
                "execution": "Walking", "visibleActor": True} for time, z in trajectories]
            (capture / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            for name, values in (("events.jsonl", events), ("trajectories.jsonl", samples)):
                (capture / name).write_text("\n".join(json.dumps(value) for value in values) + "\n", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                return audit(capture, capture / "audit.json")

    def test_continuous_walk_and_unequal_source_counts_are_observed(self):
        result = self.fixture([(i + .1, -60 + i * 2) for i in range(18)], finished=True)
        self.assertTrue(result["success"])
        self.assertEqual(result["observedFirstInwardCrossings"], 1)
        self.assertEqual(result["continuousEntryToHallJourneys"], 1)
        self.assertEqual(result["journeys"][0]["firstInwardCrossing"]["nearestPhysicalDoorX"], -24)
        self.assertEqual(result["rightCensoredLifecycles"], 0)
        self.assertGreater(result["positivePortalCountVariance"]["populationVariance"], 0)

    def test_teleport_like_segment_cannot_be_reported_as_a_walk(self):
        result = self.fixture([(.1, -60), (.2, -26)])
        self.assertEqual(result["observedFirstInwardCrossings"], 0)
        self.assertEqual(result["excludedSegments"]["implausibleWalkingJump"], 1)
        self.assertIsNone(result["success"])

    def test_coincident_entry_sample_does_not_break_continuity(self):
        result = self.fixture([(i, -60 + i * 2) for i in range(18)], finished=True)
        self.assertEqual(result["coincidentPoseSamplesIgnored"], 1)
        self.assertEqual(result["continuousEntryToHallJourneys"], 1)

    def test_recording_gap_and_censoring_do_not_invent_a_hall_entry(self):
        result = self.fixture([(.1, -60), (20, -26)])
        self.assertEqual(result["observedFirstInwardCrossings"], 0)
        self.assertEqual(result["excludedSegments"]["sampleGapOver2Seconds"], 1)
        self.assertEqual(result["rightCensoredBeforeObservedHallEntry"], 1)
        self.assertEqual(result["rightCensoredLifecycles"], 1)


if __name__ == "__main__":
    unittest.main()
