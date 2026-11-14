#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Short, real Play-mode camera/UI checks. Fixtures never move a simulation participant.</summary>
    [InitializeOnLoad]
    public static class StationDecisionLabPresentationVerification
    {
        const string RunKey = "StationFlow.DecisionLab.PresentationCheck";
        const string LogKey = RunKey + ".Errors";
        const string ReportPath = ReportDirectory + "/presentation-check.json";
        [Serializable] public sealed class Report
        {
            public string startedUtc, completedUtc, phase, unityVersion, sourceSceneHash, sourceScheduleHash, labSceneHash, originalPlayScene;
            public string overviewScreenshot, followScreenshot;
            public bool success, selectionPassed, selectionOrbitPreserved, trackingPassed, stableOrbitPassed, wallCollisionPassed, roofCollisionPassed;
            public bool pauseOrbitPassed, terminalPosePassed, cctvHandoffPassed, overviewRestored, catalogAccessible, pointerBlockingPassed;
            public bool originalScenePreserved, originalSchedulePreserved, labScenePreserved, oldOptionsEnabled, oldPaused, oldRunInBackground;
            public int oldOptions, oldTargetFrameRate, oldVsync, checkAfterFrame, architectureChecks, pointerSamples, cameraCount;
            public long deadlineTicks;
            public float oldTimeScale, phaseStarted, elapsedSimulation, startedSimulation, yaw, pitch, orbitDistance;
            public float actualPassengerDisplacement, focusTranslation, cameraTranslation, maximumCameraStep, wallOriginalDistance, wallLimitedDistance, roofLimitedDistance;
            public double pausedRailSeconds;
            public Vector3 passengerStart, focusStart, cameraStart, priorCameraPosition, frozenFocus, frozenCamera, pausePassengerPosition;
            public List<string> errors = new List<string>();
            public string inputCoverage = "Exercises the same pointer-blocking gate used by runtime input at rendered panel coordinates; OS mouse-button delivery is reserved for visual/manual QA.";
            public string fixture = "Disabled temporary passenger anchor and temporary wall/ceiling colliders; no real participant is teleported or disabled.";
        }
        static Report current;
        static StationDecisionWorld world;
        static StationDecisionDashboard dashboard;
        static StationDecisionFollowCamera follow;
        static TrainStationViewControls controls;
        static StationDecisionPassenger sentinel;
        static GameObject obstacle;
        static double nextPoll;

        static StationDecisionLabPresentationVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Check presentation in actual Play mode")]
        public static void BeginPlayCheck()
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(RunKey, "")) &&
                string.IsNullOrEmpty(SessionState.GetString("StationFlow.DecisionLab.PlayCheck", "")), "Another Decision Lab verification is active.");
            RequireEditAndClean(); Open();
            var run = new Report { startedUtc = DateTime.UtcNow.ToString("O"), phase = "entering",
                unityVersion = Application.unityVersion, deadlineTicks = DateTime.UtcNow.AddMinutes(3).Ticks,
                sourceSceneHash = Hash(SourceScene), sourceScheduleHash = Hash(SourceSchedule), labSceneHash = Hash(ScenePath),
                originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene),
                oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled, oldOptions = (int)EditorSettings.enterPlayModeOptions,
                oldPaused = EditorApplication.isPaused, oldTimeScale = Time.timeScale, oldRunInBackground = Application.runInBackground,
                oldTargetFrameRate = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount };
            SessionState.EraseString(LogKey); Save(run);
            EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Capture actual Game view with interface")]
        public static void CaptureGame()
        {
            Require(EditorApplication.isPlaying && SceneManager.GetActiveScene().path == ScenePath, "A Game-view capture requires the running Decision Lab.");
            string path = Capture("game");
            Write(ReportDirectory + "/game-capture-request.json", new CaptureRequest { requestedUtc = DateTime.UtcNow.ToString("O"), path = path,
                width = Screen.width, height = Screen.height });
        }
        [Serializable] sealed class CaptureRequest { public string requestedUtc, path; public int width, height; }
        static string Capture(string name)
        {
            Directory.CreateDirectory(ReportDirectory);
            string path = Path.GetFullPath(ReportDirectory + "/presentation-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .05;
            var run = current ?? Load(); if (run == null) return;
            try
            {
                if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Finish(run); return; }
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Presentation verification timed out during " + run.phase);
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey, "")), "Unity reported an error: " + SessionState.GetString(LogKey, ""));
                if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.checkAfterFrame) Initialize(run);
                else if (EditorApplication.isPlaying && run.phase != "entering" && run.phase != "initializing" && run.phase != "exiting")
                {
                    Require(world && dashboard && follow && controls, "Presentation fixture references were lost during an Editor reload; rerun from a clean Play start.");
                    Advance(run);
                }
                Save(run);
            }
            catch (Exception error)
            {
                run.errors.Add(error.ToString()); run.phase = "exiting"; Save(run); Write(ReportPath, run);
                if (world && world.Paused) world.TogglePause();
                Time.timeScale = run.oldTimeScale;
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false;
                else Finish(run);
            }
        }

        static void Initialize(Report run)
        {
            Require(SceneManager.GetActiveScene().path == ScenePath, "Presentation verification loaded the wrong scene.");
            var scene = SceneManager.GetActiveScene();
            world = Components<StationDecisionWorld>(scene).Single();
            dashboard = Components<StationDecisionDashboard>(scene).Single();
            follow = Components<StationDecisionFollowCamera>(scene).Single();
            controls = Components<TrainStationViewControls>(scene).Single();
            Require(world.rail && world.rail.IsReady && world.participants.Length == ParticipantCount, "Experiment failed to initialize.");
            Require(dashboard.world == world && dashboard.followCamera == follow && dashboard.viewControls == controls &&
                follow.world == world && follow.dashboard == dashboard && follow.viewControls == controls, "Presentation bindings were not saved.");
            Require(!follow.IsFollowing && follow.overviewOrbit.enabled && controls.selectedView == 0, "Scene should begin in the Hall overview.");
            foreach (var item in Components<Transform>(scene).Where(t => StationDecisionLabPresentation.LegacyPanelNames.Contains(t.name)))
                Require(!item.gameObject.activeInHierarchy, "A legacy panel obscures the new interface: " + item.name);
            run.cameraCount = controls.ViewCount;
            Require(run.cameraCount > 1 && controls.cameraCatalog && !controls.cameraCatalog.activeInHierarchy, "Saved camera catalog is unavailable.");
            Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0; Time.timeScale = 1f;
            run.startedSimulation = world.ElapsedSeconds;
            current = run;
            ChangePhase(run, "overview");
        }

        static void Advance(Report run)
        {
            float age = Time.unscaledTime - run.phaseStarted;
            run.elapsedSimulation = world.ElapsedSeconds - run.startedSimulation;
            Require(run.elapsedSimulation < 60f, "Presentation verification exceeded its 60 simulation-second scope.");
            if (follow.IsFollowing) CheckArchitecture(run);
            switch (run.phase)
            {
                case "overview":
                    if (age < .5f) return;
                    run.overviewScreenshot = Capture("overview");
                    ChangePhase(run, "overview-capture");
                    break;
                case "overview-capture":
                    if (age < .2f || !File.Exists(run.overviewScreenshot)) return;
                    dashboard.SelectParticipant(0);
                    Require(dashboard.SelectedPassenger == world.participants[0] && follow.FollowedPassenger == world.participants[0] && follow.IsFollowing,
                        "Selecting participant 1 did not bind the follow target.");
                    follow.Orbit(new Vector2(25f, -8f)); follow.Zoom(.5f);
                    run.yaw = follow.CurrentYaw; run.pitch = follow.CurrentPitch; run.orbitDistance = follow.RequestedDistance;
                    run.passengerStart = world.participants[0].transform.position;
                    ChangePhase(run, "actual-traveller");
                    break;
                case "actual-traveller":
                    if (age < 2f) return;
                    run.actualPassengerDisplacement = Vector3.Distance(run.passengerStart, world.participants[0].transform.position);
                    Require(run.actualPassengerDisplacement > .2f, "The selected real traveller did not move during observation.");
                    dashboard.SelectParticipant(4);
                    run.selectionPassed = dashboard.SelectedPassenger == world.participants[4] && follow.FollowedPassenger == world.participants[4] && follow.IsFollowing;
                    Require(run.selectionPassed, "Selecting participant 5 did not change both dashboard and camera target.");
                    run.selectionOrbitPreserved = Mathf.Abs(Mathf.DeltaAngle(run.yaw, follow.CurrentYaw)) < .01f &&
                        Mathf.Abs(run.pitch - follow.CurrentPitch) < .01f && Mathf.Abs(run.orbitDistance - follow.RequestedDistance) < .01f;
                    Require(run.selectionOrbitPreserved, "Selecting another traveller reset the user's yaw, pitch or zoom.");
                    ChangePhase(run, "follow-capture");
                    break;
                case "follow-capture":
                    if (age < .4f) return;
                    run.followScreenshot = Capture("follow");
                    ChangePhase(run, "follow-shutter");
                    break;
                case "follow-shutter":
                    if (age < .2f || !File.Exists(run.followScreenshot)) return;
                    var anchor = new GameObject("Presentation verification — temporary anchor") { hideFlags = HideFlags.DontSave };
                    anchor.SetActive(false);
                    sentinel = anchor.AddComponent<StationDecisionPassenger>(); sentinel.enabled = false; sentinel.passengerId = "camera-test-anchor";
                    anchor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); anchor.SetActive(true);
                    follow.Follow(sentinel); follow.ResetOrbit();
                    run.focusStart = follow.FocusPosition; run.cameraStart = follow.controlledCamera.transform.position;
                    run.priorCameraPosition = run.cameraStart; run.yaw = follow.CurrentYaw; run.pitch = follow.CurrentPitch; run.orbitDistance = follow.RequestedDistance;
                    ChangePhase(run, "tracking");
                    break;
                case "tracking":
                    sentinel.transform.position = new Vector3(Mathf.Min(age, 2f), 0f, Mathf.Min(age, 2f) * .5f);
                    run.maximumCameraStep = Mathf.Max(run.maximumCameraStep, Vector3.Distance(run.priorCameraPosition, follow.controlledCamera.transform.position));
                    run.priorCameraPosition = follow.controlledCamera.transform.position;
                    Require(Mathf.Abs(Mathf.DeltaAngle(run.yaw, follow.CurrentYaw)) < .01f && Mathf.Abs(run.pitch - follow.CurrentPitch) < .01f &&
                        Mathf.Abs(run.orbitDistance - follow.RequestedDistance) < .01f, "Target movement changed the chosen orbit.");
                    if (age < 2.8f) return;
                    run.focusTranslation = Vector3.Distance(run.focusStart, follow.FocusPosition);
                    run.cameraTranslation = Vector3.Distance(run.cameraStart, follow.controlledCamera.transform.position);
                    run.trackingPassed = run.focusTranslation > 2f && run.cameraTranslation > 1f &&
                        Vector3.Distance(follow.FocusPosition, sentinel.transform.position + Vector3.up * follow.focusHeight) < .1f;
                    run.stableOrbitPassed = run.maximumCameraStep < 1f;
                    Require(run.trackingPassed && run.stableOrbitPassed, "Camera failed to track a moving anchor smoothly.");
                    CreateObstacle(false); run.wallOriginalDistance = follow.ActualDistance; follow.ReapplyView();
                    run.wallLimitedDistance = follow.ActualDistance;
                    run.wallCollisionPassed = follow.OcclusionLimited && run.wallLimitedDistance < run.wallOriginalDistance - .2f &&
                        Vector3.Distance(obstacle.GetComponent<Collider>().ClosestPoint(follow.controlledCamera.transform.position), follow.controlledCamera.transform.position) >= follow.collisionRadius - .015f;
                    Require(run.wallCollisionPassed, "Wall fixture did not shorten the camera orbit before collision.");
                    ChangePhase(run, "wall");
                    break;
                case "wall":
                    if (age < .25f) return;
                    UnityEngine.Object.DestroyImmediate(obstacle); obstacle = null; Physics.SyncTransforms();
                    follow.Orbit(new Vector2(0, -1000)); follow.Zoom(-100);
                    CreateObstacle(true); follow.ReapplyView(); run.roofLimitedDistance = follow.ActualDistance;
                    run.roofCollisionPassed = follow.OcclusionLimited && follow.ActualDistance < follow.RequestedDistance - .2f;
                    Require(run.roofCollisionPassed, "Ceiling fixture did not limit a high-pitch orbit.");
                    CheckArchitecture(run);
                    ChangePhase(run, "roof");
                    break;
                case "roof":
                    if (age < .25f) return;
                    UnityEngine.Object.DestroyImmediate(obstacle); obstacle = null; Physics.SyncTransforms(); follow.ResetOrbit();
                    world.TogglePause();
                    run.pausedRailSeconds = world.rail.ElapsedSimulationSeconds; run.pausePassengerPosition = world.participants[0].transform.position;
                    run.frozenFocus = follow.FocusPosition; run.yaw = follow.CurrentYaw; run.cameraStart = follow.controlledCamera.transform.position;
                    follow.Orbit(new Vector2(90, 10)); follow.Zoom(1);
                    ChangePhase(run, "paused");
                    break;
                case "paused":
                    if (age < .5f) return;
                    run.pauseOrbitPassed = world.Paused && Math.Abs(world.rail.ElapsedSimulationSeconds - run.pausedRailSeconds) < .01 &&
                        Vector3.Distance(run.pausePassengerPosition, world.participants[0].transform.position) < .03f &&
                        Mathf.Abs(Mathf.DeltaAngle(run.yaw, follow.CurrentYaw)) > 5f &&
                        Vector3.Distance(run.cameraStart, follow.controlledCamera.transform.position) > .5f &&
                        Vector3.Distance(run.frozenFocus, follow.FocusPosition) < .02f;
                    Require(run.pauseOrbitPassed, "Camera should orbit while paused without advancing railway or travellers.");
                    world.TogglePause();
                    VerifyPointerGate(run);
                    sentinel.gameObject.SetActive(false);
                    ChangePhase(run, "freeze");
                    break;
                case "freeze":
                    if (age < .25f) return;
                    Require(follow.TargetFrozen, "Inactive target did not freeze the last valid focus.");
                    run.frozenFocus = follow.FocusPosition; run.frozenCamera = follow.controlledCamera.transform.position;
                    sentinel.transform.position += new Vector3(7, 0, 3);
                    ChangePhase(run, "terminal");
                    break;
                case "terminal":
                    if (age < .4f) return;
                    run.terminalPosePassed = follow.TargetFrozen && Vector3.Distance(run.frozenFocus, follow.FocusPosition) < .02f &&
                        Vector3.Distance(run.frozenCamera, follow.controlledCamera.transform.position) < .02f;
                    Require(run.terminalPosePassed, "An inactive target changed the final camera pose.");
                    controls.SelectView(1); ChangePhase(run, "cctv");
                    break;
                case "cctv":
                    if (age < .25f) return;
                    run.cctvHandoffPassed = !follow.IsFollowing && controls.selectedView == 1 && controls.viewpoints[1].enabled &&
                        !follow.controlledCamera.enabled && !follow.overviewOrbit.enabled;
                    Require(run.cctvHandoffPassed, "Selecting a saved camera did not release passenger follow.");
                    follow.ReturnToOverview(); ChangePhase(run, "return");
                    break;
                case "return":
                    if (age < .25f) return;
                    run.overviewRestored = !follow.IsFollowing && controls.selectedView == 0 && follow.controlledCamera.enabled && follow.overviewOrbit.enabled;
                    Require(run.overviewRestored, "Returning to overview did not restore the existing Hall orbit.");
                    controls.ToggleCameraCatalog(); run.catalogAccessible = controls.cameraCatalog.activeInHierarchy; controls.ToggleCameraCatalog();
                    Require(run.catalogAccessible, "The retained camera catalog cannot be opened.");
                    Require(File.Exists(run.overviewScreenshot) && File.Exists(run.followScreenshot), "Actual Game-view screenshots were not produced.");
                    UnityEngine.Object.DestroyImmediate(sentinel.gameObject); sentinel = null;
                    world.FlushLogs(); run.phase = "exiting"; Save(run); EditorApplication.isPlaying = false;
                    break;
            }
        }

        static void CreateObstacle(bool ceiling)
        {
            obstacle = new GameObject(ceiling ? "Presentation verification — temporary ceiling" : "Presentation verification — temporary wall") { hideFlags = HideFlags.DontSave };
            var collider = obstacle.AddComponent<BoxCollider>();
            if (ceiling)
            {
                obstacle.transform.position = follow.FocusPosition + Vector3.up * 2.5f;
                collider.size = new Vector3(12, .3f, 12);
            }
            else
            {
                Vector3 cameraPosition = follow.controlledCamera.transform.position;
                Require(Vector3.Distance(cameraPosition, follow.FocusPosition) > 1.5f, "Open-hall camera fixture is already obstructed.");
                obstacle.transform.position = Vector3.Lerp(follow.FocusPosition, cameraPosition, .55f);
                obstacle.transform.rotation = follow.controlledCamera.transform.rotation;
                collider.size = new Vector3(3f, 3f, .3f);
            }
            Physics.SyncTransforms();
        }

        static void VerifyPointerGate(Report run)
        {
            // Inspect actual rendered panel geometry without moving the user's OS pointer.
            int panels = 0, clear = 0;
            for (int y = 12; y < Screen.height; y += 24)
                for (int x = 12; x < Screen.width; x += 24)
                {
                    var point = new Vector2(x, y);
                    if (dashboard.IsPointerOverDashboard(point))
                    { ++panels; Require(follow.ShouldBlockPointer(point), "Camera input is not blocked over a dashboard panel."); }
                    else ++clear;
                }
            run.pointerSamples = panels;
            run.pointerBlockingPassed = panels > 10 && clear > 10;
            Require(run.pointerBlockingPassed, "Dashboard input regions or clear scene area are unavailable.");
        }

        static void CheckArchitecture(Report run)
        {
            Vector3 cameraPosition = follow.controlledCamera.transform.position;
            Require(Finite(cameraPosition) && Finite(follow.FocusPosition) && follow.ActualDistance >= .099f, "Camera produced an invalid pose.");
            foreach (var collider in Physics.OverlapSphere(cameraPosition, Mathf.Max(.01f, follow.collisionRadius - .015f), follow.collisionMask, QueryTriggerInteraction.Ignore))
                Require(Ignore(collider), "Camera overlaps architecture: " + collider.name);
            Vector3 vector = cameraPosition - follow.FocusPosition;
            if (vector.magnitude > .03f)
                foreach (var hit in Physics.RaycastAll(follow.FocusPosition, vector.normalized, vector.magnitude - .02f, follow.collisionMask, QueryTriggerInteraction.Ignore))
                    Require(Ignore(hit.collider), "Camera-to-target ray crosses architecture: " + hit.collider.name);
            ++run.architectureChecks;
        }
        static bool Ignore(Collider collider)
        {
            if (!collider || collider.GetComponentInParent<StationActorMotion>() || collider.GetComponentInParent<StationDecisionPassenger>()) return true;
            if (collider.transform == follow.controlledCamera.transform || collider.transform.IsChildOf(follow.controlledCamera.transform)) return true;
            return follow.ignoredCollisionRoots != null && follow.ignoredCollisionRoots.Any(root => root && (collider.transform == root || collider.transform.IsChildOf(root)));
        }
        static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) &&
            !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        static void ChangePhase(Report run, string phase) { run.phase = phase; run.phaseStarted = Time.unscaledTime; }
        static void ModeChanged(PlayModeStateChange change)
        {
            var run = current ?? Load(); if (run == null) return;
            if (change == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.checkAfterFrame = Time.frameCount + 12; }
            else if (change == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting")
            { run.errors.Add("Play stopped before the presentation check completed."); run.phase = "exiting"; }
            else if (change == PlayModeStateChange.EnteredEditMode) run.phase = "restoring";
            Save(run);
        }
        static void Finish(Report run)
        {
            try
            {
                Time.timeScale = run.oldTimeScale; Application.runInBackground = run.oldRunInBackground;
                Application.targetFrameRate = run.oldTargetFrameRate; QualitySettings.vSyncCount = run.oldVsync;
                EditorSettings.enterPlayModeOptionsEnabled = run.oldOptionsEnabled; EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.oldOptions;
                EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                EditorApplication.isPaused = run.oldPaused;
                run.originalScenePreserved = Hash(SourceScene) == run.sourceSceneHash;
                run.originalSchedulePreserved = Hash(SourceSchedule) == run.sourceScheduleHash;
                run.labScenePreserved = Hash(ScenePath) == run.labSceneHash;
                Require(run.originalScenePreserved && run.originalSchedulePreserved && run.labScenePreserved, "Play verification changed a saved scene or source schedule.");
                string log = SessionState.GetString(LogKey, ""); if (!string.IsNullOrEmpty(log)) run.errors.Add(log);
            }
            catch (Exception error) { run.errors.Add(error.ToString()); }
            run.completedUtc = DateTime.UtcNow.ToString("O"); run.phase = "complete"; run.success = run.errors.Count == 0;
            Write(ReportPath, run); SessionState.EraseString(RunKey); SessionState.EraseString(LogKey);
            current = null; world = null; dashboard = null; follow = null; controls = null; sentinel = null; obstacle = null;
        }
        static void OnLog(string message, string trace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (current == null && string.IsNullOrEmpty(SessionState.GetString(RunKey, ""))) return;
            string prior = SessionState.GetString(LogKey, "");
            if (prior.Length < 12000) SessionState.SetString(LogKey, prior + message + "\n" + trace + "\n");
        }
        static Report Load() { string json = SessionState.GetString(RunKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Report>(json); }
        static void Save(Report run) => SessionState.SetString(RunKey, JsonUtility.ToJson(run));
    }
}
#endif
