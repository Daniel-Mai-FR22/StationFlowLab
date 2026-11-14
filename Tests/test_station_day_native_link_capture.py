"""Daily-validator integration for the narrow native-link audit contract."""
import unittest

import test_station_day_capture as fixtures


def native_fixture(adjacent_interval=False):
    data = fixtures.DailyCaptureValidation().controlled_fixture()
    manifest, _, rows, events = data
    manifest["nativeGateLinkTelemetry"] = True
    start = dict(x=34.6,y=.02,z=44.8)
    end = dict(x=34.6,y=.02,z=47.2)
    gate = manifest["platformControls"][0]
    gate.update(ingressArea=7, ingressStart=start, ingressEnd=end, ingressWidth=.85,
                egressStart=dict(x=37.3,y=.02,z=47.2),egressEnd=dict(x=37.3,y=.02,z=44.8),egressWidth=2.2,
                platform=end,center=dict(x=36,y=0,z=46))
    before = dict(x=34.608429,y=.02,z=44.016861)
    after = dict(x=34.607063,y=.02,z=45.462585)
    if adjacent_interval:
        before["z"],after["z"] = 43.7535706,44.6037369
    for row in rows:
        row["isOnNativeLink"] = False
        if row["agentId"] != "a": continue
        row["position"] = before if row["time"] == 0 else after
        if row["time"] > 0:
            row.update(isOnNativeLink=True,nativeLinkTrack=1,nativeLinkDirection="Ingress",
                       nativeLinkStart=start,nativeLinkEnd=end,requestedNavigationSpeed=1.6,
                       navigationAreaMask=1 | (1 << 4) | (1 << 7),platformAccessGranted=True)
    manifest["coachAnchors"][0]["position"] = after
    for event in events:
        if event.get("agentId") == "a" and event["kind"] == "journey_entered": event["position"] = before
        elif event.get("agentId") == "a" and event["kind"] in ("boarded", "journey_finished"): event["position"] = after
    return data


class DailyNativeLinkIntegrationTests(unittest.TestCase):
    def test_explicit_bounded_native_step_has_separate_report(self):
        report = fixtures.validate(native_fixture())
        self.assertTrue(report["success"], report["errors"])
        native = report["nativeGateLinkAudit"]
        self.assertEqual(native["ordinaryContinuityExceptions"], 1)
        self.assertEqual(native["flaggedSamples"], 1)
        self.assertAlmostEqual(native["exceptionDistanceMetres"],1.445725,places=5)

    def test_unflagged_same_step_fails_ordinary_bound(self):
        data = native_fixture(); data[2][1]["isOnNativeLink"] = False
        report = fixtures.validate(data)
        self.assertTrue(any("sampled-position jump" in e for e in report["errors"]))

    def test_adjacent_interval_allowance_is_counted_separately(self):
        report = fixtures.validate(native_fixture(adjacent_interval=True))
        self.assertTrue(report["success"],report["errors"])
        self.assertEqual(report["nativeGateLinkAudit"]["adjacentSampleIntervalGeometryAllowances"],1)

    def test_bool_flag_is_required(self):
        data = native_fixture(); data[2][0]["isOnNativeLink"] = "false"
        self.assertTrue(any("flag must be boolean" in e for e in fixtures.validate(data)["errors"]))

    def test_small_flagged_movement_still_checks_permission(self):
        data = native_fixture()
        data[2][1]["position"] = dict(data[2][0]["position"])
        data[2][1]["platformAccessGranted"] = False
        self.assertTrue(any("granted ticket-control token" in e for e in fixtures.validate(data)["errors"]))

    def test_boolean_token_cannot_replace_independent_grant_event(self):
        data = native_fixture()
        next(e for e in data[3] if e["kind"] == "platform_access_granted")["time"] = .25
        data[3].sort(key=lambda e:e["time"])
        report = fixtures.validate(data)
        self.assertTrue(any("before its independently logged grant" in e for e in report["errors"]))
        self.assertEqual(report["nativeGateLinkAudit"]["ordinaryContinuityExceptions"], 0)

    def test_first_sample_on_link_is_not_an_entrance_exception(self):
        data = native_fixture(); data[2][0].update({k:v for k,v in data[2][1].items() if k.startswith("native") or k == "requestedNavigationSpeed" or k == "navigationAreaMask"})
        data[2][0].update(isOnNativeLink=True,platformAccessGranted=True)
        self.assertTrue(any("no previous physical sample" in e for e in fixtures.validate(data)["errors"]))

    def test_terminal_snapshot_still_cannot_teleport(self):
        data = native_fixture()
        next(e for e in data[3] if e.get("agentId") == "a" and e["kind"] == "journey_finished")["position"] = dict(x=40,y=.02,z=47.2)
        self.assertTrue(any("terminal snapshot teleported" in e for e in fixtures.validate(data)["errors"]))

    def test_unflagged_lateral_approach_corner_is_counted_separately(self):
        data = native_fixture()
        data[2][0]["position"]["x"] = 33.817054748535156
        entry = next(e for e in data[3] if e.get("agentId") == "a" and e["kind"] == "journey_entered")
        entry["position"] = dict(data[2][0]["position"])
        report = fixtures.validate(data)
        self.assertTrue(report["success"], report["errors"])
        self.assertEqual(1, report["nativeGateLinkAudit"]["adjacentSampleIntervalGeometryAllowances"])

    def test_flagged_lane_remains_strict_at_the_same_lateral_offset(self):
        data = native_fixture(); data[2][1]["position"]["x"] = 33.817054748535156
        self.assertTrue(any("finite saved lane" in e for e in fixtures.validate(data)["errors"]))


if __name__ == "__main__":
    unittest.main()
