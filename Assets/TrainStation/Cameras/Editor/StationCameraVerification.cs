#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using StationFlow.TrainStation.Social;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Checks the authored camera catalogue and its real Play-mode presentation controls.</summary>
    [InitializeOnLoad]
    public static class StationCameraVerification
    {
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        const string StateKey = "StationFlow.StationCamera.Verification";
        const string LogKey = StateKey + ".Errors";
        const string ReportDirectory = "Reports/TrainStationCameras";
        [Serializable] sealed class SavedScene { public string path; public bool isLoaded, isActive; }
        [Serializable] sealed class CameraResult
        {
            public int index;
            public string name, cameraId, purpose, mount, target;
            public int coveragePoints;
            public bool selectedInPlay, physicalRigStayedActive;
        }
        [Serializable] sealed class SavedReport
        {
            public string scene, unityVersion, checkedUtc;
            public bool success;
            public int cameras, fixedCameras, orbitCameras, catalogueButtons, activeCameras, activeListeners;
            public int passengerCount, humanCount, trainsets, timetableDisplays, objects;
            public int roofCollisionProxies;
            public List<CameraResult> catalogue = new List<CameraResult>();
            public List<string> errors = new List<string>();
        }
        [Serializable] sealed class PointerProbeResult
        {
            public string phase;
            public Vector2 screenPosition;
            public List<string> hitGraphics = new List<string>();
        }
        [Serializable] sealed class Run
        {
            public string startedUtc, completedUtc, unityVersion, phase, originalPlayScene;
            public bool success, abort, capturedSettings, originalOptionsEnabled, originalPaused, originalRunInBackground;
            public bool restoredSettings, restoredScenes, allViewsPassed, navigationPassed, cataloguePassed;
            public bool orbitPassed, panPassed, zoomPassed, boundsPassed, resetPassed, retainedOrbitPassed, fixedPosesPassed, simulationPassed;
            public bool viewportRaycastPassed, catalogueRaycastPassed, architectureCollisionPassed, roofCollisionPassed;
            public bool rawMouseAttempted, rawMousePassed;
            public string rawMouseSkippedReason, rawMouseScope;
            public int originalOptions, originalTargetFrameRate, originalVSyncCount, checkAfterFrame, nextFrame, selectedIndex, mouseStage;
            public int baselineObjects, framesObserved, objectChecks, timetableChecks, collisionSamples;
            public float originalTimeScale, startedAt, simulatedSeconds, originalYaw, originalPitch, originalDistance;
            public float changedYaw, changedPitch, changedDistance, rawMouseInitialYaw, rawMouseInitialDistance;
            public float rawWheelEventDelta, rawWheelExpectedDistance, rawWheelActualDistance;
            public float roofRequestedDistance, roofActualDistance, roofHitDistance;
            public string roofHitObject;
            public Vector3 originalFocusOffset, changedFocusOffset;
            public double initialRailTime, finalRailTime, totalPassengerTravel;
            public long deadlineTicks;
            public SavedScene[] scenes;
            public List<CameraResult> catalogue = new List<CameraResult>();
            public List<PointerProbeResult> pointerProbes = new List<PointerProbeResult>();
            public List<string> errors = new List<string>();
        }
        static Run liveRun;
        static TrainStationViewControls controls;
        static TrainStationOrbitCamera orbit;
        static StationSurveillanceCamera[] fixedCameras;
        static StationRailOperations rail;
        static StationTimetableDisplay[] displays;
        static StationSocialWorld social;
        static readonly Dictionary<Transform, Pose> fixedPoses = new Dictionary<Transform, Pose>();
        static readonly Dictionary<StationPassenger, Vector3> lastPassengerPositions = new Dictionary<StationPassenger, Vector3>();
        static double nextPoll, nextProgressWrite;
#if ENABLE_INPUT_SYSTEM
        static Mouse virtualMouse, previousMouse;
#endif

        static StationCameraVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += Log;
        }

        [MenuItem("Efrei Research Lab/Train Station Cameras/Validate saved cameras")]
        public static void ValidateSavedScene()
        {
            Require(!Application.isPlaying, "Saved camera verification requires Edit mode.");
            var report = InspectSaved(SceneManager.GetActiveScene());
            Write("saved-scene-check.json", report);
            Require(report.success, string.Join("\n", report.errors));
        }

        static SavedReport InspectSaved(Scene scene)
        {
            var result = new SavedReport { scene = scene.path, unityVersion = Application.unityVersion, checkedUtc = DateTime.UtcNow.ToString("O") };
            try
            {
                Require(scene.path == ScenePath, "Open the saved TrainStation scene first.");
                var view = Components<TrainStationViewControls>(scene).Single();
                var viewpoints = view.viewpoints;
                Require(viewpoints != null && viewpoints.Length == 15 && viewpoints.All(c => c), "Expected fifteen saved camera viewpoints.");
                Require(viewpoints.Distinct().Count() == viewpoints.Length, "The camera catalogue contains duplicate references.");
                result.cameras = Components<Camera>(scene).Length;
                Require(result.cameras == viewpoints.Length, "Scene cameras and the saved catalogue disagree.");
                Require(view.viewNames != null && view.viewNames.Length == viewpoints.Length && view.viewNames.All(n => !string.IsNullOrWhiteSpace(n)), "Every camera requires an editable display name.");
                Require(view.viewNames.Distinct().Count() == viewpoints.Length, "Camera display names should be distinct.");
                var orbitCameras = Components<TrainStationOrbitCamera>(scene);
                result.orbitCameras = orbitCameras.Length;
                Require(orbitCameras.Length == 1 && orbitCameras[0].controlledCamera == viewpoints[0] && orbitCameras[0].focusPoint, "The main camera needs its own saved orbit rig and focus reference.");
                var orbitView = orbitCameras[0];
                Require(orbitView.minimumPitch < orbitView.maximumPitch && orbitView.minimumDistance > 0f && orbitView.minimumDistance < orbitView.maximumDistance, "Orbit limits are invalid.");
                Require(orbitView.panBoundsMin.x <= orbitView.panBoundsMax.x && orbitView.panBoundsMin.y <= orbitView.panBoundsMax.y && orbitView.panBoundsMin.z <= orbitView.panBoundsMax.z, "Camera pan bounds are invalid.");
                Require(orbitView.collisionMask.value != 0 && orbitView.ignoredCollisionRoots != null && orbitView.ignoredCollisionRoots.All(t => t), "Camera collision configuration is incomplete.");
                var roofProxies = Components<Collider>(scene).Where(IsRoofCollisionProxy).ToArray();
                Require(roofProxies.Length > 0, "Renderer-only roofs require saved camera collision proxies.");
                Require(roofProxies.Any(proxy => proxy.transform.parent && proxy.transform.parent.name.StartsWith("Vault glazing bay", StringComparison.Ordinal)), "The main hall vault is missing saved camera collision geometry.");
                foreach (var proxy in roofProxies)
                {
                    Require(proxy.enabled && !proxy.isTrigger && proxy.gameObject.hideFlags == HideFlags.None && proxy.gameObject.scene == scene, "Roof collision proxy is disabled, temporary or not saved in the station scene: " + proxy.name);
                    var modifier = proxy.GetComponent<NavMeshModifier>();
                    Require(modifier && modifier.enabled && modifier.ignoreFromBuild && !modifier.overrideArea, "A roof camera collision proxy must be explicitly excluded from NavMesh baking: " + proxy.name);
                    Require(proxy.transform.parent && proxy.transform.parent.GetComponent<Renderer>(), "Roof collision proxy must remain attached to its visible roof renderer: " + proxy.name);
                    Require(view.roofGroups.Any(root => root && proxy.transform.IsChildOf(root.transform)), "A camera roof proxy has been detached from the saved roof visibility groups.");
                    if (proxy is MeshCollider meshProxy) Require(meshProxy.sharedMesh && meshProxy.sharedMesh == proxy.transform.parent.GetComponent<MeshFilter>().sharedMesh, "A custom roof collision proxy no longer matches its visible mesh.");
                    Require((orbitView.collisionMask.value & (1 << proxy.gameObject.layer)) != 0, "The main camera collision mask ignores a saved roof proxy.");
                }
                result.roofCollisionProxies = roofProxies.Length;
                var surveillance = Components<StationSurveillanceCamera>(scene);
                result.fixedCameras = surveillance.Length;
                Require(surveillance.Length == 14, "Expected fourteen fixed surveillance cameras.");
                Require(surveillance.All(s => !string.IsNullOrWhiteSpace(s.cameraId) && !string.IsNullOrWhiteSpace(s.displayName) && !string.IsNullOrWhiteSpace(s.purpose)), "Surveillance metadata is incomplete.");
                Require(surveillance.Select(s => s.cameraId).Distinct().Count() == surveillance.Length, "Surveillance camera IDs are duplicated.");
                Require(surveillance.Select(s => s.cameraComponent).Distinct().Count() == surveillance.Length, "Surveillance metadata points to duplicate cameras.");
                for (int i = 0; i < viewpoints.Length; i++)
                {
                    var camera = viewpoints[i];
                    Require(camera.gameObject.activeInHierarchy, "Saved camera housings must stay visible even when another view is selected: " + camera.name);
                    Require(camera.GetComponent<AudioListener>(), "A saved view is missing its switchable AudioListener: " + camera.name);
                    var entry = new CameraResult { index = i, name = view.GetViewName(i) };
                    if (i > 0)
                    {
                        var metadata = surveillance.Single(s => s.cameraComponent == camera);
                        Require(metadata.mountAnchor && metadata.aimTarget && metadata.coveragePoints != null && metadata.coveragePoints.Length > 0 && metadata.coveragePoints.All(t => t), "Saved mount, aim or coverage references are missing: " + camera.name);
                        Require(PrefabUtility.GetCorrespondingObjectFromOriginalSource(metadata.gameObject), "A surveillance rig is not a reusable prefab instance: " + metadata.name);
                        Require(metadata.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled), "A surveillance rig has no visible housing.");
                        var aim = camera.WorldToViewportPoint(metadata.aimTarget.position);
                        Require(aim.z > camera.nearClipPlane && aim.z < camera.farClipPlane && aim.x >= 0f && aim.x <= 1f && aim.y >= 0f && aim.y <= 1f, "Primary monitoring target is outside the saved camera frustum: " + metadata.displayName);
                        entry.cameraId = metadata.cameraId; entry.purpose = metadata.purpose; entry.mount = metadata.mountAnchor.name;
                        entry.target = metadata.aimTarget.name; entry.coveragePoints = metadata.coveragePoints.Length;
                    }
                    result.catalogue.Add(entry);
                }
                result.activeCameras = viewpoints.Count(c => c.isActiveAndEnabled);
                result.activeListeners = Components<AudioListener>(scene).Count(a => a.isActiveAndEnabled);
                Require(result.activeCameras == 1 && result.activeListeners == 1 && viewpoints[view.selectedView].isActiveAndEnabled, "Exactly one saved camera and AudioListener should be active.");
                Require(view.cameraCatalog && !view.cameraCatalog.activeSelf, "Camera catalogue should be authored and initially closed.");
                Require(view.viewButtons != null && view.viewButtons.Length == 15 && view.viewButtons.All(b => b), "All fifteen camera selection buttons must exist in the saved hierarchy.");
                for (int i = 0; i < view.viewButtons.Length; i++)
                {
                    var button = view.viewButtons[i].GetComponent<Button>();
                    Require(button && HasSelectionCall(button, view, i), "Camera selection button has no serialized SelectView index: " + i);
                }
                result.catalogueButtons = view.viewButtons.Length;
                var uiButtons = Components<Button>(scene);
                foreach (var method in new[] { "PreviousView", "NextView", "ToggleCameraCatalog", "ResetGameView" })
                    Require(uiButtons.Any(b => HasCall(b, view, method)), "The saved interface is missing its " + method + " control.");
                var world = Components<StationSocialWorld>(scene).Single(); result.passengerCount = world.passengers.Length;
                result.humanCount = Components<StationActorMotion>(scene).Length;
                result.trainsets = Components<StationTrain>(scene).Length; result.timetableDisplays = Components<StationTimetableDisplay>(scene).Length;
                Require(result.passengerCount == 60 && result.humanCount == 67 && result.trainsets == 4 && result.timetableDisplays == 23, "Camera work altered the saved social or rail population.");
                Require(Components<Transform>(scene).All(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) == 0), "Scene contains missing scripts.");
                result.objects = Components<Transform>(scene).Length; result.success = true;
            }
            catch (Exception error) { result.errors.Add(error.ToString()); }
            return result;
        }

        [MenuItem("Efrei Research Lab/Train Station Cameras/Check actual Play mode camera controls")]
        public static void BeginPlayCheck()
        {
            Require(Load() == null, "A camera Play check is already running.");
            var run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion, phase = "opening", deadlineTicks = DateTime.UtcNow.AddSeconds(120).Ticks };
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Finish Play/import before checking cameras.");
                CleanScenes(); var setup = EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length > 0 && setup.All(s => !string.IsNullOrEmpty(s.path)), "Save all open scenes before checking.");
                run.scenes = setup.Select(s => new SavedScene { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray();
                run.originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled; run.originalOptions = (int)EditorSettings.enterPlayModeOptions;
                run.originalPaused = EditorApplication.isPaused; run.originalTimeScale = Time.timeScale;
                run.originalRunInBackground = Application.runInBackground; run.originalTargetFrameRate = Application.targetFrameRate; run.originalVSyncCount = QualitySettings.vSyncCount;
                run.capturedSettings = true; SessionState.EraseString(LogKey); Save(run);
                EditorSettings.enterPlayModeOptionsEnabled = false; EditorSceneManager.playModeStartScene = null; EditorApplication.isPaused = false;
            }
            catch (Exception error) { run.errors.Add(error.ToString()); run.abort = true; Finish(run); }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return; nextPoll = EditorApplication.timeSinceStartup + .035;
            var run = liveRun ?? Load(); if (run == null) return;
            if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Finish(run); return; }
            if (run.abort && run.phase == "exiting") { if (!EditorApplication.isPlayingOrWillChangePlaymode) Finish(run); return; }
            try
            {
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Camera Play check timed out in " + run.phase + ".");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey, "")), "Unity logged an error during the camera check.");
                if (run.phase == "opening" && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    CleanScenes(); var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    var saved = InspectSaved(scene); Write("saved-scene-check.json", saved);
                    Require(saved.success, string.Join("\n", saved.errors));
                    run.baselineObjects = saved.objects; run.catalogue = saved.catalogue;
                    run.phase = "entering"; Save(run); EditorApplication.isPlaying = true;
                }
                else if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.checkAfterFrame)
                { liveRun = run; Initialize(run); Save(run); }
                else if (EditorApplication.isPlaying && run.phase != "entering" && run.phase != "initializing" && run.phase != "exiting")
                { Observe(run); Advance(run); Save(run); }
            }
            catch (Exception error)
            {
                run.errors.Add(error.ToString()); run.abort = true; run.phase = "exiting"; Save(run); Write("play-mode-check.json", run);
                CleanupVirtualMouse();
                if (EditorApplication.isPlayingOrWillChangePlaymode) { Time.timeScale = run.originalTimeScale; EditorApplication.isPlaying = false; }
                else Finish(run);
            }
        }

        static void Initialize(Run run)
        {
            var scene = SceneManager.GetActiveScene(); controls = Components<TrainStationViewControls>(scene).Single();
            orbit = Components<TrainStationOrbitCamera>(scene).Single(); fixedCameras = Components<StationSurveillanceCamera>(scene);
            rail = Components<StationRailOperations>(scene).Single(); displays = Components<StationTimetableDisplay>(scene);
            social = Components<StationSocialWorld>(scene).Single(); fixedPoses.Clear(); lastPassengerPositions.Clear();
            foreach (var camera in fixedCameras)
            {
                fixedPoses[camera.cameraComponent.transform] = new Pose(camera.cameraComponent.transform.position, camera.cameraComponent.transform.rotation);
                fixedPoses[camera.transform] = new Pose(camera.transform.position, camera.transform.rotation);
            }
            foreach (var passenger in social.passengers) lastPassengerPositions[passenger] = passenger.transform.position;
            run.startedAt = Time.time; run.initialRailTime = rail.ElapsedSimulationSeconds;
            Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0; Time.timeScale = 1f;
            controls.SelectView(0); run.selectedIndex = 0; run.nextFrame = Time.frameCount + 3; run.phase = "selecting saved viewpoints";
        }

        static void Observe(Run run)
        {
            run.framesObserved++; run.simulatedSeconds = Time.time - run.startedAt;
            Require(controls.viewpoints.Count(c => c.isActiveAndEnabled) == 1, "Camera switching enabled zero or multiple render cameras.");
            Require(Components<AudioListener>(SceneManager.GetActiveScene()).Count(a => a.isActiveAndEnabled) == 1, "Camera switching enabled zero or multiple audio listeners.");
            Require(controls.viewpoints[controls.selectedView].isActiveAndEnabled && Camera.main == controls.viewpoints[controls.selectedView], "Selected view and MainCamera disagree.");
            Require(controls.viewpoints.All(c => c.gameObject.activeInHierarchy), "Switching views hid a physical CCTV housing.");
            Require(orbit.enabled == (controls.selectedView == 0), "Mouse orbit remains enabled on a fixed surveillance view.");
            foreach (var item in fixedPoses)
                Require(Vector3.Distance(item.Key.position, item.Value.position) < .0001f && Quaternion.Angle(item.Key.rotation, item.Value.rotation) < .001f, "A fixed surveillance camera moved during orbit or camera selection: " + item.Key.name);
            foreach (var passenger in social.passengers)
            {
                run.totalPassengerTravel += Vector3.Distance(lastPassengerPositions[passenger], passenger.transform.position);
                lastPassengerPositions[passenger] = passenger.transform.position;
            }
            if (run.framesObserved % 5 == 0)
            {
                Require(Components<Transform>(SceneManager.GetActiveScene()).Length == run.baselineObjects, "Camera controls or catalogue created runtime scene objects."); run.objectChecks++;
            }
            Require(rail.IsReady && string.IsNullOrEmpty(rail.ConfigurationIssue), "Rail operations were interrupted by camera controls.");
            foreach (var display in displays)
            {
                if (display.clockLabel) Require(display.clockLabel.text == StationTimetableDisplay.FormatTime(rail.ClockSeconds, true), "A camera switch interrupted the station clock display.");
                for (int i = 0; i < display.rows.Length; i++)
                {
                    var row = display.rows[i]; if (row == null) continue; var value = display.GetRowValues(i);
                    Require(!row.time || row.time.text == (value.time ?? ""), "Train timetable time became stale while switching cameras.");
                    Require(!row.status || row.status.text == (value.status ?? ""), "Train timetable state became stale while switching cameras.");
                }
                run.timetableChecks++;
            }
        }

        static void Advance(Run run)
        {
            if (Time.frameCount < run.nextFrame) return;
            if (run.phase == "selecting saved viewpoints")
            {
                Require(controls.selectedView == run.selectedIndex && controls.viewLabel.text == controls.GetViewName(run.selectedIndex), "The interface does not identify its selected camera.");
                run.catalogue[run.selectedIndex].selectedInPlay = true; run.catalogue[run.selectedIndex].physicalRigStayedActive = true;
                if (++run.selectedIndex < controls.viewpoints.Length)
                { controls.SelectView(run.selectedIndex); run.nextFrame = Time.frameCount + 3; return; }
                run.allViewsPassed = true;
                controls.NextView(); Require(controls.selectedView == 0, "Next camera did not wrap from the final camera to the main view.");
                controls.PreviousView(); Require(controls.selectedView == 14, "Previous camera did not wrap to the final camera.");
                controls.SelectView(-1); Require(controls.selectedView == 14, "An invalid negative camera selection changed the current camera.");
                controls.SelectView(controls.viewpoints.Length); Require(controls.selectedView == 14, "An invalid camera selection changed the current camera.");
                run.navigationPassed = true; controls.SelectView(0);
                CheckViewportRaycasts(run, false);
                controls.ToggleCameraCatalog(); Require(controls.cameraCatalog.activeSelf, "The saved camera catalogue did not open.");
                Require(Components<Transform>(SceneManager.GetActiveScene()).Length == run.baselineObjects, "Opening the catalogue instantiated UI objects.");
                run.phase = "open catalogue"; run.nextFrame = Time.frameCount + 4;
            }
            else if (run.phase == "open catalogue")
            {
                CheckViewportRaycasts(run, true);
                Require(controls.viewButtons.All(i => i.gameObject.activeInHierarchy), "An authored camera selection button remains hidden in the open catalogue.");
                // Exercise the actual serialized UI event, including its persistent integer argument.
                controls.viewButtons[14].GetComponent<Button>().onClick.Invoke(); Require(controls.selectedView == 14, "Saved camera button did not select its advertised view.");
                Require(!controls.cameraCatalog.activeSelf, "Selecting a camera did not close its catalogue.");
                controls.ToggleCameraCatalog(); controls.ToggleCameraCatalog(); Require(!controls.cameraCatalog.activeSelf, "The camera catalogue toggle did not close the panel.");
                CheckViewportRaycasts(run, false);
                run.cataloguePassed = true; controls.SelectView(0); orbit.ResetView();
                run.phase = "orbit handlers"; run.nextFrame = Time.frameCount + 3;
            }
            else if (run.phase == "orbit handlers")
            {
                CheckOrbitHandlers(run); run.phase = "orbit retained across switching"; run.nextFrame = Time.frameCount + 3;
            }
            else if (run.phase == "orbit retained across switching")
            {
                controls.SelectView(0);
                Require(Mathf.Abs(Mathf.DeltaAngle(orbit.CurrentYaw, run.changedYaw)) < .001f && Mathf.Abs(orbit.CurrentPitch - run.changedPitch) < .001f && Mathf.Abs(orbit.CurrentDistance - run.changedDistance) < .001f && Vector3.Distance(orbit.CurrentFocusOffset, run.changedFocusOffset) < .001f, "Returning from CCTV discarded the user's main camera pose.");
                run.retainedOrbitPassed = true; controls.ResetGameView();
                CheckReset(run); run.resetPassed = true;
                BeginRawMouse(run);
            }
            else if (run.phase == "virtual mouse input") AdvanceRawMouse(run);
            else if (run.phase == "simulation continuity")
            {
                if (run.simulatedSeconds < 12f) return;
                FinalAssertions(run); CleanupVirtualMouse(); controls.SelectView(0); controls.ResetGameView();
                run.phase = "exiting"; Save(run); Time.timeScale = run.originalTimeScale; EditorApplication.isPlaying = false;
            }
        }

        static void CheckOrbitHandlers(Run run)
        {
            run.originalYaw = orbit.CurrentYaw; run.originalPitch = orbit.CurrentPitch; run.originalDistance = orbit.CurrentDistance; run.originalFocusOffset = orbit.CurrentFocusOffset;
            var initial = controls.viewpoints[0].transform.rotation;
            orbit.Orbit(new Vector2(100f, 40f)); orbit.ReapplyView();
            Require(Mathf.Abs(Mathf.DeltaAngle(run.originalYaw, orbit.CurrentYaw)) > 1f && Quaternion.Angle(initial, controls.viewpoints[0].transform.rotation) > 1f, "Orbit input did not rotate the main camera."); run.orbitPassed = true;
            orbit.Pan(new Vector2(45f, -30f)); orbit.ReapplyView();
            Require(Vector3.Distance(orbit.CurrentFocusOffset, run.originalFocusOffset) > .1f, "Pan input did not move the camera focus."); run.panPassed = true;
            orbit.Zoom(2f); orbit.ReapplyView();
            Require(orbit.CurrentDistance < run.originalDistance - .01f, "Wheel zoom input did not reduce main camera distance."); run.zoomPassed = true;
            orbit.Orbit(new Vector2(0f, 100000f)); CheckOrbitBounds();
            Require(Mathf.Min(Mathf.Abs(orbit.CurrentPitch - orbit.minimumPitch), Mathf.Abs(orbit.CurrentPitch - orbit.maximumPitch)) < .001f, "Orbit pitch did not clamp at its limit.");
            orbit.Orbit(new Vector2(0f, -100000f)); CheckOrbitBounds();
            orbit.Zoom(100000f); CheckOrbitBounds(); Require(Mathf.Abs(orbit.CurrentDistance - orbit.minimumDistance) < .001f, "Near zoom limit was not applied.");
            orbit.Zoom(-100000f); CheckOrbitBounds(); Require(Mathf.Abs(orbit.CurrentDistance - orbit.maximumDistance) < .001f, "Far zoom limit was not applied.");
            orbit.Pan(new Vector2(100000f, -100000f)); CheckOrbitBounds();
            orbit.Pan(new Vector2(-100000f, 100000f)); CheckOrbitBounds(); run.boundsPassed = true;
            orbit.ResetView(); CheckReset(run);
            CheckRoofCollision(run);
            orbit.Orbit(new Vector2(80f, 15f)); orbit.Pan(new Vector2(30f, -12f)); orbit.Zoom(1f); orbit.ReapplyView();
            run.changedYaw = orbit.CurrentYaw; run.changedPitch = orbit.CurrentPitch; run.changedDistance = orbit.CurrentDistance; run.changedFocusOffset = orbit.CurrentFocusOffset;
            controls.SelectView(3);
        }

        static void CheckOrbitBounds()
        {
            Require(Finite(orbit.CurrentYaw) && Finite(orbit.CurrentPitch) && Finite(orbit.CurrentDistance) && Finite(orbit.CurrentFocusOffset) && Finite(orbit.controlledCamera.transform.position), "Mouse input produced a non-finite camera pose.");
            Require(orbit.CurrentPitch >= orbit.minimumPitch - .001f && orbit.CurrentPitch <= orbit.maximumPitch + .001f, "Mouse orbit exceeded pitch limits.");
            Require(orbit.CurrentDistance >= orbit.minimumDistance - .001f && orbit.CurrentDistance <= orbit.maximumDistance + .001f, "Mouse zoom exceeded distance limits.");
            var focus = orbit.CurrentFocusOffset;
            Require(focus.x >= orbit.panBoundsMin.x - .001f && focus.x <= orbit.panBoundsMax.x + .001f && focus.y >= orbit.panBoundsMin.y - .001f && focus.y <= orbit.panBoundsMax.y + .001f && focus.z >= orbit.panBoundsMin.z - .001f && focus.z <= orbit.panBoundsMax.z + .001f, "Mouse pan exceeded focus bounds.");
            orbit.ReapplyView();
            Require(orbit.ActualDistance > 0f && orbit.ActualDistance <= orbit.CurrentDistance + .01f, "Collision-resolved camera distance is invalid.");
            Require(Vector3.Angle(orbit.controlledCamera.transform.forward, orbit.FocusPosition - orbit.controlledCamera.transform.position) < .1f, "Orbit camera no longer looks at its focus.");
            CheckCameraOutsideArchitecture();
        }

        static void CheckViewportRaycasts(Run run, bool catalogueOpen)
        {
            var eventSystem = EventSystem.current;
            Require(eventSystem && eventSystem.isActiveAndEnabled, "The saved interface has no active EventSystem for pointer routing.");
            Require(Screen.width > 32 && Screen.height > 32, "The Game view has no valid size for UI pointer testing.");
            Require(controls.interfaceRoot.activeInHierarchy, "UI pointer testing requires the saved interface to be visible.");
            Require(Components<GraphicRaycaster>(SceneManager.GetActiveScene()).Any(r => r.isActiveAndEnabled), "The saved Canvas has no active GraphicRaycaster.");
            Canvas.ForceUpdateCanvases();
            var positions = new List<Vector2>();
            if (catalogueOpen)
            {
                var rect = (RectTransform)controls.cameraCatalog.transform;
                var canvas = rect.GetComponentInParent<Canvas>();
                var uiCamera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                positions.Add(RectTransformUtility.WorldToScreenPoint(uiCamera, rect.TransformPoint(rect.rect.center)));
            }
            else
            {
                // Probe the central open viewport, away from the saved header, sidebars and footer.
                foreach (var point in new[] { new Vector2(.5f,.5f), new Vector2(.45f,.4f), new Vector2(.55f,.4f), new Vector2(.45f,.6f), new Vector2(.55f,.6f) })
                    positions.Add(new Vector2(Screen.width * point.x, Screen.height * point.y));
            }
            foreach (var point in positions)
            {
                var pointer = new PointerEventData(eventSystem) { position = point, pointerId = -1 };
                var hits = new List<RaycastResult>(); eventSystem.RaycastAll(pointer, hits);
                var graphics = hits.Where(h => h.gameObject && h.module is GraphicRaycaster).ToArray();
                var sample = new PointerProbeResult { phase = catalogueOpen ? "catalogue open" : "catalogue closed", screenPosition = point };
                sample.hitGraphics.AddRange(graphics.Select(h => HierarchyPath(h.gameObject.transform))); run.pointerProbes.Add(sample);
                if (catalogueOpen)
                    Require(graphics.Length > 0 && (graphics[0].gameObject == controls.cameraCatalog || graphics[0].gameObject.transform.IsChildOf(controls.cameraCatalog.transform)), "The open camera catalogue does not intercept pointer input at its centre.");
                else
                    Require(graphics.Length == 0, "A saved UI graphic intercepts the open viewport at " + point + ": " + string.Join(", ", sample.hitGraphics));
            }
            if (catalogueOpen) run.catalogueRaycastPassed = true; else run.viewportRaycastPassed = true;
        }

        static void CheckCameraOutsideArchitecture()
        {
            var camera = orbit.controlledCamera;
            var position = camera.transform.position;
            foreach (var obstacle in Physics.OverlapSphere(position, .025f, orbit.collisionMask, QueryTriggerInteraction.Ignore))
            {
                if (!obstacle || !obstacle.enabled || obstacle.GetComponentInParent<StationActorMotion>()) continue;
                var target = obstacle.transform;
                if (target == orbit.transform || target.IsChildOf(orbit.transform) || target == camera.transform || target.IsChildOf(camera.transform)) continue;
                if (orbit.ignoredCollisionRoots != null && orbit.ignoredCollisionRoots.Any(root => root && (target == root || target.IsChildOf(root)))) continue;
                // Non-convex mesh surfaces do not provide a supported solid-volume ClosestPoint query.
                // They are covered by the roof sphere-cast assertion below.
                if (obstacle is MeshCollider meshObstacle && !meshObstacle.convex) continue;
                Require(Vector3.Distance(obstacle.ClosestPoint(position), position) > .001f, "Orbit or zoom placed the camera centre inside station geometry: " + HierarchyPath(target));
            }
            if (liveRun != null) { liveRun.collisionSamples++; liveRun.architectureCollisionPassed = true; }
        }

        static void CheckRoofCollision(Run run)
        {
            Require(controls.roofGroups != null && controls.roofGroups.Any(root => root && root.activeInHierarchy && root.GetComponentsInChildren<Collider>().Any(IsRoofCollisionProxy)), "The roof collision scenario requires the authored roof to be visible.");
            orbit.ResetView();
            orbit.Orbit(new Vector2(0f, -100000f)); orbit.Zoom(-100000f); orbit.ReapplyView();
            CheckOrbitBounds();
            Require(Mathf.Abs(orbit.CurrentPitch - orbit.maximumPitch) < .001f && Mathf.Abs(orbit.CurrentDistance - orbit.maximumDistance) < .001f && orbit.CurrentFocusOffset.sqrMagnitude < .00001f, "The roof scenario did not use the maximum authored pitch and distance above the home focus.");
            var direction = (orbit.controlledCamera.transform.position - orbit.FocusPosition).normalized;
            var roofHits = Physics.SphereCastAll(orbit.FocusPosition, orbit.collisionRadius, direction, orbit.CurrentDistance, orbit.collisionMask, QueryTriggerInteraction.Ignore)
                .Where(hit => IsRoofCollisionProxy(hit.collider)).OrderBy(hit => hit.distance).ToArray();
            Require(roofHits.Length > 0, "The maximum-pitch home camera ray never intersected the visible roof collision proxy.");
            run.roofRequestedDistance = orbit.CurrentDistance; run.roofActualDistance = orbit.ActualDistance;
            run.roofHitDistance = roofHits[0].distance; run.roofHitObject = HierarchyPath(roofHits[0].collider.transform);
            Require(run.roofActualDistance < run.roofRequestedDistance - .1f && run.roofActualDistance <= run.roofHitDistance - orbit.collisionPadding + .01f, "The maximum-pitch camera passed through the visible roof instead of stopping inside the hall.");
            run.roofCollisionPassed = true;
            orbit.ResetView(); CheckReset(run);
        }

        static bool IsRoofCollisionProxy(Collider collider) => collider && collider.name == StationCameraObstacles.ProxyName;

        static string HierarchyPath(Transform value)
        {
            var names = new List<string>();
            for (var current = value; current; current = current.parent) names.Add(current.name);
            names.Reverse(); return string.Join("/", names);
        }

        static void CheckReset(Run run)
        {
            orbit.ReapplyView(); CheckOrbitBounds();
            Require(Mathf.Abs(Mathf.DeltaAngle(orbit.CurrentYaw, orbit.defaultYaw)) < .001f && Mathf.Abs(orbit.CurrentPitch - Mathf.Clamp(orbit.defaultPitch, orbit.minimumPitch, orbit.maximumPitch)) < .001f, "Reset did not restore authored main camera angles.");
            Require(Mathf.Abs(orbit.CurrentDistance - Mathf.Clamp(orbit.defaultDistance, orbit.minimumDistance, orbit.maximumDistance)) < .001f && orbit.CurrentFocusOffset.sqrMagnitude < .00001f, "Reset did not restore authored distance and focus offset.");
        }

        static void BeginRawMouse(Run run)
        {
#if ENABLE_INPUT_SYSTEM
            if (Application.isFocused)
            {
                previousMouse = Mouse.current; virtualMouse = InputSystem.AddDevice<Mouse>("TrainStation camera verification mouse"); virtualMouse.MakeCurrent();
                run.rawMouseAttempted = true; run.rawMouseInitialYaw = orbit.CurrentYaw; run.rawMouseInitialDistance = orbit.CurrentDistance;
                run.rawMouseScope = "Input System wheel event only. Right/middle drag handlers are tested through public APIs so verification does not lock or move the physical pointer.";
                QueueMouse(Vector2.zero, Vector2.zero, false); run.mouseStage = 0; run.phase = "virtual mouse input"; run.nextFrame = Time.frameCount + 3; return;
            }
            run.rawMouseSkippedReason = "Unity is not focused; public mouse handlers were exercised without changing OS focus or moving the physical pointer.";
#else
            run.rawMouseSkippedReason = "Input System is not enabled; public orbit, pan and wheel handlers were exercised.";
#endif
            run.phase = "simulation continuity";
        }

        static void AdvanceRawMouse(Run run)
        {
#if ENABLE_INPUT_SYSTEM
            if (!Application.isFocused)
            {
                run.rawMouseSkippedReason = "Unity lost focus during virtual mouse delivery."; CleanupVirtualMouse(); run.phase = "simulation continuity"; return;
            }
            if (Mouse.current != virtualMouse)
            {
                run.rawMouseSkippedReason = "An external mouse became current during verification; its input was left untouched."; CleanupVirtualMouse(); run.phase = "simulation continuity"; return;
            }
            if (run.mouseStage == 0)
            {
                // QueueStateEvent feeds MouseState directly. Normalization is performed by native input backends,
                // so synthetic events must already use the configured one-notch range.
                bool nativeWindows = InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange &&
                    (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer);
                run.rawWheelEventDelta = nativeWindows ? 120f : 1f;
                run.rawWheelExpectedDistance = Mathf.Clamp(run.rawMouseInitialDistance * Mathf.Exp(-orbit.zoomSensitivity), orbit.minimumDistance, orbit.maximumDistance);
                QueueMouse(Vector2.zero, new Vector2(0f, run.rawWheelEventDelta), false); run.mouseStage = 1;
            }
            else
            {
                run.rawWheelActualDistance = orbit.CurrentDistance;
                Require(Mathf.Abs(run.rawWheelActualDistance - run.rawWheelExpectedDistance) < .002f, "One real Input System wheel notch did not apply the configured zoom ratio. Expected " + run.rawWheelExpectedDistance + ", received " + run.rawWheelActualDistance + ".");
                run.rawMousePassed = true; CleanupVirtualMouse(); orbit.ResetView(); run.phase = "simulation continuity";
            }
            run.nextFrame = Time.frameCount + 3;
#else
            run.phase = "simulation continuity";
#endif
        }

#if ENABLE_INPUT_SYSTEM
        static void QueueMouse(Vector2 delta, Vector2 scroll, bool rightHeld)
        {
            var state = new MouseState { position = new Vector2(Screen.width * .5f, Screen.height * .5f), delta = delta, scroll = scroll }.WithButton(MouseButton.Right, rightHeld);
            InputSystem.QueueStateEvent(virtualMouse, state);
        }
#endif
        static void CleanupVirtualMouse()
        {
#if ENABLE_INPUT_SYSTEM
            if (virtualMouse != null && virtualMouse.added) InputSystem.RemoveDevice(virtualMouse);
            if (previousMouse != null && previousMouse.added) previousMouse.MakeCurrent();
            virtualMouse = null; previousMouse = null;
#endif
        }

        static void FinalAssertions(Run run)
        {
            Require(run.allViewsPassed && run.navigationPassed && run.cataloguePassed && run.orbitPassed && run.panPassed && run.zoomPassed && run.boundsPassed && run.resetPassed && run.retainedOrbitPassed && run.viewportRaycastPassed && run.catalogueRaycastPassed && run.architectureCollisionPassed && run.roofCollisionPassed, "Camera behavior checks did not all complete.");
            Require(run.catalogue.Count == 15 && run.catalogue.All(c => c.selectedInPlay && c.physicalRigStayedActive), "A saved camera was not exercised in Play mode.");
            Require(Components<Transform>(SceneManager.GetActiveScene()).Length == run.baselineObjects && run.objectChecks > 0, "The camera UI left runtime objects behind.");
            run.finalRailTime = rail.ElapsedSimulationSeconds;
            Require(run.finalRailTime > run.initialRailTime + 5d && run.totalPassengerTravel > 5d && run.timetableChecks > 23, "Camera selection stopped the social or train simulation.");
            Require(social.passengers.Length == 60 && Components<StationActorMotion>(SceneManager.GetActiveScene()).Length == 67 && rail.trains.Length == 4 && displays.Length == 23, "Camera controls altered the social or rail actors.");
            run.fixedPosesPassed = true; run.simulationPassed = true;
        }

        static void ModeChanged(PlayModeStateChange state)
        {
            var run = liveRun ?? Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.checkAfterFrame = Time.frameCount + 10; }
            else if (state == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting") { run.abort = true; run.errors.Add("Play was stopped before the camera verification completed."); run.phase = "exiting"; }
            else if (state == PlayModeStateChange.EnteredEditMode) run.phase = "restoring";
            Save(run);
        }
        static void Finish(Run run)
        {
            CleanupVirtualMouse();
            if (run.capturedSettings)
            {
                try
                {
                    Time.timeScale = run.originalTimeScale; Application.runInBackground = run.originalRunInBackground; Application.targetFrameRate = run.originalTargetFrameRate; QualitySettings.vSyncCount = run.originalVSyncCount;
                    EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                    EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.originalOptions; EditorSettings.enterPlayModeOptionsEnabled = run.originalOptionsEnabled; EditorApplication.isPaused = run.originalPaused;
                    run.restoredSettings = true; CleanScenes(); EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray()); run.restoredScenes = true;
                }
                catch (Exception error) { run.errors.Add("Restoration failed: " + error); }
            }
            var log = SessionState.GetString(LogKey, ""); if (!string.IsNullOrEmpty(log)) run.errors.Add("Unity errors:\n" + log);
            run.success = !run.abort && run.errors.Count == 0 && run.restoredSettings && run.restoredScenes && run.allViewsPassed && run.orbitPassed && run.boundsPassed && run.cataloguePassed && run.fixedPosesPassed && run.simulationPassed && run.viewportRaycastPassed && run.catalogueRaycastPassed && run.architectureCollisionPassed && run.roofCollisionPassed;
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O"); SessionState.EraseString(StateKey); SessionState.EraseString(LogKey);
            Write("play-mode-check.json", run); liveRun = null; controls = null; orbit = null; rail = null; social = null; fixedPoses.Clear(); lastPassengerPositions.Clear();
            Debug.Log("Station camera Play verification " + (run.success ? "PASSED" : "FAILED") + ". See " + ReportDirectory + "/play-mode-check.json");
        }
        static void Log(string message, string trace, LogType type)
        {
            if (Load() == null || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            var previous = SessionState.GetString(LogKey, ""); if (previous.Length < 16000) SessionState.SetString(LogKey, previous + message + "\n" + trace + "\n");
        }
        static bool HasCall(Button button, TrainStationViewControls view, string method)
        {
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                if (button.onClick.GetPersistentTarget(i) == view && button.onClick.GetPersistentMethodName(i) == method) return true;
            return false;
        }
        static bool HasSelectionCall(Button button, TrainStationViewControls view, int index)
        {
            var calls = new SerializedObject(button).FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
            if (calls == null) return false;
            for (int i = 0; i < calls.arraySize; i++)
            {
                var call = calls.GetArrayElementAtIndex(i);
                if (call.FindPropertyRelative("m_Target").objectReferenceValue == view && call.FindPropertyRelative("m_MethodName").stringValue == "SelectView" && call.FindPropertyRelative("m_Arguments.m_IntArgument").intValue == index) return true;
            }
            return false;
        }
        static Run Load() { var json = SessionState.GetString(StateKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json); }
        static void Save(Run run)
        {
            SessionState.SetString(StateKey, JsonUtility.ToJson(run));
            if (EditorApplication.timeSinceStartup >= nextProgressWrite) { nextProgressWrite = EditorApplication.timeSinceStartup + 2d; Write("play-mode-progress.json", run); }
        }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static void CleanScenes() { for (int i = 0; i < SceneManager.sceneCount; i++) Require(!SceneManager.GetSceneAt(i).isDirty, "Save or revert unsaved scenes before checking; the check never discards user edits."); }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Write(string name, object value) { Directory.CreateDirectory(ReportDirectory); File.WriteAllText(Path.Combine(ReportDirectory, name), JsonUtility.ToJson(value, true)); }
    }
}
#endif
