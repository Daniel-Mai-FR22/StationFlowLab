using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Capture-only rendering scope. Projection, pose, geometry and the authored game view are preserved.</summary>
    public sealed class StationDecisionCaptureRenderProfile : IDisposable
    {
        public const string DefaultProfile = "copies";
        static readonly FieldInfo RendererIndex = typeof(UniversalAdditionalCameraData).GetField("m_RendererIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly UniversalAdditionalCameraData data;
        readonly UniversalRenderPipelineAsset pipeline;
        readonly int captureRenderer;
        readonly bool ownsData;
        public string Name { get; }

        public static string Normalize(string profile)
        {
            if (string.IsNullOrEmpty(profile) || profile == "default") return DefaultProfile;
            switch (profile)
            {
                case "full": case "ao-half": case "ao-off": case "balanced": case "copies": case "lean": return profile;
                default: throw new ArgumentException("Unknown capture render profile: " + profile);
            }
        }

        public StationDecisionCaptureRenderProfile(Camera camera, string profile)
        {
            if (!camera) throw new ArgumentNullException(nameof(camera));
            Name = Normalize(profile);
            if (Name == "full") return;
            pipeline = UniversalRenderPipeline.asset;
            if (!pipeline || RendererIndex == null)
                throw new NotSupportedException("The capture rendering profile requires the packaged URP renderer configuration.");
            captureRenderer = Name == "lean" ? 3 : Name == "ao-off" ? 2 : 1;
            // The packaged PC asset keeps its authored renderer at 0; capture-only renderers occupy 1/2/3.
            if (ReferenceEquals(pipeline.GetRenderer(captureRenderer), pipeline.GetRenderer(0)))
                throw new NotSupportedException("The active pipeline is missing the capture-only renderer at index " + captureRenderer + ".");
            data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (!data) { data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>(); ownsData = true; }
        }

        public RenderScope Apply()
        {
            if (Name == "full") return default;
            if (!data || UniversalRenderPipeline.asset != pipeline)
                throw new InvalidOperationException("Capture camera or render pipeline changed during the session.");
            int originalRenderer = (int)RendererIndex.GetValue(data);
            var scope = new RenderScope(data, originalRenderer, pipeline, pipeline.shadowDistance, pipeline.shadowCascadeCount,
                pipeline.mainLightShadowmapResolution, data.requiresDepthOption, data.requiresColorOption);
            try
            {
                data.SetRenderer(captureRenderer);
                if (Name == "balanced" || Name == "copies" || Name == "lean")
                {
                    pipeline.shadowDistance = Mathf.Min(pipeline.shadowDistance, 70f);
                    pipeline.shadowCascadeCount = Math.Min(pipeline.shadowCascadeCount, 2);
                }
                if (Name == "copies" || Name == "lean")
                {
                    // These are optional camera copies, not the depth buffer used for visibility.
                    // The capture renderer feature independently declares the depth/normal inputs it needs.
                    // Packaged station shaders do not sample _CameraOpaqueTexture or _CameraDepthTexture.
                    data.requiresDepthOption = CameraOverrideOption.Off;
                    data.requiresColorOption = CameraOverrideOption.Off;
                }
                if (Name == "lean")
                {
                    pipeline.shadowCascadeCount = 1;
                    pipeline.mainLightShadowmapResolution = Math.Min(pipeline.mainLightShadowmapResolution, 1024);
                }
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (ownsData && data) UnityEngine.Object.Destroy(data);
        }

        // URP SingleCameraRequest processes the camera synchronously before SubmitRenderRequest returns.
        // Restore settings in finally before any subsequent game-view render. GPU commands already own their constants.
        public struct RenderScope : IDisposable
        {
            readonly UniversalAdditionalCameraData data;
            readonly int rendererIndex, cascadeCount, shadowmapResolution;
            readonly CameraOverrideOption depthOption, colorOption;
            readonly UniversalRenderPipelineAsset pipeline;
            readonly float shadowDistance;
            internal RenderScope(UniversalAdditionalCameraData data, int rendererIndex, UniversalRenderPipelineAsset pipeline, float shadowDistance, int cascadeCount,
                int shadowmapResolution, CameraOverrideOption depthOption, CameraOverrideOption colorOption)
            {
                this.data = data; this.rendererIndex = rendererIndex; this.pipeline = pipeline; this.shadowDistance = shadowDistance; this.cascadeCount = cascadeCount;
                this.shadowmapResolution = shadowmapResolution; this.depthOption = depthOption; this.colorOption = colorOption;
            }
            public void Dispose()
            {
                if (pipeline) { pipeline.shadowDistance = shadowDistance; pipeline.shadowCascadeCount = cascadeCount; pipeline.mainLightShadowmapResolution = shadowmapResolution; }
                if (data) { data.SetRenderer(rendererIndex); data.requiresDepthOption = depthOption; data.requiresColorOption = colorOption; }
            }
        }
    }
}
