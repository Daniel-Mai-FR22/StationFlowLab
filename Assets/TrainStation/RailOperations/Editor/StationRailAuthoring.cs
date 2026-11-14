#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.RailOperations.Editor
{
    /// <summary>Saved modular rolling stock and terminus routes. Geometry is created only by this explicit Editor command.</summary>
    public static class StationRailAuthoring
    {
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        public const string RootName = "12 Rail operations — saved trains and timetable";
        public const string AssetRoot = "Assets/TrainStation/RailOperations";
        public const string SchedulePath = AssetRoot + "/Resources/TrainStationRail/StationRailSchedule.asset";
        public const string CabPrefabPath = AssetRoot + "/Prefabs/Station_TER_CabCar.prefab";
        public const string TrailerPrefabPath = AssetRoot + "/Prefabs/Station_TER_TrailerCar.prefab";
        public const string TrainPrefabPath = AssetRoot + "/Prefabs/Station_TER_FourCar.prefab";
        public const string ReportPath = "Reports/TrainStationRail/rail-authoring.json";
        public const float RailHeadY = -.585f, BerthZ = 60, ApproachZ = 360, ExitZ = 372, CorridorEndZ = 480;
        static readonly float[] TrackX = { -41.7f, -30.3f, -17.7f, -6.3f, 6.3f, 17.7f, 30.3f, 41.7f };
        static readonly int[] ActiveTracks = { 2, 4, 5, 7 };
        static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();
        [Serializable] public sealed class TrainRecord
        {
            public string serviceId, prefab; public int track, cars, movingDoors, wheels;
            public Vector3 position; public float frontZ, rearZ, width, minimumWheelBottom, maximumWheelBottom;
        }
        [Serializable] public sealed class RailReport
        {
            public bool success, alreadyPopulated; public string scene = ScenePath, backup, error, schedule;
            public int trains, cars, railOpenings; public float railHeadY = RailHeadY, corridorEndZ = CorridorEndZ;
            public List<string> adaptedOriginalObjects = new List<string>();
            public List<TrainRecord> services = new List<TrainRecord>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Rail/Populate saved trains and timetable")]
        public static void Populate()
        {
            RequireCleanEditor();
            var report = new RailReport();
            var scene = SceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            GameObject root = null;
            var changed = new List<GameObject>();
            try
            {
                var existing = scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
                if (existing) { report.alreadyPopulated = true; ValidateCore(existing.GetComponent<StationRailOperations>(), report); report.success = true; return; }
                report.backup = "BuildRequests/Backups/TrainStation.before-rail-operations." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
                Directory.CreateDirectory(Path.GetDirectoryName(report.backup)); File.Copy(ScenePath, report.backup, false);
                foreach (string folder in new[] { "/Editor", "/Prefabs", "/Materials", "/Meshes", "/Resources/TrainStationRail" }) EnsureFolder(AssetRoot + folder);
                MakeMaterials();
                var cab = EnsureCar(true); var trailer = EnsureCar(false); var trainPrefab = EnsureTrain(cab, trailer);
                var schedule = EnsureSchedule();
                root = new GameObject(RootName); SceneManager.MoveGameObjectToScene(root, scene);
                var operations = root.AddComponent<StationRailOperations>(); operations.schedule = schedule; operations.simulationRate = 3; operations.playOnStart = true;
                var corridor = Group("01 Extended rail corridor — open routes to staging", root.transform);
                BuildCorridor(corridor);
                AdaptRearCrossing(scene, Group("02 Rear service access — protected sections and rail openings", root.transform), report, changed);
                var routes = Group("03 Routes — independent editable anchors", root.transform);
                var trains = Group("04 Trains — reusable four-car prefabs", root.transform);
                operations.trains = new StationTrain[schedule.services.Length];
                for (int i = 0; i < schedule.services.Length; i++)
                {
                    var service = schedule.services[i]; float x = TrackX[service.trackNumber - 1];
                    var route = Group("Voie " + service.trackNumber + " — " + service.serviceId, routes);
                    var approach = Marker("Approach — incoming front", route, new Vector3(x,RailHeadY,ApproachZ));
                    var berth = Marker("Berth — front beside platform", route, new Vector3(x,RailHeadY,BerthZ));
                    var exit = Marker("Exit — reverse movement to staging", route, new Vector3(x,RailHeadY,ExitZ));
                    var instance = Instantiate(trainPrefab, trains, "TER " + service.trainNumber + " — voie " + service.trackNumber);
                    var train = instance.GetComponent<StationTrain>(); train.serviceId = service.serviceId; train.trackNumber = service.trackNumber;
                    train.approachPoint = approach; train.berthPoint = berth; train.exitPoint = exit; train.hideOffstage = true;
                    train.transform.SetPositionAndRotation(i < 2 ? berth.position : approach.position, Quaternion.identity);
                    // Only doors adjacent to the existing island platform open. Opposite-side doors remain authored and closed.
                    float platformSide = service.trackNumber == 2 || service.trackNumber == 4 ? -1 : 1;
                    ConfigureDoors(train, train.visualRoot.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Sliding leaf ", StringComparison.Ordinal) &&
                        Mathf.Sign(train.transform.InverseTransformPoint(t.position).x) == platformSide).ToArray());
                    foreach (var label in train.visualRoot.GetComponentsInChildren<TMP_Text>(true))
                        if (label.name == "Service display") label.text = "TER " + service.trainNumber + "\n" + service.destination.ToUpperInvariant();
                    operations.trains[i] = train;
                }
                var configurationErrors = operations.ValidateConfiguration();
                if (configurationErrors.Count != 0) throw new InvalidOperationException(string.Join("; ", configurationErrors));
                operations.EvaluatePreview(0);
                Physics.SyncTransforms();
                ValidateCore(operations, report);
                StationRailPresentation.Ensure(operations);
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                    if (component && PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save TrainStation rail operations.");
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                if (root) UnityEngine.Object.DestroyImmediate(root);
                foreach (var item in changed) if (item) item.SetActive(true);
                throw;
            }
            finally { Write(report); }
        }

        [Serializable] sealed class DoorLayerReport
        {
            public bool success, fileIdsPreserved, doorArraysPreserved;
            public int changedPrefabDoors, changedPrefabRecesses, inheritedSceneDoors, preservedOverrides;
            public string backupDirectory, error;
            public List<string> preserved = new List<string>();
        }
        [MenuItem("Efrei Research Lab/Train Station/Rail/Repair saved door surface layers")]
        public static void RepairDoorLayers()
        {
            RequireCleanEditor();
            var report = new DoorLayerReport();
            Scene station = SceneManager.GetSceneByPath(ScenePath), previousActive = SceneManager.GetActiveScene();
            bool opened = !station.IsValid() || !station.isLoaded;
            try
            {
                report.backupDirectory = "BuildRequests/Backups/RailDoorLayers-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(report.backupDirectory);
                foreach (string path in new[] { ScenePath, CabPrefabPath, TrailerPrefabPath, TrainPrefabPath })
                    File.Copy(path, report.backupDirectory + "/" + Path.GetFileName(path), false);
                if (opened) station = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                var operations = station.GetRootGameObjects().Single(g => g.name == RootName).GetComponent<StationRailOperations>();
                var trainSnapshots = operations.trains.Select(t => new DoorArraySnapshot(t)).ToArray();
                var doors = operations.trains.SelectMany(t => t.GetComponentsInChildren<Transform>(true))
                    .Where(t => t.name.StartsWith("Passenger door ", StringComparison.Ordinal)).Select(t => new DoorTransformSnapshot(t)).ToArray();
                foreach (string path in new[] { CabPrefabPath, TrailerPrefabPath })
                {
                    var before = SavedDoorFileIds(path);
                    var contents = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        foreach (var door in contents.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Passenger door ", StringComparison.Ordinal)))
                        {
                            float side = Mathf.Sign(door.localPosition.x);
                            if (Mathf.Abs(Mathf.Abs(door.localPosition.x) - 1.42f) < .00001f)
                            { var position = door.localPosition; position.x = side * 1.434f; door.localPosition = position; report.changedPrefabDoors++; }
                            else if (Mathf.Abs(Mathf.Abs(door.localPosition.x) - 1.434f) > .00001f)
                            { report.preserved.Add(path + " / " + door.name + ": custom parent offset retained"); continue; }
                            var recess = door.Find("Dark vestibule recess");
                            if (!recess) throw new InvalidOperationException("Saved door recess is missing: " + door.name);
                            if (Mathf.Abs(recess.localPosition.x + side * .012f) < .00001f)
                            { var position = recess.localPosition; position.x = side * .005f; recess.localPosition = position; report.changedPrefabRecesses++; }
                            else if (Mathf.Abs(recess.localPosition.x - side * .005f) > .00001f)
                                report.preserved.Add(path + " / " + door.name + ": custom recess offset retained");
                        }
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                    }
                    finally { PrefabUtility.UnloadPrefabContents(contents); }
                    var after = SavedDoorFileIds(path);
                    if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out long id) || id != pair.Value))
                        throw new InvalidOperationException("Door repair changed a saved Transform file ID in " + path);
                }
                report.fileIdsPreserved = true;
                foreach (var snapshot in trainSnapshots) snapshot.Validate();
                report.doorArraysPreserved = true;
                foreach (var snapshot in doors) snapshot.Validate(report);
                Physics.SyncTransforms();
                var validation = new RailReport(); ValidateCore(operations, validation);
                if (!EditorSceneManager.SaveScene(station)) throw new IOException("Could not save inherited rail door layers.");
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                if (opened && station.IsValid() && station.isLoaded) EditorSceneManager.CloseScene(station, true);
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
                Directory.CreateDirectory("Reports/TrainStationRail");
                File.WriteAllText("Reports/TrainStationRail/door-layer-repair.json", JsonUtility.ToJson(report, true));
            }
        }
        static Dictionary<string, long> SavedDoorFileIds(string path)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var ids = new Dictionary<string, long>();
            foreach (var item in root.GetComponentsInChildren<Transform>(true))
                if (item.name.StartsWith("Passenger door ", StringComparison.Ordinal) || item.name == "Dark vestibule recess")
                {
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out string _, out long id))
                        throw new InvalidOperationException("Could not inspect a saved door Transform identifier.");
                    ids.Add(AnimationUtility.CalculateTransformPath(item, root.transform), id);
                }
            return ids;
        }
        sealed class DoorArraySnapshot
        {
            readonly StationTrain train;
            readonly Transform[] panels;
            readonly Vector3[] closed, offsets, livePositions;
            readonly Vector3 position;
            readonly Quaternion rotation;
            public DoorArraySnapshot(StationTrain value)
            {
                train = value; panels = value.doorPanels.ToArray(); closed = value.closedDoorLocalPositions.ToArray(); offsets = value.openDoorLocalOffsets.ToArray();
                livePositions = panels.Select(t => t.localPosition).ToArray(); position = value.transform.position; rotation = value.transform.rotation;
            }
            public void Validate()
            {
                if (!panels.SequenceEqual(train.doorPanels) || !closed.SequenceEqual(train.closedDoorLocalPositions) || !offsets.SequenceEqual(train.openDoorLocalOffsets) ||
                    !livePositions.SequenceEqual(panels.Select(t => t.localPosition)) || train.transform.position != position || train.transform.rotation != rotation)
                    throw new InvalidOperationException("Door repair unexpectedly changed a train position, door reference, leaf pose, or closed/open calibration array.");
            }
        }
        sealed class DoorTransformSnapshot
        {
            readonly Transform parent, recess;
            readonly bool parentOverride, recessOverride;
            readonly Vector3 parentPosition, recessPosition;
            public DoorTransformSnapshot(Transform value)
            {
                parent = value; recess = parent.Find("Dark vestibule recess");
                if (!recess) throw new InvalidOperationException("Saved door recess is missing.");
                parentPosition = parent.localPosition; recessPosition = recess.localPosition;
                parentOverride = new SerializedObject(parent).FindProperty("m_LocalPosition.x").prefabOverride;
                recessOverride = new SerializedObject(recess).FindProperty("m_LocalPosition.x").prefabOverride;
            }
            public void Validate(DoorLayerReport report)
            {
                float side = Mathf.Sign(parentPosition.x);
                var expectedParent = parentPosition; var expectedRecess = recessPosition;
                if (!parentOverride) expectedParent.x = side * 1.434f;
                if (!recessOverride) expectedRecess.x = side * .005f;
                if ((parent.localPosition - expectedParent).sqrMagnitude > 1e-10f || (recess.localPosition - expectedRecess).sqrMagnitude > 1e-10f)
                    throw new InvalidOperationException("A door layer did not inherit its repaired prefab or a manual transform was changed.");
                if (parentOverride || recessOverride)
                { report.preservedOverrides++; report.preserved.Add(parent.name + ": existing scene Transform override retained"); }
                else report.inheritedSceneDoors++;
            }
        }

        static StationRailSchedule EnsureSchedule()
        {
            var existing = AssetDatabase.LoadAssetAtPath<StationRailSchedule>(SchedulePath); if (existing) return existing;
            var schedule = ScriptableObject.CreateInstance<StationRailSchedule>(); schedule.name = "Valmont demonstration timetable — editable";
            schedule.startHour = 14; schedule.startMinute = 20; schedule.cycleDurationSeconds = 720;
            string[] numbers = { "862421", "175830", "882306", "841712" };
            string[] origins = { "Dijon", "Orléans", "Strasbourg", "Paris-Est" };
            string[] destinations = { "Lyon-Part-Dieu", "Tours", "Nancy", "Reims" };
            float[] arrival = { -60, -30, 60, 240 }, departure = { 120, 150, 180, 360 };
            schedule.services = new StationRailService[4];
            for (int i = 0; i < 4; i++) schedule.services[i] = new StationRailService { serviceId = "valmont-ter-" + numbers[i], trainNumber = numbers[i],
                origin = origins[i], destination = destinations[i], trackNumber = ActiveTracks[i], arrivalOffsetSeconds = arrival[i], departureOffsetSeconds = departure[i],
                approachDurationSeconds = 60, departureDurationSeconds = 50, doorDurationSeconds = 8 };
            AssetDatabase.CreateAsset(schedule, SchedulePath); return schedule;
        }

        static GameObject EnsureTrain(GameObject cab, GameObject trailer)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(TrainPrefabPath); if (existing) return existing;
            var preview = EditorSceneManager.NewPreviewScene(); var root = new GameObject("Station_TER_FourCar"); SceneManager.MoveGameObjectToScene(root, preview);
            try
            {
                var train = root.AddComponent<StationTrain>(); train.visualRoot = Group("Rolling stock — four saved car modules", root.transform);
                for (int i = 0; i < 4; i++)
                {
                    var car = Instantiate(i == 0 || i == 3 ? cab : trailer, train.visualRoot, "Car " + (i + 1).ToString("00") + (i == 0 || i == 3 ? " — driving cab" : " — passenger trailer"));
                    car.transform.localPosition = new Vector3(0,0,i == 3 ? 80 : i * 20);
                    car.transform.localRotation = i == 3 ? Quaternion.Euler(0,180,0) : Quaternion.identity;
                    foreach (var text in car.GetComponentsInChildren<TMP_Text>(true)) if (text.name == "Car number") text.text = "2   " + (i + 1).ToString("00");
                }
                for (int i = 1; i < 4; i++)
                {
                    var gangway = Group("Flexible gangway " + i, train.visualRoot);
                    Box("Bellows", gangway, new Vector3(0,2.24f,i*20), new Vector3(2.12f,2.42f,.85f), M("Rubber"));
                    for (int rib = -2; rib <= 2; rib++) Box("Bellows rib " + rib, gangway, new Vector3(0,2.24f,i*20+rib*.13f), new Vector3(2.22f,2.50f,.055f), M("Undercarriage"));
                }
                ConfigureDoors(train, train.visualRoot.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Sliding leaf ",StringComparison.Ordinal)).ToArray());
                Record(root); return PrefabUtility.SaveAsPrefabAsset(root, TrainPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(preview); }
        }
        static void ConfigureDoors(StationTrain train, Transform[] doors)
        {
            train.doorPanels = doors; train.closedDoorLocalPositions = doors.Select(t => t.localPosition).ToArray();
            train.openDoorLocalOffsets = doors.Select(t => Vector3.forward * (t.name.EndsWith("-",StringComparison.Ordinal) ? -.67f : .67f)).ToArray();
        }

        static GameObject EnsureCar(bool cab)
        {
            string path = cab ? CabPrefabPath : TrailerPrefabPath;
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path); if (existing) return existing;
            var preview = EditorSceneManager.NewPreviewScene(); var root = new GameObject(cab ? "Station_TER_CabCar" : "Station_TER_TrailerCar"); SceneManager.MoveGameObjectToScene(root,preview);
            try
            {
                var shell = Group("01 Bodyshell and livery",root.transform);
                MeshObject("Bevelled aluminium shell",shell,BodyMesh(cab),M("Body"));
                var bodyCollider = shell.gameObject.AddComponent<BoxCollider>(); bodyCollider.center = new Vector3(0,2.38f,10); bodyCollider.size = new Vector3(2.80f,2.92f,19);
                Box("Lower equipment skirt",shell,new Vector3(0,1.12f,10),new Vector3(2.66f,.37f,18.4f),M("Blue"));
                Box("Underfloor equipment",shell,new Vector3(0,.81f,10),new Vector3(2.13f,.28f,9.7f),M("Undercarriage"));
                for (int side=-1;side<=1;side+=2)
                {
                    float first = cab ? 3.9f : .7f, length = 19.1f-first;
                    Box("Window belt "+side,shell,new Vector3(side*1.421f,2.72f,first+length*.5f),new Vector3(.018f,1.05f,length),M("Blue"));
                    Box("Magenta lower stripe "+side,shell,new Vector3(side*1.426f,1.52f,10.1f),new Vector3(.015f,.15f,17.8f),M("Magenta"));
                    Box("Silver waist line "+side,shell,new Vector3(side*1.426f,2.08f,10.1f),new Vector3(.018f,.055f,17.8f),M("Trim"));
                    Label("Brand",shell,"SNCF",new Vector3(side*1.445f,1.79f,8.0f),.25f,Color.white,side<0?90:-90,new Vector2(1.6f,.4f));
                    Label("Car number",shell,"2   01",new Vector3(side*1.445f,3.33f,17.4f),.24f,Color.white,side<0?90:-90,new Vector2(1.4f,.4f));
                }
                var glazing = Group("02 Windows and passenger doors",root.transform);
                for (int side=-1;side<=1;side+=2)
                {
                    for (int window=0;window<7;window++)
                    {
                        float z=4.65f+window*1.67f;
                        Box("Window surround "+side+" / "+window,glazing,new Vector3(side*1.433f,2.77f,z),new Vector3(.018f,.86f,1.33f),M("Rubber"));
                        Box("Passenger glazing "+side+" / "+window,glazing,new Vector3(side*1.444f,2.78f,z),new Vector3(.008f,.70f,1.15f),M("Glass"));
                        Box("Window highlight "+side+" / "+window,glazing,new Vector3(side*1.449f,3.08f,z),new Vector3(.002f,.018f,1.04f),M("GlassHighlight"));
                    }
                    foreach (float z in new[]{2.9f,17.5f}) BuildDoor(glazing,side,z);
                    Box("Service display case "+side,glazing,new Vector3(side*1.434f,3.34f,11.3f),new Vector3(.024f,.40f,3.1f),M("Rubber"));
                    Label("Service display",glazing,"TER REGIONAL\nGARE DE VALMONT",new Vector3(side*1.449f,3.35f,11.3f),.11f,new Color(1,.77f,.19f),side<0?90:-90,new Vector2(2.9f,.37f));
                }
                var undercarriage = Group("03 Bogies, axles and wheelsets",root.transform);
                foreach (float z in new[]{4.25f,15.55f}) BuildBogie(undercarriage,z);
                var roof = Group("04 Roof and electrical equipment",root.transform);
                foreach (float z in new[]{6.2f,13.5f})
                {
                    Box("HVAC module",roof,new Vector3(0,3.98f,z),new Vector3(1.62f,.32f,2.2f),M("Roof"));
                    for(int vent=0;vent<8;vent++)Box("Vent grille "+vent,roof,new Vector3(0,4.149f,z-.8f+vent*.23f),new Vector3(1.40f,.018f,.075f),M("Undercarriage"));
                }
                if(!cab) BuildPantograph(roof,9.9f);
                var ends = Group("05 Couplers and driving equipment",root.transform);
                foreach(float z in new[]{.12f,19.86f})Box("Automatic coupler",ends,new Vector3(0,.88f,z),new Vector3(.40f,.28f,.34f),M("Undercarriage"));
                if(cab)
                {
                    Box("Cab dark front mask",ends,new Vector3(0,2.04f,.188f),new Vector3(1.86f,.77f,.035f),M("Rubber"));
                    MeshObject("Sloping cab windscreen",ends,WindshieldMesh(),M("Glass"));
                    for(int side=-1;side<=1;side+=2)
                    {
                        Box("Cab side window "+side,ends,new Vector3(side*1.353f,2.91f,2.0f),new Vector3(.014f,.58f,.86f),M("Glass"));
                        Box("Headlight housing "+side,ends,new Vector3(side*.72f,1.61f,.181f),new Vector3(.42f,.22f,.035f),M("Rubber"));
                        Box("White headlight "+side,ends,new Vector3(side*.78f,1.62f,.158f),new Vector3(.20f,.12f,.018f),M("Headlight"));
                        Box("Red marker light "+side,ends,new Vector3(side*.57f,1.62f,.157f),new Vector3(.085f,.095f,.02f),M("TailLight"));
                    }
                    Beam("Windscreen wiper",ends,new Vector3(-.08f,2.79f,.39f),new Vector3(.55f,3.12f,.91f),.025f,M("Undercarriage"));
                    Label("Cab TER badge",ends,"TER",new Vector3(0,1.25f,.176f),.23f,new Color(.06f,.18f,.36f),0,new Vector2(1.3f,.35f));
                }
                Record(root); return PrefabUtility.SaveAsPrefabAsset(root,path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(preview); }
        }

        static void BuildDoor(Transform parent,int side,float z)
        {
            var door=Group("Passenger door "+side+" / "+z,parent);door.localPosition=new Vector3(side*1.434f,0,z);
            Box("Dark vestibule recess",door,new Vector3(side*.005f,1.92f,0),new Vector3(.008f,2.12f,1.28f),M("Rubber"));
            foreach(int edge in new[]{-1,1})Box("Door frame side "+edge,door,new Vector3(0,1.92f,edge*.685f),new Vector3(.022f,2.22f,.06f),M("Trim"));
            Box("Door lintel",door,new Vector3(0,3.01f,0),new Vector3(.022f,.07f,1.40f),M("Trim"));
            Box("Door threshold",door,new Vector3(0,.845f,0),new Vector3(.022f,.05f,1.40f),M("Trim"));
            Box("Vestibule floor glimpse",door,new Vector3(side*.003f,.91f,0),new Vector3(.018f,.025f,1.20f),M("Roof"));
            for(int direction=-1;direction<=1;direction+=2)
            {
                var leaf=Group("Sliding leaf "+side+" / "+z+" / "+(direction<0?"-":"+"),door);
                leaf.localPosition=new Vector3(side*.006f,0,direction*.335f);
                Box("Blue door panel",leaf,new Vector3(0,1.93f,0),new Vector3(.014f,2.11f,.635f),M("Door"));
                Box("Door window",leaf,new Vector3(side*.010f,2.43f,0),new Vector3(.006f,.88f,.45f),M("Glass"));
                Box("Door hand rail",leaf,new Vector3(side*.015f,1.56f,-direction*.19f),new Vector3(.012f,.30f,.035f),M("Trim"));
                Box("Door warning line",leaf,new Vector3(side*.010f,.98f,0),new Vector3(.006f,.055f,.59f),M("Yellow"));
            }
            Box("Boarding lower step",door,new Vector3(0,.65f,0),new Vector3(.034f,.07f,1.42f),M("Trim"));
            Box("Boarding upper step",door,new Vector3(0,.86f,0),new Vector3(.034f,.055f,1.35f),M("Trim"));
            Box("Accessible door button",door,new Vector3(side*.015f,1.59f,.75f),new Vector3(.014f,.12f,.09f),M("Yellow"));
        }
        static void BuildBogie(Transform parent,float z)
        {
            var bogie=Group("Bogie at "+z,parent);bogie.localPosition=new Vector3(0,0,z);
            Box("Bogie frame",bogie,new Vector3(0,.53f,0),new Vector3(1.95f,.28f,3.1f),M("Undercarriage"));
            foreach(float axleZ in new[]{-1.02f,1.02f})
            {
                Cylinder("Axle",bogie,new Vector3(0,.40f,axleZ),.095f,1.60f,M("Axle"),Quaternion.Euler(0,0,90));
                for(int side=-1;side<=1;side+=2)
                {
                    Cylinder("Wheel tread "+side+" / "+axleZ,bogie,new Vector3(side*.755f,.40f,axleZ),.40f,.12f,M("Wheel"),Quaternion.Euler(0,0,90));
                    Cylinder("Wheel hub "+side+" / "+axleZ,bogie,new Vector3(side*.85f,.40f,axleZ),.18f,.10f,M("Axle"),Quaternion.Euler(0,0,90));
                    Box("Axlebox "+side+" / "+axleZ,bogie,new Vector3(side*.99f,.43f,axleZ),new Vector3(.22f,.24f,.38f),M("Roof"));
                    for(int spring=0;spring<3;spring++)Cylinder("Suspension ring",bogie,new Vector3(side*.92f,.65f+spring*.045f,axleZ),.12f,.025f,M("Axle"),Quaternion.identity);
                }
            }
        }
        static void BuildPantograph(Transform parent,float z)
        {
            var p=Group("Pantograph — contact height 5.4 m world",parent);
            Box("Insulated base",p,new Vector3(0,4.02f,z),new Vector3(1.42f,.18f,1.35f),M("Roof"));
            for(int side=-1;side<=1;side+=2)
            {
                Beam("Lower articulated arm "+side,p,new Vector3(side*.34f,4.12f,z-.55f),new Vector3(side*.34f,5.05f,z+.55f),.055f,M("Axle"));
                Beam("Upper articulated arm "+side,p,new Vector3(side*.34f,5.05f,z+.55f),new Vector3(side*.34f,5.97f,z-.18f),.045f,M("Axle"));
            }
            Box("Carbon contact strip",p,new Vector3(0,5.972f,z-.18f),new Vector3(1.56f,.026f,.085f),M("Rubber"));
        }

        static void BuildCorridor(Transform parent)
        {
            float length=CorridorEndZ-214, middle=214+length*.5f;
            Box("Foundation extension beyond district base",parent,new Vector3(0,-1.35f,(222+CorridorEndZ)*.5f),new Vector3(104,.55f,CorridorEndZ-222),StationMaterial("ballast"),true);
            for(int side=-1;side<=1;side+=2)
            {
                Box("Extended service walkway "+side,parent,new Vector3(side*49.5f,-.06f,(222+CorridorEndZ)*.5f),new Vector3(3,.12f,CorridorEndZ-222),StationMaterial("floor"),true);
                Box("Extended boundary wall "+side,parent,new Vector3(side*52.1f,.55f,(222+CorridorEndZ)*.5f),new Vector3(.35f,1.1f,CorridorEndZ-222),StationMaterial("stone"),true);
                for(float z=226;z<CorridorEndZ;z+=16)
                {
                    Box("Boundary fence post "+side+" / "+z,parent,new Vector3(side*52.1f,1.30f,z),new Vector3(.35f,2.6f,.35f),StationMaterial("steel"),true);
                    Box("Boundary mesh panel "+side+" / "+z,parent,new Vector3(side*52.1f,1.75f,z+7.7f),new Vector3(.055f,1.2f,15.4f),StationMaterial("steel"));
                }
            }
            for(int track=0;track<TrackX.Length;track++)
            {
                float x=TrackX[track];var line=Group("Voie "+(track+1).ToString("00")+" — extension 214 to 480 m",parent);
                Box("Extended ballast bed",line,new Vector3(x,-.99f,middle),new Vector3(4.8f,.38f,length),StationMaterial("ballast"),true);
                for(float z=214.0f;z<CorridorEndZ;z+=1.36f)Box("Concrete sleeper "+z.ToString("F2"),line,new Vector3(x,-.795f,z),new Vector3(2.6f,.18f,.24f),StationMaterial("concrete"));
                for(int side=-1;side<=1;side+=2)
                {
                    Box("Extended running rail "+side,line,new Vector3(x+side*.755f,-.65f,middle),new Vector3(.075f,.13f,length),StationMaterial("rail"),true);
                    Box("Extended rail foot "+side,line,new Vector3(x+side*.755f,-.737f,middle),new Vector3(.15f,.035f,length),StationMaterial("steel"));
                }
                Box("Extended contact wire",line,new Vector3(x,5.4f,middle),new Vector3(.026f,.026f,length),StationMaterial("dark"));
            }
            for(float z=234;z<CorridorEndZ;z+=30)
            {
                for(int side=-1;side<=1;side+=2)Box("Extension catenary mast "+side+" / "+z,parent,new Vector3(side*47.3f,3.35f,z),new Vector3(.27f,8.3f,.30f),StationMaterial("steel"),true);
                Box("Extension catenary gantry "+z,parent,new Vector3(0,7.5f,z),new Vector3(95,.18f,.20f),StationMaterial("steel"));
                foreach(float x in TrackX)Box("Extension dropper "+x+" / "+z,parent,new Vector3(x,6.45f,z),new Vector3(.04f,2.1f,.04f),StationMaterial("dark"));
            }
        }
        static void AdaptRearCrossing(Scene scene,Transform parent,RailReport report,List<GameObject> changed)
        {
            foreach(string name in new[]{"Rear service gate left","Rear service gate right","Rear service crossing"})
            {
                var matches=scene.GetRootGameObjects().Where(g=>g.name!=RootName).SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).Where(t=>t.name==name).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("Expected one original blocking object named "+name);
                var item=matches[0].gameObject;
                if(item.activeSelf){item.SetActive(false);changed.Add(item);report.adaptedOriginalObjects.Add(name);}
            }
            float start=-51;
            for(int gap=0;gap<=TrackX.Length;gap++)
            {
                float end=gap<TrackX.Length?TrackX[gap]-1.95f:51;
                float width=end-start;
                if(width>.1f)
                {
                    Box("Protected rear walkway segment "+gap,parent,new Vector3((start+end)*.5f,-.06f,218),new Vector3(width,.12f,8),StationMaterial("floor"),true);
                    if(start<0&&end>0)
                    {
                        GatePiece(parent,start,-2.1f,gap+" west");GatePiece(parent,2.1f,end,gap+" east");
                        Label("Retained staff pedestrian exit",parent,"PASSAGE PERSONNEL",new Vector3(0,2.22f,221.91f),.18f,Color.white,0,new Vector2(4,.35f));
                    }
                    else GatePiece(parent,start,end,gap.ToString());
                }
                if(gap<TrackX.Length)
                {
                    Marker("Rail clearance opening — voie "+(gap+1),parent,new Vector3(TrackX[gap],RailHeadY,222));
                    for(int side=-1;side<=1;side+=2)
                    {
                        Box("Crossing guard post "+gap+" / "+side,parent,new Vector3(TrackX[gap]+side*2.05f,.60f,218),new Vector3(.10f,1.2f,.10f),StationMaterial("yellow"),true);
                        Box("Crossing edge warning "+gap+" / "+side,parent,new Vector3(TrackX[gap]+side*2.06f,.015f,218),new Vector3(.18f,.03f,7.8f),StationMaterial("yellow"));
                    }
                    report.railOpenings++;start=TrackX[gap]+1.95f;
                }
            }
        }
        static void GatePiece(Transform parent,float start,float end,string name)
        {
            if(end-start<.1f)return;
            Box("Rear fence retained safe span "+name,parent,new Vector3((start+end)*.5f,1.05f,222),new Vector3(end-start,2.1f,.12f),StationMaterial("steel"),true);
        }

        [MenuItem("Efrei Research Lab/Train Station/Rail/Validate saved rolling stock")]
        public static void Validate()
        {
            var report=new RailReport();
            try
            {
                var scene=SceneManager.GetSceneByPath(ScenePath);if(!scene.IsValid()||!scene.isLoaded)throw new InvalidOperationException("Open TrainStation first.");
                var operations=scene.GetRootGameObjects().Single(g=>g.name==RootName).GetComponent<StationRailOperations>();ValidateCore(operations,report);report.success=true;
            }
            catch(Exception error){report.error=error.ToString();throw;}
            finally{Write(report);}
        }
        static void ValidateCore(StationRailOperations operations,RailReport report)
        {
            if(!operations||!operations.schedule||operations.trains.Length!=4)throw new InvalidOperationException("Four saved trains and their shared schedule are required.");
            var configurationErrors=operations.ValidateConfiguration();if(configurationErrors.Count!=0)throw new InvalidOperationException(string.Join("; ",configurationErrors));
            report.schedule=AssetDatabase.GetAssetPath(operations.schedule);report.trains=operations.trains.Length;
            if(report.schedule!=SchedulePath)throw new InvalidOperationException("Timetable must reference the saved Resources asset.");
            foreach(var train in operations.trains)
            {
                if(!train||!train.visualRoot||!train.approachPoint||!train.berthPoint||!train.exitPoint||!PrefabUtility.IsPartOfPrefabInstance(train))throw new InvalidOperationException("Saved train prefab or route references missing.");
                if(train.approachPoint.IsChildOf(train.transform)||train.berthPoint.IsChildOf(train.transform)||train.exitPoint.IsChildOf(train.transform))throw new InvalidOperationException("Route anchors must not move with the train.");
                if(train.doorPanels.Length!=16||train.closedDoorLocalPositions.Length!=16||train.openDoorLocalOffsets.Length!=16)throw new InvalidOperationException("Each train requires16 configured platform-side sliding door leaves.");
                var cars=train.visualRoot.Cast<Transform>().Where(t=>t.name.StartsWith("Car ",StringComparison.Ordinal)).ToArray();
                var wheels=train.visualRoot.GetComponentsInChildren<Renderer>(true).Where(r=>r.name.StartsWith("Wheel tread ",StringComparison.Ordinal)).ToArray();
                if(cars.Length!=4||wheels.Length!=32)throw new InvalidOperationException("Expected4 reusable car modules and32 wheels per train.");
                var renderers=train.visualRoot.GetComponentsInChildren<Renderer>(true).Where(r=>!(r is MeshRenderer mesh&&mesh.GetComponent<TMP_Text>())).ToArray();
                var bounds=renderers[0].bounds;foreach(var renderer in renderers.Skip(1))bounds.Encapsulate(renderer.bounds);
                var item=new TrainRecord{serviceId=train.serviceId,track=train.trackNumber,prefab=PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(train.gameObject),cars=cars.Length,movingDoors=train.doorPanels.Length,wheels=wheels.Length,
                    position=train.transform.position,frontZ=bounds.min.z,rearZ=bounds.max.z,width=bounds.size.x,minimumWheelBottom=wheels.Min(r=>r.bounds.min.y),maximumWheelBottom=wheels.Max(r=>r.bounds.min.y)};
                report.services.Add(item);report.cars+=cars.Length;
                if(Mathf.Abs(item.minimumWheelBottom-RailHeadY)>.005f||Mathf.Abs(item.maximumWheelBottom-RailHeadY)>.005f)throw new InvalidOperationException("Wheel treads do not meet the existing rail-head height.");
                if(item.width>2.94f||item.rearZ-item.frontZ>81f||item.rearZ>CorridorEndZ)throw new InvalidOperationException("Train envelope exceeds authored rail clearance.");
                if(Mathf.Abs(train.transform.position.x-TrackX[train.trackNumber-1])>.01f)throw new InvalidOperationException("Train is not centred on its assigned track.");
                // Test the moving body corridor above sleepers/rails and below wires for every active track.
                for(float z=BerthZ;z<ExitZ+80;z+=8)
                {
                    var hits=Physics.OverlapBox(new Vector3(TrackX[train.trackNumber-1],1.15f,z),new Vector3(1.46f,1.12f,3.8f),Quaternion.identity,~0,QueryTriggerInteraction.Ignore);
                    if(hits.Any(c=>c&&!c.GetComponentInParent<StationTrain>()))throw new InvalidOperationException("A station collider blocks the train corridor near Z="+z+": "+string.Join(", ",hits.Where(c=>c&&!c.GetComponentInParent<StationTrain>()).Select(c=>c.name)));
                }
            }
        }

        static Mesh BodyMesh(bool cab)
        {
            string path=AssetRoot+"/Meshes/"+(cab?"Cab_BevelledBody":"Trailer_BevelledBody")+".asset";
            var existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(existing)return existing;
            float[] z=cab?new[]{.2f,1.7f,3.3f,19.5f}:new[]{.5f,19.5f};
            float[] widths=cab?new[]{1.05f,1.32f,1.40f,1.40f}:new[]{1.40f,1.40f};
            float[] tops=cab?new[]{2.65f,3.60f,3.82f,3.82f}:new[]{3.82f,3.82f};
            var vertices=new List<Vector3>();var triangles=new List<int>();
            for(int ring=0;ring<z.Length;ring++)
            {
                float w=widths[ring],t=tops[ring],b=cab&&ring==0?1.15f:.95f;
                vertices.AddRange(new[]{new Vector3(-w,b,z[ring]),new Vector3(w,b,z[ring]),new Vector3(w,t-.65f,z[ring]),new Vector3(w*.88f,t-.15f,z[ring]),new Vector3(w*.65f,t,z[ring]),new Vector3(-w*.65f,t,z[ring]),new Vector3(-w*.88f,t-.15f,z[ring]),new Vector3(-w,t-.65f,z[ring])});
                if(ring>0)for(int side=0;side<8;side++){int a=(ring-1)*8+side,bn=(ring-1)*8+(side+1)%8,c=ring*8+side,d=ring*8+(side+1)%8;triangles.AddRange(new[]{a,bn,c,bn,d,c});}
            }
            for(int i=1;i<7;i++){triangles.AddRange(new[]{0,i+1,i});int last=(z.Length-1)*8;triangles.AddRange(new[]{last,last+i,last+i+1});}
            var mesh=new Mesh{name=cab?"Original TER cab body":"Original TER trailer body"};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        static Mesh WindshieldMesh()
        {
            string path=AssetRoot+"/Meshes/Cab_Windscreen.asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(mesh)return mesh;
            mesh=new Mesh{name="Original sloping cab windscreen"};mesh.vertices=new[]{new Vector3(-.65f,2.672f,.21f),new Vector3(.65f,2.672f,.21f),new Vector3(-.85f,3.625f,1.70f),new Vector3(.85f,3.625f,1.70f)};
            mesh.triangles=new[]{0,2,1,1,2,3};mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        static void MakeMaterials()
        {
            Materials.Clear();
            Material("Body",new Color(.78f,.82f,.85f),.45f,.55f);Material("Blue",new Color(.06f,.22f,.39f),.25f,.45f);Material("Door",new Color(.11f,.34f,.52f),.15f,.4f);
            Material("Magenta",new Color(.68f,.04f,.25f),.15f,.4f);Material("Trim",new Color(.64f,.70f,.74f),.75f,.7f);Material("Roof",new Color(.33f,.39f,.43f),.45f,.4f);
            Material("Rubber",new Color(.035f,.047f,.055f),.05f,.23f);Material("Undercarriage",new Color(.10f,.13f,.15f),.6f,.35f);Material("Axle",new Color(.30f,.35f,.39f),.8f,.6f);
            Material("Wheel",new Color(.28f,.31f,.33f),.85f,.7f);Material("Glass",new Color(.075f,.18f,.24f),.5f,.88f);Material("GlassHighlight",new Color(.34f,.58f,.65f),.3f,.7f);
            Material("Yellow",new Color(.95f,.73f,.08f),.1f,.4f);Material("Headlight",new Color(1,.94f,.76f),.1f,.4f,true);Material("TailLight",new Color(.8f,.025f,.018f),.1f,.4f,true);
        }
        static void Material(string name,Color color,float metallic,float smoothness,bool emission=false)
        {
            string path=AssetRoot+"/Materials/Rail_"+name+".mat";var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!material){material=new Material(Shader.Find("Universal Render Pipeline/Lit")){name="Rail "+name};material.SetColor("_BaseColor",color);material.SetFloat("_Metallic",metallic);material.SetFloat("_Smoothness",smoothness);if(emission){material.EnableKeyword("_EMISSION");material.SetColor("_EmissionColor",color*2);}AssetDatabase.CreateAsset(material,path);}Materials[name]=material;
        }
        static Material M(string name)=>Materials[name];
        static Material StationMaterial(string name)=>AssetDatabase.LoadAssetAtPath<Material>("Assets/TrainStation/Materials/Station_"+name+".mat")??throw new InvalidOperationException("Missing station material "+name);
        static Transform Group(string name,Transform parent){var go=new GameObject(name);go.transform.SetParent(parent,false);return go.transform;}
        static Transform Marker(string name,Transform parent,Vector3 point){var marker=Group(name,parent);marker.position=point;marker.rotation=Quaternion.identity;return marker;}
        static GameObject Instantiate(GameObject prefab,Transform parent,string name){var go=(GameObject)PrefabUtility.InstantiatePrefab(prefab,parent.gameObject.scene);go.transform.SetParent(parent,false);go.name=name;return go;}
        static GameObject Box(string name,Transform parent,Vector3 point,Vector3 size,Material material,bool collider=false)
        {var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent,false);go.transform.localPosition=point;go.transform.localScale=size;go.GetComponent<Renderer>().sharedMaterial=material;if(!collider)UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());return go;}
        static void Cylinder(string name,Transform parent,Vector3 point,float radius,float length,Material material,Quaternion rotation)
        {var go=GameObject.CreatePrimitive(PrimitiveType.Cylinder);go.name=name;go.transform.SetParent(parent,false);go.transform.localPosition=point;go.transform.localRotation=rotation;go.transform.localScale=new Vector3(radius*2,length*.5f,radius*2);go.GetComponent<Renderer>().sharedMaterial=material;UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());}
        static void Beam(string name,Transform parent,Vector3 a,Vector3 b,float thickness,Material material)
        {var go=Box(name,parent,(a+b)*.5f,new Vector3(thickness,Vector3.Distance(a,b),thickness),material);go.transform.localRotation=Quaternion.FromToRotation(Vector3.up,(b-a).normalized);}
        static void MeshObject(string name,Transform parent,Mesh mesh,Material material)
        {var go=new GameObject(name);go.transform.SetParent(parent,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=material;}
        static void Label(string name,Transform parent,string text,Vector3 position,float size,Color color,float yaw,Vector2 dimensions)
        {var go=new GameObject(name);go.transform.SetParent(parent,false);go.transform.localPosition=position;go.transform.localRotation=Quaternion.Euler(0,yaw,0);var label=go.AddComponent<TextMeshPro>();label.text=text;label.font=TMP_Settings.defaultFontAsset;label.fontSize=size*10;label.alignment=TextAlignmentOptions.Center;label.color=color;label.rectTransform.sizeDelta=dimensions;label.textWrappingMode=TextWrappingModes.NoWrap;}
        static void Record(GameObject root){foreach(var component in root.GetComponentsInChildren<Component>(true))if(component&&PrefabUtility.IsPartOfPrefabInstance(component))PrefabUtility.RecordPrefabInstancePropertyModifications(component);}
        static void EnsureFolder(string path){if(AssetDatabase.IsValidFolder(path))return;string parent=Path.GetDirectoryName(path).Replace('\\','/');EnsureFolder(parent);AssetDatabase.CreateFolder(parent,Path.GetFileName(path));}
        static void RequireCleanEditor(){if(EditorApplication.isPlayingOrWillChangePlaymode||AnimationMode.InAnimationMode())throw new InvalidOperationException("Stop Play/preview before rail authoring.");for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("Save scene edits first. Rail authoring never discards unsaved work.");}
        static void Write(RailReport report){Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));}
    }
}
#endif

