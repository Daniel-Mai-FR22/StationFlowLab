#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Editor
{
    public static class TrainStationAccessCheck
    {
        [MenuItem("Efrei Research Lab/Train Station/Check circulation clearances")]
        public static void Check()
        {
            Physics.SyncTransforms();
            var errors=new List<string>();
            int samples=0;
            Sample("Entry to concourse",new[]{new Vector2(0,-32),new Vector2(0,-1),new Vector2(-6,-1),new Vector2(-6,9),new Vector2(0,9),new Vector2(0,36)},errors,ref samples);
            foreach(float x in new[]{-36f,-12f,12f,36f})
                Sample("Concourse to platform "+x,new[]{new Vector2(0,36),new Vector2(x+2.6f,36),new Vector2(x+2.6f,90)},errors,ref samples);
            foreach(float z in new[]{-16f,0f,16f})
                Sample("West service entrance "+z,new[]{new Vector2(-18,z),new Vector2(-18,z+1.4f),new Vector2(-25,z+1.4f),new Vector2(-25,z)},errors,ref samples);
            foreach(float z in new[]{-18f,-6f,6f,18f})
                Sample("East service entrance "+z,new[]{new Vector2(18,z),new Vector2(25,z)},errors,ref samples);
            var roots=SceneManager.GetActiveScene().GetRootGameObjects();
            var controls=roots.SelectMany(r=>r.GetComponentsInChildren<TrainStationViewControls>(true)).Single();
            var buttons=controls.interfaceRoot.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            if(buttons.Length!=8)errors.Add("Expected eight serialized UI buttons");
            foreach(var button in buttons)
                if(button.onClick.GetPersistentEventCount()!=1 || button.onClick.GetPersistentTarget(0)!=controls)errors.Add("Unsaved button callback: "+button.name);
            if(roots.SelectMany(r=>r.GetComponentsInChildren<MonoBehaviour>(true)).Any(b=>b!=null && b.GetType().Namespace=="StationFlow.SyntheticTraffic"))errors.Add("Unexpected JunctionLab runtime component");
            Directory.CreateDirectory("Reports/TrainStation");
            File.WriteAllText("Reports/TrainStation/access-check.json",JsonUtility.ToJson(new Result{sampledPoints=samples,authoredRoutes=12,bodyWidth=.6f,bodyHeight=1.8f,errors=errors.Distinct().ToArray()},true));
            if(errors.Count>0)throw new Exception("TrainStation access check:\n"+string.Join("\n",errors.Distinct()));
        }

        static void Sample(string name,Vector2[] route,List<string> errors,ref int samples)
        {
            for(int i=1;i<route.Length;i++)
            {
                int steps=Mathf.CeilToInt(Vector2.Distance(route[i-1],route[i])*2);
                for(int s=0;s<=steps;s++)
                {
                    var p=Vector2.Lerp(route[i-1],route[i],s/(float)steps);
                    var at=new Vector3(p.x,1.05f,p.y);samples++;
                    var hits=Physics.OverlapBox(at,new Vector3(.3f,.75f,.3f),Quaternion.identity,~0,QueryTriggerInteraction.Ignore);
                    foreach(var hit in hits)errors.Add(name+": blocked by "+hit.name+" near ("+p.x.ToString("0.0")+", "+p.y.ToString("0.0")+")");
                    if(!Physics.Raycast(new Vector3(p.x,.15f,p.y),Vector3.down,.3f,~0,QueryTriggerInteraction.Ignore))errors.Add(name+": missing floor at "+p);
                }
            }
        }
        [Serializable]class Result{public int sampledPoints,authoredRoutes;public float bodyWidth,bodyHeight;public string[] errors;}
    }
}
#endif
