#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using StationFlow.TrainStation.Social.Editor;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Isolated scene authoring: usable amenities, separate counter departures and real furniture exclusions.</summary>
    public static class StationDecisionLabAmenitiesAuthoring
    {
        public const string RootName = "14 Decision Lab — accessible amenities";
        public const string NavigationPath = "Assets/TrainStation/DecisionLab/Navigation/DecisionLab_Amenities.asset";
        public const string ReportPath = ReportDirectory + "/amenities-authoring.json";
        const string PassengerPrefab = "Assets/TrainStation/Social/Prefabs/Station_Passenger.prefab";
        const float NavTolerance = .16f;
        [Serializable] public sealed class FileEvidence { public string path, before, after; }
        [Serializable] public sealed class SeatRecord
        {
            public string id, activity, path; public Vector3 approach, seatedRoot;
            public float transitionDistance, approachSnapError;
        }
        [Serializable] public sealed class ServiceRecord
        {
            public string key, service, poiId; public Vector3 entry, frontAisle, hallApproach, departure, staff;
            // Design requirements only; the independent amenities verifier measures actual clearance.
            public float requiredEntryPhysicalWidth, requiredEntryAgentCenterWidth, requiredAislePhysicalWidth, requiredAisleAgentCenterWidth;
            public Vector3[] queue; public int seats;
        }
        [Serializable] public sealed class CameraRecord
        {
            public string cameraId; public Vector3 lensBefore, lensAfter, target;
            public float fieldOfViewBefore, fieldOfViewAfter; public bool mountAndLensPreserved, targetInFrame;
        }
        [Serializable] public sealed class Report
        {
            public string checkedUtc, scene, navigationAsset, previousNavigationAsset, backup, error;
            public bool success, alreadyAuthored, sourceAssetsPreserved, savedSceneClean;
            public int cafeTables, cafeSeats, readingSeats, restSeats, floorColliders, furnitureFootprints, pathChecks, queueMarkers, triangles;
            public float agentRadius, agentHeight, minNavHeight, maxNavHeight;
            public List<FileEvidence> protectedFiles = new List<FileEvidence>();
            public List<SeatRecord> seats = new List<SeatRecord>();
            public List<ServiceRecord> services = new List<ServiceRecord>();
            public List<CameraRecord> cameras = new List<CameraRecord>();
            public string seatContract = "Reserved side/front approach then a short local sit transition; existing seated-idle clip represents coffee, reading and rest. No sip/page-turn gestures are claimed.";
            public string navigationContract = "Fresh Lab-owned bake from physical colliders. Existing floor allowlist, default NotWalkable and track exclusions retained; moved/new furniture gets floor-to-head footprint exclusions.";
        }
        sealed class Context
        {
            public Scene scene; public StationDecisionWorld world; public StationSocialWorld social; public NavMeshSurface surface;
            public Transform root, seats, circulation, blockers, cafe, relay, tickets, waiting, info;
            public Report report; public Vector3 seatedHip;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Upgrade accessible shops and seating")]
        public static void UpgradeOpenScene()
        {
            RequireEditAndClean();
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == ScenePath, "Amenities authoring only accepts the saved isolated Decision Lab scene.");
            var existing = scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
            if (existing) { ValidateOpenScene(); return; }
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path };
            string createdNavigation = null; bool saved = false;
            try
            {
                var c = ReadContext(scene, report);
                Protect(report, SourceScene); Protect(report, SourceSchedule);
                report.previousNavigationAsset = AssetDatabase.GetAssetPath(c.surface.navMeshData);
                Protect(report, report.previousNavigationAsset);
                Protect(report, PassengerPrefab);
                Protect(report, "Assets/TrainStation/Social/Prefabs/Station_ShopServicePoint.prefab");
                report.backup = "BuildRequests/Backups/DecisionLabAmenities-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
                Directory.CreateDirectory(Path.GetDirectoryName(report.backup)); File.Copy(ScenePath, report.backup, false);
                c.root = new GameObject(RootName).transform; SceneManager.MoveGameObjectToScene(c.root.gameObject, scene);
                c.seats = Child(c.root, "Seats"); c.circulation = Child(c.root, "Circulation"); c.blockers = Child(c.root, "Furniture footprints — Not Walkable");
                c.seatedHip = ReadSavedSeatedHip(scene);
                ConfigureCafe(c); ConfigureRelay(c); ConfigureTickets(c); ConfigureInformation(c); ConfigureRest(c);
                ReaimServiceCameras(c);
                ConfigureFloorOnlyNavigation(c);
                Physics.SyncTransforms();
                c.surface.BuildNavMesh();
                Require(c.surface.navMeshData, "Unity did not produce Lab amenities navigation.");
                Require(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(c.surface.navMeshData)), "A new bake unexpectedly reused a persistent navigation asset.");
                Directory.CreateDirectory(Path.GetDirectoryName(NavigationPath)); AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                createdNavigation = AssetDatabase.GenerateUniqueAssetPath(NavigationPath);
                AssetDatabase.CreateAsset(c.surface.navMeshData, createdNavigation);
                c.surface.RemoveData(); c.surface.AddData(); Record(c.surface);
                SnapAuthoredAnchors(c);
                Validate(c);
                foreach (var evidence in report.protectedFiles) { evidence.after = Hash(evidence.path); Require(evidence.before == evidence.after, "Protected source changed: " + evidence.path); }
                report.sourceAssetsPreserved = true;
                Record(c.world); Record(c.social);
                AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save the Lab amenities layout.");
                saved = true; report.savedSceneClean = !scene.isDirty; report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                if (!saved)
                {
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    if (!string.IsNullOrEmpty(createdNavigation) && createdNavigation.StartsWith("Assets/TrainStation/DecisionLab/Navigation/", StringComparison.Ordinal))
                        AssetDatabase.DeleteAsset(createdNavigation);
                }
                throw;
            }
            finally { Write(ReportPath, report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Validate accessible shops and seating")]
        public static void ValidateOpenScene()
        {
            RequireEditAndClean();
            var scene = SceneManager.GetActiveScene(); Require(scene.path == ScenePath, "Open Decision Lab before checking amenities.");
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path, alreadyAuthored = true };
            try
            {
                var c = ReadContext(scene, report);
                c.root = scene.GetRootGameObjects().Single(g => g.name == RootName).transform;
                c.seats = c.root.Find("Seats"); c.circulation = c.root.Find("Circulation"); c.blockers = c.root.Find("Furniture footprints — Not Walkable");
                Validate(c); report.savedSceneClean = !scene.isDirty; report.success = true;
                var prior = File.Exists(ReportPath) ? JsonUtility.FromJson<Report>(File.ReadAllText(ReportPath)) : null;
                if (prior != null)
                {
                    report.protectedFiles = prior.protectedFiles;
                    foreach (var evidence in report.protectedFiles) { evidence.after = Hash(evidence.path); Require(evidence.before == evidence.after, "Protected source changed: " + evidence.path); }
                    report.sourceAssetsPreserved = report.protectedFiles.Count > 0;
                }
            }
            catch (Exception error) { report.success = false; report.error = error.ToString(); throw; }
            finally { Write(ReportDirectory + "/amenities-saved-check.json", report); }
        }

        static Context ReadContext(Scene scene, Report report)
        {
            var transforms = Components<Transform>(scene);
            return new Context { scene = scene, report = report, world = Components<StationDecisionWorld>(scene).Single(),
                social = Components<StationSocialWorld>(scene).Single(), surface = Components<NavMeshSurface>(scene).Single(),
                cafe = transforms.Single(t => t.name == "02_Cafe_et_boulangerie"), relay = transforms.Single(t => t.name == "03_Relay_presse_et_livres"),
                tickets = transforms.Single(t => t.name == "01_Accueil_et_billetterie"), waiting = transforms.Single(t => t.name == "05_Salon_attente"),
                info = transforms.Single(t => t.name == "Point_information_central") };
        }
        static StationInformationDesk Desk(Context c, string contains) => c.social.shopDesks.Single(d => d.serviceName.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
        static StationDecisionPoi Poi(Context c, string id) => c.world.pois.Single(p => p.poiId == id);

        static void ConfigureCafe(Context c)
        {
            var tables = c.cafe.Cast<Transform>().Where(t => t.name == "Table_bistrot_et_chaises").ToArray();
            Require(tables.Length == 6, "Expected six existing café table clusters to rearrange.");
            var seats = new List<StationSeat>();
            for (int i = 0; i < tables.Length; ++i)
            {
                var table = tables[i]; table.gameObject.SetActive(i < 2); Record(table.gameObject);
                if (i >= 2) continue;
                table.name = "Lab Café table " + (i + 1).ToString("00");
                table.localPosition = new Vector3(5.2f, 0, i == 0 ? -3f : 2.4f); table.localRotation = Quaternion.identity;
                Record(table);
                AuthorTableSeats(c, table, "cafe-" + (i + 1), DecisionSeatedActivity.Coffee, seats);
            }
            var desk = Desk(c, "Cafe");
            ConfigureService(c, "Cafe", desk, c.cafe, new Vector3(-3.7f, 0, 4.45f),
                new[] { P(-3.7f,.9f), P(-3.7f,-.35f), P(-3.7f,-1.6f), P(-3.7f,-2.85f) }, P(-1.2f,.9f), "cafe");
            var poi = Poi(c, "cafe");
            poi.seats = seats.ToArray(); poi.seatedActivity = DecisionSeatedActivity.Coffee;
            poi.seatedMeanDurationSeconds = 24f; poi.seatedDurationSpreadSeconds = 6f; poi.capacity = desk.queueSlots.Length + seats.Count; Record(poi);
            AddFootprint(c, c.cafe.Find("Comptoir_boulangerie"), "Cafe counter");
            c.report.cafeTables = 2; c.report.cafeSeats = seats.Count;
        }

        static void ConfigureRelay(Context c)
        {
            var racks = c.relay.Cast<Transform>().Where(t => t.name.StartsWith("Rayonnage_presse_", StringComparison.Ordinal)).OrderBy(t => t.name).ToArray();
            Require(racks.Length == 3, "Expected three original Relay display racks.");
            racks[0].localPosition = new Vector3(-6.85f,0,2.9f); racks[0].localRotation = Quaternion.Euler(0,-90,0);
            racks[1].localPosition = new Vector3(6.85f,0,2.4f); racks[1].localRotation = Quaternion.Euler(0,90,0);
            racks[2].gameObject.SetActive(false);
            foreach (var rack in racks) { Record(rack); Record(rack.gameObject); if (rack.gameObject.activeSelf) AddFootprint(c, rack, rack.name); }
            var postcards = c.relay.Find("Presentoir_cartes_postales");
            postcards.localPosition = new Vector3(6.85f,0,-3.1f); postcards.localRotation = Quaternion.Euler(0,90,0); Record(postcards);
            AddFootprint(c, postcards, "Relay postcards");
            var pivot = Child(c.relay, "Lab Relay counter");
            var originalCenter = new Vector3(-4.9f,0,-3.5f);
            foreach (string name in new[] { "Comptoir_caisse", "Bandeau_caisse", "Terminal_informatique" })
            {
                var item = c.relay.Find(name); Require(item, "Missing Relay counter part: " + name);
                var position = item.localPosition - originalCenter; var rotation = item.localRotation;
                item.SetParent(pivot, false); item.localPosition = position; item.localRotation = rotation; Record(item);
            }
            pivot.localPosition = new Vector3(-5.5f,0,-2.8f); pivot.localRotation = Quaternion.Euler(0,-90,0); Record(pivot);
            // The red fascia faces the public aisle; the register screen faces the clerk behind it.
            var terminal = pivot.Find("Terminal_informatique"); terminal.localRotation *= Quaternion.Euler(0,180,0); Record(terminal);
            AddFootprint(c, pivot, "Relay counter"); AddFootprint(c, c.relay.Find("Librairie_du_fond"), "Relay back books");
            var sourceTable = c.cafe.Cast<Transform>().First(t => t.name.StartsWith("Lab Café table", StringComparison.Ordinal));
            var reading = UnityEngine.Object.Instantiate(sourceTable.gameObject, c.relay).transform;
            reading.name = "Lab Reading table"; reading.localPosition = new Vector3(3.3f,0,2.4f); reading.localRotation = Quaternion.identity;
            foreach (var stale in reading.GetComponentsInChildren<StationSeat>(true)) UnityEngine.Object.DestroyImmediate(stale);
            var card = reading.Find("Carte_menu"); if (card) card.gameObject.SetActive(false);
            AddReadingProps(reading);
            var seats = new List<StationSeat>(); AuthorTableSeats(c, reading, "reading-1", DecisionSeatedActivity.Reading, seats);
            var desk = Desk(c, "Relay");
            ConfigureService(c, "Relay", desk, c.relay, P(-6.85f,-2.8f),
                new[] { P(-3.9f,-2.8f), P(-3.9f,-1.5f), P(-3.9f,-.2f), P(-3.9f,1.1f) }, P(-1.7f,-2.8f), "shop-relay");
            var poi = Poi(c, "shop-relay"); poi.seats = seats.ToArray(); poi.seatedActivity = DecisionSeatedActivity.Reading;
            poi.seatedMeanDurationSeconds = 32f; poi.seatedDurationSpreadSeconds = 8f; poi.capacity = desk.queueSlots.Length + seats.Count; Record(poi);
            c.report.readingSeats = seats.Count;
        }

        static void ConfigureTickets(Context c)
        {
            foreach (var item in c.tickets.Cast<Transform>().Where(t => t.name.StartsWith("Poteau", StringComparison.Ordinal) || t.name.StartsWith("Ruban_file_", StringComparison.Ordinal)))
            { item.gameObject.SetActive(false); Record(item.gameObject); }
            // Counter posts have a reusable group name in the authored interior.
            foreach (var item in c.tickets.Cast<Transform>().Where(t => t.name.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("Queue", StringComparison.OrdinalIgnoreCase) >= 0))
            { item.gameObject.SetActive(false); Record(item.gameObject); }
            var counters = c.tickets.Find("Guichets_accessibles");
            foreach (var chair in counters.Cast<Transform>().Where(t => t.name == "Siege_agent" && t.localPosition.x < -2f))
            { chair.localPosition = new Vector3(chair.localPosition.x,0,4.7f); Record(chair); }
            var desk = Desk(c, "Billetterie");
            ConfigureService(c, "Billetterie", desk, c.tickets, P(-4.8f,3.35f),
                new[] { P(-4.8f,.7f), P(-4.8f,-.6f), P(-4.8f,-1.9f), P(-4.8f,-3.2f) }, P(-1.8f,-.2f), null);
            foreach (var counter in counters.Cast<Transform>().Where(t => t.name.StartsWith("Comptoir_", StringComparison.Ordinal)))
                AddFootprint(c, counter, "Billetterie " + counter.name);
        }

        static void ConfigureInformation(Context c)
        {
            c.info.position = new Vector3(7f,0,-1.8f); Record(c.info);
            AddFootprint(c, c.info, "Information counter and chairs");
            var desk = c.social.informationDesk;
            var queue = Enumerable.Range(0,6).Select(i => P(7f,-4.35f - i * 1.25f)).ToArray();
            ConfigureWorldService(desk, new Vector3(7f, .02f, -1.75f), queue);
            var group = Child(c.circulation, "Info");
            Marker(group, "Entry", P(4.4f,-5f), Quaternion.identity);
            Marker(group, "Front aisle", P(4.4f,-4.35f), Quaternion.identity);
            Marker(group, "Hall approach", P(0,-6f), Quaternion.identity);
            var exit = Marker(group, "Departure", P(4.4f,-4.35f), Quaternion.identity);
            var poi = Poi(c, "info-accueil"); poi.departurePoint = exit; poi.interactionPoint.position = queue[0]; poi.capacity = queue.Length;
            Record(poi); Record(poi.interactionPoint);
        }

        static void ConfigureRest(Context c)
        {
            var allBenches = c.waiting.Cast<Transform>().Where(t => t.name.StartsWith("Banquette_", StringComparison.Ordinal)).ToArray();
            Require(allBenches.Length == 6, "Expected six saved waiting-room benches.");
            // Use the four front benches; the rear pair remains beside the charging table.
            var benches = allBenches.Where(t => t.localPosition.z < 2f).OrderBy(t => t.localPosition.z).ThenBy(t => t.localPosition.x).ToArray();
            Require(benches.Length == 4, "Expected four front waiting-room benches with clear approaches.");
            var seats = new List<StationSeat>();
            for (int i = 0; i < benches.Length; ++i)
            {
                var bench = benches[i];
                var targetHip = bench.TransformPoint(new Vector3(0,.65f,-.025f));
                var facing = bench.rotation * Quaternion.Euler(0,180,0);
                seats.Add(Seat(c, "rest-" + (i + 1), DecisionSeatedActivity.Rest, targetHip, facing,
                    bench.TransformPoint(new Vector3(0,0,-.98f))));
            }
            var poi = Poi(c, "rest-lounge-entrance"); poi.seats = seats.ToArray(); poi.seatedActivity = DecisionSeatedActivity.Rest;
            poi.seatedMeanDurationSeconds = 28f; poi.seatedDurationSpreadSeconds = 8f; poi.capacity = seats.Count; poi.spreadRadius = 0f;
            poi.interactionPoint.position = c.waiting.TransformPoint(P(0,-4.8f)); Record(poi); Record(poi.interactionPoint);
            c.report.restSeats = seats.Count;
        }

        static void ConfigureService(Context c, string key, StationInformationDesk desk, Transform bay, Vector3 staffLocal, Vector3[] queueLocal, Vector3 departureLocal, string poiId)
        {
            ConfigureWorldService(desk, bay.TransformPoint(staffLocal) + Vector3.up * .02f, queueLocal.Select(bay.TransformPoint).ToArray());
            var group = Child(c.circulation, key);
            Marker(group, "Entry", bay.TransformPoint(P(0,-6.9f)), bay.rotation);
            Marker(group, "Front aisle", bay.TransformPoint(P(0,-3f)), bay.rotation);
            // The hall has 0.85 m column plinths at x=-20 and z=-16/0/16.
            // Approach beside each plinth, while retaining the actual doorway centre as Entry.
            float hallSideOffset = key == "Billetterie" ? -2.5f : 2.5f;
            Marker(group, "Hall approach", bay.TransformPoint(P(hallSideOffset,-9f)), bay.rotation);
            var exit = Marker(group, "Departure", bay.TransformPoint(departureLocal), bay.rotation);
            if (poiId == null) return;
            var poi = Poi(c, poiId); poi.departurePoint = exit; poi.interactionPoint.position = desk.queueSlots[0].position; poi.spreadRadius = 0f;
            Record(poi); Record(poi.interactionPoint);
        }
        static void ConfigureWorldService(StationInformationDesk desk, Vector3 staff, Vector3[] positions)
        {
            Require(desk && desk.receptionist && desk.queueSlots.Length > 0, "A saved service is incomplete.");
            var parent = desk.queueSlots[0].parent;
            var previous = desk.queueSlots.ToArray(); var slots = new List<Transform>();
            var customerFacing = staff - positions[0]; customerFacing.y = 0;
            for (int i = 0; i < positions.Length; ++i)
            {
                var point = i < previous.Length && previous[i] ? previous[i] : Child(parent, "Lab queue " + i);
                point.name = i == 0 ? "00 Service front — clear departure beside queue" : "Queue position " + i.ToString("00");
                point.gameObject.SetActive(true); point.position = positions[i];
                point.rotation = Quaternion.LookRotation(customerFacing.normalized, Vector3.up);
                slots.Add(point); Record(point); Record(point.gameObject);
            }
            foreach (var unused in previous.Where(t => t && !slots.Contains(t))) { unused.gameObject.SetActive(false); Record(unused.gameObject); }
            desk.queueSlots = slots.ToArray(); desk.receptionist.transform.position = staff;
            desk.receptionist.transform.rotation = Quaternion.LookRotation(-customerFacing.normalized, Vector3.up);
            if (desk.receptionist.interactionLookTarget) { desk.receptionist.interactionLookTarget.position = positions[0] + Vector3.up * 1.55f; Record(desk.receptionist.interactionLookTarget); }
            desk.receptionRadius = Mathf.Max(desk.receptionRadius, Vector3.Distance(staff, positions[0]) + .3f);
            Record(desk); Record(desk.receptionist.transform);
        }

        static void ReaimServiceCameras(Context c)
        {
            foreach (var pair in new[] { new[] { "HALL-01", "Info" }, new[] { "SHOP-01", "Cafe" }, new[] { "SHOP-02", "Relay" }, new[] { "SHOP-03", "Billetterie" } })
            {
                var metadata = Components<StationSurveillanceCamera>(c.scene).Single(m => m.cameraId == pair[0]);
                var camera = metadata.cameraComponent;
                var desk = pair[1] == "Info" ? c.social.informationDesk : Desk(c, pair[1]);
                var exit = c.circulation.Find(pair[1]).Find("Departure");
                var samples = new List<Vector3> { desk.receptionist.transform.position + Vector3.up * 1.55f,
                    desk.queueSlots[0].position + Vector3.up * 1.55f, desk.queueSlots.Last().position + Vector3.up * 1.55f,
                    exit.position + Vector3.up * 1.55f };
                var poi = c.world.pois.FirstOrDefault(p => p && p.service == desk);
                if (poi && poi.seats != null) samples.AddRange(poi.seats.Select(s => s.seatedPose.position + Vector3.up * 1.3f));
                Require(camera && metadata.aimTarget && metadata.mountAnchor && metadata.coveragePoints.Length >= 4, "Incomplete saved CCTV references: " + pair[0]);
                var lens = camera.transform.position; var mount = metadata.mountAnchor; var rigPosition = metadata.transform.position;
                var fieldOfView = camera.fieldOfView;
                // Average view directions, so nearby and distant service points both contribute to the surveyed aim.
                var directions = samples.Take(4).Select(p => (p-lens).normalized).ToArray();
                var direction = directions.Aggregate(Vector3.zero, (sum,p) => sum+p).normalized;
                var target = lens + direction * samples.Take(4).Average(p => Vector3.Distance(p,lens));
                var head = camera.transform.parent;
                Require(head && head.name == "Camera head — fixed surveyed orientation", "Unexpected fixed CCTV hierarchy.");
                head.rotation = Quaternion.LookRotation(direction, Vector3.up);
                head.position += lens - camera.transform.position; // Preserve the exact optical lens location.
                metadata.aimTarget.position = target;
                for (int i = 0; i < metadata.coveragePoints.Length; ++i)
                { metadata.coveragePoints[i].position = samples[Mathf.Min(i,samples.Count-1)]; Record(metadata.coveragePoints[i]); }
                Record(head); Record(metadata.aimTarget); Record(metadata);
                var viewport = camera.WorldToViewportPoint(target);
                var record = new CameraRecord { cameraId = metadata.cameraId, lensBefore = lens, lensAfter = camera.transform.position,
                    target = target, fieldOfViewBefore = fieldOfView, fieldOfViewAfter = camera.fieldOfView,
                    mountAndLensPreserved = metadata.mountAnchor == mount && metadata.transform.position == rigPosition && Vector3.Distance(lens,camera.transform.position) < .0001f && camera.fieldOfView == fieldOfView,
                    targetInFrame = viewport.z > camera.nearClipPlane && viewport.x > 0 && viewport.x < 1 && viewport.y > 0 && viewport.y < 1 };
                c.report.cameras.Add(record);
                Require(record.mountAndLensPreserved && record.targetInFrame, "CCTV re-aim changed a mount/lens or lost its target: " + metadata.cameraId);
            }
        }

        static void AuthorTableSeats(Context c, Transform table, string prefix, DecisionSeatedActivity activity, List<StationSeat> result)
        {
            AddFootprint(c, table.Find("Plateau"), prefix + " table top and pedestal");
            foreach (var chair in table.Cast<Transform>().Where(t => t.name.StartsWith("Chaise_", StringComparison.Ordinal)).OrderBy(t => t.name))
            {
                AddFootprint(c, chair, prefix + " " + chair.name);
                var facing = chair.rotation * Quaternion.Euler(0,180,0);
                var hip = chair.TransformPoint(new Vector3(0,.65f,-.025f));
                // Side access avoids walking through the tabletop in front of an inward-facing café chair.
                var approach = chair.position - table.right * 1.06f; approach.y = 0;
                result.Add(Seat(c, prefix + "-" + chair.name, activity, hip, facing, approach));
            }
        }
        static StationSeat Seat(Context c, string id, DecisionSeatedActivity activity, Vector3 targetHip, Quaternion facing, Vector3 approach)
        {
            var root = Child(c.seats, id);
            var seat = root.gameObject.AddComponent<StationSeat>(); seat.seatId = "DecisionLab-" + id;
            seat.seatedPose = Marker(root, "Seated actor root — calibrated", targetHip - facing * c.seatedHip, facing);
            seat.approachPoint = Marker(root, "Standing approach — floor", approach, facing); seat.luggagePoint = null;
            Record(seat); return seat;
        }
        static Vector3 ReadSavedSeatedHip(Scene scene)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PassengerPrefab); Require(prefab, "Missing saved seated-passenger calibration prefab.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            try
            {
                var animator = instance.GetComponentInChildren<Animator>(true);
                Require(animator && animator.isHuman, "The saved seated passenger must retain its Humanoid rig.");
                return instance.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Hips).position);
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        static void AddReadingProps(Transform table)
        {
            var material = table.Find("Carte_menu") ? table.Find("Carte_menu").GetComponent<Renderer>().sharedMaterial : null;
            var book = Child(table, "Livre ouvert — lecture sur place");
            book.localPosition = new Vector3(-.08f,.84f,0); book.localRotation = Quaternion.Euler(0,18,0);
            foreach (int side in new[] { -1, 1 })
            {
                var page = GameObject.CreatePrimitive(PrimitiveType.Cube); page.name = side < 0 ? "Pages gauche" : "Pages droite";
                page.transform.SetParent(book, false); page.transform.localPosition = new Vector3(side * .12f,.012f,0);
                page.transform.localScale = new Vector3(.23f,.018f,.33f); page.transform.localRotation = Quaternion.Euler(0,0,-side * 5f);
                UnityEngine.Object.DestroyImmediate(page.GetComponent<Collider>());
                if (material) page.GetComponent<Renderer>().sharedMaterial = material;
            }
        }

        static void AddFootprint(Context c, Transform furniture, string name)
        {
            Require(furniture, "Missing furniture for footprint: " + name);
            var colliders = furniture.GetComponentsInChildren<Collider>(true).Where(x => x.enabled && !x.isTrigger && x.gameObject.activeInHierarchy).ToArray();
            Require(colliders.Length > 0, "Furniture has no physical collider: " + name);
            Physics.SyncTransforms();
            var bounds = colliders[0].bounds; foreach (var collider in colliders.Skip(1)) bounds.Encapsulate(collider.bounds);
            var item = Child(c.blockers, name); item.position = new Vector3(bounds.center.x,1f,bounds.center.z);
            var volume = item.gameObject.AddComponent<NavMeshModifierVolume>(); volume.area = 1;
            volume.size = new Vector3(bounds.size.x + .06f, 2.6f, bounds.size.z + .06f);
            volume.center = Vector3.zero; Record(volume);
        }
        static void ConfigureFloorOnlyNavigation(Context c)
        {
            c.surface.defaultArea = 1; c.surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            c.surface.overrideVoxelSize = true; c.surface.voxelSize = .08f;
            var surface = new SerializedObject(c.surface); surface.FindProperty("m_GenerateLinks").boolValue = false; surface.ApplyModifiedPropertiesWithoutUndo();
            foreach (var collider in Components<Collider>(c.scene).Where(StationNavigationSafety.IsStationFloor))
            {
                var modifier = collider.GetComponent<NavMeshModifier>() ?? collider.gameObject.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true; modifier.area = 0; modifier.ignoreFromBuild = false; modifier.applyToChildren = false;
                modifier.overrideGenerateLinks = true; modifier.generateLinks = false; Record(modifier);
                ++c.report.floorColliders;
            }
            Record(c.surface);
        }
        static void SnapAuthoredAnchors(Context c)
        {
            foreach (var seat in c.seats.GetComponentsInChildren<StationSeat>(true))
            { seat.approachPoint.position = ExactFloor(seat.approachPoint.position); Record(seat.approachPoint); }
            foreach (var desk in new[] { c.social.informationDesk }.Concat(c.social.shopDesks))
                foreach (var marker in desk.queueSlots) { marker.position = ExactFloor(marker.position); Record(marker); }
            foreach (var marker in c.circulation.GetComponentsInChildren<Transform>().Where(t => t.childCount == 0))
            { marker.position = ExactFloor(marker.position); Record(marker); }
            foreach (var poi in c.world.pois.Where(p => p && (p.service || p.seatedActivity != DecisionSeatedActivity.None)))
            {
                if (poi.service) poi.interactionPoint.position = poi.service.queueSlots[0].position;
                else poi.interactionPoint.position = ExactFloor(poi.interactionPoint.position);
                Record(poi.interactionPoint); Record(poi);
            }
        }
        static Vector3 ExactFloor(Vector3 point)
        {
            Require(NavMesh.SamplePosition(point, out var hit, NavTolerance, NavMesh.AllAreas) && Mathf.Abs(hit.position.y) < .1f,
                "Authored approach is not on the exact safe floor: " + point);
            Require(Vector2.Distance(new Vector2(point.x,point.z), new Vector2(hit.position.x,hit.position.z)) < NavTolerance,
                "An anchor required an unsafe navigation snap.");
            return hit.position;
        }

        static void Validate(Context c)
        {
            var r = c.report; r.navigationAsset = AssetDatabase.GetAssetPath(c.surface.navMeshData);
            Require(r.navigationAsset.StartsWith("Assets/TrainStation/DecisionLab/Navigation/", StringComparison.Ordinal), "Amenities must use their own Lab navigation asset.");
            Require(c.surface.defaultArea == 1 && c.surface.useGeometry == NavMeshCollectGeometry.PhysicsColliders, "Furniture must remain part of a floor-only physical navigation bake.");
            r.agentRadius = c.surface.GetBuildSettings().agentRadius; r.agentHeight = c.surface.GetBuildSettings().agentHeight;
            var triangulation = NavMesh.CalculateTriangulation(); r.triangles = triangulation.indices.Length / 3;
            Require(r.triangles > 0, "Amenities navigation is empty.");
            r.minNavHeight = triangulation.vertices.Min(p => p.y); r.maxNavHeight = triangulation.vertices.Max(p => p.y);
            Require(r.minNavHeight > -.1f && r.maxNavHeight < .1f, "The bake includes elevated furniture or a non-floor surface.");
            r.furnitureFootprints = c.blockers.GetComponentsInChildren<NavMeshModifierVolume>().Length;
            Require(r.furnitureFootprints >= 15, "Furniture exclusion footprints are incomplete.");
            var origin = ExactFloor(new Vector3(0,0,-18f));
            r.seats.Clear(); r.services.Clear();
            foreach (var poi in c.world.pois.Where(p => p && p.seats != null && p.seats.Length > 0))
            {
                foreach (var seat in poi.seats)
                {
                    Require(seat && seat.seatedPose && seat.approachPoint && seat.transform.IsChildOf(c.seats), "A Lab seat has incomplete isolated ownership.");
                    var approach = ExactFloor(seat.approachPoint.position);
                    float distance = Vector3.Distance(seat.seatedPose.position, approach);
                    Require(distance <= 1.2f, "Seat transition is too long: " + seat.seatId + " (" + distance + " m)");
                    CheckPath(c, origin, approach);
                    foreach (var platform in c.world.pois.Where(p => p && p.activity == DecisionActivity.Platform)) CheckPath(c, approach, ExactFloor(platform.Position));
                    r.seats.Add(new SeatRecord { id = seat.seatId, activity = poi.seatedActivity.ToString(), path = Hierarchy(seat.transform),
                        approach = approach, seatedRoot = seat.seatedPose.position, transitionDistance = distance, approachSnapError = Vector3.Distance(approach, seat.approachPoint.position) });
                }
            }
            r.cafeSeats = Poi(c,"cafe").seats.Length; r.readingSeats = Poi(c,"shop-relay").seats.Length; r.restSeats = Poi(c,"rest-lounge-entrance").seats.Length;
            r.cafeTables = c.cafe.Cast<Transform>().Count(t => t.gameObject.activeSelf && t.name.StartsWith("Lab Café table", StringComparison.Ordinal));
            Require(r.cafeSeats == 4 && r.readingSeats == 2 && r.restSeats == 4 && r.cafeTables == 2, "Expected café4, reading2 and waiting-room4 usable seats.");
            Require(!c.social.seats.Any(s => s && s.transform.IsChildOf(c.seats)), "Legacy background seat ownership must stay separate.");
            foreach (string key in new[] { "Cafe", "Relay", "Billetterie", "Info" })
            {
                var group = c.circulation.Find(key); Require(group, "Missing circulation markers: " + key);
                var desk = key == "Info" ? c.social.informationDesk : Desk(c,key);
                var poi = c.world.pois.FirstOrDefault(p => p && p.service == desk);
                var entry = ExactFloor(group.Find("Entry").position); var aisle = ExactFloor(group.Find("Front aisle").position);
                var exit = ExactFloor(group.Find("Departure").position); var hall = ExactFloor(group.Find("Hall approach").position);
                foreach (var q in desk.queueSlots)
                { CheckPath(c, hall, ExactFloor(q.position)); CheckPath(c, q.position, exit); ++r.queueMarkers; }
                CheckPath(c, origin, entry); CheckPath(c, entry, aisle); CheckPath(c, aisle, exit); CheckPath(c, exit, hall);
                if (poi) Require(poi.departurePoint == group.Find("Departure") && Vector3.Distance(poi.Position, desk.queueSlots[0].position) < .01f, "POI did not follow its relocated counter: " + poi.poiId);
                for (int i = 1; i < desk.queueSlots.Length; ++i)
                    Require(Vector3.Distance(desk.queueSlots[i-1].position,desk.queueSlots[i].position) >= 1.1f, "Queue spacing is compressed: " + key);
                Require(Vector3.Distance(desk.queueSlots[0].position,exit) >= 1.5f, "Counter departure needs a distinct bypass.");
                r.services.Add(new ServiceRecord { key = key, service = desk.serviceName, poiId = poi ? poi.poiId : "", entry = entry, frontAisle = aisle,
                    departure = exit, hallApproach = hall, staff = desk.receptionist.transform.position, queue = desk.queueSlots.Select(t=>t.position).ToArray(),
                    seats = poi && poi.seats != null ? poi.seats.Length : 0, requiredEntryPhysicalWidth = key == "Info" ? 2.4f : 4f,
                    requiredEntryAgentCenterWidth = key == "Info" ? 1.2f : 2.8f, requiredAislePhysicalWidth = key == "Info" ? 2.4f : 3f,
                    requiredAisleAgentCenterWidth = key == "Info" ? 1.2f : 1.8f });
            }
            // Main hall and all platform routes remain present after the independent full-surface bake.
            CheckPath(c, ExactFloor(new Vector3(0,0,-25f)), ExactFloor(new Vector3(0,0,25f)));
            foreach (var platform in c.world.pois.Where(p => p && p.activity == DecisionActivity.Platform)) CheckPath(c, origin, ExactFloor(platform.Position));
        }
        static void CheckPath(Context c, Vector3 from, Vector3 to)
        {
            var path = new NavMeshPath(); Require(NavMesh.CalculatePath(from,to,NavMesh.AllAreas,path) && path.status == NavMeshPathStatus.PathComplete,
                "No complete amenities route: " + from + " → " + to); ++c.report.pathChecks;
        }
        static Transform Child(Transform parent, string name)
        { var item = new GameObject(name).transform; item.SetParent(parent,false); return item; }
        static Transform Marker(Transform parent, string name, Vector3 position, Quaternion rotation)
        { var item = Child(parent,name); item.SetPositionAndRotation(position,rotation); return item; }
        static Vector3 P(float x, float z) => new Vector3(x,0,z);
        static void Protect(Report report, string path)
        { if (!string.IsNullOrEmpty(path) && File.Exists(path) && !report.protectedFiles.Any(p=>p.path==path)) report.protectedFiles.Add(new FileEvidence { path=path,before=Hash(path) }); }
        static string Hierarchy(Transform item) => item.parent ? Hierarchy(item.parent)+"/"+item.name : item.name;
        static void Record(UnityEngine.Object item)
        { EditorUtility.SetDirty(item); if (PrefabUtility.IsPartOfPrefabInstance(item)) PrefabUtility.RecordPrefabInstancePropertyModifications(item); }
    }
}
#endif
