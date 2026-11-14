#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.RailOperations.Editor
{
    /// <summary>Renders the actual saved/live train and timetable scene, restoring all temporary capture state.</summary>
    public static class StationRailCapture
    {
        sealed class CanvasState
        {
            public Canvas canvas;
            public bool enabled;
            public RenderMode mode;
            public Camera camera;
            public float planeDistance;
        }
        [Serializable] sealed class CaptureStateReport
        {
            public bool beforeDirty,afterDirty,success;
            public int beforeObjects,afterObjects;
            public string[] addedObjects,removedObjects,changedComponents;
        }

        [MenuItem("Efrei Research Lab/Train Station/Rail/Save reference images")]
        public static void Capture()=>CaptureInternal(false);
        public static void CaptureLive()=>CaptureInternal(true);

        static void CaptureInternal(bool live)
        {
            if(AnimationMode.InAnimationMode()||live!=Application.isPlaying||(!live&&EditorApplication.isPlayingOrWillChangePlaymode))
                throw new InvalidOperationException("Use saved rail capture in Edit mode and live capture in Play mode, outside animation preview.");
            var scene=SceneManager.GetActiveScene();var roots=scene.GetRootGameObjects();
            var operations=roots.SelectMany(g=>g.GetComponentsInChildren<StationRailOperations>(true)).Single();
            var camera=roots.SelectMany(g=>g.GetComponentsInChildren<Camera>(true)).First(c=>c.isActiveAndEnabled);
            var displays=roots.SelectMany(g=>g.GetComponentsInChildren<StationTimetableDisplay>(true)).ToArray();
            var canvases=roots.SelectMany(g=>g.GetComponentsInChildren<Canvas>(true)).Select(c=>new CanvasState
            {canvas=c,enabled=c.enabled,mode=c.renderMode,camera=c.worldCamera,planeDistance=c.planeDistance}).ToArray();
            var skins=roots.SelectMany(g=>g.GetComponentsInChildren<SkinnedMeshRenderer>(true)).Where(s=>s.enabled&&s.gameObject.activeInHierarchy).ToArray();
            var objects=new List<GameObject>();var meshes=new List<Mesh>();
            var position=camera.transform.position;var rotation=camera.transform.rotation;
            float fov=camera.fieldOfView;var rect=camera.rect;var targetBefore=camera.targetTexture;var activeBefore=RenderTexture.active;
            bool dirty=scene.isDirty;
            var priorTransforms=roots.SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).ToDictionary(t=>t,t=>HierarchyPath(t));
            int objectCount=priorTransforms.Count;
            var watchedComponents=canvases.Select(s=>(Component)s.canvas)
                .Concat(canvases.SelectMany(s=>s.canvas.GetComponentsInChildren<RectTransform>(true)).Cast<Component>())
                .Append(camera).Append(camera.transform).Distinct().ToArray();
            var componentStates=watchedComponents.ToDictionary(c=>c,c=>EditorJsonUtility.ToJson(c));
            RenderTexture target=null;
            try
            {
                foreach(var skin in skins)
                {
                    var mesh=new Mesh{name="Rail capture actor pose",hideFlags=HideFlags.HideAndDontSave};meshes.Add(mesh);
                    skin.BakeMesh(mesh,false);mesh.RecalculateBounds();
                    if(mesh.vertexCount==0||mesh.vertices.Any(v=>!Finite(v)))throw new InvalidOperationException("Invalid actor pose in rail capture: "+skin.name);
                    var proxy=new GameObject("Capture only — "+skin.name){hideFlags=HideFlags.HideAndDontSave,layer=skin.gameObject.layer};objects.Add(proxy);
                    proxy.transform.SetParent(skin.transform,false);proxy.AddComponent<MeshFilter>().sharedMesh=mesh;
                    var renderer=proxy.AddComponent<MeshRenderer>();renderer.sharedMaterials=skin.sharedMaterials;
                    renderer.shadowCastingMode=skin.shadowCastingMode;renderer.receiveShadows=skin.receiveShadows;
                    renderer.lightProbeUsage=skin.lightProbeUsage;renderer.reflectionProbeUsage=skin.reflectionProbeUsage;
                    renderer.renderingLayerMask=skin.renderingLayerMask;renderer.probeAnchor=skin.probeAnchor;
                    skin.enabled=false;
                }
                target=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32);target.Create();
                camera.targetTexture=target;camera.rect=new Rect(0,0,1,1);
                Directory.CreateDirectory("Reports/TrainStationRail");
                foreach(var state in canvases)if(state.mode!=RenderMode.WorldSpace)state.canvas.enabled=false;
                var train=operations.trains.Where(t=>t&&t.berthPoint).OrderBy(t=>Vector3.Distance(t.transform.position,t.berthPoint.position)).First();
                var berth=train.berthPoint.position;float side=Mathf.Sign(berth.x);
                camera.fieldOfView=52f;
                Render(camera,target,berth+new Vector3(side*7.7f,3f,-5f),berth+new Vector3(0,1.8f,12f),
                    live?"platform-trains-live.png":"platform-trains.png");
                var board=displays.First(d=>d.mode==StationTimetableMode.Departures&&!d.GetComponentInParent<Canvas>());
                var centre=board.transform.position;camera.fieldOfView=45f;
                var boardCamera=centre-board.transform.forward*18f+board.transform.right*3f-Vector3.up;
                Render(camera,target,boardCamera,centre+Vector3.up*.2f,live?"hall-timetable-live.png":"hall-timetable.png");
                foreach(var state in canvases)
                {
                    state.canvas.enabled=state.enabled;
                    if(state.mode==RenderMode.ScreenSpaceOverlay)
                    {
                        state.canvas.renderMode=RenderMode.ScreenSpaceCamera;state.canvas.worldCamera=camera;
                        state.canvas.planeDistance=Mathf.Max(.3f,camera.nearClipPlane+.05f);
                    }
                }
                Render(camera,target,boardCamera,
                    centre+Vector3.up*.2f,live?"hall-timetable-ui-live.png":"hall-timetable-ui.png");
            }
            finally
            {
                foreach(var skin in skins)if(skin)skin.enabled=true;
                foreach(var proxy in objects)if(proxy)UnityEngine.Object.DestroyImmediate(proxy);
                foreach(var mesh in meshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
                foreach(var state in canvases)
                {
                    state.canvas.enabled=state.enabled;state.canvas.renderMode=state.mode;
                    state.canvas.worldCamera=state.camera;state.canvas.planeDistance=state.planeDistance;
                }
                camera.transform.SetPositionAndRotation(position,rotation);camera.fieldOfView=fov;camera.rect=rect;camera.targetTexture=targetBefore;
                RenderTexture.active=activeBefore;
                if(target){target.Release();UnityEngine.Object.DestroyImmediate(target);}
                Canvas.ForceUpdateCanvases();
                var afterTransforms=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).ToArray();
                var afterSet=new HashSet<Transform>(afterTransforms);
                var diagnostic=new CaptureStateReport{beforeDirty=dirty,afterDirty=scene.isDirty,beforeObjects=objectCount,afterObjects=afterTransforms.Length,
                    addedObjects=afterTransforms.Where(t=>!priorTransforms.ContainsKey(t)).Select(t=>HierarchyPath(t)+" ["+string.Join(", ",t.GetComponents<Component>().Where(c=>c).Select(c=>c.GetType().Name))+"]").ToArray(),
                    removedObjects=priorTransforms.Where(p=>!afterSet.Contains(p.Key)).Select(p=>p.Value).ToArray(),
                    changedComponents=watchedComponents.Where(c=>!c||EditorJsonUtility.ToJson(c)!=componentStates[c]).Select(c=>c?HierarchyPath(c.transform)+" ["+c.GetType().Name+"]":"Destroyed component").ToArray()};
                diagnostic.success=diagnostic.beforeDirty==diagnostic.afterDirty&&diagnostic.addedObjects.Length==0&&diagnostic.removedObjects.Length==0&&diagnostic.changedComponents.Length==0;
                Directory.CreateDirectory("Reports/TrainStationRail");
                File.WriteAllText("Reports/TrainStationRail/"+(live?"capture-state-live.json":"capture-state.json"),JsonUtility.ToJson(diagnostic,true));
                if(!diagnostic.success)
                    throw new InvalidOperationException("Rail capture preservation failed: dirty "+dirty+" → "+scene.isDirty+", objects "+objectCount+" → "+afterTransforms.Length+
                        ", added "+diagnostic.addedObjects.Length+", removed "+diagnostic.removedObjects.Length+", changed components "+diagnostic.changedComponents.Length+". See capture-state"+(live?"-live":"")+".json.");
            }
        }
        static string HierarchyPath(Transform transform)
        {
            var names=new List<string>();for(var node=transform;node;node=node.parent)names.Add(node.name);names.Reverse();
            return string.Join("/",names);
        }

        static void Render(Camera camera,RenderTexture target,Vector3 position,Vector3 lookAt,string filename)
        {
            camera.transform.SetPositionAndRotation(position,Quaternion.LookRotation(lookAt-position));
            foreach(var text in SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<TMP_Text>(true)))text.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target;var texture=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            try{texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0);texture.Apply();File.WriteAllBytes("Reports/TrainStationRail/"+filename,texture.EncodeToPNG());}
            finally{UnityEngine.Object.DestroyImmediate(texture);}
        }
        static bool Finite(Vector3 value)=>!float.IsNaN(value.x)&&!float.IsInfinity(value.x)&&!float.IsNaN(value.y)&&!float.IsInfinity(value.y)&&!float.IsNaN(value.z)&&!float.IsInfinity(value.z);
    }
}
#endif
