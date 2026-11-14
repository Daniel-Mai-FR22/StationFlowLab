#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Native JSON preference migration and the public bindings used by measured FPS observation.</summary>
    public static class StationDecisionVideoOptionsVerification
    {
        [Serializable] sealed class ResolutionCase { public int width,height,nominalAviPlaybackFps; public bool passed; }
        [Serializable] sealed class Report
        {
            public bool success,fpsSelectorAbsent,measuredTelemetryBindings;
            public string checkedUtc,error,cadenceMode="variable; real timestamps and measured game/per-camera FPS";
            public int checks;
            public List<ResolutionCase> resolutions=new List<ResolutionCase>();
        }
        public static int Run()
        {
            var report=new Report{checkedUtc=DateTime.UtcNow.ToString("O")};
            try
            {
                int[] widths={854,1280,1920},heights={480,720,1080};
                for(int size=0;size<3;++size)
                {
                    var options=new StationDecisionCaptureOptions{videoFps=17,profiles=false,skeletons=false,selectedCameraIds=new[]{"HALL-01","HALL-04"}};
                    options.SetVideoResolution(size);options.Validate();
                    string json=JsonUtility.ToJson(options);
                    var restored=JsonUtility.FromJson<StationDecisionCaptureOptions>(json);restored.Validate();restored.NormalizeVideoChoicesForUi();
                    Require(restored.videoWidth==widths[size] && restored.videoHeight==heights[size] && restored.videoFps==17,"Native preference loading changed resolution or rounded legacy nominal AVI metadata.",report);
                    Require(!restored.profiles && !restored.skeletons && restored.trajectories && restored.selectedCameraIds.Length==2 && restored.selectedCameraIds[1]=="HALL-04","Native preference loading changed unrelated selections.",report);
                    Require(!json.Contains("VideoResolutionIndex") && !json.Contains("VideoFrameRateIndex"),"Computed UI properties leaked into the field-based contract.",report);
                    report.resolutions.Add(new ResolutionCase{width=restored.videoWidth,height=restored.videoHeight,nominalAviPlaybackFps=restored.videoFps,passed=true});
                }
                var legacy=JsonUtility.FromJson<StationDecisionCaptureOptions>("{\"videoWidth\":640,\"videoHeight\":360,\"videoFps\":5,\"jpegQuality\":75,\"profiles\":false,\"selectedCameraIds\":[\"HALL-01\"]}");
                legacy.Validate();legacy.NormalizeVideoChoicesForUi();
                Require(legacy.videoWidth==854 && legacy.videoHeight==480 && legacy.videoFps==5 && !legacy.profiles && legacy.selectedCameraIds[0]=="HALL-01","Legacy migration reinstated a fixed FPS choice or lost independent choices.",report);
                var defaults=new StationDecisionCaptureOptions();defaults.Validate();Require(defaults.videoWidth==1280 && defaults.videoHeight==720,"Wrong default resolution.",report);
                Require(defaults.skeletons && defaults.Copy().skeletons,"Skeleton default/copy is missing.",report);
                var poseOnly = new StationDecisionCaptureOptions{video=false,timetable=false,profiles=false,trajectories=false,animations=false,skeletons=true,decisions=false,events=false};
                var poseCopy=JsonUtility.FromJson<StationDecisionCaptureOptions>(JsonUtility.ToJson(poseOnly));
                Require(poseCopy.skeletons&&!poseCopy.video&&!poseCopy.trajectories&&!poseCopy.animations&&!poseCopy.profiles&&!poseCopy.events,"Skeleton-only native JSON options changed unrelated flags.",report);
                // Source evidence supplements the native UI capture; actual rate mathematics has its own pure tracker fixture.
                string panel=File.ReadAllText("Assets/TrainStation/DecisionLab/Runtime/StationDecisionCollectionPanel.cs");
                report.fpsSelectorAbsent=!panel.Contains("SetVideoFrameRate(") && !panel.Contains("VideoFrameRateIndex") && !panel.Contains("FrameRateLabels") && !panel.Contains("frameRateOpen");
                Require(report.fpsSelectorAbsent,"The setup UI still contains a fixed frame-rate selector.",report);
                report.measuredTelemetryBindings=typeof(StationDecisionCaptureSession).GetProperty("GameFpsRolling1s")?.PropertyType==typeof(double) &&
                    typeof(StationDecisionCaptureSession).GetMethod("GetActualVideoRates")?.ReturnType==typeof(StationDecisionCameraFrameRate[]);
                Require(report.measuredTelemetryBindings,"Measured game/video FPS observation is not bound to the runtime session.",report);
                report.success=true;return report.checks;
            }
            catch(Exception error){report.error=error.ToString();throw;}
            finally
            {
                const string directory="Reports/TrainStationDaily/VideoOptions";Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory,"options-native.json"),JsonUtility.ToJson(report,true));
            }
        }
        static void Require(bool condition,string message,Report report){++report.checks;if(!condition)throw new InvalidOperationException(message);}
    }
}
#endif
