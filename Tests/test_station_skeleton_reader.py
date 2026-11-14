"""Independent binary world-skeleton reader corruption and synchronization tests."""
import copy
import importlib.util
import json
import shutil
from pathlib import Path
import struct
import sys
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"Tests"))
from normalized_capture_fixture import NormalizedFixture
SPEC=importlib.util.spec_from_file_location("skeleton_capture_reader",ROOT/"Assets/StreamingAssets/TrainStationData/read_capture.py")
R=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(R)
VSPEC=importlib.util.spec_from_file_location("skeleton_capture_validator",ROOT/"BuildRequests/validate_station_collection_capture.py")
V=importlib.util.module_from_spec(VSPEC);VSPEC.loader.exec_module(V)

class SkeletonReaderTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix="station-v4-skeleton-");self.root=Path(self.temp.name)/"capture"
        self.fixture=NormalizedFixture(self.root);self.fixture.add_skeletons()
        self.relative=self.fixture.directory+"/skeleton-poses.bin"
    def tearDown(self):self.temp.cleanup()
    def reader(self):return R.CaptureReader(self.root)
    def fails(self,fragment):
        with self.assertRaisesRegex(R.CaptureFormatError,fragment):
            with self.reader() as reader:reader.validate_references()
    def mutate_jsonl(self,path,fn):
        rows=self.fixture.read_lines(path);fn(rows);self.fixture.lines(path,rows)
    def mutate_bin(self,offset,fmt,value):
        p=self.root/self.relative;data=bytearray(p.read_bytes());struct.pack_into(fmt,data,offset,value);p.write_bytes(data)

    def test_float32_world_positions_and_validity_roundtrip_without_camera(self):
        with self.reader() as reader:
            inventory=reader.validate_references();rows=list(reader.iter_records(self.relative))
            self.assertEqual(rows,self.fixture.skeleton_rows)
            self.assertEqual(inventory["skeleton-rig"],1)
            self.assertEqual(len(rows[0]["jointPositionsWorld"]),55)
            self.assertFalse(rows[0]["valid"][54]);self.assertEqual(rows[0]["jointPositionsWorld"][54],[0,0,0])
            self.assertEqual(rows[1]["bindingId"],2);self.assertNotIn("stamp",rows[0])
    def test_skeleton_only_without_profile_animation_trajectory_or_events(self):
        self.fixture.add_skeletons(only=True)
        result=V.validate(self.root);self.assertTrue(result["success"],result["errors"])
        self.assertEqual(result["checks"]["skeleton-poses.bin"],3)
        self.assertFalse((self.root/self.fixture.directory/"profile.json").exists())
        self.assertFalse((self.root/"video").exists())
    def test_every256_pose_index_offset_and_binding_are_exact(self):
        self.fixture=NormalizedFixture(self.root,observations=258);self.fixture.add_skeletons()
        with self.reader() as reader:
            rows=list(reader.iter_records(self.relative));self.assertEqual(rows,self.fixture.skeleton_rows)
        cp=self.fixture.read_lines(self.fixture.directory+"/skeleton-index.jsonl")
        self.assertEqual([x["observationIndex"] for x in cp],[1,257])
        self.assertEqual(cp[1]["byteOffset"],16+256*699)
    def test_raw_binary_inspection_retains_reference_clock(self):
        with self.reader() as reader:
            rows=list(reader.iter_raw_records(self.relative))
            self.assertEqual(rows[0]["syncSampleId"],1);self.assertNotIn("captureStamp",rows[0])
            self.assertEqual(rows[0]["jointPositionsWorld"],self.fixture.skeleton_rows[0]["jointPositionsWorld"])
    def test_header_magic_is_required(self):
        self.mutate_bin(0,"<B",0);self.fails("binary header")
    def test_header_version_is_required(self):
        self.mutate_bin(8,"<I",2);self.fails("binary header")
    def test_header_byte_size_is_required(self):
        self.mutate_bin(12,"<I",12);self.fails("binary header")
    def test_binary_record_length_is_bounded(self):
        self.mutate_bin(16,"<I",2**32-1);self.fails("byte length")
    def test_truncated_record_is_rejected(self):
        p=self.root/self.relative;p.write_bytes(p.read_bytes()[:-1]);self.fails("Truncated skeleton")
    def test_trailing_partial_length_is_rejected(self):
        p=self.root/self.relative;p.write_bytes(p.read_bytes()+b"\0");self.fails("Truncated skeleton record length")
    def test_joint_count_is_exact(self):
        self.mutate_bin(36,"<i",54);self.fails("count")
    def test_mask_byte_count_is_exact(self):
        self.mutate_bin(40,"<i",8);self.fails("count")
    def test_unknown_clock_is_rejected(self):
        self.mutate_bin(20,"<q",999);self.fails("Missing clock")
    def test_sample_sequence_is_strict(self):
        self.mutate_bin(16+699+12,"<i",1);self.fails("sample")
    def test_binary_time_agrees_with_original_float32_clock(self):
        self.mutate_bin(44,"<f",99);self.fails("time differs")
    def test_unknown_binding_is_rejected(self):
        self.mutate_bin(32,"<i",999);self.fails("Missing skeleton-binding")
    def test_future_binding_is_rejected(self):
        self.mutate_bin(32,"<i",2);self.fails("activation")
    def test_stale_binding_is_rejected(self):
        self.mutate_bin(16+699+16,"<i",1);self.fails("stale binding")
    def test_validity_mask_unused_bit_is_zero(self):
        self.mutate_bin(54,"<B",191);self.fails("unused bits")
    def test_missing_joint_cannot_be_valid(self):
        self.mutate_bin(54,"<B",127);self.fails("joint position or validity")
    def test_invalid_joint_coordinates_must_be_zero(self):
        self.mutate_bin(48,"<B",254);self.fails("joint position or validity")
    def test_nonfinite_joint_coordinate_is_rejected(self):
        self.mutate_bin(55,"<f",float("nan"));self.fails("joint position")
    def test_rig_definition_hash_is_verified(self):
        self.mutate_jsonl("skeleton-definitions.jsonl",lambda rows:rows[0]["transformPaths"].__setitem__(0,"Changed"));self.fails("SHA256")
    def test_joint_order_is_canonical(self):
        self.mutate_jsonl("skeleton-definitions.jsonl",lambda rows:rows[0]["jointNames"].__setitem__(0,"head"));self.fails("joint order")
    def test_parent_cycles_are_rejected(self):
        self.mutate_jsonl("skeleton-definitions.jsonl",lambda rows:rows[0]["parentIndices"].__setitem__(0,1));self.fails("cycle")
    def test_duplicate_transform_paths_are_rejected(self):
        self.mutate_jsonl("skeleton-definitions.jsonl",lambda rows:rows[0]["transformPaths"].__setitem__(1,rows[0]["transformPaths"][0]));self.fails("Duplicate skeleton Transform")
    def test_missing_checkpoint_is_rejected(self):
        self.fixture.lines(self.fixture.directory+"/skeleton-index.jsonl",[]);self.fails("checkpoint index")
    def test_bad_checkpoint_offset_is_rejected(self):
        self.mutate_jsonl(self.fixture.directory+"/skeleton-index.jsonl",lambda rows:rows[0].update(byteOffset=17));self.fails("exact pose")
    def test_extra_checkpoint_is_rejected(self):
        self.mutate_jsonl(self.fixture.directory+"/skeleton-index.jsonl",lambda rows:rows.append(copy.deepcopy(rows[0])));self.fails("unmatched")
    def test_missing_binding_file_is_rejected(self):
        (self.root/self.fixture.directory/"skeleton-bindings.jsonl").unlink();self.fails("incomplete")
    def test_bindings_must_activate_on_the_first_referenced_pose(self):
        self.mutate_jsonl(self.fixture.directory+"/skeleton-bindings.jsonl",lambda rows:rows[0].update(effectiveFromSyncSampleId=2));self.fails("sequence")
    def test_unreferenced_binding_is_rejected(self):
        self.mutate_jsonl(self.fixture.directory+"/skeleton-bindings.jsonl",lambda rows:rows.append(dict(rows[-1],bindingId=3,effectiveFromSyncSampleId=3)))
        self.fails("stale binding|unreferenced")
    def test_disabled_skeleton_stream_is_rejected(self):
        self.fixture.options["skeletons"]=False;self.fixture.write("manifest.json",self.fixture.manifest);self.fails("toggle")
    def test_declared_skeleton_definitions_must_exist(self):
        (self.root/"skeleton-definitions.jsonl").unlink();self.fails("definitions file")
    def test_old_v3_capture_is_not_supported(self):
        self.fixture.manifest["schema"]="stationflow.station_capture.v3";self.fixture.write("manifest.json",self.fixture.manifest)
        with self.assertRaisesRegex(R.CaptureFormatError,"Unknown capture schema"):self.reader()
    def test_export_decodes_binary_to_explicit_analysis_jsonl_and_keeps_rig(self):
        output=self.root.parent/"expanded"
        with self.reader() as reader:R.export_expanded(reader,output,self.fixture.agent_id)
        self.assertFalse((output/"skeleton-poses.bin").exists())
        rows=[json.loads(x) for x in (output/"skeleton-poses.jsonl").read_text().splitlines()]
        self.assertEqual(rows,self.fixture.skeleton_rows)
        self.assertTrue((output/"skeleton-bindings.jsonl").is_file())
        self.assertTrue((output/"skeleton-definitions.jsonl").is_file())
        self.assertEqual(json.loads((output/"analysis-view.json").read_text())["sourceManifest"]["schema"],R.V4)
    def test_skeleton_video_exact_active_agent_set_joins_without_other_avatar_streams(self):
        self.fixture=NormalizedFixture(self.root,observations=15);self.fixture.add_skeletons(only=True,rebind=False);self.fixture.add_camera()
        source=ROOT/"Reports/TrainStationDaily/CollectionTests/streaming-mjpeg.avi"
        if not source.exists():self.skipTest("Standalone AVI fixture unavailable")
        shutil.copyfile(source,self.root/"video/HALL-TEST/segment-0001.avi")
        result=V.validate(self.root);self.assertTrue(result["success"],result["errors"])
        self.assertEqual(result["checks"]["exactVideoFrameAvatarJoins"],15)
        path=self.root/self.relative;data=path.read_bytes();path.write_bytes(data[:16+699]+data[16+1398:])
        result=V.validate(self.root);self.assertFalse(result["success"])
        self.assertTrue(any("active avatar set differs from skeleton-poses.bin" in error for error in result["errors"]),result["errors"])
    def test_skeleton_toggle_must_be_explicit_boolean(self):
        self.fixture.options.pop("skeletons");self.fixture.write("manifest.json",self.fixture.manifest);self.fails("explicit boolean")
    def test_skeleton_descriptor_keys_are_required_even_when_disabled(self):
        self.fixture=NormalizedFixture(self.root);self.fixture.manifest.pop("skeletonPoseFormat");self.fixture.write("manifest.json",self.fixture.manifest)
        self.fails("manifest declaration")
    def test_empty_skeleton_capture_is_valid_without_materialized_agent_streams(self):
        for name in ("skeleton-bindings.jsonl","skeleton-poses.bin","skeleton-index.jsonl"):
            (self.root/self.fixture.directory/name).unlink()
        self.fixture.lines("skeleton-definitions.jsonl",[])
        with self.reader() as reader:self.assertEqual(reader.validate_references()["schema"],R.V4)

if __name__=="__main__":unittest.main()

