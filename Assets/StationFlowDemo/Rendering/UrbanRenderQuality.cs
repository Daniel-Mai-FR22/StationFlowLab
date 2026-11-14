using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StationFlow.SyntheticTraffic
{
    /// <summary>Consistent HDR lighting, filmic response and antialiasing for the live scene and sensors.</summary>
    public sealed class UrbanRenderQuality : MonoBehaviour
    {
        UniversalRenderPipelineAsset pipeline;
        RenderPipelineAsset originalPipeline;
        Volume volume;
        ReflectionProbe probe;
        Material sky;
        ColorAdjustments colors;
        WhiteBalance whiteBalance;
        public bool HighQuality { get; private set; } = true;

        public void Initialize()
        {
            originalPipeline=QualitySettings.renderPipeline;
            var source=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if(source!=null)
            {
                pipeline=Instantiate(source);pipeline.name="Junction Lab high quality";
                pipeline.supportsHDR=true;pipeline.renderScale=1;pipeline.shadowDistance=115;pipeline.msaaSampleCount=1;
                QualitySettings.renderPipeline=pipeline;
            }
            QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
            QualitySettings.lodBias=2;QualitySettings.shadowDistance=115;
            volume=gameObject.AddComponent<Volume>();volume.isGlobal=true;volume.priority=50;
            volume.sharedProfile=ScriptableObject.CreateInstance<VolumeProfile>();
            var tone=volume.sharedProfile.Add<Tonemapping>(true);tone.mode.Override(TonemappingMode.ACES);
            colors=volume.sharedProfile.Add<ColorAdjustments>(true);colors.postExposure.Override(-.15f);colors.contrast.Override(5);colors.saturation.Override(-5);
            whiteBalance=volume.sharedProfile.Add<WhiteBalance>(true);whiteBalance.temperature.Override(3);
            var bloom=volume.sharedProfile.Add<Bloom>(true);bloom.intensity.Override(.12f);bloom.threshold.Override(1.4f);bloom.scatter.Override(.45f);
            Texture2D hdr=Resources.Load<Texture2D>("StationFlowLighting/clouds");
            Shader skyShader=Shader.Find("Skybox/Panoramic");
            if(hdr!=null&&skyShader!=null)
            {
                sky=new Material(skyShader){name="CC0 photographic sky"};sky.SetTexture("_MainTex",hdr);sky.SetFloat("_Exposure",.85f);sky.SetFloat("_Rotation",145);
                RenderSettings.skybox=sky;
            }
            probe=new GameObject("Quarter reflection capture").AddComponent<ReflectionProbe>();probe.transform.SetParent(transform,false);
            probe.transform.position=new Vector3(0,5,0);probe.size=new Vector3(180,70,180);
            probe.mode=ReflectionProbeMode.Realtime;probe.refreshMode=ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode=ReflectionProbeTimeSlicingMode.IndividualFaces;probe.resolution=256;
            probe.hdr=true;probe.boxProjection=true;probe.intensity=.85f;probe.clearFlags=ReflectionProbeClearFlags.Skybox;
            probe.farClipPlane=180;probe.nearClipPlane=.3f;
            StartCoroutine(RefreshReflections());
        }
        public void SetLighting(int preset)
        {
            if(colors==null)return;
            colors.postExposure.Override(preset==1?-.1f:preset==2?.15f:-.15f);
            whiteBalance.temperature.Override(preset==1?9:preset==2?-3:3);
            if(sky!=null){sky.SetFloat("_Exposure",preset==2?.65f:.85f);RenderSettings.skybox=sky;}
            StartCoroutine(RefreshReflections());
        }
        IEnumerator RefreshReflections(){yield return null;yield return null;if(probe!=null)probe.RenderProbe();}
        public void ToggleQuality()
        {HighQuality=!HighQuality;if(pipeline!=null)pipeline.renderScale=HighQuality?1:.8f;QualitySettings.lodBias=HighQuality?2:1;}
        public static void ConfigureCamera(Camera camera,bool sensor=false)
        {
            camera.allowHDR=true;camera.allowMSAA=false;
            var data=camera.GetUniversalAdditionalCameraData();data.renderPostProcessing=true;
            data.antialiasing=AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality=AntialiasingQuality.High;
            data.requiresDepthOption=CameraOverrideOption.On;data.renderShadows=true;
        }
        void OnDestroy()
        {
            if(pipeline!=null){QualitySettings.renderPipeline=originalPipeline;Destroy(pipeline);}
            if(volume!=null&&volume.sharedProfile!=null)Destroy(volume.sharedProfile);
            if(sky!=null)Destroy(sky);
        }
    }
}
