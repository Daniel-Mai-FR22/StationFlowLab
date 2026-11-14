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
using UnityEngine.UI;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Renders every saved camera without moving its optical pose; restores all temporary render state.</summary>
    public static class StationCameraCapture
    {
        const string DirectoryPath="Reports/TrainStationCameras";
        [Serializable] sealed class CaptureReport
        {
            public bool success,beforeDirty,afterDirty;
            public int beforeObjects,afterObjects;
            public string[] images,addedObjects,removedObjects,changedComponents;
        }
        [MenuItem("Efrei Research Lab/Train Station/Cameras/Save all camera reference images")]
        public static void Capture()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Capture saved cameras in Edit mode outside animation preview.");
            var scene=SceneManager.GetActiveScene(); var roots=scene.GetRootGameObjects();
            var controls=roots.SelectMany(g=>g.GetComponentsInChildren<TrainStationViewControls>(true)).Single();
            var cameras=controls.viewpoints;
            if(cameras.Length!=15) throw new InvalidOperationException("Author all fifteen station cameras first.");
            var cameraTargets=cameras.ToDictionary(c=>c,c=>c.targetTexture);
            var canvases=roots.SelectMany(g=>g.GetComponentsInChildren<Canvas>(true)).ToArray();
            var skins=roots.SelectMany(g=>g.GetComponentsInChildren<SkinnedMeshRenderer>(true)).Where(s=>s.enabled&&s.gameObject.activeInHierarchy).ToArray();
            var beforeTransforms=roots.SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).ToDictionary(t=>t,t=>Path(t));
            var gameObjectStates=beforeTransforms.Keys.Select(t=>t.gameObject).ToDictionary(g=>g,g=>(active:g.activeSelf,tag:g.tag));
            var watched=canvases.SelectMany(c=>c.GetComponentsInChildren<Component>(true))
                .Where(c=>c is RectTransform||c is Canvas||c is TMP_Text||c is Image)
                .Concat(cameras.SelectMany(c=>c.GetComponents<Component>()).Where(c=>c is Camera||c is Transform||c is AudioListener||c is TrainStationOrbitCamera))
                .Concat(roots.SelectMany(g=>g.GetComponentsInChildren<TrainStationOrbitCamera>(true)))
                .Append(controls).Distinct().ToArray();
            var states=watched.ToDictionary(c=>c,c=>EditorJsonUtility.ToJson(c));
            var dirty=scene.isDirty; var oldActive=RenderTexture.active;
            var proxies=new List<GameObject>();var meshes=new List<Mesh>();var names=new List<string>();
            RenderTexture target=null,small=null;Texture2D sheet=null;
            Directory.CreateDirectory(DirectoryPath);
            try
            {
                foreach(var skin in skins)
                {
                    var mesh=new Mesh{name="Temporary saved camera pose",hideFlags=HideFlags.HideAndDontSave};meshes.Add(mesh);
                    skin.BakeMesh(mesh,false);mesh.RecalculateBounds();
                    var proxy=new GameObject("Capture only - "+skin.name){hideFlags=HideFlags.HideAndDontSave,layer=skin.gameObject.layer};proxies.Add(proxy);
                    proxy.transform.SetParent(skin.transform,false);proxy.AddComponent<MeshFilter>().sharedMesh=mesh;
                    var renderer=proxy.AddComponent<MeshRenderer>();renderer.sharedMaterials=skin.sharedMaterials;
                    renderer.shadowCastingMode=skin.shadowCastingMode;renderer.receiveShadows=skin.receiveShadows;
                    renderer.lightProbeUsage=skin.lightProbeUsage;renderer.reflectionProbeUsage=skin.reflectionProbeUsage;
                    renderer.renderingLayerMask=skin.renderingLayerMask;renderer.probeAnchor=skin.probeAnchor;skin.enabled=false;
                }
                target=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32);target.Create();
                small=new RenderTexture(480,270,0,RenderTextureFormat.ARGB32);small.Create();
                sheet=new Texture2D(1440,1350,TextureFormat.RGB24,false);
                foreach(var canvas in canvases) if(canvas.renderMode!=RenderMode.WorldSpace) canvas.enabled=false;
                for(int i=0;i<cameras.Length;i++)
                {
                    controls.SelectView(i);
                    var camera=cameras[i];camera.targetTexture=target;camera.rect=new Rect(0,0,1,1);
                    string filename=(i+1).ToString("00")+"-"+(i==0?"hall-game":camera.GetComponentInParent<StationSurveillanceCamera>().cameraId)+".png";
                    Render(camera,target,filename);names.Add(filename+" | "+controls.GetViewName(i));
                    Graphics.Blit(target,small);RenderTexture.active=small;
                    sheet.ReadPixels(new Rect(0,0,480,270),(i%3)*480,(4-i/3)*270,false);
                }
                sheet.Apply();File.WriteAllBytes(DirectoryPath+"/camera-contact-sheet.png",sheet.EncodeToPNG());
                controls.SelectView(0);var hall=cameras[0];
                foreach(var canvas in canvases)
                {
                    EditorJsonUtility.FromJsonOverwrite(states[canvas],canvas);
                    if(canvas.renderMode==RenderMode.ScreenSpaceOverlay)
                    {canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=hall;canvas.planeDistance=Mathf.Max(.3f,hall.nearClipPlane+.05f);}
                }
                controls.interfaceRoot.SetActive(true);controls.cameraCatalog.SetActive(false);
                Render(hall,target,"hall-game-ui.png");controls.ToggleCameraCatalog();Render(hall,target,"camera-catalog-ui.png");
            }
            finally
            {
                foreach(var skin in skins)if(skin)skin.enabled=true;
                foreach(var proxy in proxies)if(proxy)UnityEngine.Object.DestroyImmediate(proxy);
                foreach(var mesh in meshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
                foreach(var state in gameObjectStates)if(state.Key){state.Key.SetActive(state.Value.active);state.Key.tag=state.Value.tag;}
                foreach(var state in states)if(state.Key&&EditorJsonUtility.ToJson(state.Key)!=state.Value)EditorJsonUtility.FromJsonOverwrite(state.Value,state.Key);
                // Transient RenderTexture references can serialize as fileID 0, just like null.
                foreach(var camera in cameras)if(camera)camera.targetTexture=cameraTargets[camera];
                RenderTexture.active=oldActive;
                if(target){target.Release();UnityEngine.Object.DestroyImmediate(target);}if(small){small.Release();UnityEngine.Object.DestroyImmediate(small);}
                if(sheet)UnityEngine.Object.DestroyImmediate(sheet);
                Canvas.ForceUpdateCanvases();
                var after=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).ToArray();var afterSet=new HashSet<Transform>(after);
                var report=new CaptureReport{beforeDirty=dirty,afterDirty=scene.isDirty,beforeObjects=beforeTransforms.Count,afterObjects=after.Length,images=names.ToArray(),
                    addedObjects=after.Where(t=>!beforeTransforms.ContainsKey(t)).Select(Path).ToArray(),
                    removedObjects=beforeTransforms.Where(p=>!afterSet.Contains(p.Key)).Select(p=>p.Value).ToArray(),
                    changedComponents=watched.Where(c=>!c||EditorJsonUtility.ToJson(c)!=states[c]).Select(c=>c?Path(c.transform)+" ["+c.GetType().Name+"]":"Destroyed").ToArray()};
                report.success=report.beforeDirty==report.afterDirty&&report.addedObjects.Length==0&&report.removedObjects.Length==0&&report.changedComponents.Length==0;
                File.WriteAllText(DirectoryPath+"/capture-state.json",JsonUtility.ToJson(report,true));
                if(!report.success)throw new InvalidOperationException("Camera capture did not preserve saved scene state; inspect capture-state.json.");
            }
        }
        static void Render(Camera camera,RenderTexture target,string filename)
        {
            foreach(var text in SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<TMP_Text>(true)))text.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target;var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            try{image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();File.WriteAllBytes(DirectoryPath+"/"+filename,image.EncodeToPNG());}
            finally{UnityEngine.Object.DestroyImmediate(image);}
        }
        static string Path(Transform t){var names=new List<string>();for(var n=t;n;n=n.parent)names.Add(n.name);names.Reverse();return string.Join("/",names);}
    }
}
#endif
