"""Adversarial bounds for explicitly recorded native gate-link traversal."""
import copy
import unittest

from native_gate_link_audit import audit_native_link_step


def v(x, z):
    return dict(x=x, y=.02, z=z)


def fixture():
    control = dict(track=7, ingressArea=7, ingressStart=v(34.6, 44.8), ingressEnd=v(34.6, 47.2),
                   ingressWidth=.85, egressStart=v(37.3, 47.2), egressEnd=v(37.3, 44.8), egressWidth=2.2)
    manifest = dict(nativeGateLinkTelemetry=True, controlledPlatformAccess=True, platformControls=[control])
    profile = dict(walkSpeed=1.1, fastWalkSpeed=1.6, trackNumber=7, purpose="Boarding")
    before = dict(time=504.0513, position=v(34.608429,44.016861), isOnNativeLink=False)
    after = dict(time=504.339813, position=v(34.607063,45.462585), isOnNativeLink=True,
                 nativeLinkTrack=7, nativeLinkDirection="Ingress", nativeLinkStart=control["ingressStart"],
                 nativeLinkEnd=control["ingressEnd"], requestedNavigationSpeed=1.6,
                 navigationAreaMask=(1 << 7) | (1 << 4) | 1, platformAccessGranted=True)
    return manifest, before, after, profile


def observed_interval_fixture():
    """Final capture row28017: an unflagged approach is46mm past the fixed envelope."""
    manifest,before,after,profile = fixture()
    gate = manifest["platformControls"][0]
    gate.update(track=5,ingressArea=6)
    for key in ("ingressStart","ingressEnd","egressStart","egressEnd"): gate[key]["x"] -= 24
    before.update(time=116.19738006591797,position=v(10.595399856567383,43.753570556640625))
    after.update(time=116.44966888427734,position=v(10.596378326416016,44.603736877441406),
                 nativeLinkTrack=5,nativeLinkStart=v(10.596604347229004,44.80000305175781),
                 nativeLinkEnd=v(10.595864295959473,47.20000076293945),
                 requestedNavigationSpeed=1.5999999046325684,navigationAreaMask=-169)
    profile["trackNumber"] = 5
    return manifest,before,after,profile


class NativeGateLinkAuditTests(unittest.TestCase):
    def check_bad(self, data, text):
        result = audit_native_link_step(*data)
        self.assertFalse(result["allowed"])
        self.assertTrue(any(text in e for e in result["errors"]), result)

    def test_actual_canonical_ingress_step_is_bounded(self):
        result = audit_native_link_step(*fixture())
        self.assertTrue(result["allowed"], result)
        self.assertAlmostEqual(result["details"]["stepMetres"], 1.445725, places=5)

    def test_execution_state_alone_never_grants_exception(self):
        data = fixture(); data[2]["isOnNativeLink"] = False; data[2]["execution"] = "GateCrossing"
        result = audit_native_link_step(*data)
        self.assertFalse(result["candidate"]); self.assertFalse(result["allowed"])

    def test_old_capture_without_telemetry_optin_stays_strict(self):
        data = fixture(); del data[0]["nativeGateLinkTelemetry"]
        self.check_bad(data, "not explicitly enabled")

    def test_wrong_track_is_rejected(self):
        data = fixture(); data[2]["nativeLinkTrack"] = 5
        self.check_bad(data, "saved platform control")

    def test_wrong_assigned_track_is_rejected(self):
        data = fixture(); data[3]["trackNumber"] = 5
        self.check_bad(data, "wrong assigned track")

    def test_unknown_direction_is_rejected(self):
        data = fixture(); data[2]["nativeLinkDirection"] = "Shortcut"
        self.check_bad(data, "track/direction")

    def test_ingress_requires_token(self):
        data = fixture(); data[2]["platformAccessGranted"] = False
        self.check_bad(data, "granted ticket-control token")

    def test_ingress_requires_own_area(self):
        data = fixture(); data[2]["navigationAreaMask"] = 1 | (1 << 4) | (1 << 3)
        self.check_bad(data, "own saved permission")

    def test_ingress_cannot_enable_all_gate_areas(self):
        data = fixture(); data[2]["navigationAreaMask"] = -1
        self.check_bad(data, "another platform gate")

    def test_free_egress_area_cannot_be_used_as_ingress(self):
        data = fixture(); data[0]["platformControls"][0]["ingressArea"] = 4
        self.check_bad(data, "own saved permission")

    def test_lateral_escape_is_rejected(self):
        data = fixture(); data[2]["position"]["x"] += 2
        self.check_bad(data, "finite saved lane")

    def test_far_exit_is_rejected(self):
        data = fixture(); data[2]["position"]["z"] = 60
        self.check_bad(data, "finite saved lane")

    def test_vertical_jump_is_rejected(self):
        data = fixture(); data[2]["position"]["y"] = 2
        self.check_bad(data, "finite saved lane")

    def test_engine_link_endpoints_must_match_owned_link(self):
        data = fixture(); data[2]["nativeLinkEnd"] = v(34.6,50)
        self.check_bad(data, "endpoints differ")

    def test_instantaneous_whole_link_teleport_is_rejected(self):
        data = fixture(); data[2]["time"] = data[1]["time"]+.001
        self.check_bad(data, "bounded speed")

    def test_zero_interval_is_rejected(self):
        data = fixture(); data[2]["time"] = data[1]["time"]
        self.check_bad(data, "strictly increase")

    def test_requested_speed_cannot_inflate_allowance(self):
        data = fixture(); data[2]["requestedNavigationSpeed"] = 20
        self.check_bad(data, "exceeds the passenger profile")

    def test_interior_does_not_repeat_endpoint_snap_allowance(self):
        data = fixture()
        data = (data[0], copy.deepcopy(data[2]), data[2], data[3])
        data[1].update(time=500,position=v(34.6,45.5))
        data[2].update(time=500.1,position=v(34.6,46.5))
        self.check_bad(data, "bounded speed")

    def test_free_egress_does_not_require_boarding_token(self):
        data = fixture(); control = data[0]["platformControls"][0]
        data[1].update(time=10,position=v(37.3,48.02))
        data[2].update(time=10.22,position=v(37.3,47.18),nativeLinkDirection="Egress",
                       nativeLinkStart=control["egressStart"],nativeLinkEnd=control["egressEnd"],
                       platformAccessGranted=False,requestedNavigationSpeed=1.25)
        data[3].update(walkSpeed=1.25,purpose="Alighting")
        self.assertTrue(audit_native_link_step(*data)["allowed"])

    def test_reverse_egress_endpoint_data_is_rejected(self):
        data = fixture(); control = data[0]["platformControls"][0]
        data[2].update(nativeLinkDirection="Egress",nativeLinkStart=control["egressEnd"],nativeLinkEnd=control["egressStart"])
        self.check_bad(data, "endpoints differ")

    def test_flagged_first_sample_cannot_hide_entrance_teleport(self):
        data = fixture(); data = (data[0],None,data[2],data[3])
        self.check_bad(data, "no previous physical sample")

    def test_observed_adjacent_approach_46mm_is_bounded(self):
        result = audit_native_link_step(*observed_interval_fixture())
        self.assertTrue(result["allowed"],result)
        self.assertTrue(result["details"]["usedSampleIntervalGeometryAllowance"])
        self.assertAlmostEqual(result["details"]["ordinaryApproachExtensionMetres"],.403662,places=5)

    def test_approach_just_beyond_one_ordinary_interval_is_rejected(self):
        data = observed_interval_fixture()
        dt = data[2]["time"]-data[1]["time"]
        data[1]["position"]["z"] = 44.8-1-data[2]["requestedNavigationSpeed"]*dt-.001
        self.check_bad(data,"finite saved lane")

    def test_flagged_position_keeps_fixed_endpoint_envelope(self):
        data = observed_interval_fixture();data[2]["position"]["z"] = 43.799
        self.check_bad(data,"finite saved lane")

    def test_preentry_unflagged_position_cannot_extend_exit_side(self):
        data = observed_interval_fixture();data[1]["position"]["z"] = 48.201
        self.check_bad(data,"finite saved lane")

    def test_postexit_unflagged_position_cannot_extend_approach_side(self):
        data = observed_interval_fixture()
        data = (data[0],copy.deepcopy(data[2]),data[2],data[3])
        data[1].update(time=10,position=v(10.6,45))
        data[2].update(time=10.25,position=v(10.6,43.799),isOnNativeLink=False)
        self.check_bad(data,"finite saved lane")

    def test_ordinary_interval_extension_is_capped_at_half_metre(self):
        data = observed_interval_fixture();data[2]["time"] = data[1]["time"]+10
        data[1]["position"]["z"] = 44.8-1.501
        self.check_bad(data,"finite saved lane")

    def lateral_fixture(self):
        data = fixture()
        dt = data[2]["time"] - data[1]["time"]
        radius = min(data[2]["requestedNavigationSpeed"] * dt, .5)
        data[1]["position"].update(x=34.6 - .775 - radius * .3, z=44.8 - 1 - radius * .3)
        return data, radius

    def test_unflagged_approach_corner_shares_one_2d_interval(self):
        data, radius = self.lateral_fixture()
        result = audit_native_link_step(*data)
        self.assertTrue(result["allowed"], result)
        self.assertTrue(result["details"]["usedSampleIntervalGeometryAllowance"])
        self.assertAlmostEqual(radius * .3, result["details"]["ordinaryApproachLateralExcessMetres"])

    def test_lateral_approach_beyond_ordinary_interval_is_rejected(self):
        data, radius = self.lateral_fixture()
        data[1]["position"].update(x=34.6 - .775 - radius - .001, z=44.2)
        self.check_bad(data, "finite saved lane")

    def test_diagonal_cannot_spend_full_allowance_twice(self):
        data, radius = self.lateral_fixture()
        data[1]["position"].update(x=34.6 - .775 - radius * .8, z=44.8 - 1 - radius * .8)
        self.check_bad(data, "finite saved lane")

    def test_flagged_lateral_geometry_cannot_borrow_ordinary_allowance(self):
        data, _ = self.lateral_fixture()
        data[2]["position"]["x"] = 34.6 + .775 + .001
        self.check_bad(data, "finite saved lane")

    def test_unflagged_approach_lateral_padding_does_not_expand_link_interior(self):
        data, radius = self.lateral_fixture()
        data[1]["position"].update(x=34.6 + .775 + radius * .1, z=46)
        self.check_bad(data, "finite saved lane")

    def test_unflagged_exit_corner_gets_own_interval_only(self):
        data = fixture()
        before = copy.deepcopy(data[2])
        before.update(time=10, position=v(34.6, 47.1))
        data = (data[0], before, data[2], data[3])
        data[2].update(time=10.2, isOnNativeLink=False, position=v(35.45, 48.28))
        result = audit_native_link_step(*data)
        self.assertTrue(result["allowed"], result)
        self.assertTrue(result["details"]["usedSampleIntervalGeometryAllowance"])
        self.assertAlmostEqual(.075, result["details"]["ordinaryExitLateralExcessMetres"])


if __name__ == "__main__":
    unittest.main()
