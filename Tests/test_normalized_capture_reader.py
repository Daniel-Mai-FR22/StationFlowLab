"""Independent corruption, checkpoint and expanded-view tests for current normalized data."""
import copy
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Tests"))
from normalized_capture_fixture import NormalizedFixture
SPEC = importlib.util.spec_from_file_location("normalized_reader", ROOT / "Assets/StreamingAssets/TrainStationData/read_capture.py")
READER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(READER)


class NormalizedReaderTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="station-v4-reader-")
        self.root = pathlib.Path(self.temp.name) / "capture"
        self.fixture = NormalizedFixture(self.root)

    def tearDown(self):
        self.temp.cleanup()

    def reader(self):
        return READER.CaptureReader(self.root)

    def fails(self, text):
        with self.assertRaisesRegex(READER.CaptureFormatError, text):
            with self.reader() as reader:
                reader.validate_references()

    def mutate(self, path, fn):
        values = self.fixture.read_lines(path)
        fn(values)
        self.fixture.lines(path, values)

    def test_exact_clock_roster_default_policy_profile_and_schedule_expansion(self):
        with self.reader() as reader:
            report = reader.validate_references()
            self.assertEqual(report["clock"], 3)
            timeline = list(reader.iter_records("sync.jsonl"))
            self.assertEqual(timeline[1]["stamp"], self.fixture.stamps[0])
            self.assertEqual(timeline[2]["activeJourneyIds"], [self.fixture.agent_id])
            packet = reader.read_json(self.fixture.directory + "/profile.json")
            self.assertEqual(packet["journey"], dict(groupSize=1, journeyId="journey-1", serviceId="S1"))
            self.assertEqual(packet["profile"]["policyParameters"], dict(temperature=1.25, enabled=True))
            self.assertNotIn("policyId", packet["profile"])
            event = list(reader.iter_records(self.fixture.directory + "/events.jsonl"))[0]
            self.assertEqual(event["profile"], packet["profile"])
            self.assertEqual(reader.read_json("scenario.json")["services"], reader.read_json("timetable.json")["services"])
            self.assertNotIn("servicesFile", reader.read_json("scenario.json"))

    def test_state_delta_and_full_checkpoint_roundtrip(self):
        self.fixture = NormalizedFixture(self.root, observations=258)
        with self.reader() as reader:
            reader.validate_references()
            rows = list(reader.iter_records(self.fixture.directory + "/trajectories.jsonl"))
            self.assertEqual(len(rows), 258)
            self.assertEqual([rows[i]["activity"] for i in (0, 1, 255, 256, 257)], ["Walk", "Read", "Read", "Read", "Read"])
            self.assertEqual(rows[256]["position"]["x"], 257)
            self.assertEqual(rows[256]["captureStamp"], self.fixture.stamps[256])
            self.assertEqual(rows[256]["agentId"], "journey-1")
            self.assertNotIn("stateId", rows[256])

    def test_checkpoint_has_self_contained_full_values_and_exact_crlf_offsets(self):
        self.fixture = NormalizedFixture(self.root, observations=258)
        with self.reader() as reader:
            reader.validate_references()
            cp = self.fixture.read_lines("state-checkpoints.jsonl")[1]
            state = reader._at_offset(cp["path"], cp["byteOffset"])
            self.assertTrue(state["checkpoint"])
            self.assertEqual(state["values"], dict(actorSlotId="slot-1", activity="Read"))
            dense = reader._at_offset(cp["densePath"], cp["denseByteOffset"])
            self.assertEqual(dense["syncSampleId"], 257)
            self.assertEqual(dense["stateId"], state["stateId"])

    def test_foreign_normalization_header_is_rejected(self):
        header=self.fixture.read("normalization.json");header["runId"]="foreign";self.fixture.write("normalization.json",header)
        self.fails("normalization metadata runId")

    def test_definition_stamp_field_must_match_its_template(self):
        self.mutate("record-definitions.jsonl", lambda rr: rr[0].update(stampField=""))
        self.fails("stampField disagrees")

    def test_same_state_returns_after_change_as_new_revision(self):
        # Revisit an old value through a new delta; dense observations retain all times.
        path=self.fixture.directory+"/state-changes.jsonl"
        self.mutate(path,lambda rr:rr.append(dict(stream="trajectories",stateId=3,fromSyncSampleId=3,observationIndex=3,checkpoint=False,changes=dict(activity="Walk"))))
        self.mutate(self.fixture.directory+"/trajectories.jsonl",lambda rr:rr[2].update(stateId=3))
        with self.reader() as reader:
            rows=list(reader.iter_records(self.fixture.directory+"/trajectories.jsonl"))
            self.assertEqual([r["activity"] for r in rows],["Walk","Read","Walk"])

    def test_policy_and_journey_alias_override_remains_exact(self):
        path=self.fixture.directory+"/profile.json";packet=self.fixture.read(path);packet["journey"]["serviceId"]="different";self.fixture.write(path,packet)
        with self.reader() as reader:self.assertEqual(reader.read_json(path)["journey"]["serviceId"],"different")

    def test_empty_roster_is_a_valid_exact_revision(self):
        self.mutate("rosters.jsonl",lambda rr:rr.append(dict(rosterId=2,agentIds=[])))
        self.mutate("sync.jsonl",lambda rr:rr[-2].update(rosterId=2))
        with self.reader() as reader:self.assertEqual(list(reader.iter_records("sync.jsonl"))[-2]["activeJourneyIds"],[])

    def test_missing_clock_reference(self):
        self.mutate("events.jsonl", lambda rr: rr[0].update(syncSampleId=999))
        self.fails("Missing clock")

    def test_duplicate_clock_reference(self):
        self.mutate("sync.jsonl", lambda rr: rr.insert(1, copy.deepcopy(rr[0])))
        self.fails("monotonic|Duplicate clock")

    def test_foreign_clock_run(self):
        self.mutate("sync.jsonl", lambda rr: rr[0]["stamp"].update(runId="foreign"))
        self.fails("foreign run")

    def test_same_unity_frame_distinct_sync_ids_are_retained(self):
        self.mutate("sync.jsonl", lambda rr: [r["stamp"].update(unityFrame=1) for r in rr if r.get("kind")=="clock"])
        with self.reader() as reader:
            reader.validate_references()
            ids = [r["stamp"]["syncSampleId"] for r in reader.iter_records("sync.jsonl") if r["kind"]=="clock"]
            self.assertEqual(ids, [1, 2, 3])

    def test_paused_equal_simulation_time_keeps_distinct_real_observations(self):
        self.mutate("sync.jsonl", lambda rr: [r["stamp"].update(simulationSeconds=0, stationClockSeconds=18000, paused=True, timeScale=0) for r in rr if r.get("kind")=="clock"])
        with self.reader() as reader:
            rows=list(reader.iter_records(self.fixture.directory + "/trajectories.jsonl"))
            self.assertEqual([r["captureStamp"]["realElapsedSeconds"] for r in rows], [.1,.2,.3])

    def test_duplicate_definition(self):
        self.mutate("record-definitions.jsonl", lambda rr: rr.append(copy.deepcopy(rr[0])))
        self.fails("Duplicate definition")

    def test_missing_definition(self):
        self.mutate("events.jsonl", lambda rr: rr[0].update(definitionId=999))
        self.fails("Missing definition")

    def test_missing_policy_reference(self):
        p=self.fixture.directory+"/profile.json";o=self.fixture.read(p);o["profile"]["policyId"]=999;self.fixture.write(p,o)
        self.fails("Missing policy")

    def test_missing_roster_reference(self):
        self.mutate("events.jsonl", lambda rr: rr[0].update(rosterId=999))
        self.fails("Missing roster")

    def test_foreign_roster_member(self):
        self.mutate("rosters.jsonl", lambda rr: rr[0].update(agentIds=["foreign"]))
        self.fails("foreign roster")

    def test_duplicate_roster_member(self):
        self.mutate("rosters.jsonl", lambda rr: rr[0].update(agentIds=["journey-1", "journey-1"]))
        self.fails("roster membership")

    def test_missing_initial_state(self):
        self.mutate(self.fixture.directory+"/state-changes.jsonl", lambda rr: rr.pop(0))
        self.fails("state revisions|Initial")

    def test_stale_state_reference(self):
        self.mutate(self.fixture.directory+"/trajectories.jsonl", lambda rr: rr[1].update(stateId=1))
        self.fails("stale agent state")

    def test_future_state_reference(self):
        self.mutate(self.fixture.directory+"/trajectories.jsonl", lambda rr: rr[0].update(stateId=2))
        self.fails("future agent state|Checkpoint dense/state offsets")

    def test_context_agent_cannot_be_overridden(self):
        self.mutate(self.fixture.directory+"/decisions.jsonl", lambda rr: rr[0].update(agentId="foreign"))
        self.fails("contextual agent")

    def test_dense_row_cannot_repeat_state_payload(self):
        self.mutate(self.fixture.directory+"/trajectories.jsonl", lambda rr: rr[0].update(activity="Override"))
        self.fails("repeats state")

    def test_wrong_checkpoint_offset(self):
        self.mutate("state-checkpoints.jsonl", lambda rr: rr[0].update(byteOffset=1))
        self.fails("differs from physical|physical line")

    def test_wrong_dense_checkpoint_offset(self):
        self.mutate("state-checkpoints.jsonl", lambda rr: rr[0].update(denseByteOffset=1))
        self.fails("physical line")

    def test_missing_checkpoint_index(self):
        self.mutate("state-checkpoints.jsonl", lambda rr: rr.pop())
        self.fails("Missing state checkpoint")

    def test_traversal_checkpoint_path(self):
        self.mutate("state-checkpoints.jsonl", lambda rr: rr[0].update(path="../outside"))
        self.fails("differs from physical")

    def test_duplicate_json_keys(self):
        self.fixture.write_text("normalization.json", '{"schema":"x","schema":"y"}')
        self.fails("Duplicate JSON key")

    def test_nonfinite_json(self):
        self.mutate("sync.jsonl", lambda rr: rr[0]["stamp"].update(realElapsedSeconds=float("nan")))
        self.fails("Non-finite")

    def test_current_format_only(self):
        self.fixture.manifest["schema"]="stationflow.station_capture.v2";self.fixture.write("manifest.json",self.fixture.manifest)
        with self.assertRaisesRegex(READER.CaptureFormatError,"Unknown capture schema"):
            self.reader()

    def test_optional_all_avatar_streams_off(self):
        import shutil
        shutil.rmtree(self.root)
        self.fixture=NormalizedFixture(self.root,agents=False)
        with self.reader() as reader:
            self.assertEqual(reader.validate_references()["clock"],3)

    def test_camera_state_revisions_restore_pose_and_original_clock(self):
        self.fixture.add_camera()
        with self.reader() as reader:
            reader.validate_references()
            rows=list(reader.iter_records("video/HALL-TEST/frames.jsonl"))
            self.assertEqual([r["camera"]["position"]["x"] for r in rows],[0,3,3])
            self.assertEqual(rows[0]["stamp"],self.fixture.stamps[0])
            self.assertIsNone(rows[0]["captureFpsInstantaneous"])
            self.assertNotIn("cameraStateId",rows[0])

    def test_camera_missing_state(self):
        self.fixture.add_camera();self.mutate("video/HALL-TEST/frames.jsonl",lambda rr: rr[0].update(cameraStateId=999))
        self.fails("Missing camera")

    def test_camera_stale_state(self):
        self.fixture.add_camera();self.mutate("video/HALL-TEST/frames.jsonl",lambda rr: rr[1].update(cameraStateId=1))
        self.fails("stale state")

    def test_camera_future_state(self):
        self.fixture.add_camera();self.mutate("video/HALL-TEST/frames.jsonl",lambda rr: rr[0].update(cameraStateId=2))
        self.fails("future state")

    def test_camera_duplicate_state(self):
        self.fixture.add_camera();self.mutate("video/HALL-TEST/camera-states.jsonl",lambda rr: rr.append(copy.deepcopy(rr[0])))
        self.fails("nonmonotonic")

    def test_camera_missing_measured_fps_field(self):
        self.fixture.add_camera();self.fixture.manifest["captureCadence"]="each_unity_frame";self.fixture.write("manifest.json",self.fixture.manifest)
        self.mutate("video/HALL-TEST/frames.jsonl",lambda rr: rr[0].pop("captureIntervalRealSeconds"))
        self.fails("lost measured FPS")

    def test_unsafe_direct_path(self):
        with self.reader() as reader:
            for path in ("../x", "a/../x", "a\\x", "/tmp/x", "C:/x", "a//x"):
                with self.subTest(path=path),self.assertRaises(READER.CaptureFormatError):reader.path(path)

    def test_source_is_unchanged_and_export_has_own_analysis_schema(self):
        before={str(p.relative_to(self.root)):p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        output=self.root.parent/"expanded"
        with self.reader() as reader:
            result=READER.export_expanded(reader,output)
        self.assertEqual(result["schema"],"stationflow.expanded_analysis_view.v1")
        self.assertFalse((output/"manifest.json").exists())
        self.assertEqual(json.loads((output/"analysis-view.json").read_text())["sourceManifest"]["schema"],READER.V4)
        self.assertNotIn("stateId",json.loads((output/self.fixture.directory/"trajectories.jsonl").read_text().splitlines()[0]))
        self.assertEqual(before,{str(p.relative_to(self.root)):p.read_bytes() for p in self.root.rglob("*") if p.is_file()})

    def test_agent_export_is_self_contained_with_full_stamps(self):
        output=self.root.parent/"one-agent"
        with self.reader() as reader:READER.export_expanded(reader,output,self.fixture.agent_id)
        self.assertEqual(json.loads((output/"analysis-view.json").read_text())["schema"],"stationflow.agent_analysis_view.v1")
        rows=[json.loads(x) for x in (output/"trajectories.jsonl").read_text().splitlines()]
        clocks=[json.loads(x) for x in (output/"clocks.jsonl").read_text().splitlines()]
        self.assertEqual(rows[0]["captureStamp"],clocks[0])
        self.assertNotIn("policyId",json.loads((output/"profile.json").read_text())["profile"])

    def test_export_refuses_overwrite_and_source_descendant(self):
        with self.reader() as reader:
            for output in (self.root,self.root/"export",self.root.parent):
                with self.assertRaisesRegex(READER.CaptureFormatError,"new directory"):
                    READER.export_expanded(reader,output)

    def test_french_guide_preserves_exact_wire_literals_and_executable_example(self):
        guide=(ROOT/"Assets/StreamingAssets/TrainStationData/LireDonnees.fr.txt").read_text(encoding="utf-8")
        self.assertIn("recordEncoding = " + READER.ENCODING + ".",guide)
        self.assertIn("status=complete",guide)
        snippet=guide.split("from read_capture import CaptureReader\n\n",1)[1].split("\nL'identifiant",1)[0]
        self.assertTrue(snippet.isascii())
        class ExampleCapture:
            def __init__(self,*_):pass
            def __enter__(self):return self
            def __exit__(self,*_):pass
            def validate_references(self):return {}
            def read_json(self,*_):return {}
            def iter_records(self,*_):return iter([{"stamp":{"syncSampleId":1,"realElapsedSeconds":.1},"camera":{}}])
        exec(compile(snippet,"FrenchReaderExample","exec"),{"CaptureReader":ExampleCapture,"print":lambda *_:None})

    def test_close_releases_temporary_index(self):
        reader=self.reader();reader.validate_references();self.assertIsNotNone(reader._db);reader.close();self.assertIsNone(reader._db)
        with self.assertRaisesRegex(READER.CaptureFormatError,"closed"):reader.read_json("scenario.json")


if __name__ == "__main__":
    unittest.main()