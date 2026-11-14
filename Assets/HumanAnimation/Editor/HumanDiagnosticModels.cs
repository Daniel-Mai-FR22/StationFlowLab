#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StationFlow.HumanAnimation.Editor
{
    /// <summary>
    /// Creates original, deliberately simple diagnostic mannequins. These are not Mixamo assets.
    /// Geometry, skin weights, two independent Avatars and prefabs are saved as editable assets.
    /// Existing assets are preserved so later hand edits are never replaced by running the tool.
    /// </summary>
    public static class HumanDiagnosticModels
    {
        public const string StandardPrefabPath = "Assets/HumanAnimation/Prefabs/Diagnostics/Diagnostic_Standard.prefab";
        public const string TallPrefabPath = "Assets/HumanAnimation/Prefabs/Diagnostics/Diagnostic_Tall.prefab";
        public const string StandardAvatarPath = "Assets/HumanAnimation/Avatars/Diagnostic_Standard_Avatar.asset";
        public const string TallAvatarPath = "Assets/HumanAnimation/Avatars/Diagnostic_Tall_Avatar.asset";
        const string MeshFolder = "Assets/HumanAnimation/Meshes/Diagnostics";
        const string MaterialFolder = "Assets/HumanAnimation/Materials/Diagnostics";

        [MenuItem("Efrei Research Lab/Human Animation/Create diagnostic model prefabs")]
        public static void CreateFromMenu()
        {
            EnsurePrefabs();
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(StandardPrefabPath));
        }

        public static GameObject[] EnsurePrefabs()
        {
            EnsureFolder(Path.GetDirectoryName(StandardPrefabPath));
            EnsureFolder(Path.GetDirectoryName(StandardAvatarPath));
            EnsureFolder(MeshFolder);
            EnsureFolder(MaterialFolder);
            var models = new[]
            {
                EnsureModel("Standard", StandardPrefabPath, StandardAvatarPath, new Proportions(1f, 1f, 1f, 1f), new Color(.035f, .43f, .48f)),
                EnsureModel("Tall", TallPrefabPath, TallAvatarPath, new Proportions(1.14f, .84f, 1.16f, 1.08f), new Color(.82f, .39f, .08f))
            };
            AssetDatabase.SaveAssets();
            return models;
        }

        /// <summary>Targeted calibration repair for the original diagnostic rigs, preserving asset GUIDs.</summary>
        [MenuItem("Efrei Research Lab/Human Animation/Repair diagnostic anatomical rig calibration")]
        public static void RepairDiagnosticRigs()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Stop playback and animation preview before repairing saved diagnostic rigs.");
            var report = new CalibrationReport { before = CaptureRigCalibration() };
            EnsureModel("Standard", StandardPrefabPath, StandardAvatarPath, new Proportions(1f, 1f, 1f, 1f), new Color(.035f, .43f, .48f), true);
            EnsureModel("Tall", TallPrefabPath, TallAvatarPath, new Proportions(1.14f, .84f, 1.16f, 1.08f), new Color(.82f, .39f, .08f), true);
            AssetDatabase.SaveAssets();
            report.after = CaptureRigCalibration();
            report.success = report.after.Count >= 2 && report.after.TrueForAll(r => r.anatomicalSidesCorrect);
            Directory.CreateDirectory("Reports/HumanAnimation");
            File.WriteAllText("Reports/HumanAnimation/rig-calibration.json", JsonUtility.ToJson(report, true));
            if (!report.success) throw new InvalidOperationException("Rig anatomical calibration failed; see Reports/HumanAnimation/rig-calibration.json.");
            Debug.Log("Diagnostic rigs calibrated: +Z facing, anatomical Left on -X, independently rebuilt Humanoid Avatars and mesh bind poses; existing GUIDs preserved.");
        }

        [Serializable] sealed class BoneCalibration { public string bone; public Vector3 rootPosition; public Quaternion rootRotation; }
        [Serializable] sealed class RigCalibration { public string model, avatar; public bool anatomicalSidesCorrect; public List<BoneCalibration> bones = new List<BoneCalibration>(); }
        [Serializable] sealed class CalibrationReport { public bool success; public List<RigCalibration> before, after; }
        static List<RigCalibration> CaptureRigCalibration()
        {
            var report = new List<RigCalibration>();
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                foreach (string path in new[] { "Assets/HumanAnimation/Source/Mixamo/Models/XBot.fbx", StandardPrefabPath, TallPrefabPath })
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path); if (prefab == null) continue;
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview);
                    try
                    {
                        var animator = instance.GetComponentInChildren<Animator>();
                        if (animator == null || animator.avatar == null) continue;
                        var record = new RigCalibration { model = path, avatar = AssetDatabase.GetAssetPath(animator.avatar) };
                        foreach (HumanBodyBones id in new[] { HumanBodyBones.Hips, HumanBodyBones.Chest, HumanBodyBones.Head,
                            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot })
                        {
                            Transform bone = animator.GetBoneTransform(id); if (bone == null) continue;
                            record.bones.Add(new BoneCalibration { bone = id.ToString(), rootPosition = animator.transform.InverseTransformPoint(bone.position), rootRotation = Quaternion.Inverse(animator.transform.rotation) * bone.rotation });
                        }
                        record.anatomicalSidesCorrect = animator.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).position).x < 0
                            && animator.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position).x > 0;
                        report.Add(record);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(instance); }
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
            return report;
        }

        readonly struct Proportions
        {
            public readonly float Height, Width, ArmLength, FootLength;
            public Proportions(float height, float width, float armLength, float footLength)
            { Height = height; Width = width; ArmLength = armLength; FootLength = footLength; }
        }

        static GameObject EnsureModel(string variant, string prefabPath, string avatarPath, Proportions p, Color shirt, bool repair = false)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null && !repair) return existing;
            if (existing == null && File.Exists(prefabPath)) throw new InvalidOperationException("Existing diagnostic prefab could not be loaded; preserving it: " + prefabPath);

            var root = new GameObject("Diagnostic_" + variant + " — original humanoid mannequin");
            try
            {
                var rig = new Rig(root.transform);
                rig.Add("Armature", null, Vector3.zero);
                rig.Add("Hips", "Armature", new Vector3(0, .95f * p.Height, 0), HumanBodyBones.Hips);
                rig.Add("Spine", "Hips", new Vector3(0, 1.10f * p.Height, 0), HumanBodyBones.Spine);
                rig.Add("Chest", "Spine", new Vector3(0, 1.29f * p.Height, 0), HumanBodyBones.Chest);
                rig.Add("UpperChest", "Chest", new Vector3(0, 1.40f * p.Height, 0), HumanBodyBones.UpperChest);
                rig.Add("Neck", "UpperChest", new Vector3(0, 1.46f * p.Height, 0), HumanBodyBones.Neck);
                rig.Add("Head", "Neck", new Vector3(0, 1.56f * p.Height, 0), HumanBodyBones.Head);
                AddSide(rig, p, true);
                AddSide(rig, p, false);
                if (rig["LeftUpperArm"].position.x >= 0 || rig["RightUpperArm"].position.x <= 0)
                    throw new InvalidOperationException("Humanoid anatomical sides must agree with the model's +Z facing direction.");

                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
                if (avatar == null || repair)
                {
                    if (avatar == null && File.Exists(avatarPath)) throw new InvalidOperationException("Existing Avatar could not be loaded: " + avatarPath);
                    var calibrated = AvatarBuilder.BuildHumanAvatar(root, rig.Description());
                    calibrated.name = "Diagnostic_" + variant + "_Avatar";
                    if (!calibrated.isValid || !calibrated.isHuman)
                    {
                        UnityEngine.Object.DestroyImmediate(calibrated);
                        throw new InvalidOperationException("Unity rejected the independent Humanoid Avatar for " + variant);
                    }
                    if (avatar != null)
                    {
                        EditorUtility.CopySerialized(calibrated, avatar);
                        EditorUtility.SetDirty(avatar);
                        UnityEngine.Object.DestroyImmediate(calibrated);
                    }
                    else { avatar = calibrated; AssetDatabase.CreateAsset(avatar, avatarPath); }
                }
                if (!avatar.isValid || !avatar.isHuman) throw new InvalidOperationException("Saved diagnostic Avatar is not a valid Humanoid: " + avatarPath);

                var animator = root.AddComponent<Animator>();
                animator.avatar = avatar;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                string meshPath = MeshFolder + "/Diagnostic_" + variant + "_SkinnedMesh.asset";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (mesh == null || repair)
                {
                    if (mesh == null && File.Exists(meshPath)) throw new InvalidOperationException("Existing skinned mesh could not be loaded: " + meshPath);
                    var calibratedMesh = BuildMesh(rig, p, variant);
                    if (mesh != null)
                    {
                        // CopySerialized does not reliably replace a Mesh's native vertex buffers.
                        // Update through Mesh APIs so bind poses and actual skin vertices remain paired.
                        mesh.Clear();
                        mesh.indexFormat = calibratedMesh.indexFormat;
                        mesh.vertices = calibratedMesh.vertices;
                        mesh.normals = calibratedMesh.normals;
                        mesh.tangents = calibratedMesh.tangents;
                        mesh.uv = calibratedMesh.uv;
                        mesh.boneWeights = calibratedMesh.boneWeights;
                        mesh.bindposes = calibratedMesh.bindposes;
                        mesh.subMeshCount = calibratedMesh.subMeshCount;
                        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) mesh.SetTriangles(calibratedMesh.GetTriangles(submesh), submesh);
                        mesh.RecalculateBounds();
                        AssertMeshMatches(calibratedMesh, mesh);
                        EditorUtility.SetDirty(mesh);
                        UnityEngine.Object.DestroyImmediate(calibratedMesh);
                    }
                    else { mesh = calibratedMesh; AssetDatabase.CreateAsset(mesh, meshPath); }
                }
                ValidateRestSkin(mesh, rig);

                var visual = new GameObject("Body — saved skinned mesh");
                visual.transform.SetParent(root.transform, false);
                var renderer = visual.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                renderer.bones = rig.Bones.ToArray();
                renderer.rootBone = rig["Hips"];
                renderer.sharedMaterials = new[]
                {
                    EnsureMaterial("Shirt_" + variant, shirt),
                    EnsureMaterial("Trousers", new Color(.075f, .105f, .17f)),
                    EnsureMaterial("MannequinSurface", new Color(.80f, .72f, .60f)),
                    EnsureMaterial("SolesAndEyes", new Color(.027f, .036f, .05f))
                };
                // Bounds cover limbs during retargeting; no collider, navigation or behaviour assumptions.
                renderer.localBounds = new Bounds(new Vector3(0, p.Height, 0), new Vector3(3.4f, 3.5f * p.Height, 3.4f));
                renderer.updateWhenOffscreen = true;
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null) throw new InvalidOperationException("Could not save diagnostic prefab: " + prefabPath);
                return saved;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void AssertMeshMatches(Mesh expected, Mesh written)
        {
            Vector3[] a = expected.vertices, b = written.vertices;
            Matrix4x4[] bindA = expected.bindposes, bindB = written.bindposes;
            BoneWeight[] weightsA = expected.boneWeights, weightsB = written.boneWeights;
            if (a.Length != b.Length || bindA.Length != bindB.Length || weightsA.Length != weightsB.Length)
                throw new InvalidOperationException("Mesh repair changed array lengths unexpectedly.");
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i] || weightsA[i].boneIndex0 != weightsB[i].boneIndex0 || weightsA[i].weight0 != weightsB[i].weight0)
                    throw new InvalidOperationException("Written mesh vertex/weight differs from regenerated data at " + i);
            for (int i = 0; i < bindA.Length; i++)
                for (int component = 0; component < 16; component++)
                    if (bindA[i][component] != bindB[i][component])
                        throw new InvalidOperationException("Written mesh bind pose differs from regenerated data at " + i);
        }
        static void ValidateRestSkin(Mesh mesh, Rig rig)
        {
            Vector3[] positions = mesh.vertices;
            BoneWeight[] weights = mesh.boneWeights;
            var sums = new Vector3[rig.Bones.Count]; var counts = new int[rig.Bones.Count];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 vertex = positions[i];
                if (float.IsNaN(vertex.x) || float.IsNaN(vertex.y) || float.IsNaN(vertex.z)
                    || float.IsInfinity(vertex.x) || float.IsInfinity(vertex.y) || float.IsInfinity(vertex.z))
                    throw new InvalidOperationException("Saved diagnostic skin contains a non-finite vertex.");
                int bone = weights[i].boneIndex0;
                sums[bone] += vertex; counts[bone]++;
            }
            Matrix4x4[] bindposes = mesh.bindposes;
            for (int i = 0; i < rig.Bones.Count; i++)
            {
                Matrix4x4 rest = rig.Bones[i].localToWorldMatrix * bindposes[i];
                for (int c = 0; c < 16; c++)
                    if (Mathf.Abs(rest[c] - Matrix4x4.identity[c]) > .0001f)
                        throw new InvalidOperationException("Skin bind pose disagrees with rest bone " + rig.Bones[i].name);
                if (counts[i] == 0) continue;
                float averageX = sums[i].x / counts[i];
                if ((rig.Bones[i].name.StartsWith("Left", StringComparison.Ordinal) && averageX >= 0)
                    || (rig.Bones[i].name.StartsWith("Right", StringComparison.Ordinal) && averageX <= 0))
                    throw new InvalidOperationException("Weighted skin geometry is on the wrong anatomical side for " + rig.Bones[i].name);
            }
        }

        static void AddSide(Rig rig, Proportions p, bool left)
        {
            string side = left ? "Left" : "Right";
            // With +Z facing and +Y up, anatomical left is -X. Reversing this mirrors
            // Humanoid calibration and turns an arm-lowering motion into an arm raise.
            float sign = left ? -1f : 1f;
            HumanBodyBones Human(string name) { return (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + name); }
            Vector3 Point(float x, float y, float z = 0) { return new Vector3(sign * x, y, z); }
            float shoulderX = .16f * p.Width, upperArmX = .24f * p.Width;
            float elbowX = upperArmX + .28f * p.ArmLength, wristX = elbowX + .24f * p.ArmLength;
            float armY = 1.40f * p.Height, hipX = .095f * p.Width;
            rig.Add(side + "Shoulder", "UpperChest", Point(shoulderX, armY), Human("Shoulder"));
            rig.Add(side + "UpperArm", side + "Shoulder", Point(upperArmX, armY), Human("UpperArm"));
            rig.Add(side + "LowerArm", side + "UpperArm", Point(elbowX, armY), Human("LowerArm"));
            rig.Add(side + "Hand", side + "LowerArm", Point(wristX, armY), Human("Hand"));
            rig.Add(side + "UpperLeg", "Hips", Point(hipX, .91f * p.Height), Human("UpperLeg"));
            // Knees point imperceptibly forward, giving the solver an unambiguous bend direction.
            rig.Add(side + "LowerLeg", side + "UpperLeg", Point(hipX, .50f * p.Height, .018f), Human("LowerLeg"));
            rig.Add(side + "Foot", side + "LowerLeg", Point(hipX, .085f, .035f), Human("Foot"));
            rig.Add(side + "Toes", side + "Foot", Point(hipX, .055f, .20f * p.FootLength), Human("Toes"));
        }

        static Mesh BuildMesh(Rig rig, Proportions p, string variant)
        {
            var b = new SkinMesh(rig);
            float h = p.Height, w = p.Width;
            b.Ellipsoid("Hips", new Vector3(0, .955f * h, 0), new Vector3(.175f * w, .15f * h, .10f), 1);
            b.Ellipsoid("Spine", new Vector3(0, 1.105f * h, 0), new Vector3(.158f * w, .20f * h, .095f), 0);
            b.Ellipsoid("Chest", new Vector3(0, 1.30f * h, 0), new Vector3(.205f * w, .19f * h, .112f), 0);
            b.Ellipsoid("UpperChest", new Vector3(0, 1.395f * h, 0), new Vector3(.195f * w, .07f * h, .101f), 0);
            b.Segment("Neck", rig["Neck"].position - Vector3.up * .02f, rig["Head"].position + Vector3.up * .035f, .047f, .043f, 2);
            Vector3 head = new Vector3(0, 1.66f * h, .005f);
            b.Ellipsoid("Head", head, new Vector3(.104f, .142f * Mathf.Lerp(1, h, .5f), .106f), 2);
            b.Ellipsoid("Head", head + new Vector3(0, -.017f, .10f), new Vector3(.024f, .035f, .032f), 2);
            b.Ellipsoid("Head", head + new Vector3(.039f, .028f, .099f), new Vector3(.015f, .010f, .009f), 3);
            b.Ellipsoid("Head", head + new Vector3(-.039f, .028f, .099f), new Vector3(.015f, .010f, .009f), 3);
            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "Left" : "Right";
                float sign = s == 0 ? -1 : 1;
                Vector3 shoulder = rig[side + "UpperArm"].position;
                Vector3 elbow = rig[side + "LowerArm"].position;
                Vector3 wrist = rig[side + "Hand"].position;
                b.Ellipsoid(side + "UpperArm", shoulder, new Vector3(.075f, .071f, .071f), 0);
                b.Segment(side + "UpperArm", shoulder, elbow + Vector3.right * sign * .015f, .067f, .047f, 0);
                b.Ellipsoid(side + "LowerArm", elbow, new Vector3(.042f, .042f, .042f), 2);
                b.Segment(side + "LowerArm", elbow, wrist, .043f, .026f, 2);
                b.Ellipsoid(side + "Hand", wrist + Vector3.right * sign * .05f, new Vector3(.078f, .034f, .047f), 2);
                // A visible thumb helps recognise facing and hand orientation without finger animation.
                b.Ellipsoid(side + "Hand", wrist + new Vector3(sign * .029f, -.028f, .034f), new Vector3(.032f, .021f, .025f), 2);
                Vector3 hip = rig[side + "UpperLeg"].position;
                Vector3 knee = rig[side + "LowerLeg"].position;
                Vector3 ankle = rig[side + "Foot"].position;
                b.Ellipsoid(side + "UpperLeg", hip, new Vector3(.078f * w, .087f, .083f), 1);
                b.Segment(side + "UpperLeg", hip, knee, .083f * w, .055f * w, 1);
                b.Ellipsoid(side + "LowerLeg", knee, new Vector3(.056f * w, .055f, .056f), 1);
                b.Segment(side + "LowerLeg", knee, ankle, .056f * w, .034f * w, 1);
                b.Ellipsoid(side + "Foot", new Vector3(ankle.x, .064f, .092f * p.FootLength), new Vector3(.057f * w, .064f, .145f * p.FootLength), 3);
            }
            return b.Build("Diagnostic_" + variant + "_SkinnedMesh");
        }

        static Material EnsureMaterial(string name, Color color)
        {
            string path = MaterialFolder + "/" + name + ".mat";
            var saved = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (saved != null) return saved;
            if (File.Exists(path)) throw new InvalidOperationException("Existing material could not be loaded: " + path);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("No supported Lit shader is available.");
            var material = new Material(shader) { name = "Diagnostic_" + name };
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .28f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        sealed class Rig
        {
            readonly Transform root;
            readonly Dictionary<string, Transform> byName = new Dictionary<string, Transform>();
            readonly List<HumanBone> human = new List<HumanBone>();
            public readonly List<Transform> Bones = new List<Transform>();
            public Transform this[string name] { get { return byName[name]; } }
            public Rig(Transform root) { this.root = root; }
            public void Add(string name, string parent, Vector3 worldPosition, HumanBodyBones? humanBone = null)
            {
                var bone = new GameObject(name).transform;
                bone.SetParent(parent == null ? root : byName[parent], false);
                bone.position = worldPosition;
                byName.Add(name, bone);
                Bones.Add(bone);
                if (humanBone.HasValue)
                    human.Add(new HumanBone { boneName = name, humanName = HumanTrait.BoneName[(int)humanBone.Value], limit = new HumanLimit { useDefaultValues = true } });
            }
            public HumanDescription Description()
            {
                var skeleton = new List<SkeletonBone>();
                AddSkeleton(skeleton, root);
                foreach (var bone in Bones) AddSkeleton(skeleton, bone);
                return new HumanDescription
                {
                    human = human.ToArray(), skeleton = skeleton.ToArray(),
                    upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f,
                    armStretch = .05f, legStretch = .05f, feetSpacing = 0, hasTranslationDoF = false
                };
            }
            static void AddSkeleton(List<SkeletonBone> skeleton, Transform bone)
            {
                skeleton.Add(new SkeletonBone { name = bone.name, position = bone.localPosition, rotation = bone.localRotation, scale = bone.localScale });
            }
        }

        sealed class SkinMesh
        {
            const int Sides = 12;
            const int Rings = 8;
            readonly Rig rig;
            readonly List<Vector3> vertices = new List<Vector3>();
            readonly List<Vector2> uv = new List<Vector2>();
            readonly List<BoneWeight> weights = new List<BoneWeight>();
            readonly List<int>[] triangles = { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
            public SkinMesh(Rig rig) { this.rig = rig; }
            public void Ellipsoid(string bone, Vector3 center, Vector3 radius, int material)
            {
                Surface(bone, center, Quaternion.identity, radius, radius, false, material);
            }
            public void Segment(string bone, Vector3 start, Vector3 end, float startRadius, float endRadius, int material)
            {
                Vector3 direction = end - start;
                float halfLength = direction.magnitude * .5f;
                Surface(bone, (start + end) * .5f, Quaternion.FromToRotation(Vector3.up, direction.normalized),
                    new Vector3(endRadius, halfLength, endRadius), new Vector3(startRadius, halfLength, startRadius), true, material);
            }
            void Surface(string bone, Vector3 center, Quaternion rotation, Vector3 top, Vector3 bottom, bool segment, int material)
            {
                int first = vertices.Count, boneIndex = rig.Bones.IndexOf(rig[bone]);
                for (int ring = 0; ring <= Rings; ring++)
                {
                    float t = (float)ring / Rings, phi = t * Mathf.PI;
                    // float Sin(PI) is slightly negative: a fractional power at the bottom pole
                    // otherwise creates NaN vertices and poisons skinning bounds.
                    float y = Mathf.Cos(phi), radial = ring == 0 || ring == Rings ? 0f : Mathf.Max(0f, Mathf.Sin(phi));
                    // Rounded tubes retain their girth between joints; ellipsoids form torso/head/hands.
                    if (segment) radial = Mathf.Pow(radial, .35f);
                    Vector3 size = Vector3.Lerp(top, bottom, t);
                    for (int side = 0; side <= Sides; side++)
                    {
                        float u = (float)side / Sides, angle = u * Mathf.PI * 2;
                        Vector3 point = new Vector3(Mathf.Cos(angle) * radial * size.x, y * size.y, Mathf.Sin(angle) * radial * size.z);
                        vertices.Add(center + rotation * point);
                        uv.Add(new Vector2(u, t));
                        weights.Add(new BoneWeight { boneIndex0 = boneIndex, weight0 = 1 });
                    }
                }
                for (int ring = 0; ring < Rings; ring++)
                    for (int side = 0; side < Sides; side++)
                    {
                        int a = first + ring * (Sides + 1) + side, b = a + Sides + 1;
                        triangles[material].Add(a); triangles[material].Add(a + 1); triangles[material].Add(b);
                        triangles[material].Add(a + 1); triangles[material].Add(b + 1); triangles[material].Add(b);
                    }
            }
            public Mesh Build(string name)
            {
                foreach (var vertex in vertices)
                    if (!Finite(vertex.x) || !Finite(vertex.y) || !Finite(vertex.z))
                        throw new InvalidOperationException("Non-finite diagnostic mesh vertex; refusing to save " + name);
                var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(vertices);
                mesh.SetUVs(0, uv);
                mesh.boneWeights = weights.ToArray();
                var bindposes = new Matrix4x4[rig.Bones.Count];
                for (int i = 0; i < bindposes.Length; i++) bindposes[i] = rig.Bones[i].worldToLocalMatrix;
                mesh.bindposes = bindposes;
                mesh.subMeshCount = triangles.Length;
                for (int i = 0; i < triangles.Length; i++) mesh.SetTriangles(triangles[i], i);
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                if (!Finite(mesh.bounds.center.x) || !Finite(mesh.bounds.center.y) || !Finite(mesh.bounds.center.z)
                    || mesh.bounds.size.y < 1.5f || mesh.bounds.size.x < 1f)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                    throw new InvalidOperationException("Invalid diagnostic mesh bounds; refusing to save " + name);
                }
                return mesh;
            }
            static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
#endif
