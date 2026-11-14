"""Current v4 capture validation: raw references, toggles, exact joins and VFR chronology."""
import copy
import importlib.util
import json
import pathlib
import shutil
import sys
import tempfile
import unittest

ROOT=pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"Tests"))
from normalized_capture_fixture import NormalizedFixture
SPEC=importlib.util.spec_from_file_location("collection_validation",ROOT/"BuildRequests/validate_station_collection_capture.py")
VALIDATOR=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class CollectionTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix="station-v4-validator-")
        self.folder=pathlib.Path(self.temp.name)/"capture"
        self.fixture=NormalizedFixture(self.folder,observations=15)

    def tearDown(self):self.temp.cleanup()
    def validate(self):return VALIDATOR.validate(self.folder)
    def failed(self,fragment):
        report=self.validate();self.assertFalse(report["success"]);self.assertTrue(any(fragment in e for e in report["errors"]),report["errors"])
    def mutate(self,name,fn):
        rows=self.fixture.read_lines(name);fn(rows);self.fixture.lines(name,rows)
    def manifest(self):self.fixture.write("manifest.json",self.fixture.manifest)

    def camera(self,measured=False):
        avi=ROOT/"Reports/TrainStationDaily/CollectionTests/streaming-mjpeg.avi"
        if not avi.exists():self.skipTest("Standalone AVI fixture is unavailable")
        self.fixture.add_camera()
        shutil.copyfile(avi,self.folder/"video/HALL-TEST/segment-0001.avi")
        if measured:
            self.fixture.manifest.update(captureCadence="each_unity_frame",nominalPlaybackFps=5,actualVideoRateDefinition="Original render stamps define actual FPS; AVI playback remains nominal.")
            self.manifest()
            rows=self.fixture.read_lines("video/HALL-TEST/frames.jsonl")
            times=[s["realElapsedSeconds"] for s in self.fixture.stamps]
            for i,row in enumerate(rows):row["captureFpsRolling1s"]=sum(times[i]-1<t<=times[i] for t in times[:i+1])
            self.fixture.lines("video/HALL-TEST/frames.jsonl",rows)
            last=self.fixture.stamps[-1]
            self.fixture.timeline.insert(-1,dict(definitionId=1,syncSampleId=15,kind="measured_frame_rates",measuredFrameRates=dict(windowSeconds=1,gameFpsRolling1s=10,cameras=[dict(cameraId="HALL-TEST",framesWritten=15,requestsDropped=0,videoFpsRolling1s=sum(last["realElapsedSeconds"]-1<t<=last["realElapsedSeconds"] for t in times))])))
            self.fixture.lines("sync.jsonl",self.fixture.timeline)
            calibration=self.fixture.read("video/HALL-TEST/calibration.json");calibration["fps"]=5;self.fixture.write("video/HALL-TEST/calibration.json",calibration)

    def test_current_data_exact_agent_clock_profile_and_sparse_metadata(self):
        report=self.validate();self.assertTrue(report["success"],report["errors"])
        self.assertEqual(report["checks"]["clockSamples"],15)
        self.assertEqual(report["checks"]["agentLayout"],"per_agent.v3")
        self.assertEqual(report["checks"]["normalizedReferences"]["schema"],"stationflow.station_capture.v4")
        self.assertEqual(report["checks"]["perAgentProfiles"],1)

    def test_only_current_capture_schema_is_accepted(self):
        self.fixture.manifest["schema"]="stationflow.station_capture.v2";self.manifest();self.failed("Unknown capture schema")
    def test_manifest_must_be_complete(self):
        self.fixture.manifest["status"]="recording";self.manifest();self.failed("not closed")
    def test_capture_errors_cannot_be_hidden(self):
        self.fixture.manifest["errors"]=["writer failed"];self.manifest();self.failed("capture errors")
    def test_root_dangling_clock_is_rejected_before_hydration(self):
        self.mutate("events.jsonl",lambda rr:rr[0].update(syncSampleId=999));self.failed("Missing clock")
    def test_missing_definition_is_rejected(self):
        self.mutate("events.jsonl",lambda rr:rr[0].update(definitionId=999));self.failed("Missing definition")
    def test_unregistered_physical_agent_directory_is_rejected(self):
        (self.folder/"agents/unregistered").mkdir();self.failed("Unregistered")
    def test_disabled_agent_stream_is_rejected(self):
        self.fixture.options["decisions"]=False;self.manifest();self.failed("Disabled")
    def test_disabled_profiles_are_rejected(self):
        self.fixture.options["profiles"]=False;self.manifest();self.failed("Disabled")
    def test_wrong_participant_count_is_rejected(self):
        scenario=self.fixture.read("scenario.json");scenario["participantCount"]=2;self.fixture.write("scenario.json",scenario);self.failed("every planned")
    def test_duplicate_avatar_observation_is_rejected(self):
        self.mutate(self.fixture.directory+"/trajectories.jsonl",lambda rr:rr.append(copy.deepcopy(rr[-1])));self.failed("Duplicate or backwards observation")
    def test_roster_cannot_reference_foreign_agent(self):
        self.mutate("rosters.jsonl",lambda rr:rr[0].update(agentIds=["foreign"]));self.failed("foreign roster")
    def test_root_station_events_cannot_be_attributed_to_agent(self):
        self.mutate("events.jsonl",lambda rr:rr[0].update(agentId="journey-1"));self.failed("station events only")
    def test_missing_periodic_checkpoint_index_is_rejected(self):
        self.fixture.lines("state-checkpoints.jsonl",[]);self.failed("Missing state checkpoint")
    def test_missing_avatar_snapshot_is_rejected_with_video(self):
        self.camera();self.mutate("sync.jsonl",lambda rr:rr.__setitem__(slice(None),[r for r in rr if not(r.get("kind")=="avatar_snapshot" and r.get("syncSampleId")==2)]));self.failed("snapshot")
    def test_camera_video_decoded_header_counts_and_exact_avatar_joins(self):
        self.camera();report=self.validate();self.assertTrue(report["success"],report["errors"]);self.assertEqual(report["checks"]["exactVideoFrameAvatarJoins"],15)
    def test_camera_missing_state_is_rejected(self):
        self.camera();self.mutate("video/HALL-TEST/frames.jsonl",lambda rr:rr[0].update(cameraStateId=999));self.failed("Missing camera")
    def test_camera_state_count_is_checked(self):
        self.camera();self.fixture.manifest["cameraResults"][0]["cameraStatesWritten"]=99;self.manifest();self.failed("state count")
    def test_variable_fps_is_joined_to_original_clocks(self):
        self.camera(True);report=self.validate();self.assertTrue(report["success"],report["errors"]);self.assertEqual(report["checks"]["measuredVideoRateFrames"],15)
    def test_first_fps_interval_must_be_explicit_null(self):
        self.camera(True);self.mutate("video/HALL-TEST/frames.jsonl",lambda rr:rr[0].update(captureIntervalRealSeconds=0));self.failed("First measured")
    def test_instantaneous_fps_corruption_is_rejected(self):
        self.camera(True);self.mutate("video/HALL-TEST/frames.jsonl",lambda rr:rr[2].update(captureFpsInstantaneous=99));self.failed("instantaneous")
    def test_missing_fps_field_is_rejected_on_raw_frames(self):
        self.camera(True);self.mutate("video/HALL-TEST/frames.jsonl",lambda rr:rr[2].pop("captureFpsRolling1s"));self.failed("lost measured FPS")
    def test_measured_saved_prefix_cannot_include_future_completions(self):
        self.camera(True);self.mutate("sync.jsonl",lambda rr:[r["measuredFrameRates"]["cameras"][0].update(framesWritten=99) for r in rr if r.get("kind")=="measured_frame_rates"]);self.failed("finalized prefix")
    def test_all_avatar_streams_off_has_no_agent_dependencies(self):
        shutil.rmtree(self.folder);self.fixture=NormalizedFixture(self.folder,agents=False)
        report=self.validate();self.assertTrue(report["success"],report["errors"])
    def test_time_table_off_must_not_leak_services(self):
        self.fixture.options["timetable"]=False;self.manifest();self.failed("Toggle/file disagreement")
    def test_unknown_agent_state_file_is_rejected(self):
        self.fixture.write_text(self.fixture.directory+"/extra.jsonl","{}\n");self.failed("unknown")
    def test_aliased_agent_stream_is_rejected(self):
        target=self.folder/self.fixture.directory/"alias.jsonl"
        try:target.symlink_to(self.folder/self.fixture.directory/"events.jsonl")
        except OSError:self.skipTest("Symlink permission is unavailable")
        self.failed("Aliased|aliased")


if __name__=="__main__":unittest.main()