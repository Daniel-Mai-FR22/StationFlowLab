#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Adds saved passengers and reusable conversation groups without rebuilding the station or its navigation.</summary>
    public static class StationCrowdAuthoring
    {
        public const string MemberPrefabPath = StationSocialAuthoring.AssetRoot + "/Prefabs/Station_ConversationMember.prefab";
        public const string GroupPrefabPath = StationSocialAuthoring.AssetRoot + "/Prefabs/Station_ConversationGroup.prefab";
        public const string CrowdRootName = "08 Standing conversation groups";
        public const string ReportPath = "Reports/TrainStationSocial/crowd-authoring.json";
        const string ModelPath = "Assets/HumanAnimation/Source/Mixamo/Models/XBot.fbx";
        static readonly Vector3[] Centers = { new Vector3(-16,0,-16), new Vector3(16,0,-2), new Vector3(-8,0,20.8f), new Vector3(11,0,31) };
        static readonly float[] Yaws = { 20, -25, 5, 35 };
        static readonly Vector3[] MemberOffsets = { new Vector3(0,0,.85f), new Vector3(-.7361216f,0,-.425f), new Vector3(.7361216f,0,-.425f) };
        [Serializable] public sealed class GroupRecord { public string name; public Vector3 center; public Vector3[] memberPositions; }
        [Serializable] public sealed class CrowdReport
        {
            public bool success, alreadyPopulated;
            public string scene, backup, error;
            public int addedSeated, addedGroups, seatedPassengers, standingPassengers, conversationGroups, totalPassengers, totalHumans, freeSeats, completeRoutes;
            public float minimumMemberSpacing = float.MaxValue;
            public List<GroupRecord> groups = new List<GroupRecord>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Add denser saved crowd")]
        public static void Populate()
        {
            RequireCleanEditor();
            var report = new CrowdReport { scene = StationSocialAuthoring.ScenePath };
            var scene = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath, OpenSceneMode.Single);
            var social = scene.GetRootGameObjects().Single(g => g.name == StationSocialAuthoring.RootName);
            var world = social.GetComponent<StationSocialWorld>();
            if (world.shopDesks != null && world.shopDesks.Length > 0) { StationMixedCrowdAuthoring.Validate(); return; }
            var priorPassengers = world.passengers;
            var priorGroups = world.conversationGroups;
            var created = new List<GameObject>();
            try
            {
                if (social.transform.Find(CrowdRootName))
                {
                    report.alreadyPopulated = true; ValidateCore(world, report); report.success = true; return;
                }
                if (world.seats.Length < 24 || world.passengers.Length < 12)
                    throw new InvalidOperationException("Populate the original station social scene before adding the denser crowd.");
                if (world.passengers.Any(p => !p || !p.initialSeat)) throw new InvalidOperationException("Existing passenger seat references are incomplete.");
                var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(StationSocialAnimationAssets.LibraryPath);
                if (!library || library.Validate(true).Count != 0) throw new InvalidOperationException("The shared social animation library is not ready.");
                report.backup = "BuildRequests/Backups/TrainStation.before-crowd." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
                Directory.CreateDirectory(Path.GetDirectoryName(report.backup)); File.Copy(StationSocialAuthoring.ScenePath, report.backup, false);
                var passengerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(StationSocialAuthoring.PassengerPrefabPath);
                if (!passengerPrefab) throw new InvalidOperationException("Saved passenger prefab is missing.");
                var groupPrefab = EnsureGroupPrefab(EnsureMemberPrefab(passengerPrefab, library), library);
                Physics.SyncTransforms();
                // Check all planned positions before adding actors. Existing authored objects are never moved.
                for (int groupIndex = 0; groupIndex < Centers.Length; groupIndex++)
                    foreach (var member in groupPrefab.GetComponent<StationConversationGroup>().members)
                        CheckStandingPosition(world, Snap(Centers[groupIndex] + Quaternion.Euler(0,Yaws[groupIndex],0) * member.transform.localPosition));

                var actors = social.transform.Find("05 Actors — reusable prefab instances");
                if (!actors) throw new InvalidOperationException("Existing saved actor hierarchy is missing.");
                var passengers = new List<StationPassenger>(priorPassengers);
                for (int bench = 0; bench < 6 && passengers.Count < 18; bench++)
                {
                    var occupied = new HashSet<StationSeat>(passengers.Select(p => p.initialSeat));
                    var benchSeats = world.seats.Skip(bench * 4).Take(4).ToArray();
                    var seat = benchSeats.Skip(1).Concat(benchSeats.Take(1)).FirstOrDefault(s => s && !occupied.Contains(s));
                    if (!seat) throw new InvalidOperationException("No spare authored seat on bench " + (bench + 1));
                    var actor = Instantiate(passengerPrefab, actors, "Passenger " + (passengers.Count + 1).ToString("00") + " — seated traveller");
                    created.Add(actor);
                    var passenger = actor.GetComponent<StationPassenger>();
                    passenger.world = world; passenger.informationDesk = world.informationDesk; passenger.initialSeat = seat;
                    passenger.randomSeed = 3301 + bench * 131; passenger.requestInterval = new Vector2(65, 130);
                    passenger.navigationTimeout = 80; passenger.queueWaitTimeout = 150; passenger.automaticRequests = true;
                    passenger.motion.agent.enabled = false;
                    actor.transform.SetPositionAndRotation(seat.seatedPose.position, seat.seatedPose.rotation);
                    passenger.luggage = null;
                    Tint(actor, bench + 1); Record(actor); passengers.Add(passenger); report.addedSeated++;
                }
                world.passengers = passengers.ToArray();
                var crowdRoot = new GameObject(CrowdRootName); crowdRoot.transform.SetParent(social.transform, false); created.Add(crowdRoot);
                var groups = new List<StationConversationGroup>((priorGroups ?? Array.Empty<StationConversationGroup>()).Where(g => g));
                for (int i = 0; i < Centers.Length; i++)
                {
                    var instance = Instantiate(groupPrefab, crowdRoot.transform, "Conversation " + (i + 1).ToString("00") + " — voyageurs debout");
                    instance.transform.SetPositionAndRotation(Snap(Centers[i]), Quaternion.Euler(0,Yaws[i],0));
                    var group = instance.GetComponent<StationConversationGroup>();
                    group.randomSeed = 6203 + i * 137;
                    group.speakingDuration = new Vector2(5 + i * .3f, 9 + i * .4f); group.pauseDuration = new Vector2(.8f, 1.8f);
                    for (int j = 0; j < group.members.Length; j++)
                    {
                        var member = group.members[j]; member.group = group;
                        member.transform.position = Snap(member.transform.position);
                        var towardCenter = instance.transform.position - member.transform.position; towardCenter.y = 0;
                        member.transform.rotation = Quaternion.LookRotation(towardCenter);
                        member.name = "Traveller " + (j + 1).ToString("00") + (j == 0 ? " — talking pose" : " — listening pose");
                        Tint(member.gameObject, i + j);
                    }
                    Record(instance); groups.Add(group); report.addedGroups++;
                }
                world.conversationGroups = groups.ToArray();
                Physics.SyncTransforms();
                ValidateCore(world, report);
                StationSocialPresentation.RefreshPopulation(world);
                EditorUtility.SetDirty(world);
                AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the authored station crowd.");
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                world.passengers = priorPassengers; world.conversationGroups = priorGroups;
                for (int i = created.Count - 1; i >= 0; i--) if (created[i]) UnityEngine.Object.DestroyImmediate(created[i]);
                throw;
            }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Validate saved crowd")]
        public static void Validate()
        {
            var report = new CrowdReport { scene = StationSocialAuthoring.ScenePath };
            try
            {
                var scene = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
                if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open the saved TrainStation scene before validating the crowd.");
                var world = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<StationSocialWorld>(true)).Single();
                if (world.shopDesks != null && world.shopDesks.Length > 0) { StationMixedCrowdAuthoring.Validate(); report.success = true; return; }
                ValidateCore(world, report); report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally { Write(report); }
        }

        static GameObject EnsureMemberPrefab(GameObject passengerPrefab, HumanAnimationLibrary library)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(MemberPrefabPath); if (existing) return existing;
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (!source) throw new InvalidOperationException("Saved source XBot model missing.");
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Station_ConversationMember"); SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var model = Instantiate(source, root.transform, "XBot — shared Humanoid model");
                var animator = model.GetComponentInChildren<Animator>(); animator.runtimeAnimatorController = library.controller;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var player = root.AddComponent<HumanAnimationPlayer>(); player.Configure(animator, library);
                var agent = root.AddComponent<NavMeshAgent>(); agent.radius = .32f; agent.height = 1.8f; agent.speed = 1.15f;
                agent.angularSpeed = 240; agent.acceleration = 5; agent.stoppingDistance = .12f; agent.avoidancePriority = 25;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                var motion = root.AddComponent<StationActorMotion>();
                EditorUtility.CopySerialized(passengerPrefab.GetComponent<StationActorMotion>(), motion);
                motion.animationPlayer = player; motion.agent = agent;
                var member = root.AddComponent<StationConversationMember>(); member.motion = motion; member.avoidancePriority = 25;
                member.initialNavSnapDistance = .35f;
                SamplePose(animator, library.Find(motion.idleId).clip, .27f); Tint(root, 0); Record(root);
                return PrefabUtility.SaveAsPrefabAsset(root, MemberPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
        }

        static GameObject EnsureGroupPrefab(GameObject memberPrefab, HumanAnimationLibrary library)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(GroupPrefabPath); if (existing) return existing;
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Station_ConversationGroup"); SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var group = root.AddComponent<StationConversationGroup>(); group.randomSeed = 6203;
                group.members = new StationConversationMember[3];
                for (int i = 0; i < group.members.Length; i++)
                {
                    var actor = Instantiate(memberPrefab, root.transform, "Traveller " + (i + 1).ToString("00"));
                    actor.transform.localPosition = MemberOffsets[i]; actor.transform.localRotation = Quaternion.LookRotation(-MemberOffsets[i]);
                    var member = actor.GetComponent<StationConversationMember>(); member.group = group; group.members[i] = member;
                    var motion = member.motion;
                    SamplePose(motion.animationPlayer.TargetAnimator, library.Find(i == 0 ? motion.talkId : motion.idleId).clip, .24f + i * .19f);
                    Tint(actor, i); Record(actor);
                }
                return PrefabUtility.SaveAsPrefabAsset(root, GroupPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
        }

        static void ValidateCore(StationSocialWorld world, CrowdReport report)
        {
            var groups = world.conversationGroups ?? Array.Empty<StationConversationGroup>();
            report.seatedPassengers = world.passengers.Length; report.conversationGroups = groups.Length;
            report.standingPassengers = groups.Where(g => g).Sum(g => g.members.Length);
            report.totalPassengers = report.seatedPassengers + report.standingPassengers;
            report.totalHumans = world.GetComponentsInChildren<StationActorMotion>(true).Length;
            report.freeSeats = world.seats.Length - world.passengers.Select(p => p.initialSeat).Distinct().Count();
            if (report.seatedPassengers < 18 || report.standingPassengers < 12 || report.conversationGroups < 4 || report.freeSeats < 6)
                throw new InvalidOperationException("The denser crowd needs18 seated passengers,12 standing passengers in4 groups, and at least6 spare seats.");
            if (world.passengers.Select(p => p.initialSeat).Distinct().Count() != world.passengers.Length)
                throw new InvalidOperationException("Two saved passengers share the same initial seat.");
            foreach (var passenger in world.passengers)
            {
                if (passenger.luggage || passenger.motion.agent.enabled) throw new InvalidOperationException("Initial seated actor/no-luggage state is incomplete: " + passenger.name);
                float hipY = passenger.motion.animationPlayer.TargetAnimator.GetBoneTransform(HumanBodyBones.Hips).position.y;
                if (Mathf.Abs(hipY - .65f) > .045f) throw new InvalidOperationException("Saved passenger hips are not aligned with the cushion.");
            }
            foreach (var group in groups)
            {
                if (!group || group.members.Length < 3 || !PrefabUtility.IsPartOfPrefabInstance(group))
                    throw new InvalidOperationException("Saved conversation group prefab or member list is incomplete.");
                report.groups.Add(new GroupRecord { name = group.name, center = group.transform.position, memberPositions = group.members.Select(m => m.transform.position).ToArray() });
                for (int i = 0; i < group.members.Length; i++)
                {
                    var member = group.members[i];
                    if (!member || member.group != group || !member.motion || !member.motion.agent || !member.motion.agent.enabled || !PrefabUtility.IsPartOfPrefabInstance(member))
                        throw new InvalidOperationException("Saved standing member references/prefab/navigation are incomplete.");
                    var animator = member.motion.animationPlayer.TargetAnimator;
                    if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman || animator.applyRootMotion)
                        throw new InvalidOperationException("Standing member Humanoid configuration is invalid.");
                    float hipHeight = animator.GetBoneTransform(HumanBodyBones.Hips).position.y - member.transform.position.y;
                    if (hipHeight < .78f || hipHeight > 1.3f) throw new InvalidOperationException("Standing member has no saved standing pose.");
                    CheckStandingPosition(world, member.transform.position);
                    var toward = Flat(group.transform.position - member.transform.position).normalized;
                    if (Vector3.Dot(member.transform.forward, toward) < .90f) throw new InvalidOperationException("Saved standing member does not face its group.");
                    CheckPath(member.transform.position, world.informationDesk.queueSlots[0].position); report.completeRoutes++;
                    for (int j = 0; j < i; j++)
                    {
                        float spacing = Flat(member.transform.position - group.members[j].transform.position).magnitude;
                        report.minimumMemberSpacing = Mathf.Min(report.minimumMemberSpacing, spacing);
                        if (spacing < member.motion.agent.radius + group.members[j].motion.agent.radius + .25f)
                            throw new InvalidOperationException("Standing members overlap or have insufficient conversation spacing.");
                    }
                }
            }
            foreach (var seat in world.seats) { CheckPath(seat.ApproachPosition, world.informationDesk.queueSlots[0].position); report.completeRoutes++; }
            foreach (var cleaner in world.cleaners) foreach (var point in cleaner.workPoints) { CheckPath(point.position, world.informationDesk.queueSlots[0].position); report.completeRoutes++; }
            if (AnimationMode.InAnimationMode()) throw new InvalidOperationException("An animation authoring preview was left running.");
        }

        static void CheckStandingPosition(StationSocialWorld world, Vector3 position)
        {
            if ((Snap(position) - position).sqrMagnitude > .01f) throw new InvalidOperationException("Standing root is off the saved NavMesh: " + position);
            var hits = Physics.OverlapCapsule(position + Vector3.up * .42f, position + Vector3.up * 1.48f, .36f, ~0, QueryTriggerInteraction.Ignore);
            if (hits.Any(c => c && !c.transform.IsChildOf(world.transform)))
                throw new InvalidOperationException("Standing member intersects station geometry: " + string.Join(", ", hits.Where(c => c && !c.transform.IsChildOf(world.transform)).Select(c => c.name)));
            foreach (var slot in world.informationDesk.queueSlots) if (Flat(position - slot.position).magnitude < 1.5f)
                throw new InvalidOperationException("Standing group encroaches on the accueil queue.");
            foreach (var seat in world.seats) if (Flat(position - seat.ApproachPosition).magnitude < 1.1f)
                throw new InvalidOperationException("Standing group blocks a bench approach.");
            foreach (var cleaner in world.cleaners) foreach (var point in cleaner.workPoints)
                if (Flat(position - point.position).magnitude < 1.1f) throw new InvalidOperationException("Standing group blocks a cleaning work point.");
        }
        static void CheckPath(Vector3 from, Vector3 to)
        { var path = new NavMeshPath(); if (!NavMesh.CalculatePath(Snap(from), Snap(to), NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) throw new InvalidOperationException("Crowd navigation route is incomplete."); }
        static Vector3 Snap(Vector3 point)
        { if (!NavMesh.SamplePosition(point, out var hit, .3f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y) > .2f) throw new InvalidOperationException("No hall/concourse NavMesh at " + point); return hit.position; }
        static Vector3 Flat(Vector3 point) { point.y = 0; return point; }
        static GameObject Instantiate(GameObject prefab, Transform parent, string name)
        { var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene); go.transform.SetParent(parent,false); go.name = name; return go; }
        static void Tint(GameObject actor, int index)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(StationSocialAuthoring.AssetRoot + "/Materials/Passenger_Colour_" + (index % 4) + ".mat");
            if (!material) return;
            foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer.name.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0)
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(_ => material).ToArray();
        }
        static void SamplePose(Animator animator, AnimationClip clip, float phase)
        {
            var bones = animator.GetComponentsInChildren<Transform>(true).Where(t => t != animator.transform).ToArray();
            var driver = ScriptableObject.CreateInstance<AnimationModeDriver>(); driver.hideFlags = HideFlags.HideAndDontSave;
            Vector3[] positions = null, scales = null; Quaternion[] rotations = null;
            try
            {
                AnimationMode.StartAnimationMode(driver); AnimationMode.BeginSampling();
                try { AnimationMode.SampleAnimationClip(animator.gameObject, clip, clip.length * phase); }
                finally { AnimationMode.EndSampling(); }
                positions = bones.Select(t => t.localPosition).ToArray(); rotations = bones.Select(t => t.localRotation).ToArray(); scales = bones.Select(t => t.localScale).ToArray();
            }
            finally { if (AnimationMode.InAnimationMode(driver)) AnimationMode.StopAnimationMode(driver); UnityEngine.Object.DestroyImmediate(driver); }
            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = positions[i]; bones[i].localRotation = rotations[i]; bones[i].localScale = scales[i]; }
            animator.transform.localPosition = Vector3.zero; animator.transform.localRotation = Quaternion.identity; animator.transform.localScale = Vector3.one;
        }
        static void Record(GameObject root)
        { foreach (var component in root.GetComponentsInChildren<Component>(true)) if (component && PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component); }
        static void RequireCleanEditor()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode()) throw new InvalidOperationException("Stop Play and animation preview before crowd authoring.");
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save existing scene edits first; crowd authoring never discards unsaved work.");
        }
        static void Write(CrowdReport report)
        { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(report,true)); }
    }
}
#endif
