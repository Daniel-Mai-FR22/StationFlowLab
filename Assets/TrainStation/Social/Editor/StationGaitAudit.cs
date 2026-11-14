#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Measures the saved Humanoid gait without changing source clips, controllers, prefabs or open scenes.</summary>
    public static class StationGaitAudit
    {
        const string DirectoryPath = "Reports/TrainStationSocial/GaitAudit";
        const string PrefabPath = "Assets/TrainStation/Social/Prefabs/Station_Passenger.prefab";
        public static void RestoreSceneAfterAudit()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||AnimationMode.InAnimationMode())throw new InvalidOperationException("Stop previews before restoring audit state.");
            var scene=SceneManager.GetActiveScene();
            if(scene.path!=StationSocialAuthoring.ScenePath)throw new InvalidOperationException("Only restore the station scene used by the gait audit.");
            if(!scene.isDirty)return;
            // Preserve the current editor contents before reopening the saved station. A failed early
            // preview audit touched the dirty flag while creating temporary rendering objects.
            string backup="BuildRequests/Backups/TrainStation.audit-editor-state."+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".unity";
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            if(!EditorSceneManager.SaveScene(scene,backup,true))throw new IOException("Could not preserve the current editor state before audit recovery.");
            EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath,OpenSceneMode.Single);
        }
        [Serializable] sealed class Sample
        {
            public float phase, time, minimumSkinY, approximateLeftSoleY, approximateRightSoleY;
            public Vector3 modelPosition, hips, leftUpperLeg, leftKnee, leftFoot, leftToes, rightUpperLeg, rightKnee, rightFoot, rightToes;
            public float leftKneeAngle, rightKneeAngle, leftLegExtension, rightLegExtension;
        }
        [Serializable] sealed class ClipSettings
        {
            public bool loopTime, loopBlend, loopBlendPositionY, loopBlendPositionXZ, keepOriginalPositionY, keepOriginalPositionXZ, heightFromFeet;
            public float startTime, stopTime, level;
        }
        [Serializable] sealed class ClipResult
        {
            public string label, path, avatar, contactSheet, captureError;
            public bool footIK;
            public float duration, humanScale, minimumSkinY, maximumSkinY, minimumLeftFootY, maximumLeftFootY, minimumRightFootY, maximumRightFootY;
            public float minimumSupportSoleY, maximumSupportSoleY, hipVerticalRange, hipLateralRange, hipForwardRange;
            public float maximumLeftExtension, maximumRightExtension, minimumLeftKneeAngle, minimumRightKneeAngle;
            public float loopHipPositionGap, loopLeftFootGap, loopRightFootGap, loopPoseBoneRotationGap;
            public float normalizedRootTravel, sourceStrideMetres, sourceReferenceSpeed;
            public Vector3 originalBodyRootStart, originalBodyRootEnd;
            public int crossedAnkleSamples;
            public ClipSettings settings;
            public List<Sample> samples = new List<Sample>();
        }
        [Serializable] sealed class Audit
        {
            public string measuredUtc, unityVersion, prefab = PrefabPath;
            public string method = "Sixteen uniform phases of a full walk cycle plus a seam pair, sampled with Animator.Play + Update(0), root motion disabled, on a preview instance of the actual passenger prefab. Independent transient controllers compare state Foot IK off/on. Original source FBX is sampled on the same target Avatar. No source assets are modified.";
            public string soleMeasurement = "Skin minimum is measured from CPU-baked world vertices. Per-foot sole estimates use vertices within 0.20 m in XZ of the ankle and below ankle+0.04 m; these estimates are not ground-contact detection or a physics claim.";
            public bool success, savedSceneUntouched;
            public int originalSceneObjects, finalSceneObjects;
            public List<string> availableLocomotion = new List<string>();
            public List<ClipResult> clips = new List<ClipResult>();
            public List<string> errors = new List<string>();
        }
        sealed class SkinProxy
        {
            public SkinnedMeshRenderer skin;
            public Mesh mesh;
        }

        [MenuItem("Efrei Research Lab/Train Station Social/Measure gait poses and foot IK")]
        public static void Measure()
        {
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Measure gait poses in Edit mode outside animation preview.");
            var report = new Audit { measuredUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion };
            var scene = SceneManager.GetActiveScene(); bool wasDirty = scene.isDirty;
            report.originalSceneObjects = Count(scene);
            Scene scratch = default;
            try
            {
                scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(scratch);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (!prefab) throw new InvalidOperationException("Saved passenger prefab is missing.");
                var motion = prefab.GetComponent<StationActorMotion>();
                var library = motion && motion.animationPlayer ? motion.animationPlayer.Library : null;
                var walk = library ? library.Find(motion.walkId) : null;
                if (walk == null || !walk.clip) throw new InvalidOperationException("Saved passenger walking motion is missing.");
                foreach (var item in library.motions.Where(m => m.category == HumanMotionCategory.Locomotion))
                    report.availableLocomotion.Add(item.id + " | " + item.displayName + " | " + item.sourceAssetPath);
                var source = AssetDatabase.LoadAllAssetsAtPath(walk.sourceAssetPath).OfType<AnimationClip>().FirstOrDefault(c => c.humanMotion && !c.name.StartsWith("__preview__", StringComparison.Ordinal));
                Directory.CreateDirectory(DirectoryPath);
                report.clips.Add(MeasureClip(prefab, walk.clip, false, "saved_walk_ik_off", true));
                report.clips.Add(MeasureClip(prefab, walk.clip, true, "saved_walk_ik_on", true));
                if (source) report.clips.Add(MeasureClip(prefab, source, false, "source_fbx_ik_off", false));
                var mixamo = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>("Assets/HumanAnimation/Resources/HumanAnimation/Mixamo/MixamoLibrary.asset");
                var candidate = mixamo ? mixamo.Find("mixamo_male_standard_walking_ef7424170f") : null;
                if (candidate != null && candidate.clip)
                {
                    report.availableLocomotion.Add(candidate.id + " | " + candidate.displayName + " | " + candidate.sourceAssetPath);
                    report.clips.Add(MeasureClip(prefab, candidate.clip, false, "standard_walk_ik_off", true));
                    report.clips.Add(MeasureClip(prefab, candidate.clip, true, "standard_walk_ik_on", true));
                }
                report.success = true;
            }
            catch (Exception error) { report.errors.Add(error.ToString()); throw; }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.SetActiveScene(scene);
                if (scratch.IsValid()) EditorSceneManager.CloseScene(scratch, true);
                report.finalSceneObjects = Count(scene);
                report.savedSceneUntouched = scene.IsValid() && scene.isDirty == wasDirty && report.finalSceneObjects == report.originalSceneObjects;
                if (!report.savedSceneUntouched) { report.success = false; report.errors.Add("Audit changed the saved scene dirty state or left objects behind."); }
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(DirectoryPath + "/gait-pose-audit.json", JsonUtility.ToJson(report, true));
            }
        }

        static ClipResult MeasureClip(GameObject prefab, AnimationClip clip, bool footIK, string label, bool capture)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var result = new ClipResult
            {
                label = label, path = AssetDatabase.GetAssetPath(clip), footIK = footIK, duration = clip.length,
                settings = new ClipSettings { loopTime = settings.loopTime, loopBlend = settings.loopBlend,
                    loopBlendPositionY = settings.loopBlendPositionY, loopBlendPositionXZ = settings.loopBlendPositionXZ,
                    keepOriginalPositionY = settings.keepOriginalPositionY, keepOriginalPositionXZ = settings.keepOriginalPositionXZ,
                    heightFromFeet = settings.heightFromFeet,
                    startTime = settings.startTime, stopTime = settings.stopTime, level = settings.level }
            };
            var preview = new PreviewRenderUtility();
            GameObject actor = null, stage = null;
            AnimatorController controller = null;
            AnimatorStateMachine machine = null;
            AnimatorState state = null;
            Material floorMaterial = null;
            var proxies = new List<SkinProxy>();
            Texture2D sheet = null;
            try
            {
                actor = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview.camera.gameObject.scene);
                actor.name = "Gait audit preview — " + label; actor.hideFlags = HideFlags.HideAndDontSave;
                actor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (var behaviour in actor.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                foreach (var agent in actor.GetComponentsInChildren<NavMeshAgent>(true)) agent.enabled = false;
                foreach (var renderer in actor.GetComponentsInChildren<MeshRenderer>(true)) renderer.enabled = false;
                preview.AddSingleGO(actor);
                var animator = actor.GetComponentInChildren<Animator>(true);
                if (!animator || !animator.avatar || !animator.avatar.isHuman || !animator.avatar.isValid) throw new InvalidOperationException("Actual passenger has no valid own Humanoid Avatar.");
                animator.enabled = true; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                result.avatar = AssetDatabase.GetAssetPath(animator.avatar); result.humanScale = animator.humanScale;
                result.originalBodyRootStart = RootAt(clip,0f); result.originalBodyRootEnd = RootAt(clip,clip.length);
                var rootAdvance = result.originalBodyRootEnd - result.originalBodyRootStart; rootAdvance.y = 0f;
                result.normalizedRootTravel = rootAdvance.magnitude; result.sourceStrideMetres = result.normalizedRootTravel * result.humanScale;
                result.sourceReferenceSpeed = result.sourceStrideMetres / Mathf.Max(.001f,clip.length);
                controller = new AnimatorController { name = "Gait diagnostic controller", hideFlags = HideFlags.HideAndDontSave };
                machine = new AnimatorStateMachine { name = "Base Layer", hideFlags = HideFlags.HideAndDontSave };
                state = machine.AddState("Walk"); state.hideFlags = HideFlags.HideAndDontSave;
                state.motion = clip; state.iKOnFeet = footIK; state.writeDefaultValues = true; machine.defaultState = state;
                controller.layers = new[] { new AnimatorControllerLayer { name = "Base Layer", defaultWeight = 1f, stateMachine = machine, iKPass = true } };
                animator.runtimeAnimatorController = controller; animator.speed = 1f; animator.Rebind(); animator.Update(0f);
                int hash = Animator.StringToHash("Base Layer.Walk");
                foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.sharedMesh))
                {
                    var mesh = new Mesh { name = "Gait audit CPU skin", hideFlags = HideFlags.HideAndDontSave };
                    var copy = new GameObject("Gait audit CPU skin", typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = HideFlags.HideAndDontSave };
                    copy.transform.SetParent(skin.transform, false); copy.GetComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = copy.GetComponent<MeshRenderer>(); renderer.sharedMaterials = skin.sharedMaterials;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; renderer.receiveShadows = true;
                    skin.enabled = false; proxies.Add(new SkinProxy { skin = skin, mesh = mesh });
                }
                ConfigurePreview(preview);
                if (capture)
                {
                    stage = GameObject.CreatePrimitive(PrimitiveType.Cube); stage.name = "Gait audit ground reference";
                    stage.hideFlags = HideFlags.HideAndDontSave; stage.transform.SetPositionAndRotation(new Vector3(0,-.017f,0), Quaternion.identity); stage.transform.localScale = new Vector3(2.5f,.025f,2.5f);
                    UnityEngine.Object.DestroyImmediate(stage.GetComponent<Collider>());
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    floorMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = new Color(.42f,.46f,.49f) };
                    stage.GetComponent<MeshRenderer>().sharedMaterial = floorMaterial; preview.AddSingleGO(stage);
                    sheet = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
                }
                for (int i = 0; i < 16; i++)
                {
                    float phase = i / 16f;
                    SamplePose(animator, hash, phase); result.samples.Add(CaptureSample(actor, animator, proxies, phase, clip.length));
                    if (capture && i % 2 == 0)
                    {
                        try { CaptureTile(preview, sheet, i / 2, phase); }
                        catch (Exception error) { result.captureError = error.ToString(); capture = false; }
                    }
                }
                SamplePose(animator, hash, 0f);
                var seamStart = CaptureSample(actor, animator, proxies, 0f, clip.length);
                var startRotations = BoneRotations(animator);
                SamplePose(animator, hash, .9999f);
                var seamEnd = CaptureSample(actor, animator, proxies, .9999f, clip.length);
                var endRotations = BoneRotations(animator);
                result.loopHipPositionGap = Vector3.Distance(seamStart.hips,seamEnd.hips);
                result.loopLeftFootGap = Vector3.Distance(seamStart.leftFoot,seamEnd.leftFoot);
                result.loopRightFootGap = Vector3.Distance(seamStart.rightFoot,seamEnd.rightFoot);
                result.loopPoseBoneRotationGap = startRotations.Zip(endRotations,Quaternion.Angle).Max();
                Summarize(result);
                if (capture && sheet)
                {
                    sheet.Apply(); result.contactSheet = DirectoryPath + "/" + label + "-profile-8-poses.png";
                    File.WriteAllBytes(result.contactSheet, sheet.EncodeToPNG());
                }
            }
            finally
            {
                preview.Cleanup();
                if (actor) UnityEngine.Object.DestroyImmediate(actor);
                if (stage) UnityEngine.Object.DestroyImmediate(stage);
                foreach (var proxy in proxies) if (proxy.mesh) UnityEngine.Object.DestroyImmediate(proxy.mesh);
                if (controller) UnityEngine.Object.DestroyImmediate(controller);
                if (machine) UnityEngine.Object.DestroyImmediate(machine);
                if (state) UnityEngine.Object.DestroyImmediate(state);
                if (floorMaterial) UnityEngine.Object.DestroyImmediate(floorMaterial);
                if (sheet) UnityEngine.Object.DestroyImmediate(sheet);
            }
            return result;
        }

        static void SamplePose(Animator animator, int hash, float phase)
        {
            animator.Play(hash, 0, phase); animator.Update(0f);
            if (!animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Walk")) throw new InvalidOperationException("Diagnostic Animator did not enter its walk state.");
        }

        static Sample CaptureSample(GameObject actor, Animator animator, List<SkinProxy> skins, float phase, float duration)
        {
            Vector3 Point(HumanBodyBones bone)
            {
                var transform = animator.GetBoneTransform(bone);
                if (!transform) throw new InvalidOperationException("Missing diagnostic Humanoid bone: " + bone);
                return actor.transform.InverseTransformPoint(transform.position);
            }
            var sample = new Sample { phase = phase, time = phase * duration, modelPosition = actor.transform.InverseTransformPoint(animator.transform.position),
                hips = Point(HumanBodyBones.Hips), leftUpperLeg = Point(HumanBodyBones.LeftUpperLeg), leftKnee = Point(HumanBodyBones.LeftLowerLeg),
                leftFoot = Point(HumanBodyBones.LeftFoot), leftToes = Point(HumanBodyBones.LeftToes), rightUpperLeg = Point(HumanBodyBones.RightUpperLeg),
                rightKnee = Point(HumanBodyBones.RightLowerLeg), rightFoot = Point(HumanBodyBones.RightFoot), rightToes = Point(HumanBodyBones.RightToes),
                minimumSkinY = float.PositiveInfinity, approximateLeftSoleY = float.PositiveInfinity, approximateRightSoleY = float.PositiveInfinity };
            sample.leftKneeAngle = Vector3.Angle(sample.leftUpperLeg - sample.leftKnee, sample.leftFoot - sample.leftKnee);
            sample.rightKneeAngle = Vector3.Angle(sample.rightUpperLeg - sample.rightKnee, sample.rightFoot - sample.rightKnee);
            sample.leftLegExtension = Extension(sample.leftUpperLeg, sample.leftKnee, sample.leftFoot);
            sample.rightLegExtension = Extension(sample.rightUpperLeg, sample.rightKnee, sample.rightFoot);
            foreach (var skin in skins)
            {
                skin.skin.BakeMesh(skin.mesh, false); skin.mesh.RecalculateBounds();
                foreach (var vertex in skin.mesh.vertices)
                {
                    var point = actor.transform.InverseTransformPoint(skin.skin.transform.TransformPoint(vertex));
                    sample.minimumSkinY = Mathf.Min(sample.minimumSkinY, point.y);
                    if (NearFoot(point, sample.leftFoot)) sample.approximateLeftSoleY = Mathf.Min(sample.approximateLeftSoleY, point.y);
                    if (NearFoot(point, sample.rightFoot)) sample.approximateRightSoleY = Mathf.Min(sample.approximateRightSoleY, point.y);
                }
            }
            if (float.IsInfinity(sample.approximateLeftSoleY)) sample.approximateLeftSoleY = sample.leftFoot.y;
            if (float.IsInfinity(sample.approximateRightSoleY)) sample.approximateRightSoleY = sample.rightFoot.y;
            return sample;
        }

        static float Extension(Vector3 upper, Vector3 knee, Vector3 foot) => Vector3.Distance(upper, foot) / Mathf.Max(.001f, Vector3.Distance(upper,knee) + Vector3.Distance(knee,foot));
        static Vector3 RootAt(AnimationClip clip,float time)
        {
            float Axis(string property)
            {
                var curve = AnimationUtility.GetEditorCurve(clip,EditorCurveBinding.FloatCurve("",typeof(Animator),property));
                return curve != null ? curve.Evaluate(time) : 0f;
            }
            return new Vector3(Axis("RootT.x"),Axis("RootT.y"),Axis("RootT.z"));
        }
        static bool NearFoot(Vector3 vertex, Vector3 foot) => vertex.y <= foot.y + .04f && new Vector2(vertex.x-foot.x,vertex.z-foot.z).sqrMagnitude < .2f*.2f;
        static Quaternion[] BoneRotations(Animator animator) => new[] { HumanBodyBones.Hips,HumanBodyBones.LeftUpperLeg,HumanBodyBones.LeftLowerLeg,HumanBodyBones.LeftFoot,HumanBodyBones.RightUpperLeg,HumanBodyBones.RightLowerLeg,HumanBodyBones.RightFoot }
            .Select(b => animator.GetBoneTransform(b).rotation).ToArray();
        static void Summarize(ClipResult result)
        {
            var all = result.samples;
            result.minimumSkinY = all.Min(s=>s.minimumSkinY); result.maximumSkinY = all.Max(s=>s.minimumSkinY);
            result.minimumLeftFootY = all.Min(s=>s.leftFoot.y); result.maximumLeftFootY = all.Max(s=>s.leftFoot.y);
            result.minimumRightFootY = all.Min(s=>s.rightFoot.y); result.maximumRightFootY = all.Max(s=>s.rightFoot.y);
            result.minimumSupportSoleY = all.Min(s=>Mathf.Min(s.approximateLeftSoleY,s.approximateRightSoleY)); result.maximumSupportSoleY = all.Max(s=>Mathf.Min(s.approximateLeftSoleY,s.approximateRightSoleY));
            result.hipVerticalRange = all.Max(s=>s.hips.y)-all.Min(s=>s.hips.y); result.hipLateralRange = all.Max(s=>s.hips.x)-all.Min(s=>s.hips.x); result.hipForwardRange = all.Max(s=>s.hips.z)-all.Min(s=>s.hips.z);
            result.maximumLeftExtension = all.Max(s=>s.leftLegExtension); result.maximumRightExtension = all.Max(s=>s.rightLegExtension);
            result.minimumLeftKneeAngle = all.Min(s=>s.leftKneeAngle); result.minimumRightKneeAngle = all.Min(s=>s.rightKneeAngle);
            result.crossedAnkleSamples = all.Count(s=>s.leftFoot.x>s.rightFoot.x);
        }

        static void ConfigurePreview(PreviewRenderUtility preview)
        {
            preview.camera.orthographic = true; preview.camera.orthographicSize = 1.18f;
            preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 20f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor; preview.camera.backgroundColor = new Color(.88f,.91f,.94f);
            preview.camera.transform.position = new Vector3(4f, 1.0f, -.15f); preview.camera.transform.LookAt(new Vector3(0, 1.0f, 0));
            preview.lights[0].intensity = 1.4f; preview.lights[0].transform.rotation = Quaternion.Euler(35,25,0);
            preview.lights[1].intensity = .65f; preview.lights[1].transform.rotation = Quaternion.Euler(320,180,0);
            preview.ambientColor = new Color(.55f,.55f,.55f);
        }

        static void CaptureTile(PreviewRenderUtility preview, Texture2D sheet, int index, float phase)
        {
            preview.BeginStaticPreview(new Rect(0,0,400,460)); preview.Render(true);
            var frame = preview.EndStaticPreview();
            try
            {
                int x = index % 4 * 400, y = (1-index/4)*500;
                sheet.SetPixels(x,y+40,400,460,frame.GetPixels());
                var background = Enumerable.Repeat(new Color(.1f,.14f,.18f),400*40).ToArray(); sheet.SetPixels(x,y,400,40,background);
                DrawDigits(sheet, x+160,y+10,Mathf.RoundToInt(phase*100f).ToString("00")+"%",4,Color.white);
            }
            finally { UnityEngine.Object.DestroyImmediate(frame); }
        }
        static void DrawDigits(Texture2D texture,int x,int y,string text,int size,Color color)
        {
            string[] digits={"111101101101111","010110010010111","111001111100111","111001111001111","101101111001001","111100111001111","111100111101111","111001001001001","111101111101111","111101111001111"};
            foreach(char letter in text)
            {
                string glyph=letter=='%'?"101001010100101":digits[letter-'0'];
                for(int row=0;row<5;row++)for(int column=0;column<3;column++)if(glyph[row*3+column]=='1')
                    for(int dx=0;dx<size;dx++)for(int dy=0;dy<size;dy++)texture.SetPixel(x+column*size+dx,y+(4-row)*size+dy,color);
                x+=4*size;
            }
        }
        static int Count(Scene scene) => scene.IsValid() && scene.isLoaded ? scene.GetRootGameObjects().Sum(g=>g.GetComponentsInChildren<Transform>(true).Length) : 0;
    }
}
#endif
