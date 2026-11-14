"""Hand-authored normalized format fixture; independent of the runtime normalizer."""
import copy
import hashlib
import json
import struct
from pathlib import Path


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


class NormalizedFixture:
    def __init__(self, root, observations=3, agents=True):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.agent_id = "journey-1"
        self.directory = "agents/agent-journey-1-" + hashlib.sha256(self.agent_id.encode()).hexdigest()[:16]
        self.observations = observations
        self.stamps = [{"runId": "test", "syncSampleId": i, "unityFrame": i, "realElapsedSeconds": i / 10,
                        "simulationSeconds": i / 10, "stationClockSeconds": 18000 + i / 10,
                        "simulationDate": "2026-10-20", "utc": "2026-10-20T03:00:00.0000000Z", "paused": False, "timeScale": 1}
                       for i in range(1, observations + 1)]
        self.options = dict(video=False, timetable=True, profiles=agents, trajectories=agents, animations=agents,
                            decisions=agents, events=agents, skeletons=False, videoWidth=320, videoHeight=180, videoFps=5)
        self.manifest = dict(schema="stationflow.station_capture.v4", storageLayout="per_agent.v3", recordEncoding="references-and-state-revisions.v1",
                             runId="test", status="complete", errors=[], captureOptions=self.options, syncSamples=observations,
                             agentsDirectory="agents" if agents else "", agentIndexFile="agents/index.json" if agents else "",
                             selectedCameraIds=[], cameraResults=[], videoFrameAvatarSnapshots=True,
                             skeletonDefinitionsFile="", skeletonPoseFileName="", skeletonBindingsFileName="", skeletonIndexFileName="", skeletonPoseFormat="")
        self.definitions = [
            dict(definitionId=1, type="SyncRecord", stampField="stamp", defaults=dict(kind="", detail="", activeJourneyIds=[], stamp=None,
                 measuredFrameRates=dict(windowSeconds=1, gameFpsRolling1s=0, cameras=[]))),
            dict(definitionId=2, type="EventRecord", stampField="captureStamp", defaults=dict(kind="", captureStamp=None, time=0,
                 agentId="", actorSlotId="", sampleId=0, activeJourneyIds=[], profile={})),
            dict(definitionId=3, type="AgentProfileFile", stampField="captureStamp", defaults=dict(captureStamp=None, profile={}, journey={})),
            dict(definitionId=4, type="TimetableFile", stampField="captureStamp", defaults=dict(captureStamp=None, operatingDate="", services=[])),
            dict(definitionId=5, type="DecisionRecord", stampField="captureStamp", defaults=dict(captureStamp=None, agentId="", trigger="")),
            dict(policyId=1, policyParameters=dict(temperature=1.25, enabled=True)),
            dict(definitionId=6, type="Manifest", stampField="", defaults=dict(services=[], profiles=[], scheduledJourneys=[]))]
        self.write("manifest.json", self.manifest)
        self.write("normalization.json", dict(schema="stationflow.normalization.v1", captureSchema="stationflow.station_capture.v4", storageLayout="per_agent.v3", recordEncoding="references-and-state-revisions.v1", runId="test", clockFile="sync.jsonl", definitionsFile="record-definitions.jsonl",
                   rostersFile="rosters.jsonl", checkpointIndexFile="state-checkpoints.jsonl", checkpointInterval=256,
                   profileJourneyAliases=dict(journeyId="agentId", serviceId="serviceId")))
        self.lines("record-definitions.jsonl", self.definitions)
        self.lines("rosters.jsonl", [dict(rosterId=1, agentIds=[self.agent_id])] if agents else [])
        self.timeline = []
        for i, stamp in enumerate(self.stamps, 1):
            self.timeline.append(dict(definitionId=1, kind="clock", stamp=stamp))
            if i == 1:
                self.timeline.append(dict(definitionId=1, syncSampleId=i, kind="capture_started"))
            if agents:
                self.timeline.append(dict(definitionId=1, syncSampleId=i, kind="avatar_snapshot", rosterId=1, detail="simulation_sample"))
        self.timeline.append(dict(definitionId=1, syncSampleId=observations, kind="capture_stopped"))
        self.lines("sync.jsonl", self.timeline)
        self.write("scenario.json", dict(definitionId=6, participantCount=int(agents), servicesFile="timetable.json"))
        self.write("timetable.json", dict(definitionId=4, syncSampleId=1, operatingDate="2026-10-20", services=[dict(serviceId="S1", track=1)]))
        self.lines("train-states.jsonl", [])
        self.write_text("README.txt", "Fixture des donnees normalisees.\n")
        if agents:
            self.write("agents/index.json", dict(schema="stationflow.agents_index.v1", runId="test", identityPolicy="logical identity",
                       agents=[dict(agentId=self.agent_id, directory=self.directory)]))
            self.write(self.directory + "/profile.json", dict(definitionId=3, syncSampleId=1,
                       profile=dict(agentId=self.agent_id, serviceId="S1", policyId=1), journey=dict(groupSize=1)))
            self.lines("events.jsonl", [dict(definitionId=2, syncSampleId=i, kind="trajectory_sample", sampleId=i, rosterId=1) for i in range(1, observations + 1)])
            self.lines(self.directory + "/decisions.jsonl", [dict(definitionId=5, syncSampleId=1, trigger="spawn")])
            self.lines(self.directory + "/events.jsonl", [dict(definitionId=2, syncSampleId=1, kind="spawn", profileRef="profile.json#profile")])
            self.write_states()
        else:
            self.lines("state-checkpoints.jsonl", [])

    def write_text(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="")

    def write(self, name, value):
        self.write_text(name, compact(value))

    def read(self, name):
        return json.loads((self.root / name).read_text(encoding="utf-8-sig"))

    def read_lines(self, name):
        return [json.loads(x) for x in (self.root / name).read_text(encoding="utf-8-sig").splitlines() if x.strip()]

    def lines(self, name, values):
        offset, offsets, text = 0, [], []
        for value in values:
            offsets.append(offset)
            line = compact(value) + "\r\n"
            text.append(line)
            offset += len(line.encode("utf-8"))
        self.write_text(name, "".join(text))
        return offsets

    def write_states(self):
        state_rows, dense, checkpoints = [], {"trajectories": [], "animations": []}, []
        for stream in dense:
            state_id, values = 0, dict(actorSlotId="slot-1", activity="Walk") if stream == "trajectories" else dict(actorSlotId="slot-1", motionId="Idle")
            for observation in range(1, self.observations + 1):
                full = (observation - 1) % 256 == 0
                if full or observation == 2:
                    state_id += 1
                    if observation == 2:
                        values.update(activity="Read") if stream == "trajectories" else values.update(motionId="Walk")
                    row = dict(stream=stream, stateId=state_id, fromSyncSampleId=observation, observationIndex=observation, checkpoint=full)
                    row["values" if full else "changes"] = copy.deepcopy(values if full else {k: v for k, v in values.items() if k != "actorSlotId"})
                    state_rows.append(row)
                    if full:
                        checkpoints.append(dict(agentId=self.agent_id, stream=stream, stateId=state_id, fromSyncSampleId=observation,
                            observationIndex=observation, path=self.directory + "/state-changes.jsonl", densePath=self.directory + "/" + stream + ".jsonl"))
                payload = dict(syncSampleId=observation, stateId=state_id, time=observation / 10, sampleId=observation)
                if stream == "trajectories":
                    payload.update(position=dict(x=observation, y=0, z=0), rotation=dict(x=0, y=0, z=0, w=1), velocity=dict(x=1,y=0,z=0), budget=10, seatedSecondsRemaining=0)
                else:
                    payload.update(normalizedTime=observation / 100, nextNormalizedTime=0, transitionProgress=0, nativePlanarSpeed=1)
                dense[stream].append(payload)
        offsets = self.lines(self.directory + "/state-changes.jsonl", state_rows)
        for cp in checkpoints:
            cp["byteOffset"] = next(offset for offset, row in zip(offsets, state_rows) if row["stream"] == cp["stream"] and row["stateId"] == cp["stateId"])
        for stream, rr in dense.items():
            offsets = self.lines(self.directory + "/" + stream + ".jsonl", rr)
            for cp in checkpoints:
                if cp["stream"] == stream:
                    cp["denseByteOffset"] = offsets[cp["observationIndex"] - 1]
        self.lines("state-checkpoints.jsonl", checkpoints)

    def add_camera(self):
        relative = "video/HALL-TEST"
        self.options["video"] = True
        self.manifest["selectedCameraIds"] = ["HALL-TEST"]
        self.manifest["cameraResults"] = [dict(cameraId="HALL-TEST", directory=relative, framesWritten=self.observations, requestsDropped=0,
                    segments=1, cameraStateFile="camera-states.jsonl", cameraStatesWritten=2 if self.observations > 1 else 1)]
        self.write("manifest.json", self.manifest)
        states = [dict(cameraStateId=1, effectiveFromSyncSampleId=1, camera=dict(position=dict(x=0,y=1,z=2), worldToCamera=list(range(16))))]
        if self.observations > 1:
            states.append(dict(cameraStateId=2, effectiveFromSyncSampleId=2, camera=dict(position=dict(x=3,y=1,z=2), worldToCamera=list(range(16)))))
        self.lines(relative + "/camera-states.jsonl", states)
        self.lines(relative + "/frames.jsonl", [dict(syncSampleId=i, cameraStateId=min(i, 2), segment=1, videoFrame=i-1,
                   videoTimeSeconds=(i-1)/5, requestId=i, captureIntervalRealSeconds=None if i==1 else .1,
                   captureFpsInstantaneous=None if i==1 else 10, captureFpsRolling1s=i) for i in range(1, self.observations+1)])
        self.write(relative + "/calibration.json", dict(cameraId="HALL-TEST", width=320, height=180, worldToCameraMatrix=list(range(16)), projectionMatrix=list(range(16))))
    def add_skeletons(self, only=False, rebind=True):
        """Independent fixed-width binary fixture, authored without the runtime writer or reader."""
        self.options["skeletons"] = True
        self.manifest.update(skeletonDefinitionsFile="skeleton-definitions.jsonl", skeletonPoseFileName="skeleton-poses.bin",
                             skeletonBindingsFileName="skeleton-bindings.jsonl", skeletonIndexFileName="skeleton-index.jsonl",
                             skeletonPoseFormat="stationflow.skeleton_pose_binary.v1")
        if only:
            for key in ("profiles", "trajectories", "animations", "decisions", "events"):
                self.options[key] = False
            for name in ("profile.json", "trajectories.jsonl", "animations.jsonl", "decisions.jsonl", "events.jsonl", "state-changes.jsonl"):
                (self.root/self.directory/name).unlink(missing_ok=True)
            (self.root/"events.jsonl").unlink(missing_ok=True)
            self.lines("state-checkpoints.jsonl", [])
        self.write("manifest.json", self.manifest)
        names = "Hips LeftUpperLeg RightUpperLeg LeftLowerLeg RightLowerLeg LeftFoot RightFoot Spine Chest Neck Head LeftShoulder RightShoulder LeftUpperArm RightUpperArm LeftLowerArm RightLowerArm LeftHand RightHand LeftToes RightToes LeftEye RightEye Jaw LeftThumbProximal LeftThumbIntermediate LeftThumbDistal LeftIndexProximal LeftIndexIntermediate LeftIndexDistal LeftMiddleProximal LeftMiddleIntermediate LeftMiddleDistal LeftRingProximal LeftRingIntermediate LeftRingDistal LeftLittleProximal LeftLittleIntermediate LeftLittleDistal RightThumbProximal RightThumbIntermediate RightThumbDistal RightIndexProximal RightIndexIntermediate RightIndexDistal RightMiddleProximal RightMiddleIntermediate RightMiddleDistal RightRingProximal RightRingIntermediate RightRingDistal RightLittleProximal RightLittleIntermediate RightLittleDistal UpperChest".split()
        parents = [-1] + [0]*53 + [-1]
        paths = ["Rig/"+name for name in names[:-1]] + [""]
        payload = bytearray(b"stationflow.skeleton_definition.v1\n") + struct.pack("<i",55)
        for name,parent,path in zip(names,parents,paths):
            nb,pb=name.encode("utf-8"),path.encode("utf-8")
            payload += struct.pack("<i",len(nb))+nb+struct.pack("<i",parent)+struct.pack("<i",len(pb))+pb
        self.skeleton_rig = dict(schema="stationflow.skeleton_definition.v1",rigId="rig-"+hashlib.sha256(payload).hexdigest(),
            jointNames=names,parentIndices=parents,transformPaths=paths,coordinateSystem="UnityWorld",units="metres",positionEncoding="float32")
        self.lines("skeleton-definitions.jsonl",[self.skeleton_rig])
        self.skeleton_bindings=[dict(bindingId=1,rigId=self.skeleton_rig["rigId"],actorSlotId="slot-001",avatarName="Avatar1",effectiveFromSyncSampleId=1)]
        if rebind and self.observations>1:
            self.skeleton_bindings.append(dict(bindingId=2,rigId=self.skeleton_rig["rigId"],actorSlotId="slot-002",avatarName="Avatar2",effectiveFromSyncSampleId=2))
        self.lines(self.directory+"/skeleton-bindings.jsonl",self.skeleton_bindings)
        self.skeleton_rows=[]
        checkpoints=[]
        data=bytearray(b"SFLPOSE1")+struct.pack("<II",1,16)
        mask=bytes([255]*6+[63])
        for observation,stamp in enumerate(self.stamps,1):
            binding=min(observation,len(self.skeleton_bindings))
            positions=[]
            for i in range(55):
                positions.extend([observation*.12345678+i*.1,i*.2,-observation/3] if i<54 else [0,0,0])
            values=struct.unpack("<165f",struct.pack("<165f",*positions))
            time=struct.unpack("<f",struct.pack("<f",stamp["simulationSeconds"]))[0]
            length=32+7+55*12
            if (observation-1)%256==0:
                checkpoints.append(dict(observationIndex=observation,syncSampleId=stamp["syncSampleId"],byteOffset=len(data),bindingId=binding))
            data += struct.pack("<Iqiiiif",length,stamp["syncSampleId"],observation,binding,55,7,time)+mask+struct.pack("<165f",*values)
            self.skeleton_rows.append(dict(agentId=self.agent_id,captureStamp=stamp,sampleId=observation,time=time,bindingId=binding,
                rigId=self.skeleton_rig["rigId"],jointPositionsWorld=[list(values[i*3:i*3+3]) for i in range(55)],valid=[i<54 for i in range(55)]))
        (self.root/self.directory/"skeleton-poses.bin").write_bytes(data)
        self.lines(self.directory+"/skeleton-index.jsonl",checkpoints)
