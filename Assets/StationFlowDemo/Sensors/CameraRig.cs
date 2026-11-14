using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StationFlow.SyntheticTraffic
{
    [Serializable]
    public class LabelTarget
    {
        public int id;
        public string category;
        public GameObject root;
        public Vector3 velocity;
        public string state;
        public int groupId,groupSize=1;
        public bool conversing;
        public string navigationIntent;
    }

    /// <summary>
    /// Synchronized, manually rendered cameras. Call after simulation and animation evaluation.
    /// Fisheye is a central equidistant 180-degree hemisphere sampled from six 90-degree views.
    /// Labels describe approximate amodal AABBs, never visibility or segmentation ground truth.
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public int PinholeWidth = 1280;
        public int PinholeHeight = 720;
        public int FisheyeSize = 768;
        public int FaceSize = 768;
        public float VerticalFieldOfView = 70f;
        public Vector3 SensorPosition = new Vector3(22, 11, -22);
        public Vector3 SensorLookAt = new Vector3(0, 1, 0);
        public float NearClip = 0.15f;
        public float FarClip = 100f;

        public Camera OverviewCamera { get; private set; }
        public Camera PinholeCamera { get; private set; }
        public RenderTexture PinholeTexture { get; private set; }
        public RenderTexture FisheyeTexture { get; private set; }
        public bool IsCapturing { get { return frameWriter != null; } }
        public int CapturedFrameCount { get; private set; }

        static readonly Vector3[] FaceDirections = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left, Vector3.up, Vector3.down };
        static readonly Vector3[] FaceUps = { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.back, Vector3.forward };
        static readonly string[] FaceNames = { "_Front", "_Right", "_Back", "_Left", "_Up", "_Down" };
        readonly Camera[] faceCameras = new Camera[6];
        readonly RenderTexture[] faceTextures = new RenderTexture[6];
        readonly Dictionary<GameObject, Renderer[]> rendererCache = new Dictionary<GameObject, Renderer[]>();
        Material fisheyeMaterial;
        Texture2D pinholeReadback, fisheyeReadback;
        StreamWriter frameWriter;
        string captureDirectory;
        int captureSeed, captureFps, lastSavedFrame = -1;
        double lastSavedTime = -1;
        bool initialized;
        readonly Plane[] observationPinholePlanes = new Plane[6];
        readonly Plane[] observationOverviewPlanes = new Plane[6];
        bool observationEnvelopeReady;

        // Lifecycle checks use the actual sensor domains, including the fisheye's cube-plane clipping.
        // Bounds are intentionally padded: a birth/departure must clear the whole silhouette.
        public void RefreshObservationEnvelope()
        {
            if(PinholeCamera==null||OverviewCamera==null)return;
            GeometryUtility.CalculateFrustumPlanes(PinholeCamera,observationPinholePlanes);
            GeometryUtility.CalculateFrustumPlanes(OverviewCamera,observationOverviewPlanes);
            observationEnvelopeReady=true;
        }
        public bool IsObservableAt(float x,float z,float radius)
        {
            if(!observationEnvelopeReady)RefreshObservationEnvelope();
            if(!observationEnvelopeReady)return true;
            float margin=Mathf.Max(.8f,radius+.45f);
            Bounds bounds=new Bounds(new Vector3(x,1.15f,z),new Vector3(margin*2,2.5f,margin*2));
            if(GeometryUtility.TestPlanesAABB(observationPinholePlanes,bounds))return true;
            if(OverviewCamera.enabled&&GeometryUtility.TestPlanesAABB(observationOverviewPlanes,bounds))return true;
            Vector3 local=Quaternion.Inverse(PinholeCamera.transform.rotation)*(bounds.center-PinholeCamera.transform.position);
            float sphere=bounds.extents.magnitude;
            float dominant=Mathf.Max(Mathf.Abs(local.x),Mathf.Abs(local.y),Mathf.Abs(local.z));
            return local.z+sphere>=0&&dominant-sphere<=FarClip&&dominant+sphere>=NearClip;
        }

        public void Initialize()
        {
            if (initialized) return;
            if (PinholeWidth < 16 || PinholeHeight < 16 || FisheyeSize < 16 || FaceSize < 16)
                throw new ArgumentOutOfRangeException("Sensor image dimensions must be at least 16 pixels.");
            if (!(VerticalFieldOfView > 1 && VerticalFieldOfView < 179) || !(NearClip > 0 && FarClip > NearClip))
                throw new ArgumentOutOfRangeException("Invalid camera field of view or clipping distances.");

            Shader shader = Resources.Load<Shader>("StationFlowEquidistantFisheye");
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("The StationFlow equidistant fisheye shader is unavailable or unsupported.");
            fisheyeMaterial = new Material(shader) { name = "StationFlow equidistant projection" };
            PinholeTexture = MakeTexture("Pinhole RGB", PinholeWidth, PinholeHeight, 24);
            FisheyeTexture = MakeTexture("Fisheye RGB", FisheyeSize, FisheyeSize, 0);
            PinholeCamera = MakeCamera("Pinhole sensor", VerticalFieldOfView, (float)PinholeWidth / PinholeHeight);
            PinholeCamera.transform.SetPositionAndRotation(SensorPosition, Quaternion.LookRotation(SensorLookAt - SensorPosition, Vector3.up));
            OverviewCamera = MakeCamera("Overview camera", 55f, (float)Screen.width / Mathf.Max(1, Screen.height));
            OverviewCamera.transform.SetPositionAndRotation(new Vector3(31, 26, -31), Quaternion.LookRotation(new Vector3(-31, -25, 31), Vector3.up));
            OverviewCamera.enabled = true;
            OverviewCamera.farClipPlane = 125;
            OverviewCamera.tag = "MainCamera";
            OverviewCamera.gameObject.AddComponent<AudioListener>();
            for (int i = 0; i < 6; ++i)
            {
                faceCameras[i] = MakeCamera("Fisheye face " + FaceNames[i], 90, 1);
                faceTextures[i] = MakeTexture("Fisheye " + FaceNames[i], FaceSize, FaceSize, 24);
                fisheyeMaterial.SetTexture(FaceNames[i], faceTextures[i]);
            }
            pinholeReadback = new Texture2D(PinholeWidth, PinholeHeight, TextureFormat.RGB24, false, false);
            fisheyeReadback = new Texture2D(FisheyeSize, FisheyeSize, TextureFormat.RGB24, false, false);
            initialized = true;
            RefreshObservationEnvelope();
        }

        Camera MakeCamera(string cameraName, float fov, float aspect)
        {
            var go = new GameObject(cameraName);
            go.transform.SetParent(transform, false);
            var camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = fov;
            camera.aspect = aspect;
            camera.nearClipPlane = NearClip;
            camera.farClipPlane = FarClip;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.59f, 0.73f, 0.87f);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = true;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
            UrbanRenderQuality.ConfigureCamera(camera,true);
            return camera;
        }

        static RenderTexture MakeTexture(string textureName, int width, int height, int depth)
        {
            var result = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = textureName,
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false
            };
            if (!result.Create()) throw new InvalidOperationException("Cannot create render texture " + textureName);
            return result;
        }

        public void RenderSensors()
        {
            Initialize();
            RenderTexture previousActive = RenderTexture.active;
            bool oldSrgb = GL.sRGBWrite;
            try
            {
                RenderCamera(PinholeCamera, PinholeTexture);
                Quaternion rotation = PinholeCamera.transform.rotation;
                for (int i = 0; i < 6; ++i)
                {
                    faceCameras[i].transform.SetPositionAndRotation(PinholeCamera.transform.position,
                        rotation * Quaternion.LookRotation(FaceDirections[i], FaceUps[i]));
                    RenderCamera(faceCameras[i], faceTextures[i]);
                }
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(faceTextures[0], FisheyeTexture, fisheyeMaterial);
            }
            finally
            {
                GL.sRGBWrite = oldSrgb;
                // Blit changes the active target globally. Restore it so screen captures,
                // subsequent cameras and texture cleanup do not target the fisheye buffer.
                RenderTexture.active = previousActive;
            }
        }

        static void RenderCamera(Camera camera, RenderTexture destination)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = destination };
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                var previous = camera.targetTexture;
                try { camera.targetTexture = destination; camera.Render(); }
                finally { camera.targetTexture = previous; }
            }
        }

        public void SavePreview(string directory,string prefix)
        {
            Directory.CreateDirectory(directory);RenderSensors();
            WritePng(PinholeTexture,pinholeReadback,Path.Combine(directory,prefix+"_pinhole.png"));
            WritePng(FisheyeTexture,fisheyeReadback,Path.Combine(directory,prefix+"_fisheye.png"));
            SaveCameraPreview(OverviewCamera,Path.Combine(directory,prefix+"_scene.png"));
        }
        public static void SaveCameraPreview(Camera camera,string path)
        {
            var rt=MakeTexture("QA scene",1600,1000,24);var readback=new Texture2D(1600,1000,TextureFormat.RGB24,false,false);
            float aspect=camera.aspect;RenderTexture previous=RenderTexture.active;
            try{camera.aspect=1.6f;RenderCamera(camera,rt);WritePng(rt,readback,path);}
            finally{camera.aspect=aspect;RenderTexture.active=previous;rt.Release();Destroy(rt);Destroy(readback);}
        }

        public void BeginCapture(string directory, int seed, int fps)
        {
            Initialize();
            if (IsCapturing) throw new InvalidOperationException("A capture is already open.");
            if (fps < 1 || fps > 240) throw new ArgumentOutOfRangeException(nameof(fps));
            captureDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(captureDirectory);
            string metadata = Path.Combine(captureDirectory, "frames.jsonl");
            if (File.Exists(metadata) || File.Exists(Path.Combine(captureDirectory, "calibration.json")))
                throw new IOException("Capture directory already contains data; use a new output directory.");
            Directory.CreateDirectory(Path.Combine(captureDirectory, "pinhole"));
            Directory.CreateDirectory(Path.Combine(captureDirectory, "fisheye"));
            captureSeed = seed;
            captureFps = fps;
            CapturedFrameCount = 0;
            lastSavedFrame = -1;
            lastSavedTime = -1;
            rendererCache.Clear();
            File.WriteAllText(Path.Combine(captureDirectory, "calibration.json"), JsonUtility.ToJson(BuildCalibration(), true), new UTF8Encoding(false));
            frameWriter = new StreamWriter(metadata, false, new UTF8Encoding(false));
            frameWriter.AutoFlush = true;
        }

        public void SaveFrame(int frame, double time, string phase, IList<LabelTarget> targets)
        {
            if (!IsCapturing) throw new InvalidOperationException("BeginCapture must be called before SaveFrame.");
            if (frame < 0 || frame <= lastSavedFrame || double.IsNaN(time) || double.IsInfinity(time) || time < 0 || time < lastSavedTime)
                throw new ArgumentException("Frame IDs must increase and timestamps must be finite and monotonic.");
            string fileName = "frame_" + frame.ToString("D6", CultureInfo.InvariantCulture) + ".png";
            var data = new FrameData
            {
                frame = frame,
                timestamp_seconds = time,
                seed = captureSeed,
                traffic_phase = phase ?? "unknown",
                pinhole_rgb = "pinhole/" + fileName,
                fisheye_rgb = "fisheye/" + fileName,
                camera_position_world = ToArray(PinholeCamera.transform.position),
                camera_rotation_xyzw = ToArray(PinholeCamera.transform.rotation),
                world_to_camera_optical_row_major = OpticalExtrinsics(),
                objects = new List<ObjectData>()
            };
            var ids = new HashSet<int>();
            if (targets != null) foreach (LabelTarget target in targets)
            {
                if (target == null || target.root == null || !target.root.activeInHierarchy) continue;
                if (!ids.Add(target.id)) throw new ArgumentException("Duplicate object track ID " + target.id);
                if (!Finite(target.root.transform.position) || !Finite(target.velocity))
                    throw new ArgumentException("Non-finite position or velocity for track " + target.id);
                Bounds bounds;
                if (!TryBounds(target, out bounds)) continue;
                if (!Finite(bounds.center) || !Finite(bounds.size)) throw new ArgumentException("Non-finite render bounds for track " + target.id);
                var rotation = target.root.transform.rotation;
                data.objects.Add(new ObjectData
                {
                    track_id = target.id,
                    category = target.category ?? "unknown",
                    behavior_state = target.state ?? "unknown",
                    social_group_id = target.groupId,
                    social_group_size = target.groupSize,
                    conversing = target.conversing,
                    navigation_intent = target.navigationIntent ?? "unknown",
                    position_world = ToArray(target.root.transform.position),
                    rotation_xyzw = ToArray(rotation),
                    velocity_world_m_s = ToArray(target.velocity),
                    world_aabb_center = ToArray(bounds.center),
                    world_aabb_size = ToArray(bounds.size),
                    pinhole = ProjectBounds(bounds, false),
                    fisheye = ProjectBounds(bounds, true)
                });
            }
            WritePng(PinholeTexture, pinholeReadback, Path.Combine(captureDirectory, "pinhole", fileName));
            WritePng(FisheyeTexture, fisheyeReadback, Path.Combine(captureDirectory, "fisheye", fileName));
            frameWriter.WriteLine(JsonUtility.ToJson(data));
            lastSavedFrame = frame;
            lastSavedTime = time;
            ++CapturedFrameCount;
        }

        public void EndCapture()
        {
            if (frameWriter == null) return;
            frameWriter.Dispose();
            frameWriter = null;
            var summary = new CaptureSummary
            {
                frames = CapturedFrameCount,
                seed = captureSeed,
                fps = captureFps,
                last_timestamp_seconds = lastSavedTime,
                completed_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            File.WriteAllText(Path.Combine(captureDirectory, "capture_summary.json"), JsonUtility.ToJson(summary, true), new UTF8Encoding(false));
        }

        static void WritePng(RenderTexture source, Texture2D readback, string path)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readback.Apply(false, false);
                File.WriteAllBytes(path, readback.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; }
        }

        bool TryBounds(LabelTarget target, out Bounds bounds)
        {
            bounds = new Bounds(target.root.transform.position, Vector3.zero);
            Renderer[] renderers;
            if (!rendererCache.TryGetValue(target.root, out renderers))
            {
                renderers = target.root.GetComponentsInChildren<Renderer>(true);
                rendererCache[target.root] = renderers;
            }
            bool found = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return found;
        }

        ProjectionData ProjectBounds(Bounds bounds, bool fisheye)
        {
            int width = fisheye ? FisheyeSize : PinholeWidth;
            int height = fisheye ? FisheyeSize : PinholeHeight;
            Vector2 minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            int projected = 0, samples = 0;
            // 98 samples on the six AABB faces (5x5x5 lattice boundary). Curved fisheye
            // edges need more than eight corners. This is still an approximation, not a mask.
            for (int x = 0; x <= 4; ++x) for (int y = 0; y <= 4; ++y) for (int z = 0; z <= 4; ++z)
            {
                if (x > 0 && x < 4 && y > 0 && y < 4 && z > 0 && z < 4) continue;
                ++samples;
                Vector3 world = bounds.min + Vector3.Scale(bounds.size, new Vector3(x * .25f, y * .25f, z * .25f));
                Vector2 pixel;
                if (!ProjectWorld(world, fisheye, out pixel)) continue;
                minimum = Vector2.Min(minimum, pixel);
                maximum = Vector2.Max(maximum, pixel);
                ++projected;
            }
            var result = new ProjectionData { projected_samples = projected, total_samples = samples, clipped_by_projection_domain = projected < samples };
            Vector2 center;
            if (ProjectWorld(bounds.center, fisheye, out center)) result.center_px = new[] { center.x, center.y };
            if (projected == 0) return result;
            result.sampled_amodal_bbox_xyxy = new[] { minimum.x, minimum.y, maximum.x, maximum.y };
            result.in_sensor_fov = maximum.x >= 0 && minimum.x < width && maximum.y >= 0 && minimum.y < height;
            if (result.in_sensor_fov)
                result.clipped_bbox_xyxy = new[] { Mathf.Clamp(minimum.x, 0, width), Mathf.Clamp(minimum.y, 0, height), Mathf.Clamp(maximum.x, 0, width), Mathf.Clamp(maximum.y, 0, height) };
            return result;
        }

        public bool ProjectWorld(Vector3 world, bool fisheye, out Vector2 pixel)
        {
            Vector3 cameraLocal = Quaternion.Inverse(PinholeCamera.transform.rotation) * (world - PinholeCamera.transform.position);
            return ProjectCameraLocal(cameraLocal, fisheye, fisheye ? FisheyeSize : PinholeWidth,
                fisheye ? FisheyeSize : PinholeHeight, VerticalFieldOfView, NearClip, FarClip, out pixel);
        }

        /// <summary>Pure projection used by export and numerical calibration checks. Pixel origin is the image upper left.</summary>
        public static bool ProjectCameraLocal(Vector3 p, bool fisheye, int width, int height, float verticalFov, float near, float far, out Vector2 pixel)
        {
            pixel = Vector2.zero;
            if (!Finite(p) || width < 1 || height < 1) return false;
            float cx = width * .5f, cy = height * .5f;
            if (!fisheye)
            {
                if (p.z < near || p.z > far) return false;
                float focal = height / (2 * Mathf.Tan(verticalFov * Mathf.Deg2Rad * .5f));
                pixel = new Vector2(cx + focal * p.x / p.z, cy - focal * p.y / p.z);
            }
            else
            {
                // Cube cameras use plane clipping on their dominant component, rather than
                // spherical clipping; use the same valid domain in the label projection.
                float dominant = Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z));
                if (dominant < near || dominant > far || p.z < 0) return false;
                float lateral = Mathf.Sqrt(p.x * p.x + p.y * p.y);
                float theta = Mathf.Atan2(lateral, p.z);
                float radius = (Mathf.Min(width, height) / Mathf.PI) * theta;
                pixel = lateral < 1e-7f ? new Vector2(cx, cy) : new Vector2(cx + radius * p.x / lateral, cy - radius * p.y / lateral);
            }
            return !float.IsNaN(pixel.x) && !float.IsNaN(pixel.y) && !float.IsInfinity(pixel.x) && !float.IsInfinity(pixel.y);
        }

        Calibration BuildCalibration()
        {
            float focal = PinholeHeight / (2 * Mathf.Tan(VerticalFieldOfView * Mathf.Deg2Rad * .5f));
            return new Calibration
            {
                seed = captureSeed, fps = captureFps,
                created_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                pinhole = new SensorCalibration
                {
                    model = "pinhole", width = PinholeWidth, height = PinholeHeight,
                    horizontal_fov_degrees = 2 * Mathf.Atan((float)PinholeWidth / PinholeHeight * Mathf.Tan(VerticalFieldOfView * Mathf.Deg2Rad * .5f)) * Mathf.Rad2Deg,
                    vertical_fov_degrees = VerticalFieldOfView,
                    focal_pixels = focal, cx = PinholeWidth * .5f, cy = PinholeHeight * .5f,
                    K_row_major = new[] { focal, 0, PinholeWidth * .5f, 0, focal, PinholeHeight * .5f, 0, 0, 1f },
                    world_to_camera_optical_row_major = OpticalExtrinsics(),
                    position_world = ToArray(PinholeCamera.transform.position), rotation_xyzw = ToArray(PinholeCamera.transform.rotation),
                    near_clip = NearClip, far_clip = FarClip
                },
                fisheye = new SensorCalibration
                {
                    model = "equidistant_180", width = FisheyeSize, height = FisheyeSize,
                    horizontal_fov_degrees = 180, vertical_fov_degrees = 180,
                    focal_pixels = FisheyeSize / Mathf.PI, cx = FisheyeSize * .5f, cy = FisheyeSize * .5f,
                    valid_circle_radius_pixels = FisheyeSize * .5f,
                    face_resolution = FaceSize,
                    world_to_camera_optical_row_major = OpticalExtrinsics(),
                    position_world = ToArray(PinholeCamera.transform.position), rotation_xyzw = ToArray(PinholeCamera.transform.rotation),
                    near_clip = NearClip, far_clip = FarClip
                }
            };
        }

        float[] OpticalExtrinsics()
        {
            Matrix4x4 matrix = Matrix4x4.TRS(PinholeCamera.transform.position, PinholeCamera.transform.rotation, Vector3.one).inverse;
            float[] values = new float[16];
            for (int row = 0; row < 4; ++row) for (int col = 0; col < 4; ++col)
                values[row * 4 + col] = (row == 1 ? -1 : 1) * matrix[row, col];
            return values;
        }

        static bool Finite(Vector3 value) { return !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z)); }
        static float[] ToArray(Vector3 value) { return new[] { value.x, value.y, value.z }; }
        static float[] ToArray(Quaternion value) { return new[] { value.x, value.y, value.z, value.w }; }

        /// <summary>
        /// Numerical and real-GPU reprojection checks using isolated unlit markers at known
        /// angles, including rays crossing the 45-degree cube-face boundaries. Restores the rig.
        /// Call outside camera rendering callbacks, after the pipeline has initialized.
        /// </summary>
        public string ValidateProjectionRendering(string outputDirectory = null)
        {
            Initialize();
            int numericalChecks = SensorProjectionChecks.RunOrThrow();
            var cameras = new List<Camera> { PinholeCamera };
            cameras.AddRange(faceCameras);
            var masks = new int[cameras.Count];
            var flags = new CameraClearFlags[cameras.Count];
            var backgrounds = new Color[cameras.Count];
            var postProcessing = new bool[cameras.Count];
            var antialiasing = new AntialiasingMode[cameras.Count];
            var markerRoot = new GameObject("Temporary sensor calibration markers");
            var materials = new List<Material>();
            Shader markerShader = Resources.Load<Shader>("StationFlowValidationUnlit");
            if (markerShader == null) throw new InvalidOperationException("Validation shader missing.");
            float[] angles = { 0, 30, -30, 30, -30, 65, -65, 65, -65 };
            bool[] vertical = { false, false, false, true, true, false, false, true, true };
            Color[] colors = { Color.white, Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta, Color.blue, Color.yellow };
            Vector3[] localPoints = new Vector3[angles.Length];
            var report = new StringBuilder();
            int markerChecks = 0;
            try
            {
                for (int i = 0; i < cameras.Count; ++i)
                {
                    masks[i] = cameras[i].cullingMask;
                    flags[i] = cameras[i].clearFlags;
                    backgrounds[i] = cameras[i].backgroundColor;
                    var cameraData=cameras[i].GetUniversalAdditionalCameraData();
                    postProcessing[i]=cameraData.renderPostProcessing;antialiasing[i]=cameraData.antialiasing;
                    cameraData.renderPostProcessing=false;cameraData.antialiasing=AntialiasingMode.None;
                    cameras[i].cullingMask = 1 << 31;
                    cameras[i].clearFlags = CameraClearFlags.SolidColor;
                    cameras[i].backgroundColor = Color.black;
                }
                for (int i = 0; i < angles.Length; ++i)
                {
                    float angle = angles[i] * Mathf.Deg2Rad;
                    Vector3 local = vertical[i] ? new Vector3(0, Mathf.Sin(angle), Mathf.Cos(angle)) : new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
                    localPoints[i] = local * 8;
                    var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    marker.name = "Calibration ray " + i;
                    marker.layer = 31;
                    marker.transform.SetParent(markerRoot.transform, false);
                    marker.transform.position = PinholeCamera.transform.position + PinholeCamera.transform.rotation * localPoints[i];
                    marker.transform.localScale = Vector3.one * .42f;
                    var material = new Material(markerShader);
                    material.SetColor("_Color", colors[i]);
                    materials.Add(material);
                    marker.GetComponent<Renderer>().sharedMaterial = material;
                }
                RenderSensors();
                ReadTexture(PinholeTexture, pinholeReadback);
                ReadTexture(FisheyeTexture, fisheyeReadback);
                for (int sensor = 0; sensor < 2; ++sensor)
                {
                    bool isFisheye = sensor == 1;
                    Texture2D source = isFisheye ? fisheyeReadback : pinholeReadback;
                    for (int i = 0; i < localPoints.Length; ++i)
                    {
                        Vector2 point;
                        if (!ProjectCameraLocal(localPoints[i], isFisheye, source.width, source.height, VerticalFieldOfView, NearClip, FarClip, out point)
                            || point.x < 2 || point.y < 2 || point.x >= source.width - 2 || point.y >= source.height - 2) continue;
                        // Texture CPU rows start at bottom; exported pixel coordinates start at top.
                        Color actual = source.GetPixel(Mathf.FloorToInt(point.x), source.height - 1 - Mathf.FloorToInt(point.y));
                        float error = Mathf.Max(Mathf.Abs(actual.r - colors[i].r), Mathf.Abs(actual.g - colors[i].g), Mathf.Abs(actual.b - colors[i].b));
                        report.AppendLine((isFisheye ? "fisheye" : "pinhole") + " marker " + i + " at " + point + " RGB error=" + error.ToString("F5", CultureInfo.InvariantCulture));
                        if (error > .2f) throw new InvalidOperationException("Sensor GPU projection mismatch: " + report);
                        ++markerChecks;
                    }
                }
                Color outsideCircle = fisheyeReadback.GetPixel(0, 0);
                if (Mathf.Max(outsideCircle.r, outsideCircle.g, outsideCircle.b) > .01f)
                    throw new InvalidOperationException("Fisheye pixels outside the hemisphere circle must be black.");
                report.Insert(0, "PASS: " + numericalChecks + " numerical checks, " + markerChecks + " rendered marker checks, black fisheye mask.\n");
                if (!string.IsNullOrEmpty(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                    File.WriteAllBytes(Path.Combine(outputDirectory, "calibration_markers_pinhole.png"), pinholeReadback.EncodeToPNG());
                    File.WriteAllBytes(Path.Combine(outputDirectory, "calibration_markers_fisheye.png"), fisheyeReadback.EncodeToPNG());
                    File.WriteAllText(Path.Combine(outputDirectory, "sensor_projection_checks.txt"), report.ToString(), new UTF8Encoding(false));
                }
                return report.ToString();
            }
            finally
            {
                markerRoot.SetActive(false);
                Destroy(markerRoot);
                foreach (var material in materials) Destroy(material);
                for (int i = 0; i < cameras.Count; ++i)
                {
                    cameras[i].cullingMask = masks[i];
                    cameras[i].clearFlags = flags[i];
                    cameras[i].backgroundColor = backgrounds[i];
                    var cameraData=cameras[i].GetUniversalAdditionalCameraData();
                    cameraData.renderPostProcessing=postProcessing[i];cameraData.antialiasing=antialiasing[i];
                }
            }
        }

        static void ReadTexture(RenderTexture source, Texture2D readback)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readback.Apply(false, false);
            }
            finally { RenderTexture.active = previous; }
        }

        void OnDestroy()
        {
            EndCapture();
            Release(PinholeTexture); Release(FisheyeTexture);
            foreach (var texture in faceTextures) Release(texture);
            if (pinholeReadback != null) Destroy(pinholeReadback);
            if (fisheyeReadback != null) Destroy(fisheyeReadback);
            if (fisheyeMaterial != null) Destroy(fisheyeMaterial);
        }

        static void Release(RenderTexture texture)
        {
            if (texture == null) return;
            if (RenderTexture.active == texture) RenderTexture.active = null;
            texture.Release();
            Destroy(texture);
        }

        [Serializable] sealed class Calibration
        {
            public string schema = "stationflow.synthetic_traffic.sensor.v1";
            public string created_utc;
            public int seed, fps;
            public string world_coordinates = "Unity left-handed: x right, y up, z forward; metres";
            public string optical_coordinates = "x image-right, y image-down, z forward; world_to_camera rows map homogeneous world positions";
            public string pixel_coordinates = "top-left image boundary (0,0); pixel centers at (column+0.5,row+0.5); bbox xyxy boundary coordinates";
            public string fisheye_equation = "r=f*theta; theta=atan2(sqrt(X^2+Y^2),Z); u=cx+r*X/sqrt(X^2+Y^2); v=cy+r*Y/sqrt(X^2+Y^2) in optical coordinates";
            public string fisheye_rendering = "Six co-located 90-degree perspective faces; bilinear equidistant remap; radius=size/2; outside circle black; face clipping planes near/far";
            public string rgb = "lossless PNG RGB8 sRGB; no motion blur, lens noise, rolling shutter or postprocessing";
            public string synchronization = "All seven camera views rendered after one simulation/animation state; timestamp is simulation time, not wall clock";
            public string label_semantics = "98 samples on each renderers-combined world-axis-aligned bounding box; approximate AMODAL projection; in_sensor_fov does not imply visible; occlusion, instance masks and visibility fractions are not measured";
            public SensorCalibration pinhole, fisheye;
        }

        [Serializable] sealed class SensorCalibration
        {
            public string model;
            public int width, height, face_resolution;
            public float horizontal_fov_degrees, vertical_fov_degrees, focal_pixels, cx, cy, valid_circle_radius_pixels, near_clip, far_clip;
            public float[] K_row_major;
            public float[] world_to_camera_optical_row_major;
            public float[] position_world, rotation_xyzw;
        }

        [Serializable] sealed class FrameData
        {
            public string schema = "stationflow.synthetic_traffic.frame.v2";
            public int frame, seed;
            public double timestamp_seconds;
            public string traffic_phase, pinhole_rgb, fisheye_rgb;
            public float[] camera_position_world, camera_rotation_xyzw, world_to_camera_optical_row_major;
            public List<ObjectData> objects;
        }

        [Serializable] sealed class ObjectData
        {
            public int track_id;
            public string category, behavior_state;
            public int social_group_id,social_group_size;
            public bool conversing;
            public string navigation_intent;
            public float[] position_world, rotation_xyzw, velocity_world_m_s, world_aabb_center, world_aabb_size;
            public ProjectionData pinhole, fisheye;
        }

        [Serializable] sealed class ProjectionData
        {
            public string visibility = "not_measured";
            public string bounds_method = "sampled_world_aabb_amodal_approximation";
            public bool in_sensor_fov, clipped_by_projection_domain;
            public int projected_samples, total_samples;
            public float[] center_px, sampled_amodal_bbox_xyxy, clipped_bbox_xyxy;
        }

        [Serializable] sealed class CaptureSummary
        {
            public int frames, seed, fps;
            public double last_timestamp_seconds;
            public string completed_utc;
        }
    }
}
