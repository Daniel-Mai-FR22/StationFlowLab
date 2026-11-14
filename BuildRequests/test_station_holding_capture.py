"""Independent fixtures for holding marker identity and telemetry contracts."""
import copy
import unittest
from validate_station_holding_capture import HoldingAudit, CLAIM, RELEASE

def position(x=0, y=.02, z=30):
    return {"x": x, "y": y, "z": z}

def manifest():
    return {"platformControls": [{"track": 2, "hall": position(), "holdingAnchors": [position(0), position(1), position(2)]},
        {"track": 4, "hall": position(10), "holdingAnchors": [position(10), position(11)]}]}

def event(kind=CLAIM, jid="A", slot=0, count=1, time=1, track=2, actor=-100):
    return {"kind": kind, "time": time, "agentId": jid, "holdingTrack": track,
        "holdingSlotIndex": slot if kind == CLAIM else -1, "holdingLeaseCount": count,
        "holdingPosition": position(slot if track == 2 else 10 + slot) if kind == CLAIM else {"x": 0, "y": 0, "z": 0},
        "position": position(500, z=-20), "detail": f"track={track}; slot={slot}; owner={actor}"}

class HoldingCaptureTests(unittest.TestCase):
    def audit(self, *rows):
        audit = HoldingAudit(manifest())
        for row in rows:
            audit.consume(row)
        return audit

    def test_claim_from_far_is_legitimate_holding_reservation(self):
        audit = self.audit(event())
        self.assertTrue(audit.result()["success"])
        self.assertEqual(1, audit.result()["claimEvents"])

    def test_explicit_release_allows_new_journey_in_same_slot(self):
        audit = self.audit(event(), event(RELEASE, count=0, time=2), event(jid="B", time=3, actor=-200))
        self.assertTrue(audit.result()["success"])
        self.assertEqual(1, audit.result()["releaseEvents"])
        self.assertEqual("B", audit.leases[(2, 0)])

    def test_duplicate_slot_claim_detects_the_old_modulo_alias(self):
        audit = self.audit(event(), event(jid="B", time=2, count=2, actor=-200))
        self.assertFalse(audit.result()["success"])

    def test_one_journey_cannot_change_goal_before_release(self):
        self.assertFalse(self.audit(event(), event(slot=1, count=2, time=2)).result()["success"])

    def test_release_requires_correct_journey(self):
        self.assertFalse(self.audit(event(), event(RELEASE, jid="B", count=0, time=2)).result()["success"])

    def test_release_requires_correct_stable_actor_owner(self):
        self.assertFalse(self.audit(event(), event(RELEASE, count=0, time=2, actor=-200)).result()["success"])

    def test_release_requires_no_remaining_assigned_typed_slot(self):
        row = event(RELEASE, count=0, time=2); row["holdingSlotIndex"] = 0
        self.assertFalse(self.audit(event(), row).result()["success"])

    def test_release_requires_zero_poststate_position(self):
        row = event(RELEASE, count=0, time=2); row["holdingPosition"] = position()
        self.assertFalse(self.audit(event(), row).result()["success"])

    def test_release_explicit_slot_must_match_prior_active_lease(self):
        row = event(RELEASE, count=0, time=2); row["detail"] = "track=2; slot=1; owner=-100"
        self.assertFalse(self.audit(event(), row).result()["success"])

    def test_release_explicit_track_must_match_its_gate(self):
        row = event(RELEASE, count=0, time=2); row["detail"] = "track=4; slot=0; owner=-100"
        self.assertFalse(self.audit(event(), row).result()["success"])

    def test_release_count_must_describe_exact_postrelease_population(self):
        self.assertFalse(self.audit(event(), event(RELEASE, count=1, time=2)).result()["success"])

    def test_marker_goals_must_match_frozen_authored_coordinates(self):
        row = event(); row["holdingPosition"]["x"] += .01
        self.assertFalse(self.audit(row).result()["success"])

    def test_reported_capacity_cannot_exceed_authored_points(self):
        self.assertFalse(self.audit(event(count=4)).result()["success"])

    def test_reported_count_must_match_explicit_assignments(self):
        self.assertFalse(self.audit(event(count=0)).result()["success"])

    def test_track_slot_identity_is_local_to_each_gate(self):
        audit = self.audit(event(), event(jid="B", track=4, time=2, actor=-200))
        self.assertTrue(audit.result()["success"])
        self.assertEqual({2: 1, 4: 1}, dict(audit.peak))

    def test_unreleased_censored_end_lease_is_not_failure(self):
        self.assertTrue(self.audit(event()).result()["success"])

    def test_unknown_tracks_and_out_of_range_slots_fail(self):
        self.assertFalse(self.audit(event(track=9)).result()["success"])
        self.assertFalse(self.audit(event(slot=3)).result()["success"])
        row = event(); row["holdingTrack"] = {"unexpected": 2}
        self.assertFalse(self.audit(row).result()["success"])

    def test_missing_typed_identity_and_actor_owner_fail(self):
        for key in ("holdingTrack", "holdingSlotIndex", "holdingLeaseCount", "holdingPosition", "detail"):
            row = event(); del row[key]
            self.assertFalse(self.audit(row).result()["success"])

    def test_invalid_time_and_vectors_fail(self):
        self.assertFalse(self.audit(event(time=float("nan"))).result()["success"])
        row = event(); row["holdingPosition"]["y"] = float("inf")
        self.assertFalse(self.audit(row).result()["success"])

    def test_backward_event_clock_fails(self):
        self.assertFalse(self.audit(event(time=2), event(RELEASE, count=0, time=1)).result()["success"])

    def test_missing_duplicate_or_nonfloor_authored_markers_fail(self):
        for changed in ({"platformControls": []}, {"platformControls": [{"track": 2}]},
            {"platformControls": [{"track": 2, "hall": position(), "holdingAnchors": [position(), position()]}]},
            {"platformControls": [{"track": 2, "hall": position(), "holdingAnchors": [position(y=3)]}]}):
            self.assertFalse(HoldingAudit(changed).result()["success"])

    def test_audit_does_not_mutate_manifest_or_events(self):
        saved_manifest, row = manifest(), event(); saved = copy.deepcopy((saved_manifest, row))
        audit = HoldingAudit(saved_manifest); audit.consume(row)
        self.assertEqual(saved, (saved_manifest, row))

if __name__ == "__main__":
    unittest.main()
