using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace StationFlow.SyntheticTraffic
{
    public sealed partial class DemoController
    {
        [Serializable] sealed class QualityResult
        {
            public bool passed;
            public double seconds;
            public int vehicleTrips,pedestrianTrips,pedestrianCrossings,rightTurns,vehicleRed,pedestrianRed,collisions;
            public int pedestrianBirths,pedestrianDepartures,vehicleBirths,vehicleDepartures,visibleBirths,visibleDepartures;
            public int soloSamples,conversingSamples,waitingSamples,cornerWhileRedSamples,peakNearbyPeople;
            public float meanNearbyPeople,largestSameTrackStep;
            public bool followedActorStayedAlive,followedActorReleasedOffscreen;
            public string graphicsDevice;
        }
        struct LifetimeSnapshot
        {
            public int Id;
            public bool Active;
            public float X,Z;
        }
        void AuditTick(LifetimeSnapshot[] before,QualityResult result)
        {
            for(int i=0;i<before.Length;i++)
            {
                var a=Simulation.Agents[i];
                before[i]=new LifetimeSnapshot{Id=a.Id,Active=a.Active,X=a.X,Z=a.Z};
            }
            Tick();
            for(int i=0;i<before.Length;i++)
            {
                var a=Simulation.Agents[i];var b=before[i];
                // The same conservative body envelope is tested at the actual birth/departure location.
                float radius=a.Kind==AgentKind.Pedestrian?1.4f:a.Length+1;
                if(a.Active&&(!b.Active||b.Id!=a.Id)&&Rig.IsObservableAt(a.X,a.Z,radius))result.visibleBirths++;
                if(b.Active&&(!a.Active||b.Id!=a.Id)&&Rig.IsObservableAt(a.X,a.Z,radius))result.visibleDepartures++;
                if(a.Active&&b.Active&&a.Id==b.Id)
                    result.largestSameTrackStep=Mathf.Max(result.largestSameTrackStep,Vector2.Distance(new Vector2(b.X,b.Z),new Vector2(a.X,a.Z)));
            }
        }
        IEnumerator RunQualityCheck(string dir)
        {
            Directory.CreateDirectory(dir);yield return null;yield return null;yield return null;
            bool failed=false;
            var result=new QualityResult{graphicsDevice=SystemInfo.graphicsDeviceName};
            var before=new LifetimeSnapshot[Simulation.Agents.Count];
            try{Rig.ValidateProjectionRendering(Path.Combine(dir,"calibration"));}
            catch(Exception e){Debug.LogException(e);WriteFailure(e);failed=true;}
            if(failed){Application.Quit(3);yield break;}
            int[] checkpoints={6,24,48,60,90,120,240,360,600};int next=0,samples=0,totalNearby=0;
            while(next<checkpoints.Length)
            {
                try
                {
                    for(int i=0;i<30;i++)AuditTick(before,result);
                    int nearby=0;
                    foreach(var a in Simulation.Agents)
                    {
                        if(!a.Active||a.Kind!=AgentKind.Pedestrian||a.X*a.X+a.Z*a.Z>60*60)continue;
                        nearby++;
                        if(a.GroupSize==1)result.soloSamples++;
                        if(a.Conversation)result.conversingSamples++;
                        if(a.State=="Waiting")result.waitingSamples++;
                        if(a.NavigationIntent=="Corner"&&!Simulation.IsPedestrianWalk&&a.Speed>.5f&&Mathf.Abs(a.X)<16&&Mathf.Abs(a.Z)<16)result.cornerWhileRedSamples++;
                    }
                    samples++;totalNearby+=nearby;result.peakNearbyPeople=Mathf.Max(result.peakNearbyPeople,nearby);
                    if(Simulation.Time>=checkpoints[next])
                    {
                        SetView(0);Rig.SavePreview(dir,"t"+checkpoints[next].ToString("000"));
                        if(checkpoints[next]==60||checkpoints[next]==90)SavePeopleReview(dir,checkpoints[next]);
                        if(checkpoints[next]==120)
                        {
                            SetLighting(1);SetView(0);CameraRig.SaveCameraPreview(Rig.OverviewCamera,Path.Combine(dir,"golden.png"));SetLighting(0);
                        }
                        SetView(0);next++;
                    }
                }
                catch(Exception e){Debug.LogException(e);WriteFailure(e);failed=true;break;}
                yield return null;
            }
            if(!failed)
            {
                try
                {
                    int outgoing=-1;
                    // The 600-second checkpoint may fall in the protected walking phase,
                    // with every car stopped. Wait for a real departing car instead of
                    // making the camera test depend on the exact signal-cycle timing.
                    for(int wait=0;wait<90*60&&outgoing<0;wait++)
                    {
                        for(int i=0;i<Simulation.Agents.Count;i++)
                        {
                            var a=Simulation.Agents[i];
                            if(a.Kind==AgentKind.Vehicle&&a.Active&&a.Speed>2&&a.X*a.X+a.Z*a.Z>30*30&&a.X*a.VelocityX+a.Z*a.VelocityZ>0){outgoing=i;break;}
                        }
                        if(outgoing<0)AuditTick(before,result);
                    }
                    if(outgoing>=0)
                    {
                        selected=outgoing;SetView(4);int identity=Simulation.Agents[outgoing].Id;
                        result.followedActorStayedAlive=true;
                        for(int i=0;i<1200;i++)
                        {
                            AuditTick(before,result);UpdateCamera(StepSize);
                            var a=Simulation.Agents[outgoing];
                            result.followedActorStayedAlive&=a.Active&&a.Id==identity;
                        }
                        CameraRig.SaveCameraPreview(Rig.OverviewCamera,Path.Combine(dir,"following-beyond-portal.png"));
                        SetView(0);
                        for(int i=0;i<60;i++)AuditTick(before,result);
                        result.followedActorReleasedOffscreen=!Simulation.Agents[outgoing].Active||Simulation.Agents[outgoing].Id!=identity;
                    }
                }
                catch(Exception e){Debug.LogException(e);WriteFailure(e);failed=true;}
            }
            result.seconds=Simulation.Time;result.vehicleTrips=Simulation.CompletedVehicleTrips;
            result.pedestrianTrips=Simulation.CompletedPedestrianTrips;result.pedestrianCrossings=Simulation.CompletedPedestrianCrossings;
            result.rightTurns=Simulation.CompletedRightTurns;result.vehicleRed=Simulation.RedLightViolations;
            result.pedestrianRed=Simulation.PedestrianRedLightViolations;result.collisions=Simulation.Collisions;
            result.pedestrianBirths=Simulation.SpawnedPedestrians;result.pedestrianDepartures=Simulation.DespawnedPedestrians;
            result.vehicleBirths=Simulation.SpawnedVehicles;result.vehicleDepartures=Simulation.DespawnedVehicles;
            result.meanNearbyPeople=(float)totalNearby/Mathf.Max(1,samples);
            result.passed=!failed&&result.vehicleRed==0&&result.pedestrianRed==0&&result.collisions==0&&
                result.visibleBirths==0&&result.visibleDepartures==0&&result.largestSameTrackStep<.20f&&
                result.vehicleTrips>60&&result.pedestrianCrossings>40&&result.rightTurns>15&&
                result.pedestrianBirths>30&&result.vehicleBirths>30&&result.meanNearbyPeople>12&&
                result.soloSamples>0&&result.conversingSamples>0&&result.waitingSamples>0&&result.cornerWhileRedSamples>0&&
                result.followedActorStayedAlive&&result.followedActorReleasedOffscreen;
            File.WriteAllText(Path.Combine(dir,"quality-check.json"),JsonUtility.ToJson(result,true));
            Application.Quit(result.passed?0:5);
        }
        void SavePeopleReview(string dir,int second)
        {
            SetView(1);CameraRig.SaveCameraPreview(Rig.OverviewCamera,Path.Combine(dir,"street-"+second+".png"));
            Follow(AgentKind.Pedestrian);UpdateCamera(1);
            if(selected>=0)
            {
                CameraRig.SaveCameraPreview(Rig.OverviewCamera,Path.Combine(dir,"citizen-"+second+".png"));
                Transform citizen=actors[selected].Root.transform;
                Rig.OverviewCamera.transform.position=citizen.position+citizen.forward*2.7f+citizen.right*1.6f+Vector3.up*1.5f;
                Rig.OverviewCamera.transform.position=world.KeepFollowCameraClear(citizen.position+Vector3.up*1.1f,Rig.OverviewCamera.transform.position);
                Rig.OverviewCamera.transform.LookAt(citizen.position+Vector3.up*1.1f);
                CameraRig.SaveCameraPreview(Rig.OverviewCamera,Path.Combine(dir,"citizen-front-"+second+".png"));
            }
            float best=float.MaxValue;int group=-1;
            for(int i=0;i<Simulation.Agents.Count;i++)
            {
                var a=Simulation.Agents[i];float distance=a.X*a.X+a.Z*a.Z;
                if(a.Kind==AgentKind.Pedestrian&&a.Active&&a.GroupSize>1&&a.Conversation&&distance<best){best=distance;group=i;}
            }
            if(group>=0)
            {
                selected=group;SetView(4);Transform person=actors[group].Root.transform;
                Rig.OverviewCamera.transform.position=person.position+person.forward*4+person.right*3+Vector3.up*2;
                Rig.OverviewCamera.transform.position=world.KeepFollowCameraClear(person.position+Vector3.up,Rig.OverviewCamera.transform.position);
                Rig.OverviewCamera.transform.LookAt(person.position+Vector3.up);
                CameraRig.SaveCameraPreview(Rig.OverviewCamera,Path.Combine(dir,"conversation-"+second+".png"));
            }
            Follow(AgentKind.Vehicle);UpdateCamera(1);CameraRig.SaveCameraPreview(Rig.OverviewCamera,Path.Combine(dir,"vehicle-"+second+".png"));
        }
    }
}
