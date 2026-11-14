#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Bounded read-only observation of native path estimates and the Lab progress timer.</summary>
    [InitializeOnLoad]
    public static class StationDecisionLabNavigationTrace
    {
        const string Key = "StationFlow.DecisionLab.NavigationTrace";
        const string Output = "Reports/TrainStationDecisionLab";
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo LastProgress = typeof(StationDecisionPassenger).GetField("lastProgressAt", PrivateInstance);
        static readonly FieldInfo PreviousRemaining = typeof(StationDecisionPassenger).GetField("previousRemaining", PrivateInstance);
        static readonly FieldInfo StateStarted = typeof(StationDecisionPassenger).GetField("stateStarted", PrivateInstance);
        static readonly FieldInfo SeatVisit = typeof(StationDecisionPassenger).GetField("seatVisit", PrivateInstance);
        static readonly FieldInfo SeatProgress = typeof(StationDecisionSeatVisit).GetField("lastProgressAt", PrivateInstance);
        static readonly FieldInfo SeatRemaining = typeof(StationDecisionSeatVisit).GetField("previousRemaining", PrivateInstance);
        static readonly FieldInfo SeatStarted = typeof(StationDecisionSeatVisit).GetField("stateStarted", PrivateInstance);
        static Run run;
        static StationDecisionWorld world;
        static StreamWriter writer;
        static double nextPoll, nextWrite;
        static bool stopRequested;
        static readonly Dictionary<string, Watch> watches = new Dictionary<string, Watch>();
        sealed class BeforeDecisionScripts { }
        sealed class AfterDecisionAnimation { }

        [Serializable] public sealed class FileEvidence { public string path, before, after; }
        [Serializable] public sealed class Run
        {
            public string startedUtc, completedUtc, phase, archivePath, samplePath, outputDirectory, oldPlayScene;
            public bool success, completionReached, filesPreserved, beforeUpdateHook, postAnimationHook;
            public bool oldOptionsEnabled, oldPaused, oldBackground;
            public int oldOptions, oldFps, oldVsync, initializeFrame, samples, nonFiniteRemainingSamples, zeroRemainingWithLongCorners;
            public float oldScale, startedSimulation, elapsedSimulation, durationSeconds = 70f;
            public long deadlineTicks;
            public List<FileEvidence> files = new List<FileEvidence>();
            public List<string> errors = new List<string>(), warnings = new List<string>();
            public string scope = "Read-only native NavMesh diagnostics. Saved Lab defaults and all actors/events remain unchanged; simulation runs at1x for70sim. Before-script and post-animation observations run5Hz plus every change of state, target, timer baseline or remaining-distance validity. No actor teleport, destination replacement, path recalculation, progress fix or fixture needs override. Non-finite values are stored as invariant raw strings plus validity flags; numeric placeholders are0.";
        }
        [Serializable] public sealed class Sample
        {
            public string phase, passengerId, state, activity, target, issue, motionIssue, progressOwner;
            public string remainingRaw, previousRemainingRaw, pathStatus;
            public int frame, cornerCount;
            public float simulation, unityTime, timeScale, remaining, previousRemaining, lastProgressAt, noProgressSeconds, stateStarted;
            public float cornerLength, cornerEndToDestination, stoppingDistance, speed;
            public bool active, finished, agentEnabled, onNavMesh, hasPath, pathPending, pathStale, isStopped, onOffMeshLink;
            public bool remainingFinite, previousRemainingFinite, cornerLengthFinite, arrived, navigationFailed;
            public Vector3 position, velocity, desiredVelocity, destination, nextPosition, steeringTarget;
            public Vector3[] corners;
        }
        sealed class Watch
        {
            public string state, target, remainingCategory;
            public float nextSample, baseline = float.NaN, progress = float.NaN;
        }

        static StationDecisionLabNavigationTrace()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Trace native navigation progress (70 seconds)")]
        public static void BeginDiagnostic()
        {
            RequireEditAndClean();
            Require(string.IsNullOrEmpty(SessionState.GetString(Key, "")), "Navigation trace already active.");
            Require(LastProgress != null && PreviousRemaining != null && StateStarted != null && SeatVisit != null &&
                SeatProgress != null && SeatRemaining != null && SeatStarted != null, "Progress fields changed; update read-only trace reflection.");
            string prefix = Output + "/navigation-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
            run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), phase = "entering", archivePath = prefix + ".json",
                samplePath = prefix + ".jsonl", deadlineTicks = DateTime.UtcNow.AddMinutes(4).Ticks,
                oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled, oldOptions = (int)EditorSettings.enterPlayModeOptions,
                oldPaused = EditorApplication.isPaused, oldBackground = Application.runInBackground,
                oldFps = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount, oldScale = Time.timeScale,
                oldPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene), files = SnapshotFiles() };
            Save(); WriteReport();
            EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
        }

        static void Initialize()
        {
            var scene = SceneManager.GetActiveScene(); Require(scene.path == ScenePath, "Wrong navigation trace scene.");
            world = Components<StationDecisionWorld>(scene).Single();
            Require(world.rail && world.rail.IsReady, "Railway clock not initialized.");
            Time.timeScale = 1f; Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0;
            Require(!world.Paused, "Unexpected paused world at diagnostic start.");
            run.startedSimulation = world.ElapsedSeconds; run.outputDirectory = world.OutputDirectory;
            writer = new StreamWriter(run.samplePath, false, new UTF8Encoding(false));
            watches.Clear(); stopRequested = false;
            var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveHooks(ref loop);
            run.beforeUpdateHook = InsertBeforeScripts(ref loop);
            run.postAnimationHook = AppendAfterAnimation(ref loop);
            Require(run.beforeUpdateHook && run.postAnimationHook, "Could not install read-only navigation observation hooks.");
            PlayerLoop.SetPlayerLoop(loop);
            run.phase = "running"; nextWrite = EditorApplication.timeSinceStartup + 1d; Save();
        }

        static void Observe(string phase)
        {
            if (run == null || run.phase != "running" || stopRequested || !world) return;
            try
            {
                float now = world.ElapsedSeconds;
                run.elapsedSimulation = now - run.startedSimulation;
                Require(Mathf.Abs(Time.timeScale - 1f) < .0001f && !world.Paused, "Diagnostic expected uninterrupted native1x simulation.");
                foreach (var person in world.participants)
                {
                    if (!person || !person.motion) continue;
                    var agent = person.motion.agent;
                    bool native = agent && agent.enabled && agent.isOnNavMesh;
                    object visit = SeatVisit.GetValue(person);
                    bool seatTravel = visit != null && person.ExecutionState == "WalkingToSeat";
                    float baseline = (float)(seatTravel ? SeatRemaining.GetValue(visit) : PreviousRemaining.GetValue(person));
                    float progress = (float)(seatTravel ? SeatProgress.GetValue(visit) : LastProgress.GetValue(person));
                    float raw = native ? agent.remainingDistance : float.NaN;
                    string category = !native ? "inactive" : !Finite(raw) ? Raw(raw) : raw <= .001f ? "zero" : "positive";
                    string key = phase + "/" + person.passengerId;
                    if (!watches.TryGetValue(key, out var watch)) { watch = new Watch(); watches.Add(key, watch); }
                    bool changed = watch.state != person.ExecutionState || watch.target != person.CurrentTargetId ||
                        watch.remainingCategory != category || !watch.baseline.Equals(baseline) || !watch.progress.Equals(progress);
                    if (!changed && now < watch.nextSample) continue;
                    watch.state = person.ExecutionState; watch.target = person.CurrentTargetId; watch.remainingCategory = category;
                    watch.baseline = baseline; watch.progress = progress; watch.nextSample = now + .2f;
                    var sample = new Sample { phase = phase, passengerId = person.passengerId, frame = Time.frameCount,
                        simulation = now, unityTime = Time.time, timeScale = Time.timeScale, position = person.transform.position,
                        state = person.ExecutionState, activity = person.CurrentActivity.ToString(), target = person.CurrentTargetId,
                        issue = person.NavigationIssue, motionIssue = person.motion.NavigationIssue,
                        progressOwner = seatTravel ? "seatVisit" : "participant", lastProgressAt = progress, noProgressSeconds = now - progress,
                        stateStarted = (float)(seatTravel ? SeatStarted.GetValue(visit) : StateStarted.GetValue(person)),
                        remainingRaw = Raw(raw), remainingFinite = Finite(raw), remaining = Finite(raw) ? raw : 0f,
                        previousRemainingRaw = Raw(baseline), previousRemainingFinite = Finite(baseline), previousRemaining = Finite(baseline) ? baseline : 0f,
                        active = person.gameObject.activeInHierarchy, finished = person.Finished, agentEnabled = agent && agent.enabled,
                        onNavMesh = native, arrived = person.motion.HasArrived, navigationFailed = person.motion.NavigationFailed };
                    if (native)
                    {
                        sample.hasPath = agent.hasPath; sample.pathPending = agent.pathPending; sample.pathStale = agent.isPathStale;
                        sample.pathStatus = agent.pathStatus.ToString(); sample.destination = agent.destination;
                        sample.velocity = agent.velocity; sample.desiredVelocity = agent.desiredVelocity; sample.nextPosition = agent.nextPosition;
                        sample.steeringTarget = agent.steeringTarget; sample.isStopped = agent.isStopped; sample.onOffMeshLink = agent.isOnOffMeshLink;
                        sample.stoppingDistance = agent.stoppingDistance; sample.speed = agent.speed;
                        sample.corners = agent.path.corners; sample.cornerCount = sample.corners.Length;
                        Vector3 previous = person.transform.position; float length = 0f;
                        foreach (var corner in sample.corners) { length += Vector3.Distance(previous, corner); previous = corner; }
                        sample.cornerLengthFinite = Finite(length); sample.cornerLength = Finite(length) ? length : 0f;
                        sample.cornerEndToDestination = sample.cornerCount > 0 && Finite(sample.destination) ?
                            Vector3.Distance(previous, sample.destination) : 0f;
                        if (!sample.remainingFinite) ++run.nonFiniteRemainingSamples;
                        if (sample.remainingFinite && raw < .001f && length > 2f && !agent.pathPending) ++run.zeroRemainingWithLongCorners;
                    }
                    writer.WriteLine(JsonUtility.ToJson(sample)); ++run.samples;
                }
                if (run.elapsedSimulation >= run.durationSeconds) { run.completionReached = true; stopRequested = true; }
            }
            catch (Exception e) { Error(e.ToString()); stopRequested = true; }
        }

        static void BeforeScripts() { Observe("beforeScriptUpdate"); }
        static void AfterAnimation() { Observe("afterAnimation"); }
        static bool InsertBeforeScripts(ref PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.Update))
            {
                var children = (loop.subSystemList ?? Array.Empty<PlayerLoopSystem>()).ToList();
                int index = children.FindIndex(c => c.type == typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate));
                if (index < 0) return false;
                children.Insert(index, new PlayerLoopSystem { type = typeof(BeforeDecisionScripts), updateDelegate = BeforeScripts });
                loop.subSystemList = children.ToArray(); return true;
            }
            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; ++i)
            { var child = loop.subSystemList[i]; if (InsertBeforeScripts(ref child)) { loop.subSystemList[i] = child; return true; } }
            return false;
        }
        static bool AppendAfterAnimation(ref PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.PreLateUpdate))
            {
                var children = (loop.subSystemList ?? Array.Empty<PlayerLoopSystem>()).ToList();
                children.Add(new PlayerLoopSystem { type = typeof(AfterDecisionAnimation), updateDelegate = AfterAnimation });
                loop.subSystemList = children.ToArray(); return true;
            }
            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; ++i)
            { var child = loop.subSystemList[i]; if (AppendAfterAnimation(ref child)) { loop.subSystemList[i] = child; return true; } }
            return false;
        }
        static void RemoveHooks(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return;
            var children = new List<PlayerLoopSystem>();
            foreach (var original in loop.subSystemList)
            {
                if (original.type == typeof(BeforeDecisionScripts) || original.type == typeof(AfterDecisionAnimation)) continue;
                var child = original; RemoveHooks(ref child); children.Add(child);
            }
            loop.subSystemList = children.ToArray();
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return; nextPoll = EditorApplication.timeSinceStartup + .05d;
            Load(); if (run == null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Restore(); return; }
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Navigation trace wall-clock timeout.");
                if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.initializeFrame) Initialize();
                else if (run.phase == "running" && EditorApplication.isPlaying)
                {
                    Require(world && writer != null, "Reload interrupted native navigation trace; partial evidence retained.");
                    if (stopRequested)
                    { world.FlushLogs(); CloseWriter(); run.phase = "exiting"; Save(); WriteReport(); EditorApplication.isPlaying = false; }
                }
                if (EditorApplication.timeSinceStartup >= nextWrite)
                { nextWrite = EditorApplication.timeSinceStartup + 1d; writer?.Flush(); Save(); WriteReport(); }
            }
            catch (Exception e)
            {
                Error(e.ToString()); CloseWriter(); run.phase = "exiting"; Save(); WriteReport();
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false; else Restore();
            }
        }
        static void ModeChanged(PlayModeStateChange change)
        {
            Load(); if (run == null) return;
            if (change == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.initializeFrame = Time.frameCount + 2; Save(); }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveHooks(ref loop); PlayerLoop.SetPlayerLoop(loop); CloseWriter();
                if (!run.completionReached) Error("Play stopped before70sim diagnostic completed.");
                run.phase = "exiting"; Save();
            }
            else if (change == PlayModeStateChange.EnteredEditMode) { run.phase = "restoring"; Save(); }
        }
        static void Restore()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveHooks(ref loop); PlayerLoop.SetPlayerLoop(loop); CloseWriter();
            EditorSettings.enterPlayModeOptionsEnabled = run.oldOptionsEnabled; EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.oldOptions;
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.oldPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.oldPlayScene);
            EditorApplication.isPaused = run.oldPaused; Application.runInBackground = run.oldBackground;
            Application.targetFrameRate = run.oldFps; QualitySettings.vSyncCount = run.oldVsync; Time.timeScale = run.oldScale;
            run.filesPreserved = true;
            foreach (var file in run.files) { file.after = File.Exists(file.path) ? Hash(file.path) : "missing"; run.filesPreserved &= file.before == file.after; }
            if (!run.filesPreserved) Error("Saved scene/schedule/navigation files changed during diagnostic.");
            if (!run.completionReached || run.samples == 0) Error("Diagnostic did not reach observed completion.");
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O"); run.success = run.errors.Count == 0;
            WriteReport(); SessionState.EraseString(Key); run = null; world = null; watches.Clear();
        }
        static void OnLog(string message, string stack, LogType type)
        {
            if (run == null) return;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Error(message);
            else if (type == LogType.Warning && run.warnings.Count < 100) run.warnings.Add(message);
        }
        static void CloseWriter() { writer?.Flush(); writer?.Dispose(); writer = null; }
        static void Error(string message) { if (run != null && run.errors.Count < 100 && !run.errors.Contains(message)) run.errors.Add(message); }
        static void Save() { if (run != null) SessionState.SetString(Key, JsonUtility.ToJson(run)); }
        static void Load() { if (run == null) { string json = SessionState.GetString(Key, ""); if (!string.IsNullOrEmpty(json)) run = JsonUtility.FromJson<Run>(json); } }
        static void WriteReport() { if (run != null) { Write(Output + "/navigation-trace.json", run); Write(run.archivePath, run); } }
        static List<FileEvidence> SnapshotFiles()
        {
            var paths = new HashSet<string> { SourceScene, SourceSchedule, ScenePath, SchedulePath };
            foreach (string guid in AssetDatabase.FindAssets("t:NavMeshData", new[] { "Assets/TrainStation" })) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            return paths.Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal).Select(p => new FileEvidence { path = p, before = Hash(p) }).ToList();
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        static string Raw(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
#endif
