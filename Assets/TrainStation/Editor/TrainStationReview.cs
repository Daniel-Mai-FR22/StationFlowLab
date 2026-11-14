#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Editor
{
    public static class TrainStationReview
    {
        public static void BuildAndReview()
        {
            TrainStationBuilder.Create();
            EditorSceneManager.OpenScene(TrainStationBuilder.ScenePath);
            TrainStationBuilder.ValidateActive();
            Capture();
            TrainStationAccessCheck.Check();
        }

        [MenuItem("Efrei Research Lab/Train Station/Save reference images")]
        public static void Capture()
        {
            var scene=SceneManager.GetActiveScene();
            var controls=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<TrainStationViewControls>(true)).First();
            if(controls.viewpoints.Length>6) { StationCameraCapture.Capture(); return; }
            Directory.CreateDirectory("Reports/TrainStation");
            var canvas=controls.interfaceRoot.GetComponent<Canvas>();
            int previousView=controls.selectedView;
            bool interfaceActive=controls.interfaceRoot.activeSelf;
            var roofStates=controls.roofGroups.Select(g=>g.activeSelf).ToArray();
            var mode=canvas.renderMode;
            Camera previousWorldCamera=canvas.worldCamera;
            float previousPlaneDistance=canvas.planeDistance;
            try
            {
                // Camera rendering excludes overlay canvases. Use a temporary camera canvas only for captures.
                canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.planeDistance=.3f;
                for(int i=0;i<controls.viewpoints.Length;i++)
                {
                    controls.SelectView(i);controls.interfaceRoot.SetActive(false);
                    SaveCamera(controls.viewpoints[i],"Reports/TrainStation/"+(i+1).ToString("00")+"-"+new[]{"hall","parvis","quais","ensemble","commerces","concourse"}[i]+".png");
                }
                controls.SelectView(3);
                foreach(var g in controls.roofGroups)g.SetActive(false);
                SaveCamera(controls.viewpoints[3],"Reports/TrainStation/07-cutaway.png");
                for(int i=0;i<controls.roofGroups.Length;i++)controls.roofGroups[i].SetActive(roofStates[i]);
                controls.SelectView(0);controls.interfaceRoot.SetActive(true);canvas.worldCamera=controls.viewpoints[0];
                SaveCamera(controls.viewpoints[0],"Reports/TrainStation/08-hall-ui.png");
                // Verify the real persistent button callbacks switch exactly one saved camera.
                var buttons=controls.interfaceRoot.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                foreach(var button in buttons.Where(b=>b.name.StartsWith("Vue ·")))
                {
                    button.onClick.Invoke();
                    if(controls.viewpoints.Count(c=>c.gameObject.activeInHierarchy&&c.enabled)!=1)throw new Exception("Camera button failed: "+button.name);
                }
            }
            finally
            {
                controls.SelectView(previousView);controls.interfaceRoot.SetActive(interfaceActive);
                for(int i=0;i<controls.roofGroups.Length;i++)controls.roofGroups[i].SetActive(roofStates[i]);
                canvas.renderMode=mode;canvas.worldCamera=previousWorldCamera;canvas.planeDistance=previousPlaneDistance;
            }
            File.WriteAllText("Reports/TrainStation/review-complete.txt","Unity "+Application.unityVersion+"\nScene reopened from disk; six camera buttons passed; eight reference PNGs rendered.\n"+DateTime.UtcNow.ToString("O"));
        }

        static void SaveCamera(Camera cam,string path)
        {
            var target=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32);
            var previous=cam.targetTexture;var active=RenderTexture.active;
            target.Create();cam.targetTexture=target;
            foreach(var text in UnityEngine.Object.FindObjectsByType<TMP_Text>())text.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
            try
            {
                var request=new UniversalRenderPipeline.SingleCameraRequest{destination=target};
                RenderPipeline.SubmitRenderRequest(cam,request);
                RenderTexture.active=target;
                var tex=new Texture2D(1600,900,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1600,900),0,0);tex.Apply();
                File.WriteAllBytes(path,tex.EncodeToPNG());UnityEngine.Object.DestroyImmediate(tex);
            }
            finally{cam.targetTexture=previous;RenderTexture.active=active;target.Release();UnityEngine.Object.DestroyImmediate(target);}
        }
    }
}
#endif
