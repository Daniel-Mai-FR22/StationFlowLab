using System;
using System.Text.Json;
using StationFlow.TrainStation.DecisionLab;

static class Program
{
    static int checks;
    static void Check(bool value,string message){++checks;if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,message);}
    static void Main()
    {
        var format=new JsonSerializerOptions{IncludeFields=true,IgnoreReadOnlyProperties=true};
        int[] widths={854,1280,1920},heights={480,720,1080};
        for(int size=0;size<3;++size)
        {
            var source=new StationDecisionCaptureOptions{videoFps=17,profiles=false,events=false,skeletons=false,selectedCameraIds=new[]{"HALL-01","HALL-04"}};
            source.SetVideoResolution(size);source.Validate();
            Check(source.videoWidth==widths[size] && source.videoHeight==heights[size],"A supported resolution changed dimensions.");
            Check(source.videoFps==17,"Choosing resolution changed the legacy nominal AVI playback field.");
            var copy=source.Copy();copy.selectedCameraIds[0]="EXT-01";
            Check(source.selectedCameraIds[0]=="HALL-01","Copy shared its camera selection array.");
            var restored=JsonSerializer.Deserialize<StationDecisionCaptureOptions>(JsonSerializer.Serialize(source,format),format);
            restored.Validate();restored.NormalizeVideoChoicesForUi();
            Check(restored.videoWidth==widths[size] && restored.videoHeight==heights[size] && restored.videoFps==17,"Resolution preference loading reintroduced a fixed FPS choice.");
            Check(!restored.profiles && !restored.events && !restored.skeletons && restored.trajectories && restored.animations && restored.selectedCameraIds.Length==2 && restored.selectedCameraIds[1]=="HALL-04","Preference loading changed unrelated data/camera choices.");
        }
        var legacy=new StationDecisionCaptureOptions{videoWidth=640,videoHeight=360,videoFps=5,video=false,profiles=false,selectedCameraIds=new[]{"EXT-01"}};
        legacy.Validate();legacy.NormalizeVideoChoicesForUi();
        Check(legacy.videoWidth==854 && legacy.videoHeight==480,"Saved resolution did not migrate to a supported size.");
        Check(legacy.videoFps==5 && !legacy.video && !legacy.profiles && legacy.selectedCameraIds[0]=="EXT-01","Legacy preferences were rewritten as a fixed capture FPS choice or lost unrelated choices.");
        var unusual=new StationDecisionCaptureOptions{videoFps=49};unusual.NormalizeVideoChoicesForUi();
        Check(unusual.videoFps==49,"Resolution normalization rounded nominal playback metadata to a former FPS dropdown value.");
        var defaults=new StationDecisionCaptureOptions();defaults.Validate();
        Check(defaults.videoWidth==1280 && defaults.videoHeight==720,"Default resolution is not720p.");
        Check(defaults.skeletons && defaults.Copy().skeletons,"Skeleton capture default or Copy is not independently retained.");
        var skeletonOnly=new StationDecisionCaptureOptions{video=false,timetable=false,profiles=false,trajectories=false,animations=false,skeletons=true,decisions=false,events=false};
        var skeletonCopy=skeletonOnly.Copy();Check(skeletonCopy.skeletons&&!skeletonCopy.trajectories&&!skeletonCopy.animations&&!skeletonCopy.profiles&&!skeletonCopy.video,"Skeleton-only choices changed unrelated flags.");
        skeletonOnly.skeletons=false;Check(skeletonCopy.skeletons,"Skeleton Copy shared mutable options.");
        Check(defaults.videoFps==30,"The backward-compatible AVI nominal rate changed unexpectedly.");
        var custom=new StationDecisionCaptureOptions{videoWidth=320,videoHeight=180,videoFps=5};custom.Validate();
        Check(custom.videoWidth==320 && custom.videoHeight==180 && custom.videoFps==5,"Validation silently changed compatible programmatic metadata.");
        Check(new StationDecisionCaptureOptions{selectedCameraIds=null}.Copy().selectedCameraIds.Length==0,"Null camera selection did not safely copy.");
        Reject(()=>new StationDecisionCaptureOptions{videoFps=0}.Validate(),"Invalid nominal AVI playback rate was accepted.");
        Reject(()=>new StationDecisionCaptureOptions{videoFps=51}.Validate(),"Out-of-range nominal AVI playback rate was accepted.");
        Reject(()=>new StationDecisionCaptureOptions{videoWidth=1921}.Validate(),"Oversized width was accepted.");
        Reject(()=>new StationDecisionCaptureOptions{videoHeight=1081}.Validate(),"Oversized height was accepted.");
        Reject(()=>new StationDecisionCaptureOptions{jpegQuality=9}.Validate(),"Invalid JPEG quality was accepted.");
        Reject(()=>new StationDecisionCaptureOptions{selectedCameraIds=new[]{" "}}.Validate(),"Blank camera ID was accepted.");
        Reject(()=>defaults.SetVideoResolution(3),"Invalid resolution index was accepted.");
        Console.WriteLine($"PASS: {checks} checks for resolution-only preferences, compatible nominal AVI metadata and independent data/camera choices.");
    }
}
