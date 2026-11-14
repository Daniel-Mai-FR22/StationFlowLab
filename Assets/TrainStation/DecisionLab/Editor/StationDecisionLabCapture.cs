#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    public static class StationDecisionLabCapture
    {
        [Serializable] sealed class CaptureReport
        {
            public bool success, sceneDirtyPreserved, objectCountPreserved;
            public string capturedUtc, scene, error;
            public string[] files;
        }
        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Capture reference views")]
        public static void Capture()
        {
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == ScenePath, "Capture is limited to the Decision Lab scene.");
            Require(!AnimationMode.InAnimationMode(), "Finish animation preview before capture.");
            var report = new CaptureReport { capturedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path };
            string suffix = Application.isPlaying ? "live" : "saved";
            report.files = new[] { ReportDirectory + "/hall-" + suffix + ".png", ReportDirectory + "/platforms-" + suffix + ".png" };
            var camera = Components<Camera>(scene).First(c => c.isActiveAndEnabled);
            var position = camera.transform.position; var rotation = camera.transform.rotation;
            var targetBefore = camera.targetTexture; var rectBefore = camera.rect; var fovBefore = camera.fieldOfView;
            var activeBefore = RenderTexture.active;
            bool dirtyBefore = scene.isDirty;
            int countBefore = Components<Transform>(scene).Length;
            var canvases = Components<Canvas>(scene); var canvasStates = canvases.Select(c => c.enabled).ToArray();
            var skins = Components<SkinnedMeshRenderer>(scene).Where(s => s.enabled && s.gameObject.activeInHierarchy).ToArray();
            var meshes = new List<Mesh>(); var proxies = new List<GameObject>();
            RenderTexture target = null;
            try
            {
                Directory.CreateDirectory(ReportDirectory);
                foreach (var canvas in canvases) canvas.enabled = false;
                // CPU snapshots avoid rendering stale bind poses in Edit mode and preserve every saved renderer.
                foreach (var skin in skins)
                {
                    var mesh = new Mesh { name = "Decision Lab capture pose", hideFlags = HideFlags.HideAndDontSave };
                    meshes.Add(mesh); skin.BakeMesh(mesh, false); mesh.RecalculateBounds();
                    var proxy = new GameObject("Temporary Decision Lab capture") { hideFlags = HideFlags.HideAndDontSave, layer = skin.gameObject.layer };
                    proxies.Add(proxy); proxy.transform.SetParent(skin.transform, false);
                    proxy.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = proxy.AddComponent<MeshRenderer>(); renderer.sharedMaterials = skin.sharedMaterials;
                    renderer.shadowCastingMode = skin.shadowCastingMode; renderer.receiveShadows = skin.receiveShadows;
                    renderer.renderingLayerMask = skin.renderingLayerMask;
                    skin.enabled = false;
                }
                target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32); target.Create();
                camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1); camera.fieldOfView = 65f;
                Render(camera, target, new Vector3(18, 8, -21), new Vector3(-4, 1.2f, 10), report.files[0]);
                camera.fieldOfView = 61f;
                Render(camera, target, new Vector3(44, 7, 35), new Vector3(4, 1.4f, 84), report.files[1]);
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                foreach (var skin in skins) if (skin) skin.enabled = true;
                foreach (var proxy in proxies) if (proxy) UnityEngine.Object.DestroyImmediate(proxy);
                foreach (var mesh in meshes) if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                for (int i = 0; i < canvases.Length; ++i) if (canvases[i]) canvases[i].enabled = canvasStates[i];
                camera.transform.SetPositionAndRotation(position, rotation); camera.targetTexture = targetBefore;
                camera.rect = rectBefore; camera.fieldOfView = fovBefore; RenderTexture.active = activeBefore;
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                report.sceneDirtyPreserved = scene.isDirty == dirtyBefore;
                report.objectCountPreserved = Components<Transform>(scene).Length == countBefore;
                report.success &= report.sceneDirtyPreserved && report.objectCountPreserved;
                Write(ReportDirectory + "/capture-" + suffix + ".json", report);
            }
            Require(report.success, "Capture left an unexpected scene mutation.");
        }
        static void Render(Camera camera, RenderTexture target, Vector3 position, Vector3 lookAt, string filename)
        {
            camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position));
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try { image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); File.WriteAllBytes(filename, image.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }
    }
}
#endif
