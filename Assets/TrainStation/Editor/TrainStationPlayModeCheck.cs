#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.SyntheticTraffic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Runs real Play sessions against saved scenes and restores the editor afterwards.</summary>
    [InitializeOnLoad]
    public static class TrainStationPlayModeCheck
    {
        const string StateKey = "StationFlow.TrainStation.PlayModeCheck.State";
        const string LogKey = "StationFlow.TrainStation.PlayModeCheck.Errors";
        const string StationPath = "Assets/TrainStation/Scenes/TrainStation.unity";
        const string JunctionPath = "Assets/StationFlowDemo/Scenes/JunctionLab.unity";
        const string ReportPath = "Reports/TrainStation/play-mode-check.json";
        const double TimeoutSeconds = 180;

        [Serializable] sealed class SavedScene { public string path; public bool isLoaded, isActive; }
        [Serializable] sealed class ComponentState
        {
            public string path;
            public bool enabled, activeSelf, activeInHierarchy;
        }
        [Serializable] sealed class Baseline
        {
            public int gameObjects;
            public ComponentState[] cameras, lights;
        }
        [Serializable] sealed class Step
        {
            public string scene, label;
            public bool success;
            public int assertions, gameObjects, cameras, lights, viewButtons, roofButtons, interfaceButtons, actors;
        }
        [Serializable] sealed class Run
        {
            public string startedUtc, completedUtc, unityVersion, phase, originalPlayScene, firstEnabledBuildScene;
            public bool success, abort, settingsCaptured, restoredSceneSetup, restoredPlaySettings;
            public bool originalEnterOptionsEnabled, originalPaused, originalRunInBackground;
            public int originalEnterOptions, stepIndex, checkAfterFrame, originalVSyncCount, originalTargetFrameRate, originalAnisotropicFiltering;
            public float originalLodBias, originalShadowDistance;
            public long deadlineTicks;
            public SavedScene[] originalScenes;
            public Baseline baseline;
            public List<Step> steps = new List<Step>();
            public List<string> errors = new List<string>();
        }

        static TrainStationPlayModeCheck()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("Efrei Research Lab/Train Station/Check Play mode isolation")]
        public static void Begin()
        {
            if (Load() != null) throw new InvalidOperationException("A TrainStation Play mode check is already running.");
            var run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion };
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("Stop Play mode before starting the TrainStation check.");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    throw new InvalidOperationException("Wait for Unity compilation/import to finish before starting the check.");
                RequireCleanScenes();
                var setup = EditorSceneManager.GetSceneManagerSetup();
                if (setup.Length == 0 || setup.Any(s => string.IsNullOrEmpty(s.path)))
                    throw new InvalidOperationException("Save every open scene before starting the check; unnamed scenes cannot be restored safely.");
                if (!File.Exists(StationPath) || !File.Exists(JunctionPath))
                    throw new FileNotFoundException("Both saved TrainStation and JunctionLab scenes are required. No scenes will be generated.");
                run.originalScenes = setup.Select(s => new SavedScene { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray();
                run.originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalEnterOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
                run.originalEnterOptions = (int)EditorSettings.enterPlayModeOptions;
                run.originalPaused = EditorApplication.isPaused;
                run.originalVSyncCount = QualitySettings.vSyncCount;
                run.originalTargetFrameRate = Application.targetFrameRate;
                run.originalRunInBackground = Application.runInBackground;
                run.originalAnisotropicFiltering = (int)QualitySettings.anisotropicFiltering;
                run.originalLodBias = QualitySettings.lodBias;
                run.originalShadowDistance = QualitySettings.shadowDistance;
                run.firstEnabledBuildScene = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled)?.path ?? "";
                run.settingsCaptured = true;
                run.phase = "between";
                SetDeadline(run);
                SessionState.EraseString(LogKey);
                Save(run);
                // Always exercise actual scene and domain reloads, regardless of the user's fast-Play options.
                EditorSceneManager.playModeStartScene = null;
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorApplication.isPaused = false;
            }
            catch (Exception error)
            {
                run.errors.Add(error.Message);
                run.abort = true;
                Finish(run);
            }
        }

        static Run Load()
        {
            string json = SessionState.GetString(StateKey, "");
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json);
        }
        static void Save(Run run) => SessionState.SetString(StateKey, JsonUtility.ToJson(run));
        static void SetDeadline(Run run) => run.deadlineTicks = DateTime.UtcNow.AddSeconds(TimeoutSeconds).Ticks;
        static void OnLog(string condition, string trace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (Load() == null) return;
            string prior = SessionState.GetString(LogKey, "");
            if (prior.Length < 24000) SessionState.SetString(LogKey, prior + condition + "\n" + trace + "\n");
        }

        static void Update()
        {
            var run = Load();
            if (run == null) return;
            try
            {
                if (DateTime.UtcNow.Ticks > run.deadlineTicks)
                    throw new TimeoutException("Timed out during " + run.phase + " (step " + (run.stepIndex + 1) + ").");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (run.phase == "between")
                {
                    if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                    if (run.abort || run.stepIndex >= 3) { Finish(run); return; }
                    BeginStep(run);
                }
                else if (run.phase == "playing" && EditorApplication.isPlaying && Time.frameCount >= run.checkAfterFrame)
                {
                    string loggedErrors = SessionState.GetString(LogKey, "");
                    if (!string.IsNullOrEmpty(loggedErrors)) throw new InvalidOperationException("Unity logged errors:\n" + loggedErrors);
                    CheckStep(run);
                    run.stepIndex++;
                    run.phase = "exiting";
                    SetDeadline(run);
                    Save(run);
                    EditorApplication.isPlaying = false;
                }
                else if (run.phase == "exiting" && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    run.phase = "between";
                    Save(run);
                }
            }
            catch (Exception error) { Abort(run, error); }
        }

        static void BeginStep(Run run)
        {
            RequireCleanScenes();
            string path = run.stepIndex == 1 ? JunctionPath : StationPath;
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            run.baseline = CaptureBaseline(scene);
            run.phase = "entering";
            SetDeadline(run);
            Save(run);
            EditorApplication.isPlaying = true;
        }

        static void PlayModeChanged(PlayModeStateChange change)
        {
            var run = Load();
            if (run == null) return;
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                run.phase = "playing";
                run.checkAfterFrame = Time.frameCount + 12;
                SetDeadline(run);
            }
            else if (change == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting")
            {
                run.errors.Add("Play mode was stopped before the check completed.");
                run.abort = true;
                run.phase = "exiting";
                SetDeadline(run);
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                run.phase = "between";
                SetDeadline(run);
            }
            Save(run);
        }

        static void CheckStep(Run run)
        {
            var scene = SceneManager.GetActiveScene();
            var objects = Objects(scene);
            var step = new Step
            {
                scene = scene.path,
                label = run.stepIndex == 1 ? "JunctionLab compatibility" : run.stepIndex == 0 ? "TrainStation startup" : "TrainStation after JunctionLab",
                gameObjects = objects.Length,
                cameras = Components<Camera>(scene).Length,
                lights = Components<Light>(scene).Length
            };
            run.steps.Add(step);
            try
            {
                string expectedPath = run.stepIndex == 1 ? JunctionPath : StationPath;
                Require(scene.path == expectedPath, "Unexpected active scene: " + scene.path, step);
                Require(SceneManager.sceneCount == 1, "Unexpected additional loaded scene.", step);
                if (run.stepIndex == 1) CheckJunction(scene, step);
                else CheckStation(scene, run.baseline, step);
                step.success = true;
            }
            finally { Save(run); }
        }

        static void CheckStation(Scene scene, Baseline baseline, Step step)
        {
            Require(Components<DemoController>(scene).Length == 0, "TrainStation contains a traffic DemoController.", step);
            Require(Components<CameraRig>(scene).Length == 0, "TrainStation contains a traffic sensor rig.", step);
            Require(!Objects(scene).Any(t => t.name == "Tracked actors" || t.name == "Synchronized sensor rig" || t.name.StartsWith("EUROPEAN QUARTER", StringComparison.Ordinal)),
                "Junction runtime objects were injected into TrainStation.", step);
            Require(step.gameObjects == baseline.gameObjects, "Saved object count changed at Play startup: " + baseline.gameObjects + " -> " + step.gameObjects, step);
            CompareStates("camera", baseline.cameras, Components<Camera>(scene).Select(State).ToArray(), step);
            CompareStates("light", baseline.lights, Components<Light>(scene).Select(State).ToArray(), step);
            var allControls = Components<TrainStationViewControls>(scene);
            Require(allControls.Length == 1, "Expected one saved TrainStation presentation controller.", step);
            var controls = allControls[0];
            Require(controls.interfaceRoot != null && controls.interfaceRoot.GetComponent<Canvas>() != null, "Saved UI Canvas is missing.", step);
            Require(controls.viewpoints != null && controls.viewpoints.Length == step.cameras && controls.viewpoints.All(c => c != null), "All saved camera references are required.", step);
            Require(controls.viewButtons != null && controls.viewButtons.Length == controls.viewpoints.Length && controls.viewButtons.All(b => b != null), "Each saved camera requires a serialized view button.", step);
            Require(controls.roofGroups != null && controls.roofGroups.Length > 0 && controls.roofGroups.All(g => g != null), "Saved roof references are missing.", step);
            int previousView = controls.selectedView;
            bool previousInterface = controls.interfaceRoot.activeSelf;
            var roofStates = controls.roofGroups.Select(g => g.activeSelf).ToArray();
            try
            {
                for (int i = 0; i < controls.viewButtons.Length; i++)
                {
                    var button = controls.viewButtons[i].GetComponent<Button>();
                    Require(button != null && button.onClick.GetPersistentEventCount() > 0, "View button " + i + " has no saved callback.", step);
                    button.onClick.Invoke();
                    Require(controls.selectedView == i && controls.viewpoints[i].isActiveAndEnabled && controls.viewpoints.Count(c => c.isActiveAndEnabled) == 1,
                        "View button " + i + " did not select exactly its saved camera.", step);
                    step.viewButtons++;
                }
                var buttons = controls.interfaceRoot.GetComponentsInChildren<Button>(true);
                var roofButton = FindButton(buttons, controls, nameof(TrainStationViewControls.ToggleRoofs));
                Require(roofButton != null, "No serialized roof button callback found.", step);
                bool anyRoofVisible = controls.roofGroups.Any(g => g.activeSelf);
                roofButton.onClick.Invoke();
                Require(controls.roofGroups.All(g => g.activeSelf == !anyRoofVisible), "Roof button did not toggle all saved roof groups.", step);
                roofButton.onClick.Invoke();
                Require(controls.roofGroups.All(g => g.activeSelf == anyRoofVisible), "Roof button did not toggle back.", step);
                step.roofButtons++;
                var interfaceButton = FindButton(buttons, controls, nameof(TrainStationViewControls.ToggleInterface));
                Require(interfaceButton != null, "No serialized interface button callback found.", step);
                interfaceButton.onClick.Invoke();
                Require(controls.interfaceRoot.activeSelf != previousInterface, "Interface button did not toggle Canvas visibility.", step);
                interfaceButton.onClick.Invoke();
                Require(controls.interfaceRoot.activeSelf == previousInterface, "Interface button did not restore Canvas visibility.", step);
                step.interfaceButtons++;
                Require(Objects(scene).Length == baseline.gameObjects, "UI controls created or removed scene objects.", step);
            }
            finally
            {
                controls.SelectView(previousView);
                controls.interfaceRoot.SetActive(previousInterface);
                for (int i = 0; i < roofStates.Length; i++) controls.roofGroups[i].SetActive(roofStates[i]);
            }
        }

        static Button FindButton(Button[] buttons, TrainStationViewControls controls, string method)
        {
            return buttons.FirstOrDefault(b => Enumerable.Range(0, b.onClick.GetPersistentEventCount())
                .Any(i => b.onClick.GetPersistentTarget(i) == controls && b.onClick.GetPersistentMethodName(i) == method));
        }

        static void CheckJunction(Scene scene, Step step)
        {
            var controllers = Components<DemoController>(scene);
            Require(controllers.Length == 1, "JunctionLab must retain exactly one saved DemoController.", step);
            var controller = controllers[0];
            Require(controller.Simulation != null, "JunctionLab simulation did not initialize.", step);
            Require(controller.Rig != null && controller.Rig.OverviewCamera != null && controller.Rig.OverviewCamera.enabled, "JunctionLab camera rig did not initialize.", step);
            var actors = Objects(scene).SingleOrDefault(t => t.name == "Tracked actors");
            Require(actors != null && actors.childCount > 0, "JunctionLab did not create its existing actors.", step);
            step.actors = actors.childCount;
            Require(step.actors == controller.Simulation.Agents.Count, "JunctionLab actor count differs from its simulation.", step);
            Require(Components<TrainStationViewControls>(scene).Length == 0, "TrainStation leaked into JunctionLab.", step);
        }

        static Transform[] Objects(Scene scene) => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).ToArray();
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static Baseline CaptureBaseline(Scene scene) => new Baseline
        {
            gameObjects = Objects(scene).Length,
            cameras = Components<Camera>(scene).Select(State).ToArray(),
            lights = Components<Light>(scene).Select(State).ToArray()
        };
        static ComponentState State(Behaviour component)
        {
            var parts = new List<string>();
            for (var t = component.transform; t != null; t = t.parent) parts.Add(t.GetSiblingIndex() + ":" + t.name);
            parts.Reverse();
            return new ComponentState { path = string.Join("/", parts), enabled = component.enabled,
                activeSelf = component.gameObject.activeSelf, activeInHierarchy = component.gameObject.activeInHierarchy };
        }
        static void CompareStates(string label, ComponentState[] before, ComponentState[] after, Step step)
        {
            Require(before.Length == after.Length, "Saved " + label + " count changed at startup.", step);
            for (int i = 0; i < before.Length; i++)
                Require(before[i].path == after[i].path && before[i].enabled == after[i].enabled && before[i].activeSelf == after[i].activeSelf && before[i].activeInHierarchy == after[i].activeInHierarchy,
                    "Saved " + label + " state changed at startup: " + before[i].path, step);
        }
        static void Require(bool condition, string message, Step step)
        {
            step.assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        static void RequireCleanScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty) throw new InvalidOperationException("Unsaved scene changes: " + scene.name + ". Save or revert them before running the check. No scene was discarded or saved automatically.");
            }
        }

        static void Abort(Run run, Exception error)
        {
            run.errors.Add(error.ToString());
            run.abort = true;
            run.phase = "exiting";
            SetDeadline(run);
            Save(run);
            // Write the failure now as well, so a stalled editor transition still leaves diagnostics.
            WriteReport(run);
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false;
            else Finish(run);
        }

        static void Finish(Run run)
        {
            if (run.settingsCaptured)
            {
                try
                {
                    EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                    EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.originalEnterOptions;
                    EditorSettings.enterPlayModeOptionsEnabled = run.originalEnterOptionsEnabled;
                    EditorApplication.isPaused = run.originalPaused;
                    QualitySettings.vSyncCount = run.originalVSyncCount;
                    Application.targetFrameRate = run.originalTargetFrameRate;
                    Application.runInBackground = run.originalRunInBackground;
                    QualitySettings.anisotropicFiltering = (AnisotropicFiltering)run.originalAnisotropicFiltering;
                    QualitySettings.lodBias = run.originalLodBias;
                    QualitySettings.shadowDistance = run.originalShadowDistance;
                    run.restoredPlaySettings = true;
                }
                catch (Exception error) { run.errors.Add("Could not restore Play settings: " + error); }
                try
                {
                    RequireCleanScenes();
                    EditorSceneManager.RestoreSceneManagerSetup(run.originalScenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray());
                    run.restoredSceneSetup = true;
                }
                catch (Exception error) { run.errors.Add("Could not restore scene setup: " + error); }
            }
            string loggedErrors = SessionState.GetString(LogKey, "");
            if (!string.IsNullOrEmpty(loggedErrors) && !run.errors.Any(e => e.Contains(loggedErrors))) run.errors.Add("Unity logged errors:\n" + loggedErrors);
            run.success = !run.abort && run.errors.Count == 0 && run.steps.Count == 3 && run.steps.All(s => s.success) && run.restoredSceneSetup && run.restoredPlaySettings;
            run.phase = "complete";
            run.completedUtc = DateTime.UtcNow.ToString("O");
            SessionState.EraseString(StateKey);
            SessionState.EraseString(LogKey);
            WriteReport(run);
            if (run.success) Debug.Log("TrainStation Play mode check passed: station -> junction -> station. Editor scene setup and Play settings restored. " + ReportPath);
            else Debug.LogError("TrainStation Play mode check failed: " + string.Join("\n", run.errors) + "\n" + ReportPath);
        }
        static void WriteReport(Run run)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonUtility.ToJson(run, true));
        }
    }
}
#endif
