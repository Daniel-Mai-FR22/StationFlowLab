#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Visual gait evidence collected alongside the full saved-population Play regression.</summary>
    [InitializeOnLoad]
    public static class StationGaitPlayReview
    {
        const string StateKey = "StationFlow.StationGait.PlayReview";
        const string DirectoryPath = "Reports/TrainStationSocial/GaitReview";
        const string ReportPath = DirectoryPath + "/play-review.json";
        const string MixedReportPath = "Reports/TrainStationSocial/mixed-crowd-play-check.json";
        const int FrameCount = 24;
        const float Interval = .13f;

        [Serializable] public sealed class FrameSample
        {
            public string file, state, motion;
            public float simulationTime, interval, worldSpeed, playbackSpeed, normalizedTime, headingErrorDegrees;
            public float groundY, leftSoleHeight, rightSoleHeight, leftFootPlanarSpeed, rightFootPlanarSpeed;
            public float leftFootVerticalSpeed, rightFootVerticalSpeed;
            public float minimumBakedSkinWorldY, minimumSkinClearance, navMeshRootY, agentNextPositionY, agentBaseOffset, modelWorldY, modelLocalY;
            public bool groundFound, leftPlantCandidate, rightPlantCandidate, cameraAndUiRestored;
            public Vector3 position, forward, hips, leftKnee, rightKnee, leftFoot, rightFoot, leftToes, rightToes;
        }
        [Serializable] public sealed class Review
        {
            public string startedUtc, completedUtc, phase, actor, actorPath, error;
            public string mixedCrowdReport = MixedReportPath;
            public string method = "24 actual Play-mode poses of one saved solo traveller. CPU-baked skin rendered through the saved camera; the complete mixed-crowd test runs concurrently. Actual simulation timestamps are retained.";
            public string plantInterpretation = "Plant candidate means estimated sole within 4 cm of the raycast floor and ankle planar speed below 0.20 m/s. It is a diagnostic sign, not collision/contact proof.";
            public bool success, captureSuccess, mixedCrowdSuccess, everyFrameRestored = true;
            public bool ownsCaptureTimeScale;
            public float timeScaleBeforeCapture;
            public float playStartedAt = -1, nextFrameAt, actualCaptureSpan;
            public long deadlineTicks;
            public Vector3 cameraForward;
            public List<FrameSample> frames = new List<FrameSample>();
        }
        [Serializable] sealed class MixedOutcome { public string startedUtc, completedUtc; public bool success; }

        static Review live;
        static StationPassenger selected;
        static readonly Dictionary<StationPassenger, float> steadySince = new Dictionary<StationPassenger, float>();
        static readonly Dictionary<StationPassenger, Vector3> previousForward = new Dictionary<StationPassenger, Vector3>();
        static int previousFrame = -1;

        static StationGaitPlayReview()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Review walking in full Play check")]
        public static void BeginPlayCheck()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Finish Play/import before starting the gait review.");
            if (Load() != null) throw new InvalidOperationException("A gait review is already pending.");
            live = new Review { startedUtc = DateTime.UtcNow.ToString("O"), phase = "waiting for Play", deadlineTicks = DateTime.UtcNow.AddMinutes(6).Ticks };
            selected = null; previousFrame = -1; steadySince.Clear(); previousForward.Clear();
            Save();
            try { StationMixedCrowdVerification.BeginPlayCheck(); }
            catch (Exception error) { Fail(error.ToString()); Finish(false); throw; }
        }

        static void ModeChanged(PlayModeStateChange state)
        {
            live = live ?? Load(); if (live == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                live.phase = "waiting for stable solo walker"; live.playStartedAt = Time.time;
                selected = null; previousFrame = -1; steadySince.Clear(); previousForward.Clear(); Save();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // The mixed-crowd verifier owns restoration of its original editor time scale.
                live.ownsCaptureTimeScale = false; Save();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                if (!live.captureSuccess && string.IsNullOrEmpty(live.error)) Fail("Play ended before all 24 gait frames were collected.");
                live.phase = "waiting for mixed-crowd result"; Save();
            }
        }

        static void Tick()
        {
            live = live ?? Load(); if (live == null) return;
            if (DateTime.UtcNow.Ticks > live.deadlineTicks) { Fail("Gait review timed out waiting for Play or the mixed-crowd result."); Finish(false); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (File.Exists(MixedReportPath))
                {
                    var result = JsonUtility.FromJson<MixedOutcome>(File.ReadAllText(MixedReportPath));
                    if (!string.IsNullOrEmpty(result.completedUtc) && DateTime.TryParse(result.startedUtc, out var started) &&
                        DateTime.TryParse(live.startedUtc, out var ours) && started.ToUniversalTime() >= ours.ToUniversalTime().AddSeconds(-2))
                        Finish(result.success);
                }
                return;
            }
            if (!Application.isPlaying || EditorApplication.isPaused || EditorApplication.isCompiling || previousFrame == Time.frameCount) return;
            previousFrame = Time.frameCount;
            if (live.captureSuccess || !string.IsNullOrEmpty(live.error)) return;
            try
            {
                if (live.playStartedAt < 0) live.playStartedAt = Time.time;
                if (Time.time - live.playStartedAt > 30f)
                    throw new InvalidOperationException("Could not collect 24 frames of a stable solo walker above 0.8 m/s within 30 simulation seconds; captured " + live.frames.Count + ".");
                var scene = SceneManager.GetActiveScene();
                var world = Components<StationSocialWorld>(scene).FirstOrDefault();
                if (!world || world.passengers == null || Time.frameCount < 15) return;
                if (!selected && !string.IsNullOrEmpty(live.actorPath))
                    selected = world.passengers.FirstOrDefault(p => p && HierarchyPath(p.transform) == live.actorPath);
                if (!selected)
                {
                    foreach (var passenger in world.passengers.Where(p => p && p.activity == StationPassengerActivity.SoloWalking))
                    {
                        var motion = passenger.motion;
                        Vector3 forward = passenger.transform.forward;
                        bool eligible = motion && motion.IsWalkPose && motion.ActualPlanarSpeed > .8f && motion.agent && motion.agent.isOnNavMesh &&
                            !motion.agent.pathPending && motion.agent.remainingDistance > 5f && !motion.animationPlayer.TargetAnimator.IsInTransition(0) &&
                            Vector3.Angle(Flat(motion.agent.velocity), forward) < 12f;
                        Vector3 view = passenger.transform.position + Vector3.Cross(Vector3.up, forward) * 2.65f + forward * 1.8f + Vector3.up * 1.3f;
                        eligible &= !Physics.Linecast(passenger.transform.position + Vector3.up * .92f, view, ~0, QueryTriggerInteraction.Ignore);
                        if (!eligible || (previousForward.TryGetValue(passenger, out var prior) && Vector3.Angle(prior, forward) > 5f))
                            steadySince[passenger] = Time.time;
                        else if (!steadySince.ContainsKey(passenger)) steadySince[passenger] = Time.time;
                        previousForward[passenger] = forward;
                        if (!eligible || Time.time - steadySince[passenger] < .5f) continue;
                        selected = passenger; live.actor = passenger.name; live.actorPath = HierarchyPath(passenger.transform);
                        live.cameraForward = Flat(forward).normalized; live.nextFrameAt = Time.time; live.phase = "capturing"; Save(); break;
                    }
                }
                if (!selected || Time.time < live.nextFrameAt) return;
                if (!live.ownsCaptureTimeScale)
                {
                    live.timeScaleBeforeCapture = Time.timeScale; live.ownsCaptureTimeScale = true; Save();
                    Time.timeScale = 1f;
                }
                // Preserve real timing even if rendering or the concurrent test's time scale stretches the interval.
                var sample = CaptureFrame(scene, selected, live.frames.LastOrDefault(), live.cameraForward, live.frames.Count);
                live.frames.Add(sample); live.everyFrameRestored &= sample.cameraAndUiRestored;
                live.nextFrameAt = Time.time + Interval;
                if (live.frames.Count == FrameCount)
                {
                    live.captureSuccess = true; live.phase = "captured; mixed-crowd check continues";
                    live.actualCaptureSpan = live.frames.Last().simulationTime - live.frames[0].simulationTime;
                    RestoreCaptureTimeScale();
                }
                Save();
            }
            catch (Exception error) { Fail(error.ToString()); }
        }

        static FrameSample CaptureFrame(Scene scene, StationPassenger actor, FrameSample previous, Vector3 heading, int index)
        {
            var camera = Components<Camera>(scene).First(c => c.isActiveAndEnabled);
            var canvases = Components<Canvas>(scene); var enabled = canvases.Select(c => c.enabled).ToArray();
            var skins = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.enabled && s.gameObject.activeInHierarchy).ToArray();
            var proxies = new List<GameObject>(); var meshes = new List<Mesh>();
            var position = camera.transform.position; var rotation = camera.transform.rotation; var targetBefore = camera.targetTexture;
            var activeBefore = RenderTexture.active; float fov = camera.fieldOfView; var rect = camera.rect;
            int objectsBefore = Components<Transform>(scene).Length; bool dirtyBefore = scene.isDirty;
            RenderTexture target = null; Texture2D image = null;
            var animator = actor.motion.animationPlayer.TargetAnimator;
            Vector3 Bone(HumanBodyBones bone) { var transform = animator.GetBoneTransform(bone); if (!transform) throw new InvalidOperationException("Missing gait bone " + bone); return transform.position; }
            var sample = new FrameSample { file = "frame-" + index.ToString("000") + ".png", simulationTime = Time.time,
                interval = previous == null ? 0 : Time.time - previous.simulationTime, state = actor.CurrentState.ToString(), motion = actor.motion.CurrentMotion,
                worldSpeed = actor.motion.ActualPlanarSpeed, playbackSpeed = actor.motion.animationPlayer.PlaybackSpeed,
                normalizedTime = animator.GetCurrentAnimatorStateInfo(0).normalizedTime, position = actor.transform.position, forward = actor.transform.forward,
                hips = Bone(HumanBodyBones.Hips), leftKnee = Bone(HumanBodyBones.LeftLowerLeg), rightKnee = Bone(HumanBodyBones.RightLowerLeg),
                leftFoot = Bone(HumanBodyBones.LeftFoot), rightFoot = Bone(HumanBodyBones.RightFoot), leftToes = Bone(HumanBodyBones.LeftToes), rightToes = Bone(HumanBodyBones.RightToes),
                headingErrorDegrees = Vector3.Angle(Flat(actor.motion.agent.velocity), actor.transform.forward),
                navMeshRootY = actor.transform.position.y, agentNextPositionY = actor.motion.agent.nextPosition.y, agentBaseOffset = actor.motion.agent.baseOffset,
                modelWorldY = animator.transform.position.y, modelLocalY = animator.transform.localPosition.y };
            var grounds = Physics.RaycastAll(actor.transform.position + Vector3.up * .4f, Vector3.down, 1f, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.collider && !h.collider.transform.IsChildOf(actor.transform) && !h.collider.GetComponentInParent<StationActorMotion>() &&
                    !h.collider.GetComponentInParent<Camera>() && h.normal.y > .75f && h.point.y <= actor.transform.position.y + .15f)
                .OrderBy(h => h.distance).ToArray();
            sample.groundFound = grounds.Length > 0;
            sample.groundY = sample.groundFound ? grounds[0].point.y : actor.transform.position.y;
            float leftSole = float.PositiveInfinity, rightSole = float.PositiveInfinity, skinBottom = float.PositiveInfinity;
            try
            {
                foreach (var canvas in canvases) canvas.enabled = false;
                foreach (var skin in skins)
                {
                    var mesh = new Mesh { name = "Temporary actual walking pose", hideFlags = HideFlags.HideAndDontSave }; meshes.Add(mesh);
                    skin.BakeMesh(mesh, false); mesh.RecalculateBounds();
                    if (mesh.vertexCount == 0) throw new InvalidOperationException("Empty sampled walking skin.");
                    foreach (var local in mesh.vertices)
                    {
                        if (!Finite(local)) throw new InvalidOperationException("Non-finite sampled walking skin.");
                        Vector3 vertex = skin.transform.TransformPoint(local);
                        if (!Finite(vertex)) throw new InvalidOperationException("Non-finite walking skin in world coordinates.");
                        skinBottom = Mathf.Min(skinBottom, vertex.y);
                        if (Flat(vertex - sample.leftFoot).sqrMagnitude < .04f && vertex.y < sample.leftFoot.y + .04f) leftSole = Mathf.Min(leftSole, vertex.y);
                        if (Flat(vertex - sample.rightFoot).sqrMagnitude < .04f && vertex.y < sample.rightFoot.y + .04f) rightSole = Mathf.Min(rightSole, vertex.y);
                    }
                    var proxy = new GameObject("Gait capture only — " + skin.name) { hideFlags = HideFlags.HideAndDontSave, layer = skin.gameObject.layer }; proxies.Add(proxy);
                    proxy.transform.SetParent(skin.transform, false); proxy.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = proxy.AddComponent<MeshRenderer>(); renderer.sharedMaterials = skin.sharedMaterials;
                    renderer.shadowCastingMode = skin.shadowCastingMode; renderer.receiveShadows = skin.receiveShadows;
                    renderer.lightProbeUsage = skin.lightProbeUsage; renderer.reflectionProbeUsage = skin.reflectionProbeUsage;
                    renderer.renderingLayerMask = skin.renderingLayerMask; renderer.probeAnchor = skin.probeAnchor; skin.enabled = false;
                }
                if (!Finite(leftSole) || !Finite(rightSole) || !Finite(skinBottom)) throw new InvalidOperationException("Cannot measure both walking soles from the actor's baked skin.");
                sample.leftSoleHeight = leftSole - sample.groundY; sample.rightSoleHeight = rightSole - sample.groundY;
                sample.minimumBakedSkinWorldY = skinBottom; sample.minimumSkinClearance = skinBottom - sample.groundY;
                if (previous != null && sample.interval > .0001f)
                {
                    var left = (sample.leftFoot - previous.leftFoot) / sample.interval; var right = (sample.rightFoot - previous.rightFoot) / sample.interval;
                    sample.leftFootPlanarSpeed = Flat(left).magnitude; sample.rightFootPlanarSpeed = Flat(right).magnitude;
                    sample.leftFootVerticalSpeed = left.y; sample.rightFootVerticalSpeed = right.y;
                    sample.leftPlantCandidate = sample.groundFound && sample.leftSoleHeight < .04f && sample.leftSoleHeight > -.04f && sample.leftFootPlanarSpeed < .2f;
                    sample.rightPlantCandidate = sample.groundFound && sample.rightSoleHeight < .04f && sample.rightSoleHeight > -.04f && sample.rightFootPlanarSpeed < .2f;
                }
                target = new RenderTexture(640, 800, 24, RenderTextureFormat.ARGB32); target.Create();
                camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1); camera.fieldOfView = 42;
                Vector3 side = Vector3.Cross(Vector3.up, heading);
                camera.transform.position = actor.transform.position + side * 2.65f + heading * 1.8f + Vector3.up * 1.3f;
                camera.transform.LookAt(actor.transform.position + Vector3.up * .92f);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target; image = new Texture2D(640, 800, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 640, 800), 0, 0); image.Apply();
                Directory.CreateDirectory(DirectoryPath); File.WriteAllBytes(DirectoryPath + "/" + sample.file, image.EncodeToPNG());
            }
            finally
            {
                foreach (var skin in skins) if (skin) skin.enabled = true;
                foreach (var proxy in proxies) if (proxy) UnityEngine.Object.DestroyImmediate(proxy);
                foreach (var mesh in meshes) if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                for (int i = 0; i < canvases.Length; i++) if (canvases[i]) canvases[i].enabled = enabled[i];
                camera.transform.SetPositionAndRotation(position, rotation); camera.fieldOfView = fov; camera.rect = rect; camera.targetTexture = targetBefore;
                RenderTexture.active = activeBefore; if (image) UnityEngine.Object.DestroyImmediate(image);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                sample.cameraAndUiRestored = scene.isDirty == dirtyBefore && Components<Transform>(scene).Length == objectsBefore &&
                    camera.transform.position == position && Quaternion.Angle(camera.transform.rotation, rotation) < .001f && camera.targetTexture == targetBefore &&
                    Mathf.Abs(camera.fieldOfView - fov) < .0001f && camera.rect == rect && canvases.Select((c, i) => c && c.enabled == enabled[i]).All(v => v);
                if (!sample.cameraAndUiRestored) throw new InvalidOperationException("Gait frame did not restore its camera, UI, scene dirty state or object count.");
            }
            return sample;
        }

        static void RestoreCaptureTimeScale()
        {
            if (!live.ownsCaptureTimeScale) return;
            if (Application.isPlaying) Time.timeScale = live.timeScaleBeforeCapture;
            live.ownsCaptureTimeScale = false;
        }
        static void Fail(string error) { if (live == null) return; RestoreCaptureTimeScale(); live.error = string.IsNullOrEmpty(live.error) ? error : live.error + "\n" + error; live.phase = "capture failed; awaiting mixed-crowd result"; Save(); }
        static void Finish(bool mixedSuccess)
        {
            live.mixedCrowdSuccess = mixedSuccess; live.success = live.captureSuccess && live.everyFrameRestored && mixedSuccess && string.IsNullOrEmpty(live.error);
            live.completedUtc = DateTime.UtcNow.ToString("O"); live.phase = "complete"; Write();
            SessionState.EraseString(StateKey); live = null; selected = null; steadySince.Clear(); previousForward.Clear();
        }
        static Review Load() { string json = SessionState.GetString(StateKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Review>(json); }
        static void Save() { SessionState.SetString(StateKey, JsonUtility.ToJson(live)); Write(); }
        static void Write() { Directory.CreateDirectory(DirectoryPath); File.WriteAllText(ReportPath, JsonUtility.ToJson(live, true)); }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static string HierarchyPath(Transform node) { string path = node.name; while (node.parent) { node = node.parent; path = node.name + "/" + path; } return path; }
        static Vector3 Flat(Vector3 value) { value.y = 0; return value; }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
#endif
