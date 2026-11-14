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

namespace StationFlow.HumanAnimation.Editor
{
    public static class HumanAnimationCapture
    {
        [Serializable] sealed class CaptureReport
        {
            public string scene, clip, image;
            public string[] models;
            public float[] sampledBoneAngles, renderedBoneAngles, restoredBoneAngles;
            public SkinCapture[] bakedSkins;
            public int sceneObjectsBefore, sceneObjectsAfter;
            public bool success, poseRestored, renderersRestored, sceneDirtyBefore, sceneDirtyAfter;
        }
        [Serializable] sealed class SkinCapture
        {
            public string renderer, rootBone, manualSkinError;
            public string[] bones;
            public int vertexCount, sourceNonFiniteVertices, bakedNonFiniteVertices, manualNonFiniteVertices, comparedVertices;
            public float maximumVertexDisplacement, maximumBakeVersusManualError;
            public Vector3 boundsCenter, boundsSize, manualBoundsCenter, manualBoundsSize;
            public Vector3 rendererPosition, rendererLocalScale, rendererLossyScale;
        }

        [MenuItem("Efrei Research Lab/Human Animation/Save lab reference image")]
        public static void Capture()
        {
            var scene=SceneManager.GetActiveScene();
            if(scene.path!=HumanAnimationAuthoring.MixamoScenePath && scene.path!=HumanAnimationAuthoring.ScenePath)
                throw new InvalidOperationException("Open an animation lab before capturing.");
            if(EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Stop Play or the Inspector preview before capturing.");
            var roots=scene.GetRootGameObjects();
            var camera=roots.SelectMany(r=>r.GetComponentsInChildren<Camera>()).Single();
            var players=roots.SelectMany(r=>r.GetComponentsInChildren<HumanAnimationPlayer>()).ToArray();
            var panel=roots.SelectMany(r=>r.GetComponentsInChildren<HumanAnimationPreviewPanel>()).Single();
            var canvas=roots.SelectMany(r=>r.GetComponentsInChildren<Canvas>()).Single();
            var available=panel.library.motions.Where(m=>m!=null&&m.clip!=null).ToArray();
            var selected=available.FirstOrDefault(m=>m.displayName.IndexOf("walking",StringComparison.OrdinalIgnoreCase)>=0&&m.displayName.IndexOf("strafe",StringComparison.OrdinalIgnoreCase)<0)
                ??available.FirstOrDefault(m=>m.id=="diagnostic_reach")??available.First();
            var priorMode=canvas.renderMode;var priorCamera=canvas.worldCamera;float priorDistance=canvas.planeDistance;
            string priorSelection=panel.selectionLabel.text,priorStatus=panel.statusLabel.text;
            var driver=ScriptableObject.CreateInstance<AnimationModeDriver>();driver.hideFlags=HideFlags.HideAndDontSave;
            var animators=players.Select(p=>p.TargetAnimator).ToArray();
            var enabledStates=animators.Select(a=>a.enabled).ToArray();
            var bones=animators.Select(a=>a.GetComponentsInChildren<Transform>(true)).ToArray();
            var originalRotations=bones.Select(group=>group.Select(b=>b.localRotation).ToArray()).ToArray();
            var originalPositions=bones.Select(group=>group.Select(b=>b.localPosition).ToArray()).ToArray();
            var originalScales=bones.Select(group=>group.Select(b=>b.localScale).ToArray()).ToArray();
            var skins=animators.SelectMany(a=>a.GetComponentsInChildren<SkinnedMeshRenderer>()).Where(s=>s.enabled).ToArray();
            var skinEnabled=skins.Select(s=>s.enabled).ToArray();
            var baselineVertices=skins.Select(BakedVertices).ToArray();
            var temporaryObjects=new List<GameObject>();
            var temporaryMeshes=new List<Mesh>();
            var priorTarget=camera.targetTexture;
            var priorRect=camera.rect;
            RenderTexture rt=null;Texture2D image=null;var priorActive=RenderTexture.active;
            string output="Reports/HumanAnimation/"+(scene.path==HumanAnimationAuthoring.MixamoScenePath?"mixamo-lab":"diagnostic-lab")+".png";
            var report=new CaptureReport{scene=scene.path,clip=selected.clip.name,image=output,models=players.Select(p=>p.name).ToArray(),sceneDirtyBefore=scene.isDirty,
                sceneObjectsBefore=roots.Sum(r=>r.GetComponentsInChildren<Transform>(true).Length)};
            try
            {
                // Screen-space camera canvases must lay out against the capture target, not the Editor Game view.
                rt=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32);rt.Create();
                camera.targetTexture=rt;camera.rect=new Rect(0,0,1,1);
                panel.selectionLabel.text=selected.Label;
                panel.statusLabel.text=selected.source+"  /  shared clip\n"+players.Length+" independent Humanoid Avatars\nEditor-authored resources & prefabs";
                canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=.4f;
                foreach(var label in canvas.GetComponentsInChildren<TMP_Text>(true))label.ForceMeshUpdate(true);
                Canvas.ForceUpdateCanvases();

                // A renderer update must not reevaluate the controller's static Ready state after sampling.
                // Disable evaluation first, then sample the clip using each model's own Avatar.
                for(int i=0;i<animators.Length;i++)animators[i].enabled=false;
                AnimationMode.StartAnimationMode(driver);
                AnimationMode.BeginSampling();
                try
                {
                    foreach(var animator in animators)
                        AnimationMode.SampleAnimationClip(animator.gameObject,selected.clip,selected.clip.length*.28f);
                }
                finally{AnimationMode.EndSampling();}
                report.sampledBoneAngles=BoneAngles(bones,originalRotations);
                if(report.sampledBoneAngles.Any(angle=>angle<.1f))
                    throw new InvalidOperationException("Capture did not produce a sampled pose on every model. See capture-check.json.");

                // A synchronous Editor render can reuse a stale GPU skinning palette. BakeMesh evaluates
                // the sampled bones on CPU; transient renderers display precisely that deformed geometry.
                // No baked mesh or replacement object is saved to the project or scene.
                report.bakedSkins=new SkinCapture[skins.Length];
                for(int i=0;i<skins.Length;i++)
                {
                    var skin=skins[i];
                    var mesh=new Mesh{name=skin.name+" sampled pose / capture only",hideFlags=HideFlags.HideAndDontSave};
                    temporaryMeshes.Add(mesh);
                    skin.BakeMesh(mesh,false);mesh.RecalculateBounds();
                    var vertices=mesh.vertices;
                    float displacement=0f;
                    for(int j=0;j<Mathf.Min(vertices.Length,baselineVertices[i].Length);j++)
                        displacement=Mathf.Max(displacement,Vector3.Distance(vertices[j],baselineVertices[i][j]));
                    report.bakedSkins[i]=new SkinCapture{renderer=skin.name,vertexCount=mesh.vertexCount,
                        maximumVertexDisplacement=displacement,boundsCenter=mesh.bounds.center,boundsSize=mesh.bounds.size};
                    CompareManualSkin(skin,vertices,report.bakedSkins[i]);
                    var skinCheck=report.bakedSkins[i];
                    if(skinCheck.sourceNonFiniteVertices!=0||skinCheck.bakedNonFiniteVertices!=0||skinCheck.manualNonFiniteVertices!=0
                        ||!string.IsNullOrEmpty(skinCheck.manualSkinError)||skinCheck.comparedVertices!=skinCheck.vertexCount)
                        throw new InvalidOperationException("Invalid or unverified sampled mesh: "+skin.name+". See capture-check.json.");
                    var temporary=new GameObject("Capture only — "+skin.name){hideFlags=HideFlags.HideAndDontSave,layer=skin.gameObject.layer};
                    temporaryObjects.Add(temporary);
                    temporary.transform.SetParent(skin.transform,false);
                    temporary.AddComponent<MeshFilter>().sharedMesh=mesh;
                    var renderer=temporary.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials=skin.sharedMaterials;
                    renderer.shadowCastingMode=skin.shadowCastingMode;renderer.receiveShadows=skin.receiveShadows;
                    renderer.lightProbeUsage=skin.lightProbeUsage;renderer.reflectionProbeUsage=skin.reflectionProbeUsage;
                    renderer.probeAnchor=skin.probeAnchor;renderer.renderingLayerMask=skin.renderingLayerMask;
                    renderer.sortingLayerID=skin.sortingLayerID;renderer.sortingOrder=skin.sortingOrder;
                    renderer.lightmapIndex=skin.lightmapIndex;renderer.lightmapScaleOffset=skin.lightmapScaleOffset;
                    skin.enabled=false;
                }
                if(report.bakedSkins.Length==0||report.bakedSkins.Any(s=>s.vertexCount==0))
                    throw new InvalidOperationException("No sampled skin geometry was available for capture.");
                var request=new UniversalRenderPipeline.SingleCameraRequest{destination=rt};
                RenderPipeline.SubmitRenderRequest(camera,request);
                report.renderedBoneAngles=BoneAngles(bones,originalRotations);
                if(report.renderedBoneAngles.Any(angle=>angle<.1f))
                    throw new InvalidOperationException("A model returned to its original pose during rendering. See capture-check.json.");
                RenderTexture.active=rt;image=new Texture2D(1600,900,TextureFormat.RGB24,false);
                image.ReadPixels(new Rect(0,0,1600,900),0,0);image.Apply();
                Directory.CreateDirectory("Reports/HumanAnimation");File.WriteAllBytes(output,image.EncodeToPNG());
                report.success=true;
            }
            finally
            {
                for(int i=0;i<skins.Length;i++)skins[i].enabled=skinEnabled[i];
                foreach(var temporary in temporaryObjects)if(temporary)UnityEngine.Object.DestroyImmediate(temporary);
                foreach(var mesh in temporaryMeshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
                if(AnimationMode.InAnimationMode(driver))AnimationMode.StopAnimationMode(driver);
                for(int i=0;i<animators.Length;i++)animators[i].enabled=enabledStates[i];
                UnityEngine.Object.DestroyImmediate(driver);
                camera.targetTexture=priorTarget;camera.rect=priorRect;RenderTexture.active=priorActive;
                canvas.renderMode=priorMode;canvas.worldCamera=priorCamera;canvas.planeDistance=priorDistance;
                panel.selectionLabel.text=priorSelection;panel.statusLabel.text=priorStatus;
                Canvas.ForceUpdateCanvases();
                report.restoredBoneAngles=BoneAngles(bones,originalRotations);
                report.poseRestored=report.restoredBoneAngles.All(angle=>angle<.01f);
                for(int i=0;i<bones.Length;i++)for(int j=0;j<bones[i].Length;j++)
                    report.poseRestored &= Vector3.Distance(bones[i][j].localPosition,originalPositions[i][j])<.0001f
                        && Vector3.Distance(bones[i][j].localScale,originalScales[i][j])<.0001f;
                report.sceneDirtyAfter=scene.isDirty;
                report.renderersRestored=skins.Select((skin,i)=>skin.enabled==skinEnabled[i]).All(restored=>restored);
                report.sceneObjectsAfter=scene.GetRootGameObjects().Sum(r=>r.GetComponentsInChildren<Transform>(true).Length);
                report.success &= report.poseRestored&&report.renderersRestored&&report.sceneDirtyBefore==report.sceneDirtyAfter
                    && report.sceneObjectsBefore==report.sceneObjectsAfter;
                if(rt!=null){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}if(image!=null)UnityEngine.Object.DestroyImmediate(image);
                Directory.CreateDirectory("Reports/HumanAnimation");
                File.WriteAllText("Reports/HumanAnimation/capture-check.json",JsonUtility.ToJson(report,true));
                if(!report.poseRestored)Debug.LogError("HumanAnimation capture did not restore every bone. See capture-check.json.");
            }
            if(!report.success)throw new InvalidOperationException("Capture changed the saved-pose or scene dirty state. See capture-check.json.");
        }

        static Vector3[] BakedVertices(SkinnedMeshRenderer renderer)
        {
            var mesh=new Mesh{hideFlags=HideFlags.HideAndDontSave};
            try{renderer.BakeMesh(mesh,false);return mesh.vertices;}
            finally{UnityEngine.Object.DestroyImmediate(mesh);}
        }

        // Independent CPU evaluation exposes invalid source vertices, stale native skinning, or
        // a mismatch between saved bind poses and the actual nested-prefab bone references.
        static void CompareManualSkin(SkinnedMeshRenderer skin,Vector3[] baked,SkinCapture report)
        {
            report.rootBone=skin.rootBone?skin.rootBone.name:"(none)";
            report.rendererPosition=skin.transform.position;
            report.rendererLocalScale=skin.transform.localScale;
            report.rendererLossyScale=skin.transform.lossyScale;
            var boneTransforms=skin.bones;
            report.bones=boneTransforms.Select(b=>b?b.name:"(missing)").ToArray();
            var source=skin.sharedMesh;
            var vertices=source.vertices;
            var weights=source.boneWeights;
            var binds=source.bindposes;
            report.sourceNonFiniteVertices=vertices.Count(v=>!Finite(v));
            report.bakedNonFiniteVertices=baked.Count(v=>!Finite(v));
            if(weights.Length!=vertices.Length||binds.Length!=boneTransforms.Length)
            {report.manualSkinError="Vertex/weight or bone/bind-pose count mismatch.";return;}
            if(boneTransforms.Any(b=>!b))
            {report.manualSkinError="A saved renderer bone reference is missing.";return;}
            var matrices=new Matrix4x4[binds.Length];
            for(int i=0;i<matrices.Length;i++)
                matrices[i]=skin.transform.worldToLocalMatrix*boneTransforms[i].localToWorldMatrix*binds[i];
            var minimum=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);
            var maximum=new Vector3(float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity);
            for(int i=0;i<vertices.Length;i++)
            {
                var weight=weights[i];
                var point=Vector3.zero;
                if(weight.weight0>0)point+=matrices[weight.boneIndex0].MultiplyPoint3x4(vertices[i])*weight.weight0;
                if(weight.weight1>0)point+=matrices[weight.boneIndex1].MultiplyPoint3x4(vertices[i])*weight.weight1;
                if(weight.weight2>0)point+=matrices[weight.boneIndex2].MultiplyPoint3x4(vertices[i])*weight.weight2;
                if(weight.weight3>0)point+=matrices[weight.boneIndex3].MultiplyPoint3x4(vertices[i])*weight.weight3;
                if(!Finite(point)){report.manualNonFiniteVertices++;continue;}
                minimum=Vector3.Min(minimum,point);maximum=Vector3.Max(maximum,point);
                if(i<baked.Length&&Finite(baked[i]))
                {
                    report.comparedVertices++;
                    report.maximumBakeVersusManualError=Mathf.Max(report.maximumBakeVersusManualError,Vector3.Distance(point,baked[i]));
                }
            }
            if(Finite(minimum)&&Finite(maximum))
            {report.manualBoundsCenter=(minimum+maximum)*.5f;report.manualBoundsSize=maximum-minimum;}
        }

        static bool Finite(Vector3 value) => !float.IsNaN(value.x)&&!float.IsInfinity(value.x)
            && !float.IsNaN(value.y)&&!float.IsInfinity(value.y)&&!float.IsNaN(value.z)&&!float.IsInfinity(value.z);

        static float[] BoneAngles(Transform[][] bones,Quaternion[][] baseline)
        {
            var result=new float[bones.Length];
            for(int i=0;i<bones.Length;i++)for(int j=0;j<bones[i].Length;j++)
                result[i]=Mathf.Max(result[i],Quaternion.Angle(bones[i][j].localRotation,baseline[i][j]));
            return result;
        }
    }
}
#endif
