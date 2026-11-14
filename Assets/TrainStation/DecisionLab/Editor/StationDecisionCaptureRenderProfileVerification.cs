#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Native asset bindings and exception-safe capture scopes; no rendering or scene authoring.</summary>
    public static class StationDecisionCaptureRenderProfileVerification
    {
        public const string ReportPath = "Reports/FpsAsyncMulticam20261020/render-profiles-native.json";
        const string PipelinePath = "Assets/Settings/PC_RPAsset.asset";
        static readonly string[] RendererPaths = { "Assets/Settings/PC_Renderer.asset", "Assets/Settings/Capture_Renderer_HalfAO.asset", "Assets/Settings/Capture_Renderer_NoAO.asset", "Assets/Settings/Capture_Renderer_Lean.asset" };
        [Serializable] sealed class AssetEvidence
        { public string path, sha256Before, sha256After; public int rendererIndex; public bool aoActive, aoDownsample, aoAfterOpaque, filePreserved; public int aoSource; }
        [Serializable] sealed class ScopeEvidence
        {
            public string profile;
            public int originalRendererIndex, appliedRendererIndex, originalCascades, appliedCascades;
            public float originalShadowDistance, appliedShadowDistance;
            public int originalShadowmapResolution, appliedShadowmapResolution;
            public string originalDepthOption, originalColorOption, appliedDepthOption, appliedColorOption;
            public bool copyOptionsRestored, shadowmapRestored;
            public bool exceptionExit, exceptionObserved, geometryPreserved, rendererRestored, shadowsRestored, additionalDataPreserved;
        }
        [Serializable] sealed class PrefilterChange
        { public string field, before, after; }
        [Serializable] sealed class ShaderFeatureWarmup
        {
            public string method = "UnityEditor.Rendering.Universal.ShaderBuildPreprocessor.GatherShaderFeatures(false)", path = PipelinePath,
                sha256Before, sha256After, sha256AfterSecondPass;
            public bool completed, fileChanged, onlyGeneratedPrefilterFieldsChanged, secondPassStable;
            public List<PrefilterChange> changes = new List<PrefilterChange>();
        }
        [Serializable] sealed class Report
        {
            public string schema = "stationflow.capture_render_profile_verification.v1", checkedUtc, error, pipeline;
            public bool success, noRendering = true, scratchDestroyed, pipelineStatePreserved, assetFilesPreserved, nestedScopesRestored;
            public int checks;
            public ShaderFeatureWarmup shaderFeatureWarmup;
            public List<AssetEvidence> assets = new List<AssetEvidence>();
            public List<ScopeEvidence> scopes = new List<ScopeEvidence>();
            public List<string> cleanupErrors = new List<string>();
        }
        [Serializable] sealed class BuildOutcome
        { public bool success; public string buildLabel, outputDirectory, applicationBuildGuid, error; }
        [Serializable] sealed class BuildVerificationReport
        {
            public string startedUtc, completedUtc, error, contractFingerprint, buildLabel, outputDirectory, applicationBuildGuid;
            public string nativeReport = ReportPath, portableReport = StationDecisionPortableBuild.ReportPath;
            public bool success;
            public int nativeChecks, schemaChecks;
        }

        /// <summary>Batch entry: native scope checks, current French contract, then a fresh versioned Desktop build.</summary>
        public static void VerifyAndBuild()
        {
            var report = new BuildVerificationReport { startedUtc = DateTime.UtcNow.ToString("O") };
            try
            {
                report.nativeChecks = Run();
                report.contractFingerprint = StationDecisionDataSchemaDocumentation.Generate();
                report.schemaChecks = StationDecisionDataSchemaDocumentation.Validate();
                StationDecisionPortableBuild.Build();
                var built = JsonUtility.FromJson<BuildOutcome>(File.ReadAllText(StationDecisionPortableBuild.ReportPath));
                if (built == null || !built.success) throw new InvalidOperationException("Portable build receipt failed: " + (built == null ? "missing JSON result" : built.error));
                report.buildLabel = built.buildLabel; report.outputDirectory = built.outputDirectory; report.applicationBuildGuid = built.applicationBuildGuid;
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                report.completedUtc = DateTime.UtcNow.ToString("O");
                const string path = "Reports/FpsImplementation20261020/verify-and-build.json";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }
        }

        sealed class ExpectedScopeException : Exception { }
        sealed class Geometry
        {
            public readonly Vector3 position;
            public readonly Quaternion rotation;
            public readonly Matrix4x4 projection, worldToCamera;
            public readonly string cameraJson;
            public Geometry(Camera camera)
            {
                position = camera.transform.position; rotation = camera.transform.rotation;
                projection = camera.projectionMatrix; worldToCamera = camera.worldToCameraMatrix;
                cameraJson = EditorJsonUtility.ToJson(camera);
            }
            public bool Matches(Camera camera) => position.Equals(camera.transform.position) && rotation.Equals(camera.transform.rotation) &&
                SameMatrix(projection, camera.projectionMatrix) && SameMatrix(worldToCamera, camera.worldToCameraMatrix) && cameraJson == EditorJsonUtility.ToJson(camera);
        }

        public static int Run()
        {
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), pipeline = PipelinePath };
            UniversalRenderPipelineAsset pipeline = null;
            GameObject scratch = null;
            float originalDistance = 0;
            int originalCascades = 0, originalShadowmap = 0;
            string originalPipelineJson = null;
            bool originalDirty = false;
            Exception failure = null;
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "Render profile checks require stopped compiled Edit mode.", report);
                pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
                Require(pipeline && UniversalRenderPipeline.asset == pipeline, "The active quality must use the packaged PC_RPAsset.", report);
                // Explicit source setup, BEFORE the scope/asset preservation baseline. This is the same URP
                // discovery used by release builds; it saves generated keyword filters, not authored budgets.
                WarmShaderFeatures(pipeline, report);
                originalDistance = pipeline.shadowDistance; originalCascades = pipeline.shadowCascadeCount; originalShadowmap = pipeline.mainLightShadowmapResolution;
                originalPipelineJson = EditorJsonUtility.ToJson(pipeline); originalDirty = EditorUtility.IsDirty(pipeline);
                VerifyAssets(pipeline, report);
                Require(StationDecisionCaptureRenderProfile.Normalize("full") == "full" && StationDecisionCaptureRenderProfile.Normalize("ao-half") == "ao-half" &&
                    StationDecisionCaptureRenderProfile.Normalize("ao-off") == "ao-off" && StationDecisionCaptureRenderProfile.Normalize("balanced") == "balanced" &&
                    StationDecisionCaptureRenderProfile.Normalize("copies") == "copies" && StationDecisionCaptureRenderProfile.Normalize("lean") == "lean", "Known profiles were remapped.", report);
                Require(StationDecisionCaptureRenderProfile.Normalize(null) == StationDecisionCaptureRenderProfile.DefaultProfile &&
                    StationDecisionCaptureRenderProfile.Normalize("default") == StationDecisionCaptureRenderProfile.DefaultProfile, "Default profile aliases disagree.", report);
                bool invalidRejected = false;
                try { StationDecisionCaptureRenderProfile.Normalize("not-a-profile"); } catch (ArgumentException) { invalidRejected = true; }
                Require(invalidRejected, "Unknown render profile was accepted.", report);
                bool nullRejected = false;
                try { new StationDecisionCaptureRenderProfile(null, "balanced"); } catch (ArgumentNullException) { nullRejected = true; }
                Require(nullRejected, "Missing camera was accepted.", report);

                scratch = new GameObject("Capture render profile verification (temporary)") { hideFlags = HideFlags.HideAndDontSave };
                scratch.SetActive(false);
                var camera = scratch.AddComponent<Camera>(); camera.enabled = false;
                var data = scratch.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = true; data.renderShadows = true;
                camera.transform.SetPositionAndRotation(new Vector3(6.3f, 7.4f, -9.1f), Quaternion.Euler(24f, 133f, 3f));
                camera.fieldOfView = 63f; camera.aspect = 1.37f; camera.nearClipPlane = .08f; camera.farClipPlane = 241f;
                camera.rect = new Rect(.1f, .2f, .7f, .6f);
                camera.projectionMatrix = Matrix4x4.Frustum(-.11f, .14f, -.09f, .12f, .08f, 241f);
                var geometry = new Geometry(camera);
                foreach (int initialIndex in new[] { -1, 0 })
                foreach (string profile in new[] { "full", "ao-half", "ao-off", "balanced", "copies", "lean" })
                foreach (var depth in new[] { CameraOverrideOption.On, CameraOverrideOption.Off, CameraOverrideOption.UsePipelineSettings })
                foreach (bool exceptionExit in new[] { false, true })
                    VerifyScope(camera, data, geometry, pipeline, profile, initialIndex, 115f, 4, 2048, depth,
                        depth == CameraOverrideOption.On ? CameraOverrideOption.Off : CameraOverrideOption.On, exceptionExit, report);
                // Balanced must never enlarge a stricter caller's shadow range or cascade count.
                foreach (int initialIndex in new[] { -1, 0 })
                foreach (string profile in new[] { "balanced", "copies", "lean" })
                foreach (bool exceptionExit in new[] { false, true })
                    VerifyScope(camera, data, geometry, pipeline, profile, initialIndex, 35f, 1, 512,
                        CameraOverrideOption.UsePipelineSettings, CameraOverrideOption.UsePipelineSettings, exceptionExit, report);
                VerifyNested(camera, data, geometry, pipeline, report);
            }
            catch (Exception error) { failure = error; report.error = error.ToString(); }
            finally
            {
                try
                {
                    if (pipeline && originalPipelineJson != null)
                    {
                        pipeline.shadowDistance = originalDistance; pipeline.shadowCascadeCount = originalCascades; pipeline.mainLightShadowmapResolution = originalShadowmap;
                        report.pipelineStatePreserved = originalPipelineJson == EditorJsonUtility.ToJson(pipeline) && originalDirty == EditorUtility.IsDirty(pipeline);
                        Require(report.pipelineStatePreserved, "Verification changed persistent pipeline state or dirty status.", report);
                    }
                }
                catch (Exception error) { report.cleanupErrors.Add(error.ToString()); if (failure == null) failure = error; }
                try { if (scratch) UnityEngine.Object.DestroyImmediate(scratch); report.scratchDestroyed = !scratch; }
                catch (Exception error) { report.cleanupErrors.Add(error.ToString()); if (failure == null) failure = error; }
                try
                {
                    foreach (var asset in report.assets) { asset.sha256After = Digest(asset.path); asset.filePreserved = asset.sha256Before == asset.sha256After; }
                    report.assetFilesPreserved = report.assets.Count == 5 && report.assets.All(a => a.filePreserved);
                    Require(report.assetFilesPreserved, "Verification changed renderer/pipeline asset files.", report);
                }
                catch (Exception error) { report.cleanupErrors.Add(error.ToString()); if (failure == null) failure = error; }
                report.success = failure == null;
                if (failure != null && string.IsNullOrEmpty(report.error)) report.error = failure.ToString();
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }
            if (failure != null) throw new InvalidOperationException("Capture render profile verification failed; inspect " + ReportPath, failure);
            return report.checks;
        }

        static void WarmShaderFeatures(UniversalRenderPipelineAsset pipeline, Report report)
        {
            var evidence = new ShaderFeatureWarmup(); report.shaderFeatureWarmup = evidence;
            Require(!EditorUtility.IsDirty(pipeline), "Shader feature setup requires the saved pipeline; it must not persist unsaved user edits.", report);
            string before = File.ReadAllText(PipelinePath, Encoding.UTF8);
            evidence.sha256Before = Digest(PipelinePath);
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("UnityEditor.Rendering.Universal.ShaderBuildPreprocessor", false))
                .FirstOrDefault(candidate => candidate != null);
            var gather = type?.GetMethod("GatherShaderFeatures", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
            Require(gather != null, "The packaged URP release shader-feature discovery method is unavailable.", report);
            gather.Invoke(null, new object[] { false });
            // GatherShaderFeatures itself uses SaveAssetIfDirty for build-target URP assets. Save only this
            // explicit asset again, avoiding SaveAssets() which could persist unrelated unsaved user work.
            AssetDatabase.SaveAssetIfDirty(pipeline);
            string after = File.ReadAllText(PipelinePath, Encoding.UTF8);
            evidence.sha256After = Digest(PipelinePath); evidence.fileChanged = evidence.sha256Before != evidence.sha256After;
            var original = PrefilterValues(before); var current = PrefilterValues(after);
            foreach (string field in original.Keys.Union(current.Keys).OrderBy(name => name, StringComparer.Ordinal))
            {
                original.TryGetValue(field, out var oldValue); current.TryGetValue(field, out var newValue);
                if (oldValue != newValue) evidence.changes.Add(new PrefilterChange { field = field, before = oldValue, after = newValue });
            }
            evidence.onlyGeneratedPrefilterFieldsChanged = WithoutPrefilterValues(before) == WithoutPrefilterValues(after);
            Require(evidence.onlyGeneratedPrefilterFieldsChanged, "Shader-feature initialization changed an authored pipeline field; inspect warmup evidence.", report);
            gather.Invoke(null, new object[] { false }); AssetDatabase.SaveAssetIfDirty(pipeline);
            evidence.sha256AfterSecondPass = Digest(PipelinePath);
            evidence.secondPassStable = evidence.sha256After == evidence.sha256AfterSecondPass;
            Require(evidence.secondPassStable, "URP shader-feature initialization did not stabilize on the second release discovery pass.", report);
            evidence.completed = true;
        }
        static Dictionary<string, string> PrefilterValues(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in NormalizedLines(text))
            {
                if (!line.StartsWith("  m_Prefilter", StringComparison.Ordinal)) continue;
                int colon = line.IndexOf(':');
                if (colon > 0) result.Add(line.Substring(2, colon - 2), line.Substring(colon + 1).Trim());
            }
            return result;
        }
        static string WithoutPrefilterValues(string text) => string.Join("\n", NormalizedLines(text).Where(line => !line.StartsWith("  m_Prefilter", StringComparison.Ordinal)));
        static string[] NormalizedLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        static void VerifyAssets(UniversalRenderPipelineAsset pipeline, Report report)
        {
            report.assets.Add(new AssetEvidence { path = PipelinePath, rendererIndex = -1, sha256Before = Digest(PipelinePath) });
            var serialized = new SerializedObject(pipeline);
            var list = serialized.FindProperty("m_RendererDataList");
            Require(list != null && list.isArray && list.arraySize >= 4, "Packaged capture renderers are missing.", report);
            Require(serialized.FindProperty("m_DefaultRendererIndex").intValue == 0, "The authored game renderer must remain the default.", report);
            UniversalRendererData authored = null;
            ScriptableRendererFeature authoredAo = null;
            for (int index = 0; index < RendererPaths.Length; ++index)
            {
                string path = RendererPaths[index];
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                Require(renderer && list.GetArrayElementAtIndex(index).objectReferenceValue == renderer, "Wrong renderer asset at index " + index, report);
                Require(renderer.rendererFeatures.Count == 1 && renderer.rendererFeatures[0], "Unexpected feature layout in " + path, report);
                var ao = renderer.rendererFeatures.Single();
                Require(ao.GetType().Name == "ScreenSpaceAmbientOcclusion", "Expected SSAO feature in " + path, report);
                var settings = new SerializedObject(ao).FindProperty("m_Settings");
                Require(settings != null, "Missing serialized SSAO settings.", report);
                bool downsample = settings.FindPropertyRelative("Downsample").boolValue;
                bool afterOpaque = settings.FindPropertyRelative("AfterOpaque").boolValue;
                int source = settings.FindPropertyRelative("Source").intValue;
                Require(ao.isActive == (index != 2) && downsample == (index != 0), "Wrong SSAO enabled/downsample settings in " + path, report);
                Require(afterOpaque == (index == 3) && source == (index == 3 ? 0 : 1), "Wrong SSAO source/timing in " + path, report);
                if (index == 0) { authored = renderer; authoredAo = ao; }
                else
                {
                    Require(!ReferenceEquals(ao, authoredAo), "Capture feature shares the authored mutable feature object.", report);
                    var original = new SerializedObject(authoredAo).FindProperty("m_Settings");
                    foreach (string name in new[] { "AOMethod", "NormalSamples", "Samples", "BlurQuality" })
                        Require(settings.FindPropertyRelative(name).intValue == original.FindPropertyRelative(name).intValue, "Unintended SSAO quality change: " + name, report);
                    foreach (string name in new[] { "Intensity", "DirectLightingStrength", "Radius", "Falloff" })
                        Require(settings.FindPropertyRelative(name).floatValue.Equals(original.FindPropertyRelative(name).floatValue), "Unintended SSAO appearance change: " + name, report);
                    var currentRenderer = new SerializedObject(renderer); var originalRenderer = new SerializedObject(authored);
                    Require(currentRenderer.FindProperty("m_RenderingMode").intValue == originalRenderer.FindProperty("m_RenderingMode").intValue &&
                        currentRenderer.FindProperty("postProcessData").objectReferenceValue == originalRenderer.FindProperty("postProcessData").objectReferenceValue, "Capture renderer changed rendering path or postprocess bindings.", report);
                    foreach (string name in new[] { "m_OpaqueLayerMask", "m_TransparentLayerMask", "m_PrepassLayerMask" })
                        Require(currentRenderer.FindProperty(name).intValue == originalRenderer.FindProperty(name).intValue, "Capture renderer changed visibility mask: " + name, report);
                }
                report.assets.Add(new AssetEvidence { path = path, rendererIndex = index, aoActive = ao.isActive, aoDownsample = downsample, aoAfterOpaque = afterOpaque, aoSource = source, sha256Before = Digest(path) });
            }
        }

        static void VerifyScope(Camera camera, UniversalAdditionalCameraData data, Geometry geometry, UniversalRenderPipelineAsset pipeline,
            string profile, int initialIndex, float distance, int cascades, int shadowmap,
            CameraOverrideOption depthOption, CameraOverrideOption colorOption, bool exceptionExit, Report report)
        {
            data.SetRenderer(initialIndex); pipeline.shadowDistance = distance; pipeline.shadowCascadeCount = cascades;
            pipeline.mainLightShadowmapResolution = shadowmap; data.requiresDepthOption = depthOption; data.requiresColorOption = colorOption;
            string dataBefore = EditorJsonUtility.ToJson(data);
            var evidence = new ScopeEvidence { profile = profile, originalRendererIndex = initialIndex, originalShadowDistance = distance,
                originalCascades = cascades, originalShadowmapResolution = shadowmap, originalDepthOption = depthOption.ToString(),
                originalColorOption = colorOption.ToString(), exceptionExit = exceptionExit };
            report.scopes.Add(evidence);
            using (var configured = new StationDecisionCaptureRenderProfile(camera, profile))
            {
                Require(geometry.Matches(camera) && RendererIndex(data) == initialIndex && dataBefore == EditorJsonUtility.ToJson(data), "Constructing a profile changed camera state.", report);
                try
                {
                    using (configured.Apply())
                    {
                        evidence.appliedRendererIndex = RendererIndex(data); evidence.appliedShadowDistance = pipeline.shadowDistance;
                        evidence.appliedCascades = pipeline.shadowCascadeCount; evidence.appliedShadowmapResolution = pipeline.mainLightShadowmapResolution;
                        evidence.appliedDepthOption = data.requiresDepthOption.ToString(); evidence.appliedColorOption = data.requiresColorOption.ToString();
                        int expectedIndex = profile == "full" ? initialIndex : profile == "lean" ? 3 : profile == "ao-off" ? 2 : 1;
                        Require(evidence.appliedRendererIndex == expectedIndex && ReferenceEquals(data.scriptableRenderer, pipeline.GetRenderer(expectedIndex)), "Profile selected the wrong renderer: " + profile, report);
                        bool balancedShadows = profile == "balanced" || profile == "copies" || profile == "lean";
                        Require(pipeline.shadowDistance.Equals(balancedShadows ? Mathf.Min(distance, 70f) : distance) &&
                            pipeline.shadowCascadeCount == (profile == "lean" ? 1 : balancedShadows ? Math.Min(cascades, 2) : cascades) &&
                            pipeline.mainLightShadowmapResolution == (profile == "lean" ? Math.Min(shadowmap, 1024) : shadowmap), "Wrong temporary shadow budget.", report);
                        bool copiesOff = profile == "copies" || profile == "lean";
                        Require(data.requiresDepthOption == (copiesOff ? CameraOverrideOption.Off : depthOption) &&
                            data.requiresColorOption == (copiesOff ? CameraOverrideOption.Off : colorOption), "Wrong optional camera-copy overrides.", report);
                        evidence.geometryPreserved = geometry.Matches(camera);
                        Require(evidence.geometryPreserved, "Capture profile changed pose, projection or serialized camera fields.", report);
                        if (exceptionExit) throw new ExpectedScopeException();
                    }
                }
                catch (ExpectedScopeException) { evidence.exceptionObserved = true; }
                evidence.rendererRestored = RendererIndex(data) == initialIndex;
                evidence.shadowsRestored = pipeline.shadowDistance.Equals(distance) && pipeline.shadowCascadeCount == cascades;
                evidence.shadowmapRestored = pipeline.mainLightShadowmapResolution == shadowmap;
                evidence.copyOptionsRestored = data.requiresDepthOption == depthOption && data.requiresColorOption == colorOption;
                Require(evidence.exceptionObserved == exceptionExit && evidence.rendererRestored && evidence.shadowsRestored && evidence.shadowmapRestored && evidence.copyOptionsRestored,
                    "Scope failed to restore after normal/exception exit.", report);
            }
            evidence.additionalDataPreserved = camera.GetComponent<UniversalAdditionalCameraData>() == data && camera.GetComponents<UniversalAdditionalCameraData>().Length == 1;
            Require(evidence.additionalDataPreserved && dataBefore == EditorJsonUtility.ToJson(data) && geometry.Matches(camera), "Disposing a profile changed or destroyed caller-owned camera data.", report);
        }

        static void VerifyNested(Camera camera, UniversalAdditionalCameraData data, Geometry geometry, UniversalRenderPipelineAsset pipeline, Report report)
        {
            data.SetRenderer(-1); pipeline.shadowDistance = 115f; pipeline.shadowCascadeCount = 4; pipeline.mainLightShadowmapResolution = 2048;
            data.requiresDepthOption = CameraOverrideOption.On; data.requiresColorOption = CameraOverrideOption.UsePipelineSettings;
            string originalData = EditorJsonUtility.ToJson(data);
            using (var outer = new StationDecisionCaptureRenderProfile(camera, "copies"))
            using (var inner = new StationDecisionCaptureRenderProfile(camera, "lean"))
            using (outer.Apply())
            {
                Require(RendererIndex(data) == 1 && pipeline.shadowDistance.Equals(70f) && pipeline.shadowCascadeCount == 2 &&
                    pipeline.mainLightShadowmapResolution == 2048 && data.requiresDepthOption == CameraOverrideOption.Off && data.requiresColorOption == CameraOverrideOption.Off,
                    "Outer scope did not apply.", report);
                try
                {
                    using (inner.Apply())
                    {
                        Require(RendererIndex(data) == 3 && pipeline.shadowDistance.Equals(70f) && pipeline.shadowCascadeCount == 1 &&
                            pipeline.mainLightShadowmapResolution == 1024 && data.requiresDepthOption == CameraOverrideOption.Off && data.requiresColorOption == CameraOverrideOption.Off,
                            "Nested scope did not apply.", report);
                        throw new ExpectedScopeException();
                    }
                }
                catch (ExpectedScopeException) { }
                Require(RendererIndex(data) == 1 && pipeline.shadowDistance.Equals(70f) && pipeline.shadowCascadeCount == 2 &&
                    pipeline.mainLightShadowmapResolution == 2048 && data.requiresDepthOption == CameraOverrideOption.Off && data.requiresColorOption == CameraOverrideOption.Off && geometry.Matches(camera),
                    "Nested exception did not restore outer state.", report);
            }
            report.nestedScopesRestored = RendererIndex(data) == -1 && pipeline.shadowDistance.Equals(115f) && pipeline.shadowCascadeCount == 4 &&
                pipeline.mainLightShadowmapResolution == 2048 && originalData == EditorJsonUtility.ToJson(data) && geometry.Matches(camera);
            Require(report.nestedScopesRestored, "Nested scopes did not restore the original state.", report);
        }
        static int RendererIndex(UniversalAdditionalCameraData data) => new SerializedObject(data).FindProperty("m_RendererIndex").intValue;
        static bool SameMatrix(Matrix4x4 a, Matrix4x4 b) { for (int i = 0; i < 16; ++i) if (!a[i].Equals(b[i])) return false; return true; }
        static string Digest(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
        static void Require(bool condition, string message, Report report) { ++report.checks; if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif