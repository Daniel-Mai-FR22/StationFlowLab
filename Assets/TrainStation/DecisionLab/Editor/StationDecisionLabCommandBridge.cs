#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    [InitializeOnLoad]
    public static class StationDecisionLabCommandBridge
    {
        const string Inbox = "BuildRequests/station-decision-command.txt";
        static double nextPoll;
        [Serializable] sealed class Result
        {
            public bool success, playing;
            public string command, scene, error, checkedUtc, inputProbeReport, poseProbeReport;
            public int participants, decisions, boarded;
        }
        static StationDecisionLabCommandBridge() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            if (!File.Exists(Inbox)) return;
            string command = File.ReadAllText(Inbox).Trim();
            bool probeCommand = command == "input-probe-start" || command == "input-probe-status" || command == "input-probe-stop" || command == "coach-navigation-probe";
            bool poseCommand = command == "pose-probe-start" || command == "pose-probe-status" || command == "pose-probe-stop" || command == "pose-capture";
            if (command != "status" && command != "capture" && command != "capture-game" && command != "day-capture" && command != "day-fountain-capture" && command != "day-entrances-capture" && command != "day-collection-stop-preview" && !command.StartsWith("day-ui-", StringComparison.Ordinal) && !probeCommand && !poseCommand && EditorApplication.isPlayingOrWillChangePlaymode) return;
            File.Delete(Inbox);
            var result = new Result { command = command, checkedUtc = DateTime.UtcNow.ToString("O") };
            try
            {
                switch (command)
                {
                    case "create": StationDecisionLabAuthoring.Create(); break;
                    case "create-day": StationDecisionDayAuthoring.Create(); break;
                    case "day-density-double": StationDecisionDayAuthoring.ConfigureVisibleDensity(); break;
                    case "day-wc-layout-probe": StationDecisionDayAuthoring.ProbeToiletFloor(); break;
                    case "day-fountain": StationDecisionHallFountainAuthoring.Apply(); break;
                    case "day-hall-redesign": StationDecisionHallRedesignAuthoring.Apply(); break;
                    case "day-hall-redesign-check": StationDecisionHallRedesignAuthoring.Validate(); break;
                    case "day-hall-redesign-capture": StationDecisionHallRedesignAuthoring.Capture(); break;
                    case "project-identity-check": StationFlowProjectVerification.Run(); break;
                    case "day-video-options-tests": StationDecisionVideoOptionsVerification.Run(); break;
                    case "day-capture-render-tests": StationDecisionCaptureRenderProfileVerification.Run(); break;
                    case "day-fountain-check": StationDecisionHallFountainVerification.CheckLayout(); break;
                    case "day-fountain-capture": StationDecisionHallFountainAuthoring.Capture(); break;
                    case "day-flow": StationDecisionFlowAuthoring.Apply(); break;
                    case "day-entrances-check": StationDecisionDayEntranceAuthoring.Validate(); break;
                    case "day-entrances-capture": StationDecisionDayEntranceAuthoring.Capture(); break;
                    case "day-entrance-tests": StationDecisionEntrancePlannerTests.Run(); break;
                    case "day-alighting-tests": StationDecisionAlightingPolicyTests.Run(); break;
                    case "day-mobility-tests": StationDecisionMobilityProfileTests.Run(); break;
                    case "day-randomization-tests": StationDecisionRunInitializationVerification.Run(); break;
                    case "day-collection-play-check": StationDecisionCollectionVerification.BeginPlayCheck(); break;
                    case "day-normalization-play-check": StationDecisionNormalizationVerification.Begin(); break;
                    case "day-skeleton-play-check": StationDecisionSkeletonVerification.Begin(); break;
                    case "day-pause-pose-baseline": StationDecisionPausePoseVerification.Begin("baseline"); break;
                    case "day-pause-pose-early": StationDecisionPausePoseVerification.Begin("baseline-early"); break;
                    case "day-pause-pose-check": StationDecisionPausePoseVerification.Begin("fixed-early"); break;
                    case "day-build-portable": StationDecisionPortableBuild.Build(); break;
                    case "project-close-saved": StationFlowProjectMaintenance.CloseSavedProject(); break;
                    case "project-prepare-rename": StationFlowProjectMaintenance.PrepareForRename(); break;
                    case "day-data-schema": StationDecisionDataSchemaDocumentation.Generate(); StationDecisionDataSchemaDocumentation.Validate(); break;
                    case "day-collection-preview":
                        if (EditorApplication.isPlayingOrWillChangePlaymode || SceneManager.GetActiveScene().isDirty)
                            throw new InvalidOperationException("Collection preview requires a stopped, saved scene.");
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(StationDecisionDayAuthoring.ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                        EditorApplication.isPlaying = true;
                        break;
                    case "day-collection-stop-preview":
                        if (SceneManager.GetActiveScene().path != StationDecisionDayAuthoring.ScenePath)
                            throw new InvalidOperationException("Collection preview stop is limited to the daily scene.");
                        EditorApplication.isPlaying = false;
                        break;
                    case "open-day":
                        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play before opening the daily scene.");
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(StationDecisionDayAuthoring.ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                        break;
                    case "day-rail-tests": StationDecisionDayRailTests.RunTests(); break;
                    case "day-inspector-tests": StationDecisionDayScheduleInspector.RunTests(); break;
                    case "day-check": StationDecisionDayVerification.CheckSavedScene(); break;
                    case "upgrade-day-gates": StationDecisionGateAuthoring.Apply(); break;
                    case "gate-check": StationDecisionGateAuthoring.Validate(); break;
                    case "day-play-check": StationDecisionDayVerification.BeginPlayCheck(); break;
                    case "day-ui-baseline-capture": StationDecisionCollectionUiCapture.Capture("baseline"); break;
                    case "day-ui-rates-capture": StationDecisionCollectionUiCapture.Capture("rates"); break;
                    case "day-ui-resolution-capture": StationDecisionCollectionUiCapture.Capture("resolution"); break;
                    case "day-capture": StationDecisionDayAuthoring.CaptureGame(); break;
                    case "day-labels": StationDecisionDayAuthoring.SimplifyLabels(); break;
                    case "day-entrances": StationDecisionDayAuthoring.WidenEntranceAnchors(); break;
                    case "open": StationDecisionLabAuthoring.Open(); break;
                    case "open-saved-lab":
                        StationDecisionLabAuthoring.Require(!EditorApplication.isPlayingOrWillChangePlaymode && !SceneManager.GetActiveScene().isDirty,
                            "Stop Play and save the active scene before opening the saved Lab.");
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(StationDecisionLabAuthoring.ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                        break;
                    case "reload-saved-lab":
                        StationDecisionLabAuthoring.Require(!EditorApplication.isPlayingOrWillChangePlaymode && SceneManager.GetActiveScene().path == StationDecisionLabAuthoring.ScenePath,
                            "Reload is limited to the stopped Decision Lab scene.");
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(StationDecisionLabAuthoring.ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                        break;
                    case "validate": StationDecisionLabVerification.ValidateSavedScene(); break;
                    case "upgrade-amenities": StationDecisionLabAmenitiesAuthoring.UpgradeOpenScene(); break;
                    case "amenities-authoring-check": StationDecisionLabAmenitiesAuthoring.ValidateOpenScene(); break;
                    case "amenities-check": StationDecisionLabAmenitiesVerification.ValidateSavedLayout(); break;
                    case "amenities-play-check": StationDecisionLabAmenitiesVerification.BeginPlayCheck(); break;
                    case "amenities-framing-check": StationDecisionLabAmenitiesVerification.BeginFramingCheck(); break;
                    case "navigation-trace": StationDecisionLabNavigationTrace.BeginDiagnostic(); break;
                    case "play-check": StationDecisionLabVerification.BeginPlayCheck(); break;
                    case "cancellation-check": StationDecisionLabVerification.BeginCancellationCheck(); break;
                    case "native-cancellation-check": StationDecisionLabVerification.BeginNativeSpeedCancellationCheck(); break;
                    case "upgrade-presentation": StationDecisionLabPresentation.UpgradePresentation(); break;
                    case "presentation-check": StationDecisionLabPresentationVerification.BeginPlayCheck(); break;
                    case "capture-game": StationDecisionLabPresentationVerification.CaptureGame(); break;
                    case "capture": StationDecisionLabCapture.Capture(); break;
                    case "input-probe-start": StationDecisionLabInputProbe.Start(); break;
                    case "input-probe-status": StationDecisionLabInputProbe.WriteStatus(); break;
                    case "input-probe-stop": StationDecisionLabInputProbe.Stop(); break;
                    case "coach-navigation-probe": WriteCoachNavigationProbe(); break;
                    case "pose-probe-start": StationDecisionLabPoseProbe.Start(); break;
                    case "pose-probe-status": StationDecisionLabPoseProbe.WriteStatus(); break;
                    case "pose-probe-stop": StationDecisionLabPoseProbe.Stop(); break;
                    case "pose-capture": StationDecisionLabPoseProbe.Capture(); break;
                    case "pose-check": StationDecisionLabPoseProbe.BeginDiagnostic(); break;
                    case "pose-check-saved-repair": StationDecisionLabPoseProbe.BeginDiagnostic(new StationDecisionLabPoseProbe.Options { variant = "saved-repair", exercisePausedControls = true }); break;
                    case "repair-gait": StationDecisionLabGaitRepair.ApplyOpenLabScene(); break;
                    case "pose-check-no-stabilization": StationDecisionLabPoseProbe.BeginDiagnostic(new StationDecisionLabPoseProbe.Options { variant = "no-stabilization" }); break;
                    case "pose-check-no-foot-ik": StationDecisionLabPoseProbe.BeginDiagnostic(new StationDecisionLabPoseProbe.Options { variant = "no-foot-ik" }); break;
                    case "pose-check-graph-stop": StationDecisionLabPoseProbe.BeginDiagnostic(new StationDecisionLabPoseProbe.Options { variant = "graph-stop-on-pause" }); break;
                    case "gait-comparison": StationDecisionGaitComparison.Measure(); break;
                    case "gait-matched-avatar": StationDecisionGaitComparison.CopyAndMeasureMatchedAvatar(); break;
                    case "day-navigation-fixture-check": File.WriteAllText("Reports/Navigation20261028/fixture-preflight.json", Diagnostics.StationDecisionNavigationBenchmark.ValidateCrossingFixtureLayout(UnityEngine.Object.FindObjectsByType<StationDecisionWorld>(FindObjectsSortMode.None).First(w => w.IsDaily))); break;
                    case "day-short-stride-author": StationFlow.TrainStation.Social.Editor.StationSlowGaitAuthoring.PrepareAndVerify(); break;
                    case "refresh": AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); break;
                    case "status": break;
                    default: throw new ArgumentException("Unknown Decision Lab command: " + command);
                }
                result.success = true;
                if (probeCommand && command != "coach-navigation-probe") result.inputProbeReport = StationDecisionLabInputProbe.ReportPath;
                if (poseCommand || command.StartsWith("pose-check", StringComparison.Ordinal)) result.poseProbeReport = StationDecisionLabPoseProbe.ReportPath;
            }
            catch (Exception error) { result.error = error.ToString(); Debug.LogException(error); }
            result.playing = EditorApplication.isPlaying;
            result.scene = SceneManager.GetActiveScene().path;
            var world = StationDecisionLabAuthoring.Components<StationDecisionWorld>(SceneManager.GetActiveScene()).FirstOrDefault();
            if (world && world.participants != null)
            {
                result.participants = world.participants.Length;
                result.decisions = world.DecisionCount;
                result.boarded = world.BoardingCount;
            }
            StationDecisionLabAuthoring.Write("BuildRequests/station-decision-result.json", result);
            if (command == "project-close-saved" && result.success) EditorApplication.Exit(0);
        }

        [Serializable] sealed class CoachNavigationProbe
        {
            public string checkedUtc;
            public float elapsed;
            public CoachNavigationActor[] actors;
        }
        [Serializable] sealed class CoachNavigationActor
        {
            public string journey, execution, pathStatus, navigationIssue;
            public int track, coach, queueSlot, queueCount;
            public bool hasPath, pathPending, stopped, motionHasDestination;
            public float remainingDistance;
            public Vector3 position, passengerDestination, motionDestination, nativeDestination, pathEnd, queueTarget, velocity;
        }
        static void WriteCoachNavigationProbe()
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Coach destination probe requires Play.");
            var world = StationDecisionLabAuthoring.Components<StationDecisionWorld>(SceneManager.GetActiveScene()).Single();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var report = new CoachNavigationProbe { checkedUtc = DateTime.UtcNow.ToString("O"), elapsed = world.ElapsedSeconds,
                actors = world.participants.Where(p => p && p.HasEntered && !p.Finished && !p.IsAlighter && p.CoachQueue &&
                    p.CurrentActivity == DecisionActivity.Platform).Select(p => {
                    var agent = p.motion.agent;
                    var corners = agent.path.corners;
                    return new CoachNavigationActor { journey = p.JourneyId, track = p.ArrivalPlan.trackNumber, coach = p.ArrivalPlan.coachNumber,
                        execution = p.ExecutionState, position = p.transform.position, queueTarget = p.CoachQueue.PositionFor(p),
                        queueSlot = p.CoachQueue.QueueSlotIndex(p.JourneyId), queueCount = p.CoachQueue.Count,
                        passengerDestination = (Vector3)typeof(StationDecisionPassenger).GetField("destination", flags).GetValue(p),
                        motionDestination = (Vector3)p.motion.GetType().GetField("lastDestination", flags).GetValue(p.motion),
                        motionHasDestination = (bool)p.motion.GetType().GetField("hasDestination", flags).GetValue(p.motion),
                        nativeDestination = agent.destination, pathEnd = corners.Length > 0 ? corners[corners.Length - 1] : Vector3.zero,
                        velocity = agent.velocity, hasPath = agent.hasPath, pathPending = agent.pathPending, stopped = agent.isStopped,
                        pathStatus = agent.pathStatus.ToString(), navigationIssue = p.motion.NavigationIssue,
                        remainingDistance = float.IsInfinity(agent.remainingDistance) || float.IsNaN(agent.remainingDistance) ? -1f : agent.remainingDistance };
                }).ToArray() };
            string path = "Reports/TrainStationDaily/ArrivalFIFO/coach-navigation-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json";
            StationDecisionLabAuthoring.Write(path, report);
        }
    }
}
#endif
