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

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Reference images of the saved actor poses. Temporary CPU skin meshes are never saved.</summary>
    public static class StationSocialCapture
    {
        [MenuItem("Efrei Research Lab/Train Station Social/Save reference images")]
        public static void Capture()=>CaptureInternal(null,false);
        public static void CaptureLiveReception()=>CaptureInternal("conversation-live.png",false);
        public static void CaptureLiveCleaner()=>CaptureInternal("cleaning-live.png",true);
        public static void CaptureCleanerFailure(StationCleaner cleaner)=>CaptureInternal("cleaning-failure.png",true,cleaner);
        public static void CaptureLiveGroup(StationConversationGroup group)=>CaptureInternal("group-conversation-live.png",false,null,group);
        public static void CaptureLiveShop(StationInformationDesk desk)=>CaptureInternal("shop-conversation-live.png",false,null,null,desk);
        public static void CaptureLiveHall()=>CaptureInternal("mixed-hall-live.png",false,null,null,null,true);

        static void CaptureInternal(string liveFilename,bool cleanerCloseup,StationCleaner selectedCleaner=null,StationConversationGroup selectedGroup=null,StationInformationDesk selectedShop=null,bool wideHall=false)
        {
            if(AnimationMode.InAnimationMode()||(liveFilename==null&&EditorApplication.isPlayingOrWillChangePlaymode)
                ||(liveFilename!=null&&!Application.isPlaying))
                throw new InvalidOperationException("Use authored capture in Edit mode and live capture in Play mode, outside animation preview.");
            var scene=SceneManager.GetActiveScene();
            var roots=scene.GetRootGameObjects();
            var world=roots.SelectMany(g=>g.GetComponentsInChildren<StationSocialWorld>(true)).Single();
            var camera=roots.SelectMany(g=>g.GetComponentsInChildren<Camera>(true)).First(c=>c.isActiveAndEnabled);
            var canvases=roots.SelectMany(g=>g.GetComponentsInChildren<Canvas>(true)).ToArray();
            var canvasStates=canvases.Select(c=>c.enabled).ToArray();
            var skins=world.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s=>s.enabled&&s.gameObject.activeInHierarchy).ToArray();
            var skinStates=skins.Select(s=>s.enabled).ToArray();
            var objects=new List<GameObject>();var meshes=new List<Mesh>();
            Vector3 priorPosition=camera.transform.position;Quaternion priorRotation=camera.transform.rotation;
            float priorFov=camera.fieldOfView;Rect priorRect=camera.rect;var priorTarget=camera.targetTexture;
            var priorActive=RenderTexture.active;
            bool dirty=scene.isDirty;
            int priorObjects=roots.Sum(g=>g.GetComponentsInChildren<Transform>(true).Length);
            RenderTexture target=null;
            try
            {
                foreach(var canvas in canvases)canvas.enabled=false;
                foreach(var skin in skins)
                {
                    var mesh=new Mesh{name="Station social capture pose",hideFlags=HideFlags.HideAndDontSave};meshes.Add(mesh);
                    skin.BakeMesh(mesh,false);mesh.RecalculateBounds();
                    if(mesh.vertexCount==0||mesh.vertices.Any(v=>!Finite(v)))throw new InvalidOperationException("Invalid baked actor geometry: "+skin.name);
                    var proxy=new GameObject("Capture only — "+skin.name){hideFlags=HideFlags.HideAndDontSave,layer=skin.gameObject.layer};objects.Add(proxy);
                    proxy.transform.SetParent(skin.transform,false);
                    proxy.AddComponent<MeshFilter>().sharedMesh=mesh;
                    var renderer=proxy.AddComponent<MeshRenderer>();renderer.sharedMaterials=skin.sharedMaterials;
                    renderer.shadowCastingMode=skin.shadowCastingMode;renderer.receiveShadows=skin.receiveShadows;
                    renderer.lightProbeUsage=skin.lightProbeUsage;renderer.reflectionProbeUsage=skin.reflectionProbeUsage;
                    renderer.renderingLayerMask=skin.renderingLayerMask;renderer.probeAnchor=skin.probeAnchor;
                    skin.enabled=false;
                }
                target=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32);target.Create();
                camera.targetTexture=target;camera.rect=new Rect(0,0,1,1);camera.fieldOfView=50;
                Directory.CreateDirectory("Reports/TrainStationSocial");
                var desk=world.informationDesk.ReceptionistPosition;
                if(liveFilename!=null)
                {
                    camera.fieldOfView=45;
                    if(selectedShop)
                        RenderShop(camera,target,selectedShop,liveFilename);
                    else if(wideHall)
                    {
                        camera.fieldOfView=65;
                        Render(camera,target,new Vector3(18,8,-21),new Vector3(-7,1.2f,9),liveFilename);
                    }
                    else if(selectedGroup)
                    {
                        RenderGroup(camera,target,selectedGroup,liveFilename);
                        camera.fieldOfView=58;
                        Render(camera,target,desk+new Vector3(18,9,-29),desk+new Vector3(-4,1,2),"hall-density-live.png");
                    }
                    else if(cleanerCloseup)
                    {
                        var cleaner=(selectedCleaner?selectedCleaner:world.cleaners.First(c=>c.CurrentState==StationCleanerState.Working)).transform;
                        Render(camera,target,cleaner.position+cleaner.forward*4.1f+cleaner.right*3f+Vector3.up*2.1f,
                            cleaner.position+Vector3.up*.9f,liveFilename);
                    }
                    else Render(camera,target,desk+new Vector3(4.5f,2.6f,-7),desk+new Vector3(0,1.05f,-1),liveFilename);
                }
                else
                {
                    camera.fieldOfView=58;
                    Render(camera,target,desk+new Vector3(18,9,-29),desk+new Vector3(-4,1,2),"hall-social.png");
                    camera.fieldOfView=45;
                    Render(camera,target,desk+new Vector3(5,3,-7),desk+new Vector3(0,1.1f,0),"accueil-social.png");
                    if(world.passengers!=null&&world.passengers.Length>0&&world.passengers[0])
                    {
                        var actor=world.passengers[0].transform;
                        Render(camera,target,actor.position+actor.forward*4.8f+actor.right*2f+Vector3.up*1.7f,
                            actor.position+Vector3.up*.9f,"seated-passengers.png");
                    }
                    var group=world.conversationGroups?.FirstOrDefault(g=>g&&g.members!=null&&g.members.Length>0);
                    if(group)RenderGroup(camera,target,group,"standing-conversation-group.png");
                    if(world.shopDesks!=null)
                        for(int i=0;i<world.shopDesks.Length;i++)
                            if(world.shopDesks[i]) RenderShop(camera,target,world.shopDesks[i],"shop-"+(i+1).ToString("00")+"-staff.png");
                    var walkingGroup=world.walkingGroups?.FirstOrDefault(g=>g&&g.members.Length>0);
                    if(walkingGroup)
                    {
                        var centre=walkingGroup.members.Where(p=>p).Aggregate(Vector3.zero,(sum,p)=>sum+p.transform.position)/walkingGroup.members.Count(p=>p);
                        camera.fieldOfView=48;
                        Render(camera,target,centre+new Vector3(4,3,-6),centre+Vector3.up,"walking-group-saved.png");
                    }
                }
            }
            finally
            {
                for(int i=0;i<skins.Length;i++)skins[i].enabled=skinStates[i];
                foreach(var proxy in objects)if(proxy)UnityEngine.Object.DestroyImmediate(proxy);
                foreach(var mesh in meshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
                for(int i=0;i<canvases.Length;i++)canvases[i].enabled=canvasStates[i];
                camera.transform.SetPositionAndRotation(priorPosition,priorRotation);camera.fieldOfView=priorFov;
                camera.rect=priorRect;camera.targetTexture=priorTarget;RenderTexture.active=priorActive;
                if(target){target.Release();UnityEngine.Object.DestroyImmediate(target);}
                if(scene.isDirty!=dirty||scene.GetRootGameObjects().Sum(g=>g.GetComponentsInChildren<Transform>(true).Length)!=priorObjects)
                    throw new InvalidOperationException("Capture changed the scene dirty state or left temporary objects behind.");
            }
        }

        static void RenderGroup(Camera camera,RenderTexture target,StationConversationGroup group,string filename)
        {
            var members=group.members.Where(m=>m).ToArray();
            if(members.Length==0)throw new InvalidOperationException("Conversation capture requires saved group members.");
            var centre=members.Aggregate(Vector3.zero,(sum,m)=>sum+m.transform.position)/members.Length;
            camera.fieldOfView=45;
            Render(camera,target,centre+new Vector3(4,2.8f,-5.5f),centre+Vector3.up*.95f,filename);
        }

        static void RenderShop(Camera camera,RenderTexture target,StationInformationDesk desk,string filename)
        {
            var staff=desk.receptionist.transform;
            var front=desk.queueSlots[0].position;
            var towardsCustomer=front-staff.position;towardsCustomer.y=0;towardsCustomer.Normalize();
            var side=Vector3.Cross(Vector3.up,towardsCustomer);
            camera.fieldOfView=57;
            Render(camera,target,front+towardsCustomer*4.0f+side*1.2f+Vector3.up*2.3f,
                (front+staff.position)*.5f+Vector3.up*1.0f,filename);
        }

        static void Render(Camera camera,RenderTexture target,Vector3 position,Vector3 lookAt,string filename)
        {
            camera.transform.position=position;camera.transform.LookAt(lookAt);
            RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target;
            var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            try{image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();File.WriteAllBytes("Reports/TrainStationSocial/"+filename,image.EncodeToPNG());}
            finally{UnityEngine.Object.DestroyImmediate(image);}
        }
        static bool Finite(Vector3 v)=>!float.IsNaN(v.x)&&!float.IsInfinity(v.x)&&!float.IsNaN(v.y)&&!float.IsInfinity(v.y)&&!float.IsNaN(v.z)&&!float.IsInfinity(v.z);
    }
}
#endif
