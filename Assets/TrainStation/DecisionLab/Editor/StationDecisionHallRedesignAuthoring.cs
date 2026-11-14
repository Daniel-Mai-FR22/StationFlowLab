#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using StationFlow.TrainStation.Editor;
using TMPro;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Saved compact information desk, central fountain and four surveyed hall views.</summary>
    public static class StationDecisionHallRedesignAuthoring
    {
        public const string Output = "Reports/TrainStationDaily/HallRedesign";
        public static readonly Vector3 FountainCenter = Vector3.zero;
        public static readonly Vector3 AccueilCenter = new Vector3(4.2f, 0, 17.4f);
        // Turn the waiting line before the east bench island; preserve comfortable 1.3 m FIFO spacing.
        public static readonly Vector3[] AccueilQueue = {
            new Vector3(4.2f,.02f,15.1f), new Vector3(5.5f,.02f,15.1f),
            new Vector3(6.8f,.02f,15.1f), new Vector3(8.1f,.02f,15.1f),
            new Vector3(8.1f,.02f,13.8f), new Vector3(8.1f,.02f,12.5f) };
        public static readonly string[] HallCameraIds = { "HALL-01", "HALL-02", "HALL-03", "HALL-04" };
        [Serializable] sealed class CameraEvidence
        {
            public string cameraId, mount, image;
            public Vector3 position, target, eulerAngles;
            public float fieldOfView, azimuthDegrees;
            public int visibleCoveragePoints;
            public bool centerVisible, centerUnobstructed;
        }
        [Serializable] sealed class Report
        {
            public bool success, centeredOnHallSlab, compactAccueil, centralAxisClear, allPathsComplete;
            public string checkedUtc, scene, error;
            public Vector3 hallCenter, fountainCenter, accueilCenter, accueilFootprintSize, accueilFootprintCenter;
            public int hallCameras, fixedCameras, allCameras, queueSlots, displayStops, routeChecks, colliderChecks, queueSegmentChecks;
            public Vector3[] queue, displayStandingPoints;
            public List<CameraEvidence> cameras = new List<CameraEvidence>();
            public string scope = "Saved Daily hall geometry, service marker clearance, native paths and four fixed corner cameras; no forced pedestrian routes or equal-flow assumption.";
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Redesign hall and four fountain cameras")]
        public static void Apply()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "Stop Play before hall authoring.");
            Require(SceneManager.GetActiveScene().path == StationDecisionDayAuthoring.ScenePath, "Open the saved Daily scene first.");
            Require(!SceneManager.GetActiveScene().isDirty, "Save existing scene changes before hall authoring.");
            // The existing transaction preserves scene/navigation backups and runs its complete route fixture before saving.
            StationDecisionHallFountainAuthoring.Apply();
            Validate();
            Capture();
        }

        public static void ConfigureDisplayLayout(Scene scene, StationDecisionWorld world, Transform root)
        {
            var poi = world.pois.Single(p => p && p.poiId == "display-hall");
            var area = root.Find("Lecture du tableau — positions debout distinctes");
            if (!area) area = Child(root, "Lecture du tableau — positions debout distinctes");
            var points = new List<Transform>();
            for (int row = 0; row < 3; ++row)
            for (int col = 0; col < 8; ++col)
            {
                string name = "Lecture " + (row * 8 + col + 1).ToString("00");
                var point = area.Find(name);
                if (!point) point = Child(area, name);
                point.position = new Vector3(-6.4f + col * 1.1f, .02f, 8f + row * 1.4f);
                point.rotation = Quaternion.identity; points.Add(point); Dirty(point);
            }
            poi.standingPoints = points.ToArray(); poi.capacity = points.Count; poi.spreadRadius = 0;
            poi.interactionPoint.position = new Vector3(-2.55f, .02f, 9.4f);
            poi.facingTarget = Components<Transform>(scene).Single(t => t.name.StartsWith("Tableau_departs_double_face", StringComparison.Ordinal));
            Dirty(poi); Dirty(poi.interactionPoint);
        }

        public static void ConfigureAccueilLayout(Scene scene, StationDecisionWorld world, StationSocialWorld social)
        {
            var geometry = Components<Transform>(scene).Single(t => t.name == "Point_information_central");
            geometry.SetPositionAndRotation(AccueilCenter, Quaternion.identity); geometry.localScale = Vector3.one;
            Resize(geometry, "Comptoir_avant", new Vector3(0,.5f,-.9f), new Vector3(3.2f,1,1.05f));
            Resize(geometry, "Plateau_avant", new Vector3(0,1.04f,-.9f), new Vector3(3.4f,.09f,1.2f));
            Resize(geometry, "Retour_accessible", new Vector3(-2f,.38f,-.9f), new Vector3(.85f,.76f,1.05f));
            Resize(geometry, "Plateau_accessible", new Vector3(-2f,.8f,-.9f), new Vector3(.95f,.08f,1.18f));
            foreach (string name in new[] { "Retour_droit", "Siege_accueil_2", "Presentoir_informations_voyageurs", "Totem_information", "Panneau_information", "Symbole_information" })
            {
                var child = geometry.Find(name); if (child) { child.gameObject.SetActive(false); Dirty(child.gameObject); }
            }
            var monitors = geometry.Cast<Transform>().Where(t => t.name == "Terminal_informatique").ToArray();
            Require(monitors.Length > 0, "The information desk lacks a monitor.");
            for (int i = 0; i < monitors.Length; ++i)
            {
                monitors[i].gameObject.SetActive(i == 0);
                if (i == 0) monitors[i].localPosition = new Vector3(0,1.1f,-.6f);
                Dirty(monitors[i]); Dirty(monitors[i].gameObject);
            }
            var chair = geometry.Find("Siege_accueil_1");
            if (chair) { chair.localPosition = new Vector3(0,0,.75f); Dirty(chair); }
            var front = geometry.Find("Accueil_front");
            if (front)
            {
                front.localPosition = new Vector3(0,.58f,-1.44f);
                var text = front.GetComponent<TMP_Text>();
                if (text) { text.text = "ACCUEIL / INFORMATION"; text.fontSize = .24f; Dirty(text); }
                Dirty(front);
            }
            var desk = social.informationDesk;
            Require(desk && desk.queueSlots != null && desk.queueSlots.Length == 6, "The information desk must retain its six FIFO places.");
            for (int i = 0; i < desk.queueSlots.Length; ++i)
            {
                desk.queueSlots[i].position = AccueilQueue[i];
                desk.queueSlots[i].rotation = Quaternion.identity; Dirty(desk.queueSlots[i]);
            }
            desk.receptionist.transform.SetPositionAndRotation(AccueilCenter + new Vector3(0,.02f,.05f), Quaternion.Euler(0,180,0));
            if (desk.receptionist.interactionLookTarget)
            { desk.receptionist.interactionLookTarget.position = desk.queueSlots[0].position + Vector3.up * 1.55f; Dirty(desk.receptionist.interactionLookTarget); }
            desk.receptionRadius = Vector3.Distance(desk.receptionist.transform.position, desk.queueSlots[0].position) + .3f;
            var poi = world.pois.Single(p => p && p.poiId == "info-accueil");
            poi.interactionPoint.position = desk.queueSlots[0].position;
            poi.departurePoint.position = new Vector3(1f,.02f,15.1f);
            foreach (Transform marker in poi.departurePoint.parent)
            {
                if (marker.name == "Entry") marker.position = new Vector3(8.1f,.02f,11.2f);
                else if (marker.name == "Front aisle") marker.position = new Vector3(4.2f,.02f,14f);
                else if (marker.name == "Hall approach") marker.position = new Vector3(8f,.02f,9f);
                Dirty(marker);
            }
            Physics.SyncTransforms();
            Bounds bounds = CounterBounds(geometry);
            var blocker = Components<Transform>(scene).Single(t => t.name == "Information counter and chairs");
            blocker.position = new Vector3(bounds.center.x, 1f, bounds.center.z);
            var volume = blocker.GetComponent<NavMeshModifierVolume>();
            Require(volume, "The compact information desk lost its saved navigation exclusion.");
            volume.center = Vector3.zero; volume.size = new Vector3(bounds.size.x + .06f, 2.6f, bounds.size.z + .06f);
            Dirty(geometry); Dirty(blocker); Dirty(volume); Dirty(desk); Dirty(desk.receptionist.transform);
            Dirty(poi); Dirty(poi.interactionPoint); Dirty(poi.departurePoint);
        }

        public static void ConfigureHallCameras(Scene scene)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(StationCameraAuthoring.MountPrefabPath);
            Require(prefab, "The saved CCTV mounting prefab is missing.");
            Vector3[] corners = {new Vector3(-20,7.45f,-16),new Vector3(20,7.45f,-16),new Vector3(20,7.45f,16),new Vector3(-20,7.45f,16)};
            string[] labels = { "HALL SUD-OUEST / FONTAINE", "HALL SUD-EST / FONTAINE", "HALL NORD-EST / FONTAINE", "HALL NORD-OUEST / FONTAINE" };
            Vector3 aim = FountainCenter + Vector3.up * 1.1f;
            var metadata = Components<StationSurveillanceCamera>(scene).ToList();
            for (int i = 0; i < corners.Length; ++i)
            {
                var corner = corners[i];
                var anchor = Components<Transform>(scene).Where(t => t.name == "Iron column").OrderBy(t => Vector2.Distance(new Vector2(t.position.x,t.position.z),new Vector2(corner.x,corner.z))).First();
                var meta = metadata.SingleOrDefault(c => c.cameraId == HallCameraIds[i]);
                if (!meta)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    meta = instance.GetComponent<StationSurveillanceCamera>(); metadata.Add(meta);
                }
                var rig = meta.transform;
                rig.SetParent(null, true); rig.localScale = Vector3.one;
                float inward = -Mathf.Sign(corner.x);
                var attachment = new Vector3(anchor.position.x + inward * .21f, 7.45f, anchor.position.z);
                var lens = new Vector3(anchor.position.x + inward * .80f, 7.30f, anchor.position.z);
                rig.SetPositionAndRotation(attachment, Quaternion.identity);
                var camera = meta.cameraComponent; Require(camera, "The CCTV prefab lacks its optical camera.");
                var head = camera.transform.parent;
                var rotation = Quaternion.LookRotation(aim - lens, Vector3.up);
                head.SetPositionAndRotation(lens - rotation * camera.transform.localPosition, rotation);
                camera.fieldOfView = 68f; camera.aspect = 16f / 9f; camera.nearClipPlane = .08f; camera.farClipPlane = 400;
                camera.orthographic = false; camera.rect = new Rect(0,0,1,1); camera.enabled = false; camera.gameObject.SetActive(true);
                camera.gameObject.tag = "Untagged"; camera.name = "Optics — " + HallCameraIds[i];
                var listener = camera.GetComponent<AudioListener>(); if (listener) listener.enabled = false;
                var plate = rig.Find("Mount plate — attached to structural wall or column");
                if (plate) plate.rotation = Quaternion.LookRotation(Vector3.right * inward, Vector3.up);
                var arm = rig.Find("Support arm");
                if (arm)
                {
                    Vector3 end = head.position - head.forward * .12f;
                    arm.SetPositionAndRotation((attachment + end) * .5f, Quaternion.LookRotation(end - attachment, Vector3.up));
                    arm.localScale = new Vector3(.045f,.045f,Vector3.Distance(attachment,end));
                }
                meta.cameraId = HallCameraIds[i]; meta.displayName = labels[i];
                meta.purpose = "Observer les trajectoires autour de la fontaine centrale depuis un angle complémentaire du hall.";
                meta.mountAnchor = anchor;
                if (!meta.aimTarget) meta.aimTarget = Child(rig, "Purpose target — editable");
                meta.aimTarget.position = aim;
                Vector3[] coverage = { aim, FountainCenter + new Vector3(-5f,1.2f,0), FountainCenter + new Vector3(5f,1.2f,0),
                    FountainCenter + new Vector3(0,1.2f,-5f), FountainCenter + new Vector3(0,1.2f,5f) };
                var sampleRoot = rig.Find("Coverage samples — head height");
                if (!sampleRoot) sampleRoot = Child(rig, "Coverage samples — head height");
                var samples = (meta.coveragePoints ?? Array.Empty<Transform>()).Where(p => p).ToList();
                while (samples.Count < coverage.Length) samples.Add(Child(sampleRoot, "Fountain coverage " + (samples.Count + 1).ToString("00")));
                for (int p = 0; p < samples.Count; ++p)
                { samples[p].gameObject.SetActive(p < coverage.Length); if (p < coverage.Length) samples[p].position = coverage[p]; Dirty(samples[p]); }
                meta.coveragePoints = samples.Take(coverage.Length).ToArray();
                rig.name = HallCameraIds[i] + " CCTV — " + labels[i]; rig.SetParent(anchor, true);
                foreach (var component in rig.GetComponentsInChildren<Component>(true))
                    if (component)
                    {
                        Dirty(component); PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                        if (component is Transform) { Dirty(component.gameObject); PrefabUtility.RecordPrefabInstancePropertyModifications(component.gameObject); }
                    }
            }
            var controls = Components<TrainStationViewControls>(scene).Single();
            var orbit = controls.viewpoints[0];
            var nonHall = controls.viewpoints.Skip(1).Where(c => c && !c.GetComponentInParent<StationSurveillanceCamera>().cameraId.StartsWith("HALL-",StringComparison.Ordinal)).ToArray();
            controls.viewpoints = new[] { orbit }.Concat(HallCameraIds.Select(id => metadata.Single(m => m.cameraId == id).cameraComponent)).Concat(nonHall).ToArray();
            controls.viewNames = controls.viewpoints.Select((c,index) => index == 0 ? "01 / HALL - CAMERA LIBRE" :
                (index + 1).ToString("00") + " / " + c.GetComponentInParent<StationSurveillanceCamera>().cameraId + " / " + c.GetComponentInParent<StationSurveillanceCamera>().displayName).ToArray();
            var overview = orbit.GetComponent<TrainStationOrbitCamera>();
            if (overview && overview.focusPoint) { overview.focusPoint.position = FountainCenter + Vector3.up * 1.7f; Dirty(overview.focusPoint); }
            StationCameraPresentation.Ensure(controls); controls.SelectView(0); Dirty(controls);
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Validate redesigned hall")]
        public static void Validate()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "Hall validation requires stopped Edit mode.");
            var scene = SceneManager.GetActiveScene();
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path };
            Directory.CreateDirectory(Output);
            try
            {
                Require(scene.path == StationDecisionDayAuthoring.ScenePath, "Hall validation is limited to Daily.");
                var slab = Components<Transform>(scene).Single(t => t.name == "Hall slab 72 x 56 m");
                var root = scene.GetRootGameObjects().Single(g => g.name == StationDecisionHallFountainAuthoring.RootName).transform;
                var fountain = root.Cast<Transform>().Single(t => t.name.StartsWith("Fontaine centrale", StringComparison.Ordinal));
                report.hallCenter = new Vector3(slab.position.x,0,slab.position.z); report.fountainCenter = fountain.position;
                report.centeredOnHallSlab = Vector3.Distance(report.hallCenter,fountain.position) < .01f;
                Require(report.centeredOnHallSlab, "The fountain is not centered on the actual saved hall slab.");
                var geometry = Components<Transform>(scene).Single(t => t.name == "Point_information_central");
                Bounds bounds = CounterBounds(geometry); report.accueilCenter = geometry.position;
                report.accueilFootprintSize = bounds.size; report.accueilFootprintCenter = bounds.center;
                report.compactAccueil = bounds.size.x < 4.5f && bounds.size.z < 3.5f;
                report.centralAxisClear = bounds.min.x > 1.5f && bounds.min.z > 15f;
                Require(report.compactAccueil && report.centralAxisClear, "The information counter is too wide or still divides the central circulation axis.");
                var world = Components<StationDecisionWorld>(scene).Single();
                var social = Components<StationSocialWorld>(scene).Single();
                var desk = social.informationDesk;
                var display = world.pois.Single(p => p && p.poiId == "display-hall");
                report.queue = desk.queueSlots.Select(p => p.position).ToArray(); report.queueSlots = report.queue.Length;
                report.displayStandingPoints = display.standingPoints.Select(p => p.position).ToArray(); report.displayStops = report.displayStandingPoints.Length;
                Require(report.queueSlots == 6 && report.displayStops == 24, "Authored information and display capacity changed unexpectedly.");
                int mask = StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas);
                var origin = new Vector3(0,.02f,-20);
                foreach (var marker in MovedServiceMarkers(world,desk))
                {
                    var collisions = Physics.OverlapCapsule(marker.position + Vector3.up*.4f, marker.position + Vector3.up*1.5f, .27f, ~0, QueryTriggerInteraction.Ignore);
                    Require(collisions.Length == 0, "A hall standing marker intersects physical geometry: " + marker.name);
                    ++report.colliderChecks;
                    bool startOkay=NavMesh.SamplePosition(origin,out var start,.2f,mask);
                    bool endOkay=NavMesh.SamplePosition(marker.position,out var end,.2f,mask);
                    Require(startOkay && endOkay, "A hall route endpoint is off NavMesh.");
                    var path = new NavMeshPath();
                    Require(NavMesh.CalculatePath(start.position,end.position,mask,path) && path.status == NavMeshPathStatus.PathComplete, "An information/display marker lacks its complete native path.");
                    ++report.routeChecks;
                }
                for (int i=1;i<desk.queueSlots.Length;++i)
                {
                    var a=desk.queueSlots[i-1].position; var b=desk.queueSlots[i].position;
                    Require(!NavMesh.Raycast(a,b,out var obstruction,mask), "A FIFO queue segment crosses a native navigation edge: " + desk.queueSlots[i].name);
                    Require(Vector3.Distance(a,b)>1.15f, "The information queue no longer has comfortable standing spacing.");
                    ++report.queueSegmentChecks;
                }
                report.allPathsComplete = true;
                var cameras = Components<StationSurveillanceCamera>(scene);
                report.fixedCameras = cameras.Length; report.allCameras = Components<Camera>(scene).Length;
                Require(cameras.Count(c => c.cameraId.StartsWith("HALL-",StringComparison.Ordinal)) == 4, "Exactly four saved Hall cameras are required.");
                Require(report.fixedCameras == 16 && report.allCameras == 17, "The two extra Hall cameras must preserve all twelve other fixed cameras and the orbit view.");
                foreach (string id in HallCameraIds)
                {
                    var meta = cameras.Single(c => c.cameraId == id); var camera = meta.cameraComponent;
                    Require(meta.mountAnchor && meta.mountAnchor.name == "Iron column", "A Hall camera is not physically mounted on a saved structural column.");
                    Vector3 delta = camera.transform.position - FountainCenter;
                    var evidence = new CameraEvidence { cameraId = id, mount = meta.mountAnchor.name, position = camera.transform.position,
                        target = meta.aimTarget.position, image = Output + "/" + id + ".png", eulerAngles = camera.transform.eulerAngles, fieldOfView = camera.fieldOfView,
                        azimuthDegrees = Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg };
                    var centerView = camera.WorldToViewportPoint(FountainCenter + Vector3.up*1.1f);
                    evidence.centerVisible = Inside(centerView);
                    evidence.centerUnobstructed = ClearSightline(camera.transform.position,FountainCenter + Vector3.up*1.1f);
                    foreach (var point in meta.coveragePoints) if (Inside(camera.WorldToViewportPoint(point.position))) ++evidence.visibleCoveragePoints;
                    Require(evidence.centerVisible && evidence.centerUnobstructed && evidence.visibleCoveragePoints == 5,
                        "A Hall corner camera cannot clearly observe the fountain and its four rim samples: " + id);
                    report.cameras.Add(evidence);
                }
                report.hallCameras = report.cameras.Count;
                for (int a = 0; a < report.cameras.Count; ++a)
                for (int b = a+1; b < report.cameras.Count; ++b)
                    Require(Mathf.Abs(Mathf.DeltaAngle(report.cameras[a].azimuthDegrees,report.cameras[b].azimuthDegrees)) > 65f, "Two Hall cameras observe from nearly the same angle.");
                var controls = Components<TrainStationViewControls>(scene).Single();
                Require(controls.viewpoints.Length == 17 && controls.viewNames.Length == 17 && controls.viewButtons.Length == 17 && controls.viewpoints.Distinct().Count() == 17,
                    "The camera menu is missing or duplicating a saved view.");
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally { File.WriteAllText(Output + "/layout-verification.json",JsonUtility.ToJson(report,true)); }
        }

        public static void Capture()
        {
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == StationDecisionDayAuthoring.ScenePath, "Hall captures require Daily.");
            Directory.CreateDirectory(Output);
            var canvases = Components<Canvas>(scene); var states = canvases.Select(c => c.enabled).ToArray();
            var active = RenderTexture.active; RenderTexture target = null;
            try
            {
                foreach (var canvas in canvases) canvas.enabled = false;
                target = new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32); target.Create();
                foreach (var meta in Components<StationSurveillanceCamera>(scene).Where(c => HallCameraIds.Contains(c.cameraId)))
                {
                    var camera = meta.cameraComponent; float aspect = camera.aspect; var rect = camera.rect;
                    try
                    {
                        camera.aspect = 16f/9f; camera.rect = new Rect(0,0,1,1);
                        RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                        RenderTexture.active = target; var pixels = new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
                        try { pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0); pixels.Apply(); File.WriteAllBytes(Output + "/" + meta.cameraId + ".png",pixels.EncodeToPNG()); }
                        finally { UnityEngine.Object.DestroyImmediate(pixels); }
                    }
                    finally { camera.aspect = aspect; camera.rect = rect; }
                }
            }
            finally
            {
                for (int i=0;i<canvases.Length;++i) if (canvases[i]) canvases[i].enabled = states[i];
                RenderTexture.active = active; if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            }
        }

        public static IEnumerable<Transform> MovedServiceMarkers(StationDecisionWorld world, StationInformationDesk desk)
        {
            var display=world.pois.Single(p=>p && p.poiId=="display-hall");
            var info=world.pois.Single(p=>p && p.poiId=="info-accueil");
            return display.standingPoints.Concat(new[] {display.interactionPoint,info.interactionPoint,info.departurePoint})
                .Concat(desk.queueSlots).Concat(info.departurePoint.parent.Cast<Transform>().Where(t=>
                    t.name=="Entry" || t.name=="Front aisle" || t.name=="Hall approach" || t.name=="Departure")).Where(t=>t).Distinct();
        }

        [Serializable] sealed class QueueProbeReport
        {
            public string checkedUtc, scene, scope="Read-only native samples on the current saved layout; compare old straight tail and proposed bent tail before a final bake.";
            public List<QueueSample> samples=new List<QueueSample>();
            public List<NearbyGeometry> nearbyColliders=new List<NearbyGeometry>(), nearbyExclusions=new List<NearbyGeometry>();
        }
        [Serializable] sealed class QueueSample
        {
            public string name;
            public Vector3 requested, nearest, edge;
            public bool onFloor, nativePathComplete;
            public float nearestDistance, nativeEdgeDistance;
        }
        [Serializable] sealed class NearbyGeometry
        {
            public string name;
            public Vector3 center,size,eulerAngles;
        }
        public static void ProbeQueue()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling,"Queue probe requires stopped Edit mode.");
            var scene=SceneManager.GetActiveScene(); Require(scene.path==StationDecisionDayAuthoring.ScenePath,"Queue probe requires Daily.");
            Physics.SyncTransforms();
            var report=new QueueProbeReport {checkedUtc=DateTime.UtcNow.ToString("O"),scene=scene.path};
            int mask=StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas);
            var points=new List<(string name,Vector3 point)>();
            for(int i=0;i<AccueilQueue.Length;++i) points.Add(("Bent queue "+(i+1),AccueilQueue[i]));
            points.Add(("Previous straight slot 05",new Vector3(9.4f,.02f,15.1f)));
            points.Add(("Previous straight slot 06",new Vector3(10.7f,.02f,15.1f)));
            points.Add(("Bent entry",new Vector3(8.1f,.02f,11.2f)));
            foreach(var item in points)
            {
                var evidence=new QueueSample {name=item.name,requested=item.point};
                evidence.onFloor=NavMesh.SamplePosition(item.point,out var hit,.18f,mask) && Mathf.Abs(hit.position.y-.02f)<.15f;
                if(NavMesh.SamplePosition(item.point,out var nearest,5f,mask))
                {
                    evidence.nearest=nearest.position; evidence.nearestDistance=Vector3.Distance(item.point,nearest.position);
                    if(NavMesh.FindClosestEdge(nearest.position,out var edge,mask)) {evidence.edge=edge.position;evidence.nativeEdgeDistance=edge.distance;}
                    if(NavMesh.SamplePosition(new Vector3(0,.02f,-20),out var origin,.2f,mask))
                    { var path=new NavMeshPath(); evidence.nativePathComplete=NavMesh.CalculatePath(origin.position,nearest.position,mask,path) && path.status==NavMeshPathStatus.PathComplete; }
                }
                report.samples.Add(evidence);
            }
            var focus=new Vector3(9.4f,1f,15.1f);
            foreach(var collider in Components<Collider>(scene).Where(c=>c.enabled && c.gameObject.activeInHierarchy && !c.isTrigger && c.bounds.max.y>.2f && c.bounds.SqrDistance(focus)<16f))
                report.nearbyColliders.Add(new NearbyGeometry {name=Hierarchy(collider.transform),center=collider.bounds.center,size=collider.bounds.size,eulerAngles=collider.transform.eulerAngles});
            foreach(var volume in Components<NavMeshModifierVolume>(scene).Where(v=>v.enabled && v.gameObject.activeInHierarchy && v.area==1))
            {
                var bounds=new Bounds(volume.transform.TransformPoint(volume.center),Vector3.zero);
                for(int i=0;i<8;++i) bounds.Encapsulate(volume.transform.TransformPoint(volume.center+Vector3.Scale(volume.size*.5f,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));
                if(bounds.SqrDistance(focus)<16f) report.nearbyExclusions.Add(new NearbyGeometry {name=Hierarchy(volume.transform),center=bounds.center,size=bounds.size,eulerAngles=volume.transform.eulerAngles});
            }
            Directory.CreateDirectory(Output); File.WriteAllText(Output+"/queue-native-probe.json",JsonUtility.ToJson(report,true));
        }
        static string Hierarchy(Transform item) => item.parent ? Hierarchy(item.parent)+"/"+item.name : item.name;

        static bool Inside(Vector3 viewport) => viewport.z > 0 && viewport.x > .03f && viewport.x < .97f && viewport.y > .03f && viewport.y < .97f;
        static bool ClearSightline(Vector3 origin, Vector3 target)
        {
            Vector3 direction = target-origin; float length = direction.magnitude;
            // Fountain itself is the observation target; a ray terminating above its basin must not hit architecture first.
            return !Physics.Raycast(origin,direction.normalized,length-.15f,~0,QueryTriggerInteraction.Ignore);
        }
        static Bounds CounterBounds(Transform geometry)
        {
            var colliders = geometry.GetComponentsInChildren<Collider>(false).Where(c => c.enabled && !c.isTrigger).ToArray();
            Require(colliders.Length > 0, "The compact counter has no physical footprint.");
            var result = colliders[0].bounds; foreach (var collider in colliders.Skip(1)) result.Encapsulate(collider.bounds); return result;
        }
        static void Resize(Transform root, string name, Vector3 position, Vector3 size)
        { var part = root.Find(name); Require(part,"Missing information counter part: " + name); part.localPosition=position; part.localScale=size; Dirty(part); }
        static Transform Child(Transform parent, string name)
        { var child = new GameObject(name).transform; child.SetParent(parent,false); return child; }
        static void Dirty(UnityEngine.Object item) { if (item) EditorUtility.SetDirty(item); }
    }
}
#endif