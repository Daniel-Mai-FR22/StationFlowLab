using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StationFlow.TrainStation.Social
{
    /// <summary>Opt-in native-frame gait evidence, omitted from ordinary runs unless --station-gait-probe is present.</summary>
    [DefaultExecutionOrder(31000)]
    public sealed class StationSlowGaitRuntimeProbe : MonoBehaviour
    {
        const int Rate = 30, Width = 384, Height = 480;
        [Serializable] public sealed class Measurement
        {
            public string mode;
            public float speed, cadence, stride, cycleSeconds, initialPhase, finalPhase;
            public int frames, supportSamples, invalidPoseSamples, unexpectedStateSamples;
            public float actualCadence, actualStrideParameter, referenceWalkClipWeight;
            public float leftExcursion, rightExcursion, meanSupportSlip, maximumSupportSlip;
            public float minKneeAngle = 180f, maxKneeAngle, minKneeForward = 999f;
            public float minimumFootHeight = 999f, maximumFootAboveHip = -999f;
            public float maximumFrameFootDisplacement, maximumFullStrideBoneDifference;
            public string videoFrames;
        }
        [Serializable] public sealed class Report
        {
            public bool success;
            public string error, sourceActor, sourceAvatar, library, walkClip, referenceClip, output, startedUtc, finishedUtc;
            public float referenceSpeed, cycleLength, humanScale, simulationDelta = 1f / Rate;
            public int captureRate = Rate;
            public string method = "Actual Unity PlayerLoop animation evaluation on disposable clones of the saved Daily Humanoid, using its own matched Avatar/controller/walk/idle. No Animator.Update/Rebind/phase seeking during sample frames. Capture follows WaitForEndOfFrame. Root advances at commanded fixture speed; this opt-in fixture never changes production navigation. Fixed captureDeltaTime=1/30 applies only to this separate diagnostic process, not to the crowd FPS benchmark.";
            public string supportProxy = "Planar foot velocity during backward-moving phases within25mm of that foot's cycle minimum, plus measured root velocity. Approximate stance/slip, not anatomical ground-contact truth.";
            public List<Measurement> measurements = new List<Measurement>();
        }
        sealed class Pose
        {
            public Vector3 left, right, root;
            public float phase, leftKnee, rightKnee, leftForward, rightForward, footAboveHip, strideParameter, walkWeight;
            public int stateHash;
            public Vector3[] bones;
        }

        string directory;
        Report report;
        GameObject[] clones;
        Animator[] animators;
        SkinnedMeshRenderer[][] skins;
        Camera previewCamera;
        RenderTexture target;
        Texture2D image, combined, sheet;
        Material floorMaterial;
        float speed, startedSimulation;
        bool moving;
        AnimationClip fullWalkClip;
        readonly Vector3 origin = new Vector3(1000f, 0f, 1000f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Environment.GetCommandLineArgs().Contains("--station-gait-probe")) return;
            new GameObject("Opt-in native gait probe").AddComponent<StationSlowGaitRuntimeProbe>();
        }

        IEnumerator Start()
        {
            directory = Argument("--station-gait-probe-output");
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "GaitProbe");
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            report = new Report { output = directory, startedUtc = DateTime.UtcNow.ToString("O") };
            Application.runInBackground = true;
            Application.targetFrameRate = Rate;
            QualitySettings.vSyncCount = 0;
            Time.captureDeltaTime = 1f / Rate;
            Time.timeScale = 1f;
            yield return null;
            Exception setupError = null;
            try { Setup(); }
            catch (Exception exception) { setupError = exception; }
            if (setupError != null) { Finish(setupError); yield break; }
            Time.timeScale = 1f;
            yield return new WaitForEndOfFrame(); // Let native initialization evaluate the newly activated clones.
            foreach (float testSpeed in new[] { .10f, .20f, .35f, .50f, .80f, report.referenceSpeed })
            {
                speed = testSpeed;
                bool capture = Mathf.Abs(speed - .20f) < .001f || Mathf.Abs(speed - .35f) < .001f || Mathf.Abs(speed - .50f) < .001f;
                var measurements = new Measurement[2];
                var poses = new[] { new List<Pose>(), new List<Pose>() };
                for (int mode = 0; mode < 2; mode++)
                {
                    StationActorMotion.CalculateWalkPresentation(speed, report.referenceSpeed, mode == 1, out float cadence, out float stride);
                    measurements[mode] = new Measurement { mode = mode == 0 ? "legacy_speed_only" : "short_stride", speed = speed, cadence = cadence, stride = stride, cycleSeconds = report.cycleLength / cadence };
                    report.measurements.Add(measurements[mode]);
                    animators[mode].speed = cadence;
                    animators[mode].SetFloat(StationActorMotion.StrideParameter, stride);
                    animators[mode].Play("Base Layer." + (mode == 0 ? StationActorMotion.DefaultWalkId : StationActorMotion.AdaptiveWalkId), 0, 0f);
                }
                startedSimulation = Time.time;
                moving = true;
                int warmup = Mathf.CeilToInt(measurements[0].cycleSeconds * Rate);
                int count = Mathf.Max(capture ? 120 : 0, Mathf.CeilToInt(2f * measurements[0].cycleSeconds * Rate) + 2);
                for (int frame = 0; frame < warmup; frame++) yield return new WaitForEndOfFrame();
                string folder = Path.Combine(directory, "speed" + Mathf.RoundToInt(speed * 100).ToString("D3"));
                if (capture) { Directory.CreateDirectory(folder); measurements[0].videoFrames = folder; measurements[1].videoFrames = folder; }
                for (int frame = 0; frame < count; frame++)
                {
                    yield return new WaitForEndOfFrame();
                    Exception sampleError = null;
                    try
                    {
                        for (int mode = 0; mode < 2; mode++) poses[mode].Add(Observe(animators[mode], fullWalkClip));
                        if (capture && frame < 120) RenderFrame(folder, frame);
                    }
                    catch (Exception exception) { sampleError = exception; }
                    if (sampleError != null) { Finish(sampleError); yield break; }
                }
                Exception analysisError = null;
                try
                {
                    for (int mode = 0; mode < 2; mode++) Analyze(measurements[mode], poses[mode]);
                    if (measurements[1].stride >= .9999f)
                    {
                        float difference = 0f;
                        for (int i = 0; i < poses[0].Count; i++) for (int bone = 0; bone < poses[0][i].bones.Length; bone++)
                            difference = Mathf.Max(difference, Vector3.Distance(poses[0][i].bones[bone], poses[1][i].bones[bone]));
                        measurements[1].maximumFullStrideBoneDifference = difference;
                        if (difference > .005f) throw new InvalidOperationException("Full-stride native blend differs from the original walk: " + difference);
                    }
                    if (capture) { sheet.Apply(); File.WriteAllBytes(Path.Combine(folder, "contact-sheet.png"), sheet.EncodeToPNG()); }
                    Save();
                }
                catch (Exception exception) { analysisError = exception; }
                if (analysisError != null) { Finish(analysisError); yield break; }
            }
            Finish(null);
        }

        void Setup()
        {
            var source = FindObjectsByType<StationActorMotion>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(m => m.animationPlayer && m.animationPlayer.TargetAnimator && m.animationPlayer.Library &&
                    m.animationPlayer.Library.Find(StationActorMotion.AdaptiveWalkId) != null &&
                    m.animationPlayer.Library.name.IndexOf("Matched", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!source) throw new InvalidOperationException("No actual saved Daily matched Humanoid with the adaptive state was found.");
            var sourceAnimator = source.animationPlayer.TargetAnimator;
            var library = source.animationPlayer.Library;
            report.sourceActor = source.name; report.sourceAvatar = sourceAnimator.avatar.name; report.library = library.name;
            report.referenceSpeed = source.EffectiveWalkReferenceSpeed; report.humanScale = sourceAnimator.humanScale;
            fullWalkClip = library.Find(StationActorMotion.DefaultWalkId).clip;
            report.walkClip = fullWalkClip.name;
            report.referenceClip = library.Find(StationActorMotion.AdaptiveWalkId).blendChildren[0].clip.name;
            report.cycleLength = library.Find(StationActorMotion.DefaultWalkId).clip.length;
            clones = new GameObject[2]; animators = new Animator[2]; skins = new SkinnedMeshRenderer[2][];
            for (int mode = 0; mode < 2; mode++)
            {
                clones[mode] = Instantiate(sourceAnimator.gameObject);
                clones[mode].name = mode == 0 ? "GaitProbe legacy" : "GaitProbe short stride";
                clones[mode].transform.SetParent(null, false);
                clones[mode].transform.SetPositionAndRotation(origin, Quaternion.identity);
                clones[mode].transform.localScale = sourceAnimator.transform.lossyScale;
                foreach (var transform in clones[mode].GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 30;
                foreach (var behaviour in clones[mode].GetComponentsInChildren<Behaviour>(true))
                    if (!(behaviour is Animator)) behaviour.enabled = false;
                foreach (var collider in clones[mode].GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var body in clones[mode].GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
                animators[mode] = clones[mode].GetComponent<Animator>();
                animators[mode].enabled = true; animators[mode].runtimeAnimatorController = library.controller;
                animators[mode].applyRootMotion = false; animators[mode].stabilizeFeet = true;
                animators[mode].cullingMode = AnimatorCullingMode.AlwaysAnimate;
                skins[mode] = clones[mode].GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var skin in skins[mode]) { skin.enabled = true; skin.updateWhenOffscreen = true; }
                clones[mode].SetActive(true);
            }
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.layer = 30;
            floor.transform.localScale = new Vector3(20f, .03f, 50f); floor.transform.position = origin + new Vector3(0f, -.025f, 0f);
            floor.GetComponent<Collider>().enabled = false;
            floorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(.31f, .35f, .40f) };
            floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
            for (int i = -4; i <= 16; i++)
            {
                var line = GameObject.CreatePrimitive(PrimitiveType.Cube); line.layer = 30; line.GetComponent<Collider>().enabled = false;
                line.transform.position = origin + new Vector3(0f, -.003f, i); line.transform.localScale = new Vector3(20f, .005f, .015f);
            }
            var light = new GameObject("GaitProbe light").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 2.5f; light.cullingMask = 1 << 30;
            light.transform.rotation = Quaternion.Euler(35f, -45f, 0f);
            previewCamera = new GameObject("GaitProbe camera").AddComponent<Camera>();
            previewCamera.enabled = false; previewCamera.cullingMask = 1 << 30;
            previewCamera.clearFlags = CameraClearFlags.SolidColor; previewCamera.backgroundColor = new Color(.12f, .15f, .20f);
            previewCamera.fieldOfView = 32f; previewCamera.nearClipPlane = .05f; previewCamera.farClipPlane = 30f;
            previewCamera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32); target.Create();
            image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            combined = new Texture2D(Width * 2, Height, TextureFormat.RGB24, false);
            sheet = new Texture2D(Width * 4, Height * 2, TextureFormat.RGB24, false);
        }

        void Update()
        {
            if (!moving || clones == null) return;
            Vector3 position = origin + Vector3.forward * (speed * (Time.time - startedSimulation));
            foreach (var clone in clones) clone.transform.position = position;
        }

        static Pose Observe(Animator animator, AnimationClip walkClip)
        {
            Vector3 Point(HumanBodyBones b) => animator.GetBoneTransform(b).position;
            Vector3 Local(HumanBodyBones b) => Quaternion.Inverse(animator.transform.rotation) * (Point(b) - animator.transform.position);
            var leftUpper = Point(HumanBodyBones.LeftUpperLeg); var rightUpper = Point(HumanBodyBones.RightUpperLeg);
            var leftKnee = Point(HumanBodyBones.LeftLowerLeg); var rightKnee = Point(HumanBodyBones.RightLowerLeg);
            var left = Point(HumanBodyBones.LeftFoot); var right = Point(HumanBodyBones.RightFoot);
            return new Pose {
                root = animator.transform.position, left = Local(HumanBodyBones.LeftFoot), right = Local(HumanBodyBones.RightFoot),
                phase = animator.GetCurrentAnimatorStateInfo(0).normalizedTime,
                stateHash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash,
                strideParameter = animator.GetFloat(StationActorMotion.StrideParameter),
                walkWeight = animator.GetCurrentAnimatorClipInfo(0).Where(c => c.clip == walkClip).Sum(c => c.weight),
                leftKnee = Vector3.Angle(leftUpper - leftKnee, left - leftKnee),
                rightKnee = Vector3.Angle(rightUpper - rightKnee, right - rightKnee),
                leftForward = Local(HumanBodyBones.LeftLowerLeg).z - .5f * (Local(HumanBodyBones.LeftUpperLeg).z + Local(HumanBodyBones.LeftFoot).z),
                rightForward = Local(HumanBodyBones.RightLowerLeg).z - .5f * (Local(HumanBodyBones.RightUpperLeg).z + Local(HumanBodyBones.RightFoot).z),
                footAboveHip = Mathf.Max(left.y, right.y) - Point(HumanBodyBones.Hips).y,
                bones = new[] { Local(HumanBodyBones.Hips), Local(HumanBodyBones.Head), Local(HumanBodyBones.LeftUpperLeg), Local(HumanBodyBones.LeftLowerLeg), Local(HumanBodyBones.LeftFoot), Local(HumanBodyBones.RightUpperLeg), Local(HumanBodyBones.RightLowerLeg), Local(HumanBodyBones.RightFoot) }
            };
        }

        static void Analyze(Measurement result, List<Pose> poses)
        {
            result.frames = poses.Count; result.initialPhase = poses[0].phase; result.finalPhase = poses[poses.Count - 1].phase;
            result.actualCadence = (result.finalPhase - result.initialPhase) * result.cycleSeconds * result.cadence * Rate / (poses.Count - 1);
            result.actualStrideParameter = poses.Average(p => p.strideParameter);
            result.referenceWalkClipWeight = poses.Average(p => p.walkWeight);
            int expectedHash = Animator.StringToHash("Base Layer." + (result.mode == "short_stride" ? StationActorMotion.AdaptiveWalkId : StationActorMotion.DefaultWalkId));
            result.unexpectedStateSamples = poses.Count(p => p.stateHash != expectedHash);
            float minLeft = poses.Min(p => p.left.y), minRight = poses.Min(p => p.right.y);
            result.minimumFootHeight = Mathf.Min(minLeft, minRight);
            result.leftExcursion = poses.Max(p => p.left.z) - poses.Min(p => p.left.z);
            result.rightExcursion = poses.Max(p => p.right.z) - poses.Min(p => p.right.z);
            double slip = 0d;
            for (int i = 0; i < poses.Count; i++)
            {
                var pose = poses[i];
                if (pose.bones.Any(p => !Finite(p.x) || !Finite(p.y) || !Finite(p.z))) result.invalidPoseSamples++;
                result.minKneeAngle = Mathf.Min(result.minKneeAngle, pose.leftKnee, pose.rightKnee);
                result.maxKneeAngle = Mathf.Max(result.maxKneeAngle, pose.leftKnee, pose.rightKnee);
                result.minKneeForward = Mathf.Min(result.minKneeForward, pose.leftForward, pose.rightForward);
                result.maximumFootAboveHip = Mathf.Max(result.maximumFootAboveHip, pose.footAboveHip);
                if (i == 0) continue;
                for (int side = 0; side < 2; side++)
                {
                    Vector3 foot = side == 0 ? pose.left : pose.right;
                    Vector3 previous = side == 0 ? poses[i - 1].left : poses[i - 1].right;
                    Vector3 delta = foot - previous;
                    result.maximumFrameFootDisplacement = Mathf.Max(result.maximumFrameFootDisplacement, delta.magnitude);
                    Vector3 relativeVelocity = delta * Rate;
                    if (foot.y > (side == 0 ? minLeft : minRight) + .025f || relativeVelocity.z >= -.05f * result.speed) continue;
                    Vector3 rootVelocity = (pose.root - poses[i - 1].root) * Rate;
                    Vector3 velocity = relativeVelocity + rootVelocity; velocity.y = 0f;
                    slip += velocity.magnitude; result.maximumSupportSlip = Mathf.Max(result.maximumSupportSlip, velocity.magnitude);
                    result.supportSamples++;
                }
            }
            result.meanSupportSlip = result.supportSamples == 0 ? 0f : (float)(slip / result.supportSamples);
            if (result.finalPhase - result.initialPhase < 1.9f || result.leftExcursion < .01f || result.rightExcursion < .01f ||
                result.supportSamples == 0 || result.invalidPoseSamples > 0 || result.unexpectedStateSamples > 0 ||
                Mathf.Abs(result.actualCadence - result.cadence) > .01f ||
                Mathf.Abs(result.referenceWalkClipWeight - result.stride) > .01f || result.minKneeAngle < 25f || result.maximumFootAboveHip > -.2f)
                throw new InvalidOperationException("Native-frame gait evidence failed: " + result.mode + " at " + result.speed + "m/s");
        }

        void RenderFrame(string folder, int frame)
        {
            var old = RenderTexture.active;
            try
            {
                Vector3 position = clones[0].transform.position;
                previewCamera.transform.position = position + new Vector3(3.2f, 1.1f, 1.7f);
                previewCamera.transform.LookAt(position + Vector3.up * .9f);
                for (int mode = 0; mode < 2; mode++)
                {
                    foreach (var skin in skins[1 - mode]) skin.enabled = false;
                    foreach (var skin in skins[mode]) skin.enabled = true;
                    RenderPipeline.SubmitRenderRequest(previewCamera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                    RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); image.Apply();
                    var pixels = image.GetPixels(); combined.SetPixels(mode * Width, 0, Width, Height, pixels);
                    if (frame == 15 || frame == 45 || frame == 75 || frame == 105)
                        sheet.SetPixels(((frame - 15) / 30) * Width, (1 - mode) * Height, Width, Height, pixels);
                }
                combined.Apply(); File.WriteAllBytes(Path.Combine(folder, "frame-" + frame.ToString("D3") + ".png"), combined.EncodeToPNG());
                foreach (var skin in skins[0]) skin.enabled = true;
                foreach (var skin in skins[1]) skin.enabled = true;
            }
            finally { RenderTexture.active = old; }
        }

        void Finish(Exception error)
        {
            moving = false;
            report.success = error == null; report.error = error?.ToString(); report.finishedUtc = DateTime.UtcNow.ToString("O");
            Save();
            Debug.Log("[StationSlowGaitProbe] " + (report.success ? "PASS" : report.error));
            Application.Quit(report.success ? 0 : 2);
        }
        void Save() => File.WriteAllText(Path.Combine(directory, "native-gait-report.json"), JsonUtility.ToJson(report, true));
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
